using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using Azure.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SelfHostedEvaluators.Core.Configuration;
using SelfHostedEvaluators.Plugins.Abstractions;

namespace SelfHostedEvaluators.Core.Auth;

/// <summary>Creates guarded authentication contexts and bounded HTTP pipelines for configured destinations.</summary>
public sealed class AuthRegistry
{
    // Owned poll work inherits this deadline; shared clients read it per request, never at construction.
    internal static readonly AsyncLocal<DateTimeOffset?> PollDeadline = new();
    internal static readonly HttpRequestOptionsKey<bool> DiscardResponseBody = new("SelfHostedEvaluators.DiscardResponseBody");
    internal static readonly HttpRequestOptionsKey<DateTimeOffset> RequestDeadline = new("SelfHostedEvaluators.RequestDeadline");

    internal static void ThrowIfDeadlineElapsed(HttpRequestMessage request, TimeProvider time)
    {
        if (request.Options.TryGetValue(RequestDeadline, out var deadline) && deadline <= time.GetUtcNow())
            throw new OperationCanceledException("Request deadline elapsed.");
    }
    private readonly SecretResolver secrets;
    private readonly AzureCredentialFactory credentials;
    private readonly Redactor redactor;
    private readonly TimeProvider time;
    private readonly IHttpTransportFactory transports;
    private readonly ILogger logger;
    private readonly ConcurrentDictionary<string, Func<IAuthScheme>> schemes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Creates the registry with host-owned credential, transport and diagnostic services.</summary>
    /// <param name="secrets">The cached secret resolver used during authentication.</param>
    /// <param name="credentials">The factory for Azure token credentials.</param>
    /// <param name="redactor">The shared credential and diagnostic redactor.</param>
    /// <param name="time">The clock used for request deadlines and retries.</param>
    /// <param name="transports">An optional transport factory; defaults to the production HTTP transport.</param>
    /// <param name="logger">An optional logger wrapped in redaction.</param>
    public AuthRegistry(SecretResolver secrets, AzureCredentialFactory credentials, Redactor redactor,
        TimeProvider time, IHttpTransportFactory? transports = null, ILogger? logger = null)
    {
        this.secrets = secrets;
        this.credentials = credentials;
        this.redactor = redactor;
        this.time = time;
        this.transports = transports ?? new HttpTransportFactory();
        this.logger = redactor.Wrap(logger ?? NullLogger.Instance);
    }

    /// <summary>Registers a lazy custom scheme factory without replacing built-in or existing names.</summary>
    /// <param name="name">The case-insensitively unique scheme name.</param>
    /// <param name="factory">The factory invoked when a configured context is created.</param>
    public void RegisterScheme(string name, Func<IAuthScheme> factory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(factory);
        if (AuthProfileParser.BuiltInSchemes.Contains(name, StringComparer.OrdinalIgnoreCase) ||
            !schemes.TryAdd(name, factory)) throw new ArgumentException("Authentication scheme name collision.", nameof(name));
    }

    /// <summary>Registers the concrete authentication factories declared by a loaded package.</summary>
    /// <param name="registrations">The package's scheme-name-to-type mapping.</param>
    /// <param name="activate">An optional activator; otherwise uses parameterless construction.</param>
    public void RegisterPackageSchemes(IReadOnlyDictionary<string, Type> registrations,
        Func<Type, IAuthScheme>? activate = null)
    {
        foreach (var entry in registrations)
        {
            var type = entry.Value;
            if (!typeof(IAuthScheme).IsAssignableFrom(type) || type.IsAbstract)
                throw new ArgumentException("Invalid authentication scheme type.", nameof(registrations));
            RegisterScheme(entry.Key, () => activate is null ? (IAuthScheme)Activator.CreateInstance(type)! : activate(type));
        }
    }

