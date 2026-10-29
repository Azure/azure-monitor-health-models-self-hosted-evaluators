using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Logging.Abstractions;
using SelfHostedEvaluators.Core.Auth;
using SelfHostedEvaluators.Core.Configuration;
using SelfHostedEvaluators.Core.Packages;
using SelfHostedEvaluators.Plugins.Abstractions;

namespace SelfHostedEvaluators.Core.Tests;

public class AuthRegistryTests
{
    internal static AuthProfile Profile(string json) => new EnvironmentConfigSource().Load(
        EnvironmentConfigTests.Environment(EnvironmentConfigTests.Plugin.Replace("""{"type":"none"}""", json))).Plugins[0].Auth;

    [TestCase("""{"type":"none"}""", null, null)]
    [TestCase("""{"type":"bearer","token":"kv://vault/token"}""", "Authorization", "Bearer token-value")]
    [TestCase("""{"type":"basic","username":"kv://vault/user","password":"kv://vault/password"}""",
        "Authorization", "Basic dXNlcjpwYXNzd29yZA==")]
    [TestCase("""{"type":"apiKey","headers":{"DD-API-KEY":"kv://vault/key","DD-APPLICATION-KEY":"kv://vault/app"}}""",
        "DD-API-KEY", "key-value")]
    public async Task BuiltInHeadersAreAppliedToEachRequest(string json, string? header, string? value)
    {
        using var rig = new AuthRig();
        await using var context = await rig.Context(Profile(json));
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://sink.test/data");
        await context.AuthenticateAsync(request, default);
        if (header is null) Assert.That(request.Headers, Is.Empty);
        else Assert.That(request.Headers.GetValues(header), Is.EqualTo(new[] { value }));
        if (json.Contains("DD-APPLICATION", StringComparison.Ordinal))
            Assert.That(request.Headers.GetValues("DD-APPLICATION-KEY"), Is.EqualTo(new[] { "app-value" }));
    }

    [Test]
    public async Task QueryApiKeysRefreshWithoutAppendingDuplicates()
    {
        using var rig = new AuthRig();
        await using var context = await rig.Context(Profile(
            """{"type":"apiKey","queryParameter":"api_key","secret":"kv://vault/key"}"""));
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://sink.test/data?api_key=old&x=1");
        await context.AuthenticateAsync(request, default);
        Assert.That(request.RequestUri!.Query, Is.EqualTo("?x=1&api_key=key-value"));
        rig.Source.Values["key"] = "rotated & key";
        rig.Time.Advance(TimeSpan.FromMinutes(6));
        await context.AuthenticateAsync(request, default);
        Assert.That(request.RequestUri.Query, Is.EqualTo("?x=1&api_key=rotated%20%26%20key"));
        Assert.That(rig.Redactor.Redact("rotated & key"), Is.EqualTo("[REDACTED]"));
    }

    [Test]
    public async Task BearerSecretRefreshesOnExistingContext()
    {
        using var rig = new AuthRig();
        await using var context = await rig.Context(Profile("""{"type":"bearer","token":"kv://vault/token"}"""));
        using var first = new HttpRequestMessage(HttpMethod.Get, "https://sink.test");
        await context.AuthenticateAsync(first, default);
        rig.Source.Values["token"] = "rotated-token";
        rig.Time.Advance(TimeSpan.FromMinutes(6));
        using var next = new HttpRequestMessage(HttpMethod.Get, "https://sink.test");
        await context.AuthenticateAsync(next, default);
        Assert.That(next.Headers.Authorization!.Parameter, Is.EqualTo("rotated-token"));
    }

