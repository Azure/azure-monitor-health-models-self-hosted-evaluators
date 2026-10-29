using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;
using Azure.Core;
using Azure.Security.KeyVault.Secrets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SelfHostedEvaluators.Plugins.Abstractions;

namespace SelfHostedEvaluators.Core.Auth;

/// <summary>Identifies a Key Vault secret, optionally pinned to a specific version.</summary>
/// <param name="Vault">The vault name, without its DNS suffix.</param>
/// <param name="Name">The secret name.</param>
/// <param name="Version">The pinned version, or null to resolve the latest version after cache expiry.</param>
public sealed record KeyVaultReference(string Vault, string Name, string? Version)
{
    /// <summary>Gets the vault's HTTPS service endpoint.</summary>
    public Uri VaultUri => new($"https://{Vault}.vault.azure.net");

    /// <summary>Parses a strict kv://vault/secret[/version] URI without retrieving its value.</summary>
    /// <param name="reference">The absolute secret reference with no user info, query or fragment.</param>
    /// <returns>The validated vault, secret and optional version.</returns>
    /// <exception cref="AuthenticationFailureException">The reference is malformed.</exception>
    public static KeyVaultReference Parse(Uri reference)
    {
        if (!reference.IsAbsoluteUri || reference.Scheme != "kv" || reference.UserInfo.Length != 0 ||
            reference.Authority != reference.Host || reference.Query.Length != 0 || reference.Fragment.Length != 0 ||
            !Regex.IsMatch(reference.Host, @"^[a-zA-Z][a-zA-Z0-9-]{1,22}[a-zA-Z0-9]\z"))
            throw new AuthenticationFailureException("invalid_secret_reference");
        var parts = reference.AbsolutePath.Split('/');
        if (parts.Length is < 2 or > 3 ||
            parts.Skip(1).Any(p => !Regex.IsMatch(p, @"^[a-zA-Z0-9-]+\z")))
            throw new AuthenticationFailureException("invalid_secret_reference");
        return new(reference.Host, parts[1], parts.Length == 3 ? parts[2] : null);
    }
}

/// <summary>Retrieves secret values from Key Vault independently of resolver caching.</summary>
public interface IKeyVaultSource
{
    /// <summary>Retrieves the referenced secret using the source's bootstrap credential.</summary>
    /// <param name="reference">The validated vault, secret and optional version.</param>
    /// <param name="ct">Cancels retrieval.</param>
    /// <returns>The secret payload, which must not be logged.</returns>
    ValueTask<string> GetSecretAsync(KeyVaultReference reference, CancellationToken ct);
}

/// <summary>Reads Key Vault secrets through an authenticated, request-budgeted Azure SDK transport.</summary>
public sealed class KeyVaultSecretSource : IKeyVaultSource, IDisposable
{
    private readonly TokenCredential bootstrapCredential;
    private readonly PluginRequestLimiter limiter;
    private readonly AzureSdkCredentialSource sdk;
    private readonly TimeProvider time;
    private readonly ConcurrentDictionary<Uri, SecretClient> clients = new();

    /// <summary>Creates a source using the explicitly selected user-assigned managed identity.</summary>
    /// <param name="bootstrapClientId">The bootstrap identity's client UUID.</param>
    public KeyVaultSecretSource(string bootstrapClientId)
        : this(ValidateClientId(bootstrapClientId), null, null, null) { }

    /// <summary>Creates a source with an injected bootstrap credential and optional transport and clock.</summary>
    /// <param name="bootstrapCredential">The credential authorized to retrieve secrets.</param>
    /// <param name="transports">An optional transport factory for bounded SDK exchanges.</param>
    /// <param name="time">An optional clock for timeouts and request replenishment.</param>
    public KeyVaultSecretSource(TokenCredential bootstrapCredential, IHttpTransportFactory? transports = null, TimeProvider? time = null)
        : this(null, bootstrapCredential ?? throw new ArgumentNullException(nameof(bootstrapCredential)), transports, time) { }

    private KeyVaultSecretSource(string? clientId, TokenCredential? credential, IHttpTransportFactory? transports, TimeProvider? time)
    {
        this.time = time ?? TimeProvider.System;
        limiter = new(new(60, TimeSpan.FromMinutes(1)), this.time);
        sdk = new AzureSdkCredentialSource(transports).WithRequestBudget(limiter, TimeSpan.FromSeconds(30), this.time);
        bootstrapCredential = credential ?? sdk.CreateManagedIdentity(clientId);
    }

    private static string ValidateClientId(string clientId) => Guid.TryParse(clientId, out _) ? clientId :
        throw new ArgumentException("A bootstrap UAMI client ID is required.", nameof(clientId));