    /// <summary>Creates and initializes an authentication context guarded by the approved destinations.</summary>
    /// <param name="profile">The built-in or custom profile to activate.</param>
    /// <param name="endpoints">The destinations the sink may contact.</param>
    /// <param name="limiter">The shared request limiter for authentication exchanges.</param>
    /// <param name="timeout">The authentication exchange timeout.</param>
    /// <param name="ct">Cancels initialization and secret resolution.</param>
    /// <param name="azureScope">An optional token scope for Azure authentication.</param>
    /// <param name="allowedHosts">An optional package-level exact-host allowlist.</param>
    /// <returns>A host-owned context that also owns its authentication-exchange client.</returns>
    public async ValueTask<IAuthContext> CreateContextAsync(AuthProfile profile, IEnumerable<Uri> endpoints,
        PluginRequestLimiter limiter, TimeSpan timeout, CancellationToken ct, string? azureScope = null,
        IEnumerable<string>? allowedHosts = null)
    {
        ct.ThrowIfCancellationRequested();
        var destinations = endpoints.ToArray();
        var packageHosts = allowedHosts?.ToArray();
        var policy = new DestinationPolicy(destinations, packageHosts);
        var exchanges = profile is OAuth2ClientCredentialsAuthProfile oauth ? new[] { oauth.TokenEndpoint } : destinations;
        if (profile is CustomAuthProfile custom && custom.Profile.TryGetProperty("tokenEndpoint", out var property))
        {
            if (property.ValueKind != JsonValueKind.String || !Uri.TryCreate(property.GetString(), UriKind.Absolute, out var uri))
                throw new AuthenticationFailureException("invalid_auth_endpoint");
            exchanges = [.. destinations, uri];
        }
        var exchangePolicy = new DestinationPolicy(exchanges, packageHosts);
        var exchange = CreateEndpointClient(exchanges[0], null, exchangePolicy, limiter, timeout);
        IAuthContext? context = null;
        try
        {
            if (profile is CustomAuthProfile customProfile)
            {
                if (!schemes.TryGetValue(profile.Type, out var create)) throw new AuthenticationFailureException("unknown_scheme");
                context = await create().CreateContextAsync(new(customProfile.Profile, secrets, exchange, logger, time), ct).ConfigureAwait(false);
                if (context is null) throw new AuthenticationFailureException("missing_auth_context");
            }
            else
            {
                var builtIn = new BuiltInAuthContext(profile, secrets, credentials, exchange, redactor, time, azureScope, limiter, timeout);
                context = builtIn;
                await builtIn.InitializeAsync(ct).ConfigureAwait(false);
            }
            ct.ThrowIfCancellationRequested();
            return new GuardedAuthContext(context, policy, exchange, logger);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            if (context is not null) await context.DisposeAsync().ConfigureAwait(false);
            exchange.Dispose();
            throw;
        }
        catch (Exception error)
        {
            try { if (context is not null) await context.DisposeAsync().ConfigureAwait(false); }
            finally { exchange.Dispose(); }
            logger.LogError("Authentication initialization failed ({FailureType}).", error.GetType().Name);
            throw new AuthenticationFailureException("auth_initialization");
        }
    }

