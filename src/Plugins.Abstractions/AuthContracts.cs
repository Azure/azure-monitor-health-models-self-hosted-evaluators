using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace SelfHostedEvaluators.Plugins.Abstractions;

/// <summary>Applies authentication to approved transports without exposing credentials to sink clients.</summary>
/// <remarks>The host owns this context. Unsupported transport operations must fail explicitly.</remarks>
public interface IAuthContext : IAsyncDisposable
{
    /// <summary>Gets the registered authentication scheme name.</summary>
    string Scheme { get; }
    /// <summary>Configures transport authentication, such as client certificates, before the handler is used.</summary>
    /// <param name="destination">The approved destination the handler will contact.</param>
    /// <param name="handler">The unused HTTP handler to configure.</param>
    /// <param name="ct">Cancels authentication setup.</param>
    ValueTask ConfigureHttpHandlerAsync(Uri destination, HttpClientHandler handler, CancellationToken ct);
    /// <summary>Applies per-request authentication to an approved HTTP destination.</summary>
    /// <param name="request">The outgoing request to authenticate.</param>
    /// <param name="ct">Cancels credential resolution or token acquisition.</param>
    ValueTask AuthenticateAsync(HttpRequestMessage request, CancellationToken ct);
    /// <summary>Configures WebSocket authentication before a connection is established.</summary>
    /// <param name="destination">The approved secure WebSocket destination.</param>
    /// <param name="options">The connection options to configure.</param>
    /// <param name="ct">Cancels credential resolution or token acquisition.</param>
    ValueTask AuthenticateWebSocketAsync(Uri destination, ClientWebSocketOptions options, CancellationToken ct);
}

/// <summary>Creates per-instance authentication contexts for a registered custom scheme.</summary>
public interface IAuthScheme
{
    /// <summary>Creates an authentication context using host-supplied secret resolution and transport services.</summary>
    /// <param name="context">The cloned profile and services available only during auth construction.</param>
    /// <param name="ct">Cancels context creation.</param>
    /// <returns>A context whose resources will be disposed by the host.</returns>
    ValueTask<IAuthContext> CreateContextAsync(AuthSchemeContext context, CancellationToken ct);
}

/// <summary>Resolves credential references for authentication implementations, not sink clients.</summary>
public interface ISecretResolver
{
    /// <summary>Resolves a secret reference to its current or explicitly pinned value.</summary>
    /// <param name="reference">The reference URI, never a literal credential.</param>
    /// <param name="ct">Cancels secret retrieval.</param>
    /// <returns>The secret value; callers must not log it or include it in diagnostics.</returns>
    ValueTask<string> ResolveSecretAsync(Uri reference, CancellationToken ct);
}

/// <summary>Provides the profile and host-owned services needed to construct an authentication context.</summary>
public sealed class AuthSchemeContext
{
    /// <summary>Creates a context, cloning the profile independently of its source JSON document.</summary>
    /// <param name="profile">The custom authentication profile containing secret references.</param>
    /// <param name="secretResolver">The resolver available to the authentication implementation.</param>
    /// <param name="httpClient">The host-owned, bounded client for authentication exchanges.</param>
    /// <param name="logger">The logger for redacted authentication diagnostics.</param>
    /// <param name="timeProvider">The clock used for token expiry and refresh.</param>
    public AuthSchemeContext(
        JsonElement profile,
        ISecretResolver secretResolver,
        HttpClient httpClient,
        ILogger logger,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(secretResolver);
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(timeProvider);
        Profile = profile.Clone();
        SecretResolver = secretResolver;
        HttpClient = httpClient;
        Logger = logger;
        TimeProvider = timeProvider;
    }

    /// <summary>Gets the cloned authentication profile, containing references rather than resolved secrets.</summary>
    public JsonElement Profile { get; }
    /// <summary>Gets the resolver for credentials required by this scheme.</summary>
    public ISecretResolver SecretResolver { get; }
    /// <summary>Gets the host-owned HTTP client for authentication exchanges; do not dispose it.</summary>
    public HttpClient HttpClient { get; }
    /// <summary>Gets the logger; credential values must be redacted before logging.</summary>
    public ILogger Logger { get; }
    /// <summary>Gets the clock for authentication lifetimes.</summary>
    public TimeProvider TimeProvider { get; }
}
