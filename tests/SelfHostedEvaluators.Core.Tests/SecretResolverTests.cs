using System.Security.Cryptography.X509Certificates;
using System.Net;
using SelfHostedEvaluators.Core.Auth;

namespace SelfHostedEvaluators.Core.Tests;

public class SecretResolverTests
{
    [TestCase("kv://my-vault/name", "my-vault", "name", null)]
    [TestCase("kv://my-vault/name/version-1", "my-vault", "name", "version-1")]
    public void ReferencesAreParsedWithoutAllowingAlternateOrigins(string uri, string vault, string name, string? version)
    {
        var reference = KeyVaultReference.Parse(new(uri));
        Assert.That(reference, Is.EqualTo(new KeyVaultReference(vault, name, version)));
        Assert.That(reference.VaultUri, Is.EqualTo(new Uri("https://my-vault.vault.azure.net")));
    }

    [TestCase("https://vault/name")]
    [TestCase("kv://vault/name?query=secret")]
    [TestCase("kv://vault/name#fragment")]
    [TestCase("kv://user@vault/name")]
    [TestCase("kv://vault:443/name")]
    [TestCase("kv://vault/name/extra/version")]
    [TestCase("kv://vault/name%2fextra")]
    [TestCase("kv://vault/")]
    public void InvalidReferencesFailWithoutEchoingInput(string uri)
    {
        var error = Assert.Throws<AuthenticationFailureException>(() => KeyVaultReference.Parse(new(uri)));
        Assert.That(error!.Message, Does.Not.Contain(uri));
    }

    [Test]
    public async Task CacheExpiresAndRotationIsRegisteredForRedaction()
    {
        using var rig = new AuthRig();
        var reference = new Uri("kv://vault/key");
        Assert.That(await rig.Secrets.ResolveAsync(reference, default), Is.EqualTo("key-value"));
        rig.Source.Values["key"] = "rotated";
        Assert.That(await rig.Secrets.ResolveAsync(reference, default), Is.EqualTo("key-value"));
        rig.Time.Advance(TimeSpan.FromMinutes(5));
        Assert.That(await rig.Secrets.ResolveAsync(reference, default), Is.EqualTo("rotated"));
        Assert.That(rig.Source.Calls, Is.EqualTo(2));
        Assert.That(rig.Redactor.Redact("key-value rotated"), Is.EqualTo("[REDACTED] [REDACTED]"));
    }

    [Test]
    public async Task ConcurrentResolutionsCoalesceAndVersionsHaveSeparateCacheKeys()
    {
        using var rig = new AuthRig();
        await Task.WhenAll(Enumerable.Range(0, 30).Select(async _ => await rig.Secrets.ResolveAsync(new("kv://vault/key"), default)));
        Assert.That(rig.Source.Calls, Is.EqualTo(1));
        await rig.Secrets.ResolveAsync(new("kv://vault/key/version"), default);
        Assert.That(rig.Source.Calls, Is.EqualTo(2));
    }

    [Test]
    public async Task FailedRefreshDoesNotFallBackToExpiredSecretAndCancellationPropagates()
    {
        using var rig = new AuthRig();
        await rig.Secrets.ResolveAsync(new("kv://vault/key"), default);
        rig.Time.Advance(TimeSpan.FromMinutes(6));
        rig.Source.Error = new InvalidOperationException("key-value https://vault.test?secret=unregistered");
        var error = Assert.ThrowsAsync<AuthenticationFailureException>(async () =>
            await rig.Secrets.ResolveAsync(new("kv://vault/key"), default));
        Assert.That(error!.ToString(), Does.Not.Contain("key-value").And.Not.Contain("unregistered"));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsAsync<OperationCanceledException>(async () => await rig.Secrets.ResolveAsync(new("kv://vault/key"), cts.Token));
    }

    [Test]
    public async Task CertificatesAreRetrievedFromBackingSecretsWithPrivateKeysAndRejectMalformedPayloads()
    {
        using var rig = new AuthRig();
        using var cert = await rig.Secrets.ResolveCertificateAsync(new("kv://vault/cert"), default);
        Assert.That(cert.HasPrivateKey, Is.True);
        rig.Source.Values["cert"] = "invalid-certificate";
        rig.Time.Advance(TimeSpan.FromMinutes(6));
        var error = Assert.ThrowsAsync<AuthenticationFailureException>(async () =>
            await rig.Secrets.ResolveCertificateAsync(new("kv://vault/cert"), default));
        Assert.That(error!.ToString(), Does.Not.Contain("invalid-certificate"));
    }

    [Test]
    public async Task PemCertificatePrivateKeyIsSupportedAndPublicOnlyCertificatesFail()
    {
        using var rig = new AuthRig();
        using var key = System.Security.Cryptography.RSA.Create(2048);
        var request = new CertificateRequest("CN=pem-test", key, System.Security.Cryptography.HashAlgorithmName.SHA256,
            System.Security.Cryptography.RSASignaturePadding.Pkcs1);
        using var source = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        rig.Source.Values["pem"] = source.ExportCertificatePem() + "\n" + key.ExportPkcs8PrivateKeyPem();
        using var cert = await rig.Secrets.ResolveCertificateAsync(new("kv://vault/pem"), default);
        Assert.That(cert.HasPrivateKey, Is.True);
        rig.Source.Values["public"] = Convert.ToBase64String(source.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Cert));
        Assert.ThrowsAsync<AuthenticationFailureException>(async () => await rig.Secrets.ResolveCertificateAsync(new("kv://vault/public"), default));
    }

    [Test]
    public async Task KeyVaultSdkUsesInjectedHardenedBoundedTransportAndExactReference()
    {
        var transport = new FakeTransport();
        transport.Respond = _ => new(HttpStatusCode.OK)
            { Content = new StringContent("""{"value":"from-vault","id":"https://vault.vault.azure.net/secrets/key/version"}""") };
        var azure = new FakeAzure(new());
        using var source = new KeyVaultSecretSource(azure.CreateManagedIdentity("bootstrap"), transport);
        var value = await source.GetSecretAsync(new("vault", "key", "version"), default);
        Assert.That(value, Is.EqualTo("from-vault"));
        Assert.That(transport.RequestUris.Single(), Does.StartWith("https://vault.vault.azure.net/secrets/key/version?api-version="));
        Assert.That(transport.Settings.All(h => !h.AllowAutoRedirect && !h.UseCookies), Is.True);
    }
}