    /// <inheritdoc />
    public async ValueTask<string> GetSecretAsync(KeyVaultReference reference, CancellationToken ct)
    {
        var client = clients.GetOrAdd(reference.VaultUri, uri =>
        {
            var options = new SecretClientOptions();
            options.Diagnostics.IsLoggingContentEnabled = false;
            options.Transport = sdk.Transport!;
            options.Retry.MaxRetries = 0;
            options.Retry.NetworkTimeout = TimeSpan.FromSeconds(15);
            return new(uri, bootstrapCredential, options);
        });
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(45), time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, budget.Token);
        return (await client.GetSecretAsync(reference.Name, reference.Version, linked.Token).ConfigureAwait(false)).Value.Value;
    }

    /// <summary>Disposes the source-owned SDK transport and request limiter.</summary>
    public void Dispose()
    {
        try { sdk.Dispose(); }
        finally { limiter.Dispose(); }
    }
}

/// <summary>Caches referenced Key Vault secrets for a bounded lifetime and registers values for redaction.</summary>
public sealed class SecretResolver : ISecretResolver
{
    private sealed class Entry
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public string? Value { get; set; }
        public DateTimeOffset Expires { get; set; }
    }

    private readonly IKeyVaultSource source;
    private readonly TimeSpan ttl;
    private readonly TimeProvider time;
    private readonly Redactor redactor;
    private readonly ILogger logger;
    private readonly ConcurrentDictionary<KeyVaultReference, Entry> cache = new();

    /// <summary>Creates a resolver with per-reference serialized retrieval and a positive cache lifetime.</summary>
    /// <param name="source">The externally owned secret retrieval source.</param>
    /// <param name="ttl">The duration to cache a successfully resolved value.</param>
    /// <param name="time">The clock used to expire cached values.</param>
    /// <param name="redactor">The registry for resolved secret values.</param>
    /// <param name="logger">An optional logger wrapped in redaction.</param>
    public SecretResolver(IKeyVaultSource source, TimeSpan ttl, TimeProvider time, Redactor redactor, ILogger? logger = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(ttl, TimeSpan.Zero);
        this.source = source;
        this.ttl = ttl;
        this.time = time;
        this.redactor = redactor;
        this.logger = redactor.Wrap(logger ?? NullLogger.Instance);
    }

    /// <inheritdoc />
    public ValueTask<string> ResolveSecretAsync(Uri reference, CancellationToken ct) => ResolveAsync(reference, ct);

    /// <summary>Returns a cached secret or refreshes it after cache expiry; failed refreshes are not served as success.</summary>
    /// <param name="reference">A strict Key Vault reference URI.</param>
    /// <param name="ct">Cancels the wait or retrieval.</param>
    /// <returns>The resolved secret value, registered for diagnostic redaction.</returns>
    public async ValueTask<string> ResolveAsync(Uri reference, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var key = KeyVaultReference.Parse(reference);
        var entry = cache.GetOrAdd(key, _ => new());
        await entry.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (entry.Value is not null && time.GetUtcNow() < entry.Expires) return entry.Value;
            var value = await source.GetSecretAsync(key, ct).ConfigureAwait(false);
            if (string.IsNullOrEmpty(value)) throw new AuthenticationFailureException("empty_secret");
            redactor.RegisterSecret(value);
            entry.Value = value;
            entry.Expires = time.GetUtcNow() + ttl;
            return value;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            logger.LogError("Secret resolution failed ({FailureType}).", error.GetType().Name);
            throw new AuthenticationFailureException("secret_resolution");
        }
        finally { entry.Gate.Release(); }
    }

    /// <summary>Loads a client certificate with its private key from a referenced Key Vault backing secret.</summary>
    /// <param name="reference">The secret reference containing an encoded certificate payload.</param>
    /// <param name="ct">Cancels secret retrieval.</param>
    /// <returns>A caller-owned certificate, loaded without writing files or using persistent key stores.</returns>
    public async ValueTask<X509Certificate2> ResolveCertificateAsync(Uri reference, CancellationToken ct)
    {
        var value = await ResolveAsync(reference, ct).ConfigureAwait(false);
        return LoadCertificate(value);
    }

    internal static X509Certificate2 LoadCertificate(string value)
    {
        try
        {
            var certificate = value.Contains("-----BEGIN", StringComparison.Ordinal)
                ? X509Certificate2.CreateFromPem(value, value)
                : X509CertificateLoader.LoadPkcs12(Convert.FromBase64String(value), null, X509KeyStorageFlags.EphemeralKeySet);
            if (certificate.HasPrivateKey) return certificate;
            certificate.Dispose();
            throw new AuthenticationFailureException("certificate_private_key_required");
        }
        catch (Exception error) when (error is CryptographicException or FormatException or ArgumentException)
        {
            throw new AuthenticationFailureException("invalid_certificate");
        }
    }
}
