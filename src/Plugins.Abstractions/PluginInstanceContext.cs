using System.Collections.ObjectModel;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.Extensions.Logging;

namespace SelfHostedEvaluators.Plugins.Abstractions;

/// <summary>Provides a named sink instance with immutable configuration and host-owned runtime services.</summary>
/// <remarks>Settings are cloned and the client mapping is copied; the referenced services remain host-owned.</remarks>
public sealed class PluginInstanceContext
{
    /// <summary>Creates a context for one independently authenticated plugin configuration.</summary>
    /// <param name="pluginName">The configured plugin instance name, not the package identifier.</param>
    /// <param name="settings">Package-specific settings containing no resolved credentials.</param>
    /// <param name="effectiveBatchSize">The positive validated batch size for this instance.</param>
    /// <param name="httpClients">Named clients already configured for authentication, limits and retries.</param>
    /// <param name="auth">Authentication operations for non-HTTP transports.</param>
    /// <param name="rateLimiter">The shared limiter to acquire for each non-HTTP outbound request.</param>
    /// <param name="logger">The instance logger for redacted diagnostics.</param>
    /// <param name="timeProvider">The clock for queries and delays.</param>
    public PluginInstanceContext(
        string pluginName,
        JsonElement settings,
        int effectiveBatchSize,
        IReadOnlyDictionary<string, HttpClient> httpClients,
        IAuthContext auth,
        RateLimiter rateLimiter,
        ILogger logger,
        TimeProvider timeProvider)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginName);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(effectiveBatchSize);
        ArgumentNullException.ThrowIfNull(httpClients);
        ArgumentNullException.ThrowIfNull(auth);
        ArgumentNullException.ThrowIfNull(rateLimiter);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(timeProvider);

        PluginName = pluginName;
        Settings = settings.Clone();
        EffectiveBatchSize = effectiveBatchSize;
        HttpClients = new ReadOnlyDictionary<string, HttpClient>(
            new Dictionary<string, HttpClient>(httpClients, StringComparer.Ordinal));
        Auth = auth;
        RateLimiter = rateLimiter;
        Logger = logger;
        TimeProvider = timeProvider;
    }

    /// <summary>Gets the configured instance name shared by its signals.</summary>
    public string PluginName { get; }
    /// <summary>Gets the settings clone, independent of the caller's JSON document.</summary>
    public JsonElement Settings { get; }
    /// <summary>Gets the validated maximum number of queries supplied in a batch.</summary>
    public int EffectiveBatchSize { get; }
    /// <summary>Gets authenticated, host-owned clients; sinks must not authenticate again or dispose them.</summary>
    public IReadOnlyDictionary<string, HttpClient> HttpClients { get; }
    /// <summary>Gets host-owned authentication operations for non-HTTP transports.</summary>
    public IAuthContext Auth { get; }
    /// <summary>Gets the host-owned shared request limiter for non-HTTP transports.</summary>
    public RateLimiter RateLimiter { get; }
    /// <summary>Gets the instance logger; never log credentials or unredacted vendor payloads.</summary>
    public ILogger Logger { get; }
    /// <summary>Gets the clock for observations and cancellable delays.</summary>
    public TimeProvider TimeProvider { get; }
}
