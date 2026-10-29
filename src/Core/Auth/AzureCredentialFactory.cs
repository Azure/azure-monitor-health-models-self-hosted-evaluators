using System.Security.Cryptography.X509Certificates;
using Azure.Core;
using Azure.Core.Pipeline;
using Azure.Identity;
using SelfHostedEvaluators.Core.Configuration;

namespace SelfHostedEvaluators.Core.Auth;

/// <summary>Abstracts Azure credential construction so token acquisition can be exercised with controlled sources.</summary>
public interface IAzureCredentialSource
{
    /// <summary>Creates a credential for a managed identity.</summary>
    /// <param name="clientId">The user-assigned client ID, or null for system-assigned identity.</param>
    /// <returns>The identity's token credential.</returns>
    TokenCredential CreateManagedIdentity(string? clientId);
    /// <summary>Creates a credential backed by a federated workload token file.</summary>
    /// <param name="tenantId">The Entra tenant identifier.</param>
    /// <param name="clientId">The application client identifier.</param>
    /// <param name="tokenFile">The externally maintained federated token path.</param>
    /// <returns>The workload's token credential.</returns>
    TokenCredential CreateWorkloadIdentity(string tenantId, string clientId, string tokenFile);
    /// <summary>Creates a credential using a resolved service-principal secret.</summary>
    /// <param name="tenantId">The Entra tenant identifier.</param>
    /// <param name="clientId">The application client identifier.</param>
    /// <param name="secret">The resolved secret value; never log it.</param>
    /// <returns>The service principal's token credential.</returns>
    TokenCredential CreateClientSecret(string tenantId, string clientId, string secret);
    /// <summary>Creates a credential using a resolved service-principal certificate.</summary>
    /// <param name="tenantId">The Entra tenant identifier.</param>
    /// <param name="clientId">The application client identifier.</param>
    /// <param name="certificate">The caller-owned certificate, retained for the credential's lifetime.</param>
    /// <returns>The service principal's token credential.</returns>
    TokenCredential CreateClientCertificate(string tenantId, string clientId, X509Certificate2 certificate);
}

/// <summary>Constructs Azure Identity credentials with SDK content logging and SDK retries disabled.</summary>
public sealed class AzureSdkCredentialSource : IAzureCredentialSource, IDisposable
{
    private readonly HttpClient? httpClient;
    private readonly HttpPipelineTransport? transport;
    private readonly HttpClientHandler? transportSettings;
    private readonly IHttpTransportFactory transports;
    /// <summary>Creates a credential source using the supplied or production transport factory.</summary>
    /// <param name="transports">The optional factory used for budgeted authentication exchanges.</param>
    public AzureSdkCredentialSource(IHttpTransportFactory? transports = null) => this.transports = transports ?? new HttpTransportFactory();
    private AzureSdkCredentialSource(PluginRequestLimiter limiter, TimeSpan timeout, TimeProvider time, IHttpTransportFactory transports)
    {
        this.transports = transports;
        transportSettings = new() { AllowAutoRedirect = false, UseCookies = false, UseDefaultCredentials = false };
        httpClient = new(new AzureExchangeHandler(limiter, timeout, time, transports.Create(transportSettings))) { Timeout = Timeout.InfiniteTimeSpan };
        transport = new HttpClientTransport(httpClient);
    }
    internal AzureSdkCredentialSource WithRequestBudget(PluginRequestLimiter limiter, TimeSpan timeout, TimeProvider time) =>
        new(limiter, timeout, time, transports);
    internal HttpPipelineTransport? Transport => transport;
    private T Configure<T>(T options) where T : TokenCredentialOptions
    {
        if (transport is not null) options.Transport = transport;
        options.Retry.MaxRetries = 0;
        options.Diagnostics.IsLoggingContentEnabled = false;
        return options;
    }
    /// <inheritdoc />
    public TokenCredential CreateManagedIdentity(string? clientId) =>
        new ManagedIdentityCredential(Configure(new ManagedIdentityCredentialOptions(
            clientId is null ? ManagedIdentityId.SystemAssigned : ManagedIdentityId.FromUserAssignedClientId(clientId))));
    /// <inheritdoc />
    public TokenCredential CreateWorkloadIdentity(string tenantId, string clientId, string tokenFile) =>
        new WorkloadIdentityCredential(Configure(new WorkloadIdentityCredentialOptions
        { TenantId = tenantId, ClientId = clientId, TokenFilePath = tokenFile }));
    /// <inheritdoc />
    public TokenCredential CreateClientSecret(string tenantId, string clientId, string secret) =>
        new ClientSecretCredential(tenantId, clientId, secret, Configure(new ClientSecretCredentialOptions()));
    /// <inheritdoc />
    public TokenCredential CreateClientCertificate(string tenantId, string clientId, X509Certificate2 certificate) =>
        new ClientCertificateCredential(tenantId, clientId, certificate, Configure(new ClientCertificateCredentialOptions()));
    /// <summary>Disposes any request-budgeted HTTP transport owned by this source.</summary>
    public void Dispose()
    {
        try { httpClient?.Dispose(); }
        finally { transportSettings?.Dispose(); }
    }