    /// <summary>Creates an authenticated handler with its own default request budget and timeout.</summary>
    /// <param name="profile">The authentication profile to initialize.</param>
    /// <param name="endpoint">The approved destination.</param>
    /// <param name="ct">Cancels handler initialization.</param>
    /// <returns>A caller-owned handler that disposes its auth context and limiter.</returns>
    public async ValueTask<DelegatingHandler> CreateHandlerAsync(AuthProfile profile, Uri endpoint, CancellationToken ct)
    {
        var limiter = new PluginRequestLimiter(new(60, TimeSpan.FromSeconds(1)), time);
        try
        {
            var context = await CreateContextAsync(profile, [endpoint], limiter, TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
            return new OwnedAuthHandler(CreatePipeline(endpoint, context, new([endpoint]), limiter, TimeSpan.FromSeconds(30)), context, limiter);
        }
        catch { limiter.Dispose(); throw; }
    }

    /// <summary>Creates an authenticated, rate-limited client with bounded transport retries.</summary>
    /// <param name="profile">The authentication profile to initialize.</param>
    /// <param name="endpoint">The approved base address.</param>
    /// <param name="limiter">The externally owned shared outbound request limiter.</param>
    /// <param name="timeout">The effective request timeout, including body reads.</param>
    /// <param name="ct">Cancels client initialization.</param>
    /// <param name="azureScope">An optional Azure token scope.</param>
    /// <returns>A caller-owned client that also disposes its authentication context, but not the shared limiter.</returns>
    public async ValueTask<HttpClient> CreateClientAsync(AuthProfile profile, Uri endpoint, PluginRequestLimiter limiter,
        TimeSpan timeout, CancellationToken ct, string? azureScope = null)
    {
        var context = await CreateContextAsync(profile, [endpoint], limiter, timeout, ct, azureScope).ConfigureAwait(false);
        try
        {
            return new(new OwnedAuthHandler(CreatePipeline(endpoint, context, new([endpoint]), limiter, timeout), context, null))
            { BaseAddress = endpoint, Timeout = Timeout.InfiniteTimeSpan };
        }
        catch { await context.DisposeAsync().ConfigureAwait(false); throw; }
    }

    /// <summary>Creates an authenticated client that leaves data-plane retries and budgeting to the publisher.</summary>
    /// <param name="profile">The model authentication profile.</param>
    /// <param name="endpoint">The approved model endpoint.</param>
    /// <param name="timeout">The timeout for each individual request.</param>
    /// <param name="ct">Cancels client initialization.</param>
    /// <param name="azureScope">An optional model token scope.</param>
    /// <returns>A caller-owned client with its own separate authentication-exchange budget.</returns>
    public async ValueTask<HttpClient> CreateSingleAttemptClientAsync(AuthProfile profile, Uri endpoint,
        TimeSpan timeout, CancellationToken ct, string? azureScope = null)
    {
        // Identity exchanges have their own budget, never the model's data-plane request budget.
        var exchanges = new PluginRequestLimiter(new(60, TimeSpan.FromMinutes(1)), time);
        IAuthContext? context = null;
        try
        {
            context = await CreateContextAsync(profile, [endpoint], exchanges, timeout, ct, azureScope).ConfigureAwait(false);
            return new(new RequestPipelineHandler(endpoint, context, new([endpoint]),
                exchanges, timeout, transports, time, logger, singleAttempt: true))
            { BaseAddress = endpoint, Timeout = Timeout.InfiniteTimeSpan, MaxResponseContentBufferSize = 65536 };
        }
        catch
        {
            try { if (context is not null) await context.DisposeAsync().ConfigureAwait(false); }
            finally { exchanges.Dispose(); }
            throw;
        }
    }

    internal HttpClient CreateEndpointClient(Uri endpoint, IAuthContext? context, DestinationPolicy policy,
        PluginRequestLimiter limiter, TimeSpan timeout) =>
        new(CreatePipeline(endpoint, context, policy, limiter, timeout)) { BaseAddress = endpoint, Timeout = Timeout.InfiniteTimeSpan };

    private HttpMessageHandler CreatePipeline(Uri endpoint, IAuthContext? context, DestinationPolicy policy,
        PluginRequestLimiter limiter, TimeSpan timeout) =>
        new RequestPipelineHandler(endpoint, context, policy, limiter, timeout, transports, time, logger);

    private sealed class OwnedAuthHandler(HttpMessageHandler inner, IAuthContext context, PluginRequestLimiter? limiter) : DelegatingHandler(inner)
    {
        private int disposed;
        protected override void Dispose(bool disposing)
        {
            if (disposing && Interlocked.Exchange(ref disposed, 1) != 0) return;
            try { base.Dispose(disposing); }
            finally
            {
                if (disposing)
                {
                    try { context.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
                    finally { limiter?.Dispose(); }
                }
            }
        }
    }

    private sealed class GuardedAuthContext(IAuthContext inner, DestinationPolicy policy, HttpClient exchange, ILogger logger) : IAuthContext
    {
        private readonly object sync = new();
        private Task? disposal;
        public string Scheme => inner.Scheme;
        public async ValueTask ConfigureHttpHandlerAsync(Uri destination, HttpClientHandler handler, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            policy.Check(destination);
            try { await inner.ConfigureHttpHandlerAsync(destination, handler, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception error) { throw Failure(error); }
        }
        public async ValueTask AuthenticateAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            policy.Check(request);
            try
            {
                await inner.AuthenticateAsync(request, ct).ConfigureAwait(false);
                policy.Check(request);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception error) { throw Failure(error); }
        }
        public async ValueTask AuthenticateWebSocketAsync(Uri destination, ClientWebSocketOptions options, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            policy.Check(destination, webSocket: true);
            try { await inner.AuthenticateWebSocketAsync(destination, options, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception error) { throw Failure(error); }
        }
        private AuthenticationFailureException Failure(Exception error)
        {
            logger.LogError("Authentication operation failed ({FailureType}).", error.GetType().Name);
            return new("auth_operation");
        }
        public ValueTask DisposeAsync()
        {
            lock (sync) return new(disposal ??= DisposeCoreAsync());
        }
        private async Task DisposeCoreAsync()
        {
            try { await inner.DisposeAsync().ConfigureAwait(false); }
            finally { exchange.Dispose(); }
        }
    }
}