    [Test]
    public async Task OAuthCachesUntilExpiryAndRefreshesOnSecretRotation()
    {
        using var rig = new AuthRig();
        rig.Transport.Respond = _ => new(HttpStatusCode.OK)
        {
            Content = new StringContent($$"""{"access_token":"access-{{rig.Transport.Calls}}","token_type":"Bearer","expires_in":60}""")
        };
        await using var context = await rig.Context(Profile(
            """{"type":"oauth2ClientCredentials","tokenEndpoint":"https://login.test:8443/token","clientId":"client","clientSecret":"kv://vault/key","scopes":["scope.one","scope.two"]}"""));
        async Task<string?> Authenticate()
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://sink.test");
            await context.AuthenticateAsync(request, default);
            return request.Headers.Authorization!.Parameter;
        }
        Assert.That(await Authenticate(), Is.EqualTo("access-1"));
        Assert.That(await Authenticate(), Is.EqualTo("access-1"));
        Assert.That(rig.Transport.Bodies.Single(), Does.Contain("grant_type=client_credentials").And.Contain("scope=scope.one+scope.two"));
        rig.Time.Advance(TimeSpan.FromSeconds(61));
        Assert.That(await Authenticate(), Is.EqualTo("access-2"));
        rig.Source.Values["key"] = "rotated";
        rig.Time.Advance(TimeSpan.FromMinutes(6));
        Assert.That(await Authenticate(), Is.EqualTo("access-3"));
        Assert.That(rig.Redactor.Redact("access-1"), Is.EqualTo("[REDACTED]"));
    }

    [Test]
    public async Task AzureSchemesSelectExplicitIdentitiesAndCacheTokensPerModel()
    {
        using var rig = new AuthRig();
        var a = new ManagedIdentityAuthProfile("11111111-1111-1111-1111-111111111111");
        var b = new ManagedIdentityAuthProfile("22222222-2222-2222-2222-222222222222");
        var profiles = new AzureAuthProfile[]
        {
            a, b, new WorkloadIdentityAuthProfile("tenant", "workload", "external-token-file"),
            new ServicePrincipalSecretAuthProfile("tenant", "secret-client", new("kv://vault/key")),
            new ServicePrincipalCertificateAuthProfile("tenant", "cert-client", new("kv://vault/cert"))
        };
        foreach (var profile in profiles)
        {
            var credential = await rig.Credentials.CreateAsync(profile, default);
            var request = new TokenRequestContext(["scope/.default"]);
            var first = await credential.GetTokenAsync(request, default);
            Assert.That((await credential.GetTokenAsync(request, default)).Token, Is.EqualTo(first.Token));
            if (credential is IAsyncDisposable disposable) await disposable.DisposeAsync();
        }
        Assert.That(rig.Azure.Selections, Is.EqualTo(new[] { a.ClientId, b.ClientId, "workload", "secret-client", "cert-client" }));
        Assert.That(rig.Azure.TokenCalls, Is.EqualTo(5));
    }

    [Test]
    public async Task AzureSecretAndCertificateCredentialsRefreshOnRotation()
    {
        using var rig = new AuthRig();
        foreach (var profile in new AzureAuthProfile[]
        {
            new ServicePrincipalSecretAuthProfile("tenant", "client", new("kv://vault/key")),
            new ServicePrincipalCertificateAuthProfile("tenant", "client", new("kv://vault/cert"))
        })
        {
            var credential = rig.Credentials.Create(profile);
            var token = await credential.GetTokenAsync(new(["scope"]), default);
            rig.Source.Values["key"] = Guid.NewGuid().ToString();
            rig.Source.Values["cert"] = AuthRig.Certificate();
            rig.Time.Advance(TimeSpan.FromMinutes(6));
            Assert.That((await credential.GetTokenAsync(new(["scope"]), default)).Token, Is.Not.EqualTo(token.Token));
            await ((IAsyncDisposable)credential).DisposeAsync();
        }
    }

    [Test]
    public async Task EntraAndDirectAzureProfilesUseConfiguredScopesAndRenewExpiredTokens()
    {
        using var rig = new AuthRig();
        await using var entra = await rig.Context(new EntraIdAuthProfile(new ManagedIdentityAuthProfile("id"), "custom/.default"));
        await using var direct = await rig.Context(new ManagedIdentityAuthProfile("other"), "model/.default");
        using var first = new HttpRequestMessage(HttpMethod.Get, "https://sink.test");
        await entra.AuthenticateAsync(first, default);
        using var second = new HttpRequestMessage(HttpMethod.Get, "https://sink.test");
        await direct.AuthenticateAsync(second, default);
        Assert.That(rig.Azure.Scopes, Is.EqualTo(new[] { "custom/.default", "model/.default" }));
        rig.Time.Advance(TimeSpan.FromHours(2));
        using var third = new HttpRequestMessage(HttpMethod.Get, "https://sink.test");
        await entra.AuthenticateAsync(third, default);
        Assert.That(third.Headers.Authorization!.Parameter, Is.Not.EqualTo(first.Headers.Authorization!.Parameter));
    }

    [TestCase("http://sink.test")]
    [TestCase("https://evil.test")]
    [TestCase("https://sink.test:8443")]
    [TestCase("https://user:pass@sink.test")]
    public async Task OriginIsCheckedBeforeCredentialsAreApplied(string destination)
    {
        using var rig = new AuthRig();
        await using var context = await rig.Context(Profile("""{"type":"bearer","token":"kv://vault/token"}"""));
        using var request = new HttpRequestMessage(HttpMethod.Get, destination);
        Assert.ThrowsAsync<AuthenticationFailureException>(async () => await context.AuthenticateAsync(request, default));
        Assert.That(request.Headers.Authorization, Is.Null);
    }

    [Test]
    public async Task HostHeaderCannotOverrideApprovedOrigin()
    {
        using var rig = new AuthRig();
        using var client = await rig.Registry.CreateClientAsync(Profile("""{"type":"bearer","token":"kv://vault/token"}"""),
            new("https://sink.test"), rig.Limiter, TimeSpan.FromSeconds(1), default);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.Host = "evil.test";
        Assert.ThrowsAsync<AuthenticationFailureException>(async () => await client.SendAsync(request));
        Assert.That(rig.Transport.Calls, Is.Zero);
    }

    [Test]
    public async Task RedirectsAreNotFollowedAndTransportDisablesImplicitCredentials()
    {
        using var rig = new AuthRig();
        rig.Transport.Respond = _ => new(HttpStatusCode.TemporaryRedirect) { Headers = { Location = new("https://evil.test") } };
        using var client = await rig.Registry.CreateClientAsync(Profile("""{"type":"bearer","token":"kv://vault/token"}"""),
            new("https://sink.test"), rig.Limiter, TimeSpan.FromSeconds(1), default);
        var error = Assert.ThrowsAsync<AuthenticationFailureException>(async () => await client.GetAsync("/"));
        Assert.That(error!.Message, Does.Not.Contain("token-value"));
        Assert.That(rig.Transport.Calls, Is.EqualTo(1));
        Assert.That(rig.Transport.Settings.All(s => !s.AllowAutoRedirect && !s.UseDefaultCredentials && !s.UseCookies), Is.True);
    }

    [Test]
    public void PackageAllowListAndCustomSchemeCollisionsFailClosed()
    {
        using var rig = new AuthRig();
        Assert.ThrowsAsync<AuthenticationFailureException>(async () =>
            await rig.Registry.CreateContextAsync(new NoneAuthProfile(), [new("https://sink.test")],
                rig.Limiter, TimeSpan.FromSeconds(1), default, allowedHosts: ["other.test"]));
        Assert.Throws<ArgumentException>(() => rig.Registry.RegisterScheme("Bearer", () => new CustomScheme()));
        rig.Registry.RegisterScheme("custom", () => new CustomScheme());
        Assert.Throws<ArgumentException>(() => rig.Registry.RegisterScheme("CUSTOM", () => new CustomScheme()));
    }

    [TestCase("oauth2ClientCredentials")]
    [TestCase("exchange")]
    public void PackageAllowListRejectsExplicitTokenEndpointsBeforeAnyExchange(string scheme)
    {
        using var rig = new AuthRig();
        rig.Registry.RegisterScheme("exchange", () => new TokenExchangeScheme());
        rig.Transport.Respond = _ => new(HttpStatusCode.OK)
            { Content = new StringContent("""{"access_token":"access","expires_in":60,"token_type":"Bearer"}""") };
        var profile = Profile($$"""{"type":"{{scheme}}","tokenEndpoint":"https://unapproved.test/private-token-path","clientId":"client","clientSecret":"kv://vault/key","scopes":["scope"]}""");
        var error = Assert.ThrowsAsync<AuthenticationFailureException>(async () =>
        {
            await using var context = await rig.Registry.CreateContextAsync(profile, [new("https://sink.test")],
                rig.Limiter, TimeSpan.FromSeconds(2), default, allowedHosts: ["sink.test"]);
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://sink.test");
            await context.AuthenticateAsync(request, default);
        });
        Assert.That(error!.ToString(), Does.Not.Contain("unapproved.test").And.Not.Contain("private-token-path").And.Not.Contain("key-value"));
        Assert.That(rig.Transport.Calls, Is.Zero);
        Assert.That(rig.Limiter.GetStatistics()!.TotalSuccessfulLeases, Is.Zero);
    }

    [TestCase("oauth2ClientCredentials")]
    [TestCase("exchange")]
    public async Task PackageAllowListPermitsExplicitCrossHostTokenEndpoints(string scheme)
    {
        using var rig = new AuthRig();
        rig.Registry.RegisterScheme("exchange", () => new TokenExchangeScheme());
        rig.Transport.Respond = _ => new(HttpStatusCode.OK)
            { Content = new StringContent("""{"access_token":"cross-host-access","expires_in":60,"token_type":"Bearer"}""") };
        var profile = Profile($$"""{"type":"{{scheme}}","tokenEndpoint":"https://login.test:8443/token","clientId":"client","clientSecret":"kv://vault/key","scopes":["scope"]}""");
        await using var context = await rig.Registry.CreateContextAsync(profile, [new("https://sink.test")],
            rig.Limiter, TimeSpan.FromSeconds(2), default, allowedHosts: ["SINK.TEST", "LOGIN.TEST"]);
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://sink.test");
        await context.AuthenticateAsync(request, default);
        Assert.That(rig.Transport.RequestUris, Is.EqualTo(new[] { "https://login.test:8443/token" }));
        Assert.That(rig.Transport.Bodies.Single(), Does.Contain("key-value"));
        Assert.That(rig.Limiter.GetStatistics()!.TotalSuccessfulLeases, Is.EqualTo(1));
        Assert.That(request.Headers.Any(), Is.True);
    }

    [Test]
    public async Task PackageSinkAllowListDoesNotRestrictTrustedAzureCredentialExchanges()
    {
        using var rig = new AuthRig();
        await using var context = await rig.Registry.CreateContextAsync(new ManagedIdentityAuthProfile("model"),
            [new("https://sink.test")], rig.Limiter, TimeSpan.FromSeconds(2), default, allowedHosts: ["sink.test"]);
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://sink.test");
        await context.AuthenticateAsync(request, default);
        Assert.That(request.Headers.Authorization!.Parameter, Is.EqualTo("azure-token-1"));
        Assert.That(rig.Limiter.GetStatistics()!.TotalSuccessfulLeases, Is.EqualTo(1));
    }

    [Test]
    public async Task PackageSinkAllowListDoesNotRestrictKeyVaultSecretResolution()
    {
        using var rig = new AuthRig();
        var transport = new FakeTransport
        {
            Respond = _ => new(HttpStatusCode.OK)
            { Content = new StringContent("""{"value":"from-vault","id":"https://vault.vault.azure.net/secrets/key/version"}""") }
        };
        using var source = new KeyVaultSecretSource(rig.Azure.CreateManagedIdentity("bootstrap"), transport);
        var secrets = new SecretResolver(source, TimeSpan.FromMinutes(5), rig.Time, rig.Redactor);
        var registry = new AuthRegistry(secrets, rig.Credentials, rig.Redactor, rig.Time, rig.Transport);
        await using var context = await registry.CreateContextAsync(new BearerAuthProfile(new("kv://vault/key/version")),
            [new("https://sink.test")], rig.Limiter, TimeSpan.FromSeconds(2), default, allowedHosts: ["sink.test"]);
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://sink.test");
        await context.AuthenticateAsync(request, default);
        Assert.That(request.Headers.Authorization!.Parameter, Is.EqualTo("from-vault"));
        Assert.That(transport.RequestUris.Single(), Does.StartWith("https://vault.vault.azure.net/secrets/key/version?"));
        Assert.That(rig.Limiter.GetStatistics()!.TotalSuccessfulLeases, Is.Zero);
    }

    [Test]
    public async Task CustomSchemeUsesGuardedExchangeClientAndGuardedDestination()
    {
        using var rig = new AuthRig();
        var scheme = new CustomScheme();
        rig.Registry.RegisterScheme("custom", () => scheme);
        await using var context = await rig.Context(Profile("""{"type":"custom"}"""));
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://sink.test");
        await context.AuthenticateAsync(request, default);
        Assert.That(request.Headers.GetValues("X-Custom"), Is.EqualTo(new[] { "key-value" }));
        using var wrong = new HttpRequestMessage(HttpMethod.Get, "https://other.test");
        Assert.ThrowsAsync<AuthenticationFailureException>(async () => await context.AuthenticateAsync(wrong, default));
        Assert.ThrowsAsync<AuthenticationFailureException>(async () => await scheme.Exchange!.GetAsync("https://other.test"));
    }

    [Test]
    public async Task WebSocketsUseApprovedWssOriginAndRefreshedAuth()
    {
        using var rig = new AuthRig();
        await using var context = await rig.Context(Profile("""{"type":"bearer","token":"kv://vault/token"}"""));
        using var socket = new ClientWebSocket();
        await context.AuthenticateWebSocketAsync(new("wss://sink.test/stream"), socket.Options, default);
        Assert.ThrowsAsync<AuthenticationFailureException>(async () =>
            await context.AuthenticateWebSocketAsync(new("ws://sink.test"), socket.Options, default));
        Assert.ThrowsAsync<AuthenticationFailureException>(async () =>
            await context.AuthenticateWebSocketAsync(new("wss://sink.test:8443"), socket.Options, default));
    }

    [Test]
    public async Task MtlsRotatesCertificateOnExistingHttpClientAndKeepsInflightGenerationAlive()
    {
        using var rig = new AuthRig();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Transport.Send = async (_, ct) =>
        {
            if (rig.Transport.Calls == 1) { entered.SetResult(); await finish.Task.WaitAsync(ct); }
            return new(HttpStatusCode.OK);
        };
        using var client = await rig.Registry.CreateClientAsync(new MtlsAuthProfile(new("kv://vault/cert")),
            new("https://sink.test"), rig.Limiter, TimeSpan.FromMinutes(1), default);
        var pending = client.GetAsync("/");
        await entered.Task;
        var previous = rig.Transport.Settings.Single().ClientCertificates[0].GetCertHashString();
        rig.Source.Values["cert"] = AuthRig.Certificate();
        rig.Time.Advance(TimeSpan.FromMinutes(6));
        using var response = await client.GetAsync("/");
        Assert.That(rig.Transport.Settings.Last().ClientCertificates[0].GetCertHashString(), Is.Not.EqualTo(previous));
        Assert.That(rig.Transport.Disposals, Is.Zero);
        finish.SetResult();
        using var completed = await pending;
    }

    [Test]
    public async Task RetryCountsEveryRequestAndHonorsRetryAfter()
    {
        using var rig = new AuthRig();
        rig.Transport.Respond = _ => rig.Transport.Calls == 1
            ? new(HttpStatusCode.TooManyRequests) { Headers = { RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMilliseconds(60)) } }
            : new(HttpStatusCode.OK);
        using var client = await rig.Registry.CreateClientAsync(new NoneAuthProfile(), new("https://sink.test"),
            rig.Limiter, TimeSpan.FromSeconds(2), default);
        using var response = await client.GetAsync("/");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(rig.Transport.Calls, Is.EqualTo(2));
        Assert.That(rig.Limiter.GetStatistics()!.TotalSuccessfulLeases, Is.EqualTo(2));
    }

    [Test]
    public async Task RetryAfterBeyondBudgetReturnsTypedStatusWithoutSendingAgain()
    {
        using var rig = new AuthRig();
        rig.Transport.Respond = _ => new(HttpStatusCode.ServiceUnavailable)
            { Headers = { RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromHours(1)) } };
        using var client = await rig.Registry.CreateClientAsync(new NoneAuthProfile(), new("https://sink.test"),
            rig.Limiter, TimeSpan.FromMilliseconds(80), default);
        using var response = await client.GetAsync("/").WaitAsync(TimeSpan.FromSeconds(2));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
        Assert.That(rig.Transport.Calls, Is.EqualTo(1));
    }

    [Test]
    public async Task PluginManagerOwnsContextsAndWaitsForUnderlyingBatchesBeforeDisposal()
    {
        using var fixture = new PackageFixture();
        fixture.Add();
        using var catalog = PackageCatalog.Load(fixture.Root);
        using var rig = new AuthRig();
        var factory = new PluginContextFactory(catalog, rig.Registry, rig.Redactor, NullLogger.Instance, rig.Time);
        var configuration = new EnvironmentConfigSource().Load(EnvironmentConfigTests.Environment()).Plugins[0];
        var client = await new PluginManager(catalog, factory).CreateClientAsync(configuration, default);
        await client.DisposeAsync();
        Assert.That(factory.ActiveContexts, Is.Zero);
        var failing = new EnvironmentConfigSource().Load(EnvironmentConfigTests.Environment(
            EnvironmentConfigTests.Plugin.Replace("\"package\":", "\"settings\":{\"createThrows\":true},\"package\":"))).Plugins[0];
        Assert.ThrowsAsync<ConfigurationException>(async () =>
            await new PluginManager(catalog, factory).CreateClientAsync(failing, default));
        Assert.That(factory.ActiveContexts, Is.Zero);
    }

    [Test]
    public async Task DisposalRetainsAuthAndLimiterUntilIgnoringCancellationBatchActuallyCompletes()
    {
        using var fixture = new PackageFixture();
        fixture.Add();
        using var catalog = PackageCatalog.Load(fixture.Root);
        using var rig = new AuthRig();
        var factory = new PluginContextFactory(catalog, rig.Registry, rig.Redactor, NullLogger.Instance, rig.Time);
        var configuration = new EnvironmentConfigSource().Load(EnvironmentConfigTests.Environment()).Plugins[0];
        var resources = await factory.CreateAsync(configuration, default);
        var sink = new BlockingSink();
        var client = resources.Own(sink);
        using var cts = new CancellationTokenSource();
        var batch = client.QueryBatchAsync([], new(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1)), cts.Token);
        cts.Cancel();
        var disposing = client.DisposeAsync().AsTask();
        Assert.That(disposing.IsCompleted, Is.False);
        Assert.That(factory.ActiveContexts, Is.EqualTo(1));
        using (var lease = await resources.Context.RateLimiter.AcquireAsync()) Assert.That(lease.IsAcquired, Is.True);
        sink.Finish.SetResult([]);
        await batch;
        await disposing;
        Assert.That(factory.ActiveContexts, Is.Zero);
        Assert.That(sink.Disposed, Is.True);
    }

    [Test]
    public async Task SingleAttemptHeadersArriveBeforeBodyAndResponseDisposalReleasesRetiredMtlsGeneration()
    {
        using var rig = new AuthRig();
        var content = new ProbeResponseBody(throws: true);
        rig.Transport.Respond = _ => new(HttpStatusCode.TooManyRequests)
            { Content = rig.Transport.Calls == 1 ? content : new StringContent("next") };
        using var client = await rig.Registry.CreateSingleAttemptClientAsync(new MtlsAuthProfile(new("kv://vault/cert")),
            new("https://sink.test"), TimeSpan.FromSeconds(10), default);
        using var first = await client.GetAsync("/", HttpCompletionOption.ResponseHeadersRead);
        Assert.That(first.StatusCode, Is.EqualTo(HttpStatusCode.TooManyRequests));
        Assert.That(content.Reads, Is.Zero);
        var certificate = (X509Certificate2)rig.Transport.Settings.Single().ClientCertificates[0];
        rig.Source.Values["cert"] = AuthRig.Certificate();
        rig.Time.Advance(TimeSpan.FromMinutes(6));
        using (await client.GetAsync("/", HttpCompletionOption.ResponseHeadersRead)) { }
        Assert.That(rig.Transport.Calls, Is.EqualTo(2), "single-attempt clients never retry 429");
        Assert.That(rig.Transport.Disposals, Is.Zero);
        client.Dispose();
        Assert.That(rig.Transport.Disposals, Is.EqualTo(1), "only the response-free generation can close");
        Assert.That(certificate.HasPrivateKey, Is.True, "the original response still owns its certificate context");
        first.Dispose();
        Assert.That(content.Disposed, Is.True);
        Assert.That(rig.Transport.Disposals, Is.EqualTo(2));
    }

    [Test]
    public async Task SingleAttemptTransportDisposalFailureStillReleasesItsOwnedAuthContext()
    {
        using var rig = new AuthRig();
        var auth = new DisposalContext();
        rig.Registry.RegisterScheme("disposal", () => new FixedScheme(auth));
        var client = await rig.Registry.CreateSingleAttemptClientAsync(Profile("""{"type":"disposal"}"""),
            new("https://sink.test"), TimeSpan.FromSeconds(10), default);
        using (await client.GetAsync("/", HttpCompletionOption.ResponseHeadersRead)) { }
        rig.Transport.ThrowOnDispose = true;
        Assert.Throws<InvalidOperationException>(() => client.Dispose());
        Assert.That(auth.Disposals, Is.EqualTo(1));
    }

    [Test]
    public async Task StreamingResponseKeepsRetiredMtlsTransportAliveUntilBuffered()
    {
        using var rig = new AuthRig();
        var content = new BlockingContent();
        rig.Transport.Respond = _ => new(HttpStatusCode.OK)
            { Content = rig.Transport.Calls == 1 ? content : new StringContent("next") };
        using var client = await rig.Registry.CreateClientAsync(new MtlsAuthProfile(new("kv://vault/cert")),
            new("https://sink.test"), rig.Limiter, TimeSpan.FromSeconds(10), default);
        var first = client.GetAsync("/");
        await content.Entered.Task;
        rig.Source.Values["cert"] = AuthRig.Certificate();
        rig.Time.Advance(TimeSpan.FromMinutes(6));
        using var second = await client.GetAsync("/");
        Assert.That(rig.Transport.Disposals, Is.Zero);
        content.Finish.SetResult();
        using var completed = await first;
        Assert.That(rig.Transport.Disposals, Is.EqualTo(1));
    }

    [TestCase(0)]
    [TestCase(1)]
    public async Task MultiAttemptCallerCancellationDisposesPartialBodyAndResponseBeforeClientExit(int retries)
    {
        using var rig = new AuthRig();
        var content = new CancellablePartialContent();
        var responses = new List<DisposalResponse>();
        rig.Transport.Respond = request =>
        {
            Assert.That(request.Headers.Authorization?.Parameter, Is.EqualTo("token-value"));
            var response = new DisposalResponse(rig.Transport.Calls <= retries ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK)
            {
                Content = rig.Transport.Calls <= retries ? new StringContent("retry") : content
            };
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero);
            responses.Add(response);
            return response;
        };
        using var client = await rig.Registry.CreateClientAsync(Profile("""{"type":"bearer","token":"kv://vault/token"}"""),
            new("https://sink.test"), rig.Limiter, TimeSpan.FromSeconds(30), default);
        using var stop = new CancellationTokenSource();
        var pending = client.GetAsync("/", HttpCompletionOption.ResponseHeadersRead, stop.Token);
        try
        {
            await content.PartialBodyFlushed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(pending.IsCompleted, Is.False, "The multi-attempt pipeline buffers the incomplete body.");
            stop.Cancel();
            Assert.CatchAsync<OperationCanceledException>(async () => await pending.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.That(responses.Count, Is.EqualTo(retries + 1));
            Assert.That(responses.Select(r => r.Disposed), Is.All.True);
            Assert.That(content.Disposed, Is.True, "Cancellation must dispose the active body, not wait for client/process exit.");
            Assert.That(rig.Transport.Disposals, Is.Zero, "The client and process are still alive at the disposal assertions.");
            Assert.That(rig.Limiter.GetStatistics()!.TotalSuccessfulLeases, Is.EqualTo(retries + 1));
        }
        finally
        {
            stop.Cancel();
            foreach (var response in responses) response.Dispose();
        }
    }

    [Test]
    public async Task AzureAuthExchangesAreBoundedAndShareRequestBudget()
    {
        using var rig = new AuthRig();
        await using var context = await rig.Context(new ManagedIdentityAuthProfile("model"));
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://sink.test");
        await context.AuthenticateAsync(request, default);
        Assert.That(rig.Limiter.GetStatistics()!.TotalSuccessfulLeases, Is.EqualTo(1));
        using var repeat = new HttpRequestMessage(HttpMethod.Get, "https://sink.test");
        await context.AuthenticateAsync(repeat, default);
        Assert.That(rig.Limiter.GetStatistics()!.TotalSuccessfulLeases, Is.EqualTo(1));
    }

    [Test]
    public void AzureCredentialInitializationErrorsAreRedacted()
    {
        using var rig = new AuthRig();
        rig.Azure.InitializationError = new InvalidOperationException("unregistered credential value");
        var error = Assert.ThrowsAsync<AuthenticationFailureException>(async () =>
            await rig.Credentials.CreateAsync(new ManagedIdentityAuthProfile("identity"), default));
        Assert.That(error!.ToString(), Does.Not.Contain("unregistered credential value"));
    }

    [Test]
    public async Task TypedPackageSchemeRegistrationsCanUseHostActivation()
    {
        using var rig = new AuthRig();
        rig.Registry.RegisterPackageSchemes(new Dictionary<string, Type> { ["custom"] = typeof(CustomScheme) }, _ => new CustomScheme());
        await using var context = await rig.Context(Profile("""{"type":"custom"}"""));
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://sink.test");
        await context.AuthenticateAsync(request, default);
        Assert.That(request.Headers.GetValues("X-Custom"), Is.EqualTo(new[] { "key-value" }));
    }

    [Test]
    public async Task WebsocketMtlsUsesPrivateCertificateAndUnsupportedQueryAuthFailsExplicitly()
    {
        using var rig = new AuthRig();
        await using var mtls = await rig.Context(new MtlsAuthProfile(new("kv://vault/cert")));
        using var socket = new ClientWebSocket();
        using var lease = await rig.Limiter.AcquireAsync();
        await mtls.AuthenticateWebSocketAsync(new("wss://sink.test"), socket.Options, default);
        Assert.That(socket.Options.ClientCertificates!.OfType<X509Certificate2>().Single().HasPrivateKey, Is.True);
        await using var query = await rig.Context(Profile(
            """{"type":"apiKey","queryParameter":"api_key","secret":"kv://vault/key"}"""));
        Assert.ThrowsAsync<AuthenticationFailureException>(async () =>
            await query.AuthenticateWebSocketAsync(new("wss://sink.test"), socket.Options, default));
        Assert.That(rig.Limiter.GetStatistics()!.TotalSuccessfulLeases, Is.EqualTo(1));
    }

    [Test]
    public async Task LimiterIsSharedAcrossEndpointsAndNonHttpRequestsAndCancellationStopsWaiters()
    {
        using var limiter = new PluginRequestLimiter(new(1, TimeSpan.FromMinutes(1)));
        using var first = await limiter.AcquireAsync();
        using var cts = new CancellationTokenSource();
        var queued = limiter.AcquireAsync(1, cts.Token).AsTask();
        Assert.That(queued.IsCompleted, Is.False);
        cts.Cancel();
        Assert.CatchAsync<OperationCanceledException>(async () => await queued);
        Assert.That(limiter.GetStatistics()!.TotalSuccessfulLeases, Is.EqualTo(1));
    }

    [Test]
    public async Task RetryAfterHttpDateAndOauthRetryUseTheSharedLimiter()
    {
        using var rig = new AuthRig();
        rig.Transport.Respond = _ => rig.Transport.Calls == 1
            ? new(HttpStatusCode.TooManyRequests) { Headers = { RetryAfter = new RetryConditionHeaderValue(rig.Time.GetUtcNow().AddMilliseconds(30)) } }
            : new(HttpStatusCode.OK) { Content = new StringContent("""{"access_token":"retry-token","expires_in":60,"token_type":"Bearer"}""") };
        await using var context = await rig.Context(Profile(
            """{"type":"oauth2ClientCredentials","tokenEndpoint":"https://login.test/token","clientId":"client","clientSecret":"kv://vault/key","scopes":["scope"]}"""));
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://sink.test");
        await context.AuthenticateAsync(request, default);
        Assert.That(rig.Limiter.GetStatistics()!.TotalSuccessfulLeases, Is.EqualTo(2));
    }

    [Test]
    public async Task Final429StillPausesOtherEndpointsOfThePlugin()
    {
        using var rig = new AuthRig();
        rig.Transport.Respond = _ => new(HttpStatusCode.TooManyRequests)
            { Headers = { RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero) } };
        using var client = await rig.Registry.CreateClientAsync(new NoneAuthProfile(), new("https://sink.test"),
            rig.Limiter, TimeSpan.FromSeconds(1), default);
        rig.Transport.Send = (_, _) =>
        {
            var response = rig.Transport.Respond(new());
            if (rig.Transport.Calls == 3) response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(1));
            return Task.FromResult(response);
        };
        using var response = await client.GetAsync("/");
        using var next = rig.Limiter.AttemptAcquire();
        Assert.That(next.IsAcquired, Is.False);
    }

    [Test]
    public async Task SinkAndAuthExchangeEachConsumeExactlyOneRequestPermit()
    {
        using var rig = new AuthRig();
        rig.Transport.Respond = request => request.RequestUri!.Host == "login.test"
            ? new(HttpStatusCode.OK) { Content = new StringContent("""{"access_token":"token","expires_in":60,"token_type":"Bearer"}""") }
            : new(HttpStatusCode.OK);
        using var limiter = new PluginRequestLimiter(new(1, TimeSpan.FromMilliseconds(30)));
        using var client = await rig.Registry.CreateClientAsync(Profile(
            """{"type":"oauth2ClientCredentials","tokenEndpoint":"https://login.test/token","clientId":"client","clientSecret":"kv://vault/key","scopes":["scope"]}"""),
            new("https://sink.test"), limiter, TimeSpan.FromSeconds(1), default);
        using var response = await client.GetAsync("/");
        Assert.That(limiter.GetStatistics()!.TotalSuccessfulLeases, Is.EqualTo(2));
    }

    [Test]
    public async Task OAuthRefreshesAfterWaitingForTheSinkRequestPermit()
    {
        using var rig = new AuthRig();
        var limiterTime = new LimiterTime();
        using var limiter = new PluginRequestLimiter(new(2, TimeSpan.FromMinutes(1)), limiterTime);
        var tokens = new List<string?>();
        var exchanges = 0;
        rig.Transport.Respond = request =>
        {
            if (request.RequestUri!.Host == "login.test")
            {
                exchanges++;
                return new(HttpStatusCode.OK)
                { Content = new StringContent($$"""{"access_token":"access-{{exchanges}}","expires_in":60,"token_type":"Bearer"}""") };
            }
            tokens.Add(request.Headers.Authorization?.Parameter);
            return new(HttpStatusCode.OK);
        };
        using var client = await rig.Registry.CreateClientAsync(Profile(
            """{"type":"oauth2ClientCredentials","tokenEndpoint":"https://login.test/token","clientId":"client","clientSecret":"kv://vault/key","scopes":["scope"]}"""),
            new("https://sink.test"), limiter, TimeSpan.FromMinutes(2), default);
        using var first = await client.GetAsync("/");
        var pending = client.GetAsync("/");
        await limiterTime.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.That(tokens, Is.EqualTo(new[] { "access-1" }));
        rig.Time.Advance(TimeSpan.FromSeconds(61));
        limiterTime.Advance(TimeSpan.FromMinutes(1));
        using var second = await pending;
        Assert.That(tokens, Is.EqualTo(new[] { "access-1", "access-2" }));
        Assert.That(exchanges, Is.EqualTo(2));
        Assert.That(limiter.GetStatistics()!.TotalSuccessfulLeases, Is.EqualTo(4));
    }

    [Test]
    public async Task SecretInitializationFailureReleasesContextAndReportsRedactedError()
    {
        using var fixture = new PackageFixture();
        fixture.Add();
        using var catalog = PackageCatalog.Load(fixture.Root);
        using var rig = new AuthRig();
        rig.Source.Error = new InvalidOperationException("unregistered secret");
        var factory = new PluginContextFactory(catalog, rig.Registry, rig.Redactor, NullLogger.Instance, rig.Time);
        var config = new EnvironmentConfigSource().Load(EnvironmentConfigTests.Environment(
            EnvironmentConfigTests.Plugin.Replace("""{"type":"none"}""", """{"type":"bearer","token":"kv://vault/key"}"""))).Plugins[0];
        var error = Assert.ThrowsAsync<ConfigurationException>(async () => await new PluginManager(catalog, factory).CreateClientAsync(config, default));
        Assert.That(error!.ToString(), Does.Not.Contain("unregistered secret"));
        Assert.That(factory.ActiveContexts, Is.Zero);
    }

    [Test]
    public async Task ContextDisposalIsIdempotentEvenForPackageSchemes()
    {
        using var rig = new AuthRig();
        var context = new DisposalContext();
        rig.Registry.RegisterScheme("disposal", () => new FixedScheme(context));
        var created = await rig.Context(Profile("""{"type":"disposal"}"""));
        await created.DisposeAsync();
        await created.DisposeAsync();
        Assert.That(context.Disposals, Is.EqualTo(1));
    }

    [Test]
    public async Task FailingTransportDisposalDoesNotLeakAuthResourcesOrRawExceptions()
    {
        using var fixture = new PackageFixture();
        fixture.Add();
        using var catalog = PackageCatalog.Load(fixture.Root);
        using var rig = new AuthRig();
        var auth = new DisposalContext();
        rig.Registry.RegisterScheme("disposal", () => new FixedScheme(auth));
        var config = new EnvironmentConfigSource().Load(EnvironmentConfigTests.Environment(
            EnvironmentConfigTests.Plugin.Replace("""{"type":"none"}""", """{"type":"disposal"}"""))).Plugins[0];
        var factory = new PluginContextFactory(catalog, rig.Registry, rig.Redactor, NullLogger.Instance, rig.Time);
        var resources = await factory.CreateAsync(config, default);
        using var response = await resources.Context.HttpClients["default"].GetAsync("/");
        rig.Transport.ThrowOnDispose = true;
        var error = Assert.ThrowsAsync<AuthenticationFailureException>(async () => await resources.DisposeAsync());
        Assert.That(error!.ToString(), Does.Not.Contain("sensitive-disposal"));
        Assert.That(auth.Disposals, Is.EqualTo(1));
        Assert.That(factory.ActiveContexts, Is.Zero);
    }

    private sealed class FixedScheme(IAuthContext context) : IAuthScheme
    {
        public ValueTask<IAuthContext> CreateContextAsync(AuthSchemeContext input, CancellationToken ct) => ValueTask.FromResult(context);
    }

    private sealed class DisposalContext : IAuthContext
    {
        public int Disposals { get; private set; }
        public string Scheme => "disposal";
        public ValueTask AuthenticateAsync(HttpRequestMessage request, CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask ConfigureHttpHandlerAsync(Uri destination, HttpClientHandler handler, CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask AuthenticateWebSocketAsync(Uri destination, ClientWebSocketOptions options, CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() { Disposals++; return ValueTask.CompletedTask; }
    }

    [TestCase("""{"access_token":"leaked","token_type":"Bearer"}""")]
    [TestCase("""{"access_token":"leaked","expires_in":0,"token_type":"Bearer"}""")]
    [TestCase("""{"access_token":"leaked","expires_in":60,"token_type":"Basic"}""")]
    [TestCase("invalid secret-content")]
    public async Task InvalidOauthNeverBecomesAnonymousOrExposesResponseBody(string body)
    {
        using var rig = new AuthRig();
        rig.Transport.Respond = _ => new(HttpStatusCode.OK) { Content = new StringContent(body) };
        await using var context = await rig.Context(Profile(
            """{"type":"oauth2ClientCredentials","tokenEndpoint":"https://login.test/token","clientId":"client","clientSecret":"kv://vault/key","scopes":["scope"]}"""));
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://sink.test");
        var error = Assert.ThrowsAsync<AuthenticationFailureException>(async () => await context.AuthenticateAsync(request, default));
        Assert.That(error!.ToString(), Does.Not.Contain("leaked").And.Not.Contain("secret-content"));
        Assert.That(request.Headers.Authorization, Is.Null);
    }

    [Test]
    public async Task RealAzureFactoriesConstructOnlyExplicitCredentialsWithoutRequestingTokens()
    {
        var source = new AzureSdkCredentialSource();
        Assert.That(source.CreateManagedIdentity(null), Is.TypeOf<ManagedIdentityCredential>());
        Assert.That(source.CreateManagedIdentity("11111111-1111-1111-1111-111111111111"), Is.TypeOf<ManagedIdentityCredential>());
        Assert.That(source.CreateWorkloadIdentity("tenant", "client", "external-file"), Is.TypeOf<WorkloadIdentityCredential>());
        Assert.That(source.CreateClientSecret("tenant", "client", "secret"), Is.TypeOf<ClientSecretCredential>());
        using var rig = new AuthRig();
        using var certificate = await rig.Secrets.ResolveCertificateAsync(new("kv://vault/cert"), default);
        Assert.That(source.CreateClientCertificate("tenant", "client", certificate), Is.TypeOf<ClientCertificateCredential>());
        Assert.Throws<ArgumentException>(() => new KeyVaultSecretSource(""));
    }

    private sealed class BlockingSink : ISinkClient
    {
        public TaskCompletionSource<IReadOnlyList<SignalResult>> Finish { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Disposed { get; private set; }
        public Task<IReadOnlyList<SignalResult>> QueryBatchAsync(IReadOnlyList<SignalQuery> batch, QueryWindow window, CancellationToken ct) => Finish.Task;
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }

    private sealed class BlockingContent : HttpContent
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Finish { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            Entered.SetResult();
            await Finish.Task;
            await stream.WriteAsync(new byte[] { 1 });
        }
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }

    private sealed class DisposalResponse(HttpStatusCode status) : HttpResponseMessage(status)
    {
        public bool Disposed { get; private set; }
        protected override void Dispose(bool disposing)
        {
            if (disposing) Disposed = true;
            base.Dispose(disposing);
        }
    }

    private sealed class CancellablePartialContent : HttpContent
    {
        public TaskCompletionSource PartialBodyFlushed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Disposed { get; private set; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            throw new InvalidOperationException("The request cancellation token must reach the body.");
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken ct)
        {
            await stream.WriteAsync("event: data\ndata: {\"logicalTimestampMs\":"u8.ToArray(), ct);
            await stream.FlushAsync(ct);
            PartialBodyFlushed.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        }
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override void Dispose(bool disposing)
        {
            if (disposing) Disposed = true;
            base.Dispose(disposing);
        }
    }

    private sealed class CustomScheme : IAuthScheme
    {
        public HttpClient? Exchange { get; private set; }
        public async ValueTask<IAuthContext> CreateContextAsync(AuthSchemeContext context, CancellationToken ct)
        {
            Exchange = context.HttpClient;
            return new CustomContext(await context.SecretResolver.ResolveSecretAsync(new("kv://vault/key"), ct));
        }
    }
    private sealed class TokenExchangeScheme : IAuthScheme
    {
        public async ValueTask<IAuthContext> CreateContextAsync(AuthSchemeContext context, CancellationToken ct)
        {
            var secret = await context.SecretResolver.ResolveSecretAsync(new("kv://vault/key"), ct);
            using var response = await context.HttpClient.PostAsync(context.Profile.GetProperty("tokenEndpoint").GetString(),
                new StringContent(secret), ct);
            response.EnsureSuccessStatusCode();
            return new CustomContext(secret);
        }
    }
    private sealed class CustomContext(string secret) : IAuthContext
    {
        public string Scheme => "custom";
        public ValueTask AuthenticateAsync(HttpRequestMessage request, CancellationToken ct)
        { request.Headers.Add("X-Custom", secret); return ValueTask.CompletedTask; }
        public ValueTask ConfigureHttpHandlerAsync(Uri destination, HttpClientHandler handler, CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask AuthenticateWebSocketAsync(Uri destination, ClientWebSocketOptions options, CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

internal sealed class AuthRig : IDisposable
{
    public ManualTime Time { get; } = new();
    public FakeVault Source { get; } = new();
    public Redactor Redactor { get; } = new();
    public FakeTransport Transport { get; } = new();
    public FakeAzure Azure { get; }
    public SecretResolver Secrets { get; }
    public AzureCredentialFactory Credentials { get; }
    public AuthRegistry Registry { get; }
    public PluginRequestLimiter Limiter { get; } = new(new(1000, TimeSpan.FromSeconds(1)));
    public AuthRig()
    {
        Source.Values["cert"] = Certificate();
        Secrets = new(Source, TimeSpan.FromMinutes(5), Time, Redactor);
        Azure = new(Time);
        Credentials = new(Secrets, Azure, Time, Redactor);
        Registry = new(Secrets, Credentials, Redactor, Time, Transport);
    }
    public ValueTask<IAuthContext> Context(AuthProfile profile, string? scope = null) =>
        Registry.CreateContextAsync(profile, [new("https://sink.test")], Limiter, TimeSpan.FromSeconds(2), default, scope);
    public void Dispose() => Limiter.Dispose();
    public static string Certificate()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        return Convert.ToBase64String(cert.Export(X509ContentType.Pfx));
    }
}

internal sealed class ManualTime : TimeProvider
{
    private DateTimeOffset now = DateTimeOffset.UtcNow;
    public override DateTimeOffset GetUtcNow() => now;
    public void Advance(TimeSpan duration) => now += duration;
}

internal sealed class LimiterTime : TimeProvider
{
    private DateTimeOffset now = DateTimeOffset.UtcNow;
    private readonly List<DelayTimer> timers = [];
    public TaskCompletionSource Waiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public override DateTimeOffset GetUtcNow() => now;
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new DelayTimer(callback, state, now + dueTime);
        timers.Add(timer);
        Waiting.TrySetResult();
        return timer;
    }
    public void Advance(TimeSpan duration)
    {
        now += duration;
        foreach (var timer in timers.ToArray()) timer.Fire(now);
    }
    private sealed class DelayTimer(TimerCallback callback, object? state, DateTimeOffset due) : ITimer
    {
        private bool disposed;
        public void Fire(DateTimeOffset now) { if (!disposed && now >= due) { disposed = true; callback(state); } }
        public bool Change(TimeSpan dueTime, TimeSpan period) => throw new NotSupportedException();
        public void Dispose() => disposed = true;
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}

internal sealed class FakeVault : IKeyVaultSource
{
    public Dictionary<string, string> Values { get; } = new()
    { ["token"] = "token-value", ["user"] = "user", ["password"] = "password", ["key"] = "key-value", ["app"] = "app-value" };
    public int Calls { get; private set; }
    public Exception? Error { get; set; }
    public ValueTask<string> GetSecretAsync(KeyVaultReference reference, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Calls++;
        if (Error is not null) throw Error;
        return ValueTask.FromResult(Values[reference.Name]);
    }
}

internal sealed class FakeTransport : IHttpTransportFactory
{
    public int Calls { get; private set; }
    public int Disposals { get; private set; }
    public bool ThrowOnDispose { get; set; }
    public List<HttpClientHandler> Settings { get; } = [];
    public List<string> Bodies { get; } = [];
    public List<string> RequestUris { get; } = [];
    public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } = _ => new(HttpStatusCode.OK);
    public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? Send { get; set; }
    public HttpMessageHandler Create(HttpClientHandler settings)
    { Settings.Add(settings); return new Handler(this); }
    private sealed class Handler(FakeTransport owner) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            owner.Calls++;
            owner.RequestUris.Add(request.RequestUri!.AbsoluteUri);
            if (request.Content is not null) owner.Bodies.Add(await request.Content.ReadAsStringAsync(ct));
            return owner.Send is null ? owner.Respond(request) : await owner.Send(request, ct);
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                owner.Disposals++;
                if (owner.ThrowOnDispose) throw new InvalidOperationException("sensitive-disposal");
            }
            base.Dispose(disposing);
        }
    }
}

internal sealed class FakeAzure(ManualTime time) : IAzureCredentialSource
{
    public List<string?> Selections { get; } = [];
    public List<string> Scopes { get; } = [];
    public int TokenCalls { get; private set; }
    public Exception? InitializationError { get; set; }
    public TokenCredential CreateManagedIdentity(string? clientId) => Create(clientId);
    public TokenCredential CreateWorkloadIdentity(string tenantId, string clientId, string tokenFile) => Create(clientId);
    public TokenCredential CreateClientSecret(string tenantId, string clientId, string secret) => Create(clientId);
    public TokenCredential CreateClientCertificate(string tenantId, string clientId, X509Certificate2 certificate) => Create(clientId);
    private TokenCredential Create(string? selection)
    {
        if (InitializationError is not null) throw InitializationError;
        Selections.Add(selection);
        return new Credential(this, time);
    }
    private sealed class Credential(FakeAzure owner, ManualTime time) : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext context, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            owner.TokenCalls++;
            owner.Scopes.Add(string.Join(" ", context.Scopes));
            return new($"azure-token-{owner.TokenCalls}", time.GetUtcNow().AddHours(1));
        }
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext context, CancellationToken ct) =>
            ValueTask.FromResult(GetToken(context, ct));
    }
}