    private sealed class AzureExchangeHandler(PluginRequestLimiter limiter, TimeSpan timeout, TimeProvider time, HttpMessageHandler inner)
        : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var deadline = time.GetUtcNow() + timeout;
            if (AuthRegistry.PollDeadline.Value is { } pollDeadline && pollDeadline < deadline) deadline = pollDeadline;
            var remaining = deadline - time.GetUtcNow();
            if (remaining <= TimeSpan.Zero) throw new OperationCanceledException("Request deadline elapsed.");
            using var budget = new CancellationTokenSource(remaining, time);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, budget.Token);
            var body = request.Content is null ? null : await request.Content.ReadAsByteArrayAsync(linked.Token).ConfigureAwait(false);
            for (var attempt = 0; ; attempt++)
            {
                using var outbound = RequestPipelineHandler.Clone(request, body);
                using var lease = await limiter.AcquireWithinAsync(deadline - time.GetUtcNow(), linked.Token).ConfigureAwait(false);
                if (!lease.IsAcquired) throw new AuthenticationFailureException("azure_rate_limited");
                var response = await base.SendAsync(outbound, linked.Token).ConfigureAwait(false);
                try { await response.Content.LoadIntoBufferAsync(linked.Token).ConfigureAwait(false); }
                catch { response.Dispose(); throw; }
                if ((int)response.StatusCode is >= 300 and <= 399)
                { response.Dispose(); throw new AuthenticationFailureException("azure_redirect_blocked"); }
                var retryable = response.StatusCode == System.Net.HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500;
                var delay = retryable ? RequestPipelineHandler.RetryDelay(response, attempt, time) : TimeSpan.Zero;
                if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests) limiter.Pause(delay);
                if (!retryable || attempt == 2 || delay >= deadline - time.GetUtcNow()) return response;
                response.Dispose();
                await Task.Delay(delay, time, linked.Token).ConfigureAwait(false);
            }
        }
    }
}

/// <summary>Creates refreshing Azure credentials with cached tokens, secret rotation and redacted failures.</summary>
/// <param name="secrets">The resolver for referenced secrets and certificates.</param>
/// <param name="source">The underlying Azure credential constructor.</param>
/// <param name="time">The clock for token expiry and request deadlines.</param>
/// <param name="redactor">The registry for resolved secrets and acquired tokens.</param>
public sealed class AzureCredentialFactory(SecretResolver secrets, IAzureCredentialSource source, TimeProvider time, Redactor redactor)
{
    /// <summary>Creates a lazy credential; secret resolution occurs on first token acquisition.</summary>
    /// <param name="profile">The Azure authentication profile.</param>
    /// <param name="limiter">An optional shared request limiter; otherwise the credential owns a new one.</param>
    /// <param name="timeout">An optional token-operation timeout, defaulting to 30 seconds.</param>
    /// <returns>A token credential implementing <see cref="IAsyncDisposable"/>; its owner must dispose it.</returns>
    public TokenCredential Create(AzureAuthProfile profile, PluginRequestLimiter? limiter = null, TimeSpan? timeout = null) =>
        CreateCredential(profile, limiter, timeout ?? TimeSpan.FromSeconds(30));

    private RefreshingCredential CreateCredential(AzureAuthProfile profile, PluginRequestLimiter? limiter, TimeSpan timeout)
    {
        var ownLimiter = limiter is null;
        limiter ??= new(new(60, TimeSpan.FromMinutes(1)), time);
        try
        {
            var effectiveSource = source is AzureSdkCredentialSource sdk ? sdk.WithRequestBudget(limiter, timeout, time) : source;
            return new(profile, secrets, effectiveSource, time, redactor, limiter, timeout, ownLimiter,
                !ReferenceEquals(effectiveSource, source) ? (IDisposable)effectiveSource : null);
        }
        catch (Exception)
        {
            if (ownLimiter) limiter.Dispose();
            throw new AuthenticationFailureException("azure_initialization");
        }
    }

    /// <summary>Creates a credential and initializes its current identity material before returning.</summary>
    /// <param name="profile">The Azure authentication profile.</param>
    /// <param name="ct">Cancels identity initialization.</param>
    /// <param name="limiter">An optional externally owned request limiter.</param>
    /// <param name="timeout">An optional token-operation timeout, defaulting to 30 seconds.</param>
    /// <returns>An initialized credential implementing <see cref="IAsyncDisposable"/>; its owner must dispose it.</returns>
    public async ValueTask<TokenCredential> CreateAsync(AzureAuthProfile profile, CancellationToken ct,
        PluginRequestLimiter? limiter = null, TimeSpan? timeout = null)
    {
        var credential = CreateCredential(profile, limiter, timeout ?? TimeSpan.FromSeconds(30));
        try { await credential.InitializeAsync(ct).ConfigureAwait(false); return credential; }
        catch { await credential.DisposeAsync().ConfigureAwait(false); throw; }
    }

    private sealed class RefreshingCredential(AzureAuthProfile profile, SecretResolver secrets,
        IAzureCredentialSource source, TimeProvider time, Redactor redactor, PluginRequestLimiter limiter,
        TimeSpan timeout, bool ownLimiter, IDisposable? transportOwner) : TokenCredential, IAsyncDisposable
    {
        private readonly SemaphoreSlim gate = new(1, 1);
        private readonly Dictionary<string, AccessToken> tokens = new(StringComparer.Ordinal);
        private TokenCredential? current;
        private string? material;
        private X509Certificate2? certificate;
        private bool disposed;

        public async ValueTask InitializeAsync(CancellationToken ct)
        {
            using var budget = new CancellationTokenSource(timeout, time);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, budget.Token);
            await gate.WaitAsync(linked.Token).ConfigureAwait(false);
            try { await RefreshAsync(linked.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (linked.IsCancellationRequested) { throw; }
            catch (Exception) { throw new AuthenticationFailureException("azure_initialization"); }
            finally { gate.Release(); }
        }

        private async ValueTask RefreshAsync(CancellationToken ct)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            string? next = profile switch
            {
                ServicePrincipalSecretAuthProfile secret => await secrets.ResolveAsync(secret.ClientSecret, ct).ConfigureAwait(false),
                ServicePrincipalCertificateAuthProfile cert => await secrets.ResolveAsync(cert.Certificate, ct).ConfigureAwait(false),
                _ => null
            };
            if (current is not null && material == next) return;
            X509Certificate2? newCertificate = null;
            try
            {
                var credential = profile switch
                {
                    ManagedIdentityAuthProfile identity => source.CreateManagedIdentity(identity.ClientId),
                    WorkloadIdentityAuthProfile workload => source.CreateWorkloadIdentity(workload.TenantId, workload.ClientId, workload.TokenFile),
                    ServicePrincipalSecretAuthProfile secret => source.CreateClientSecret(secret.TenantId, secret.ClientId, next!),
                    ServicePrincipalCertificateAuthProfile cert => source.CreateClientCertificate(cert.TenantId, cert.ClientId,
                        newCertificate = SecretResolver.LoadCertificate(next!)),
                    _ => throw new AuthenticationFailureException("unsupported_azure_scheme")
                };
                certificate?.Dispose();
                certificate = newCertificate;
                current = credential;
                material = next;
                tokens.Clear();
            }
            catch { newCertificate?.Dispose(); throw; }
        }

        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            GetTokenAsync(requestContext, cancellationToken).AsTask().GetAwaiter().GetResult();

        public override async ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken ct)
        {
            using var budget = new CancellationTokenSource(timeout, time);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, budget.Token);
            ct = linked.Token;
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await RefreshAsync(ct).ConfigureAwait(false);
                var key = string.Join("\n", requestContext.Scopes) + "\n" + requestContext.Claims + "\n" +
                    requestContext.TenantId + "\n" + requestContext.IsCaeEnabled;
                if (tokens.TryGetValue(key, out var token) && token.ExpiresOn > time.GetUtcNow().AddSeconds(10)) return token;
                if (source is not AzureSdkCredentialSource)
                {
                    using var lease = await limiter.AcquireAsync(1, ct).ConfigureAwait(false);
                    if (!lease.IsAcquired) throw new AuthenticationFailureException("azure_rate_limited");
                    token = await current!.GetTokenAsync(requestContext, ct).ConfigureAwait(false);
                }
                else token = await current!.GetTokenAsync(requestContext, ct).ConfigureAwait(false);
                if (string.IsNullOrEmpty(token.Token) || token.ExpiresOn <= time.GetUtcNow())
                    throw new AuthenticationFailureException("invalid_azure_token");
                redactor.RegisterSecret(token.Token);
                tokens[key] = token;
                return token;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception) { throw new AuthenticationFailureException("azure_credential"); }
            finally { gate.Release(); }
        }

        public async ValueTask DisposeAsync()
        {
            await gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (disposed) return;
                disposed = true;
                certificate?.Dispose();
                certificate = null;
                tokens.Clear();
                material = null;
                current = null;
                transportOwner?.Dispose();
                if (ownLimiter) limiter.Dispose();
            }
            finally { gate.Release(); }
        }
    }
}
