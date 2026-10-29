using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Text.Json;

namespace SelfHostedEvaluators.Core.Configuration;

/// <summary>Holds the complete immutable configuration accepted at startup; changes require a new snapshot.</summary>
public sealed class ConfigSnapshot
{
    internal ConfigSnapshot(HostOptions host, IEnumerable<PluginConfiguration> plugins,
        IEnumerable<ModelConfiguration> models, IEnumerable<ConfigurationDiagnostic> warnings)
    {
        Host = host;
        Plugins = plugins.ToImmutableArray();
        Models = models.ToImmutableArray();
        Warnings = warnings.ToImmutableArray();
    }

    /// <summary>Gets process-wide defaults and host service settings.</summary>
    public HostOptions Host { get; }
    /// <summary>Gets the independently configured, named plugin instances.</summary>
    public ImmutableArray<PluginConfiguration> Plugins { get; }
    /// <summary>Gets the target models and their evaluated signal configurations.</summary>
    public ImmutableArray<ModelConfiguration> Models { get; }
    /// <summary>Gets nonfatal configuration locations, such as entities with more than 50 signals.</summary>
    public ImmutableArray<ConfigurationDiagnostic> Warnings { get; }
}

/// <summary>Contains host settings parsed from environment variables.</summary>
/// <param name="PackagesDirectory">The local directory containing bundled packages.</param>
/// <param name="DefaultInterval">The polling interval used when no narrower default is supplied.</param>
/// <param name="DefaultTimeout">The request timeout, additionally bounded by each poll interval.</param>
/// <param name="DefaultTtlMultiplier">The multiplier used to derive report expiry from the interval.</param>
/// <param name="ReportsPerMinute">The default per-model report budget.</param>
/// <param name="SecretCacheTtl">How long resolved secret values are cached.</param>
/// <param name="ShutdownTimeout">The best-effort combined drain and telemetry-flush budget.</param>
/// <param name="LogLevel">The configured logging threshold.</param>
/// <param name="AzureClientId">The optional default managed-identity client ID.</param>
/// <param name="ApplicationInsightsConnectionString">The optional telemetry destination, required in ACI.</param>
/// <param name="AzureFederatedTokenFile">The optional default workload-identity token-file path.</param>
public sealed record HostOptions(
    string PackagesDirectory,
    TimeSpan DefaultInterval,
    TimeSpan DefaultTimeout,
    double DefaultTtlMultiplier,
    int ReportsPerMinute,
    TimeSpan SecretCacheTtl,
    TimeSpan ShutdownTimeout,
    string LogLevel,
    string? AzureClientId,
    string? ApplicationInsightsConnectionString,
    string? AzureFederatedTokenFile);

/// <summary>Limits the number of queries in each plugin batch.</summary>
/// <param name="MaxSize">The positive configured batch size, bounded by package capabilities.</param>
public sealed record BatchOptions(int MaxSize);
/// <summary>Limits outbound requests shared by a plugin instance.</summary>
/// <param name="Requests">The number of permits replenished per window.</param>
/// <param name="Per">The replenishment window duration.</param>
public sealed record RateLimitOptions(int Requests, TimeSpan Per);
/// <summary>Contains optional defaults applied before effective signal settings are calculated.</summary>
/// <param name="Interval">An optional polling interval override.</param>
/// <param name="TtlMultiplier">An optional multiplier for interval-derived report expiry.</param>
/// <param name="ExpiresInMinutes">An optional explicit expiry that takes precedence at this defaults level.</param>
public sealed record ConfigurationDefaults(TimeSpan? Interval = null, double? TtlMultiplier = null, int? ExpiresInMinutes = null);

/// <summary>Describes one named, independently authenticated instance of an image-bundled package.</summary>
public sealed class PluginConfiguration
{
    internal PluginConfiguration(string variable, string name, string package, string? packageVersion,
        IReadOnlyDictionary<string, Uri> endpoints, AuthProfile auth, BatchOptions batch, RateLimitOptions rateLimit,
        int concurrency, TimeSpan timeout, ConfigurationDefaults defaults, JsonElement settings)
    {
        Variable = variable;
        Name = name;
        Package = package;
        PackageVersion = packageVersion;
        Endpoints = endpoints.ToFrozenDictionary(StringComparer.Ordinal);
        Auth = auth;
        Batch = batch;
        RateLimit = rateLimit;
        Concurrency = concurrency;
        Timeout = timeout;
        Defaults = defaults;
        Settings = settings.Clone();
    }

    /// <summary>Gets the source environment variable used in location-only diagnostics.</summary>
    public string Variable { get; }
    /// <summary>Gets the supported plugin configuration schema version.</summary>
    public int SchemaVersion => 1;
    /// <summary>Gets the instance name referenced by model signals.</summary>
    public string Name { get; }
    /// <summary>Gets the bundled package identifier.</summary>
    public string Package { get; }
    /// <summary>Gets the optional version constraint checked against the bundled package.</summary>
    public string? PackageVersion { get; }
    /// <summary>Gets the immutable, case-sensitive map of named HTTPS endpoints.</summary>
    public IReadOnlyDictionary<string, Uri> Endpoints { get; }
    /// <summary>Gets the authentication profile containing credential references, not resolved secrets.</summary>
    public AuthProfile Auth { get; }
    /// <summary>Gets the configured batch-size limit.</summary>
    public BatchOptions Batch { get; }
    /// <summary>Gets the shared outbound request-rate limit.</summary>
    public RateLimitOptions RateLimit { get; }
    /// <summary>Gets the maximum concurrent batches for this instance.</summary>
    public int Concurrency { get; }
    /// <summary>Gets the configured timeout before it is bounded by a batch's interval.</summary>
    public TimeSpan Timeout { get; }
    /// <summary>Gets plugin-level polling defaults.</summary>
    public ConfigurationDefaults Defaults { get; }
    /// <summary>Gets the cloned settings object interpreted by the package.</summary>
    public JsonElement Settings { get; }
    /// <summary>Bounds the plugin timeout by the effective polling interval.</summary>
    /// <param name="interval">The interval of the batch being executed.</param>
    /// <returns>The smaller of the configured timeout and interval.</returns>
    public TimeSpan GetEffectiveTimeout(TimeSpan interval) => Timeout < interval ? Timeout : interval;
}

/// <summary>Defines a model's reporting destination, authentication and request budget.</summary>
/// <param name="DataPlaneUri">The existing model's HTTPS data-plane endpoint.</param>
/// <param name="ApiVersion">The supported reporting API version.</param>
/// <param name="TokenScope">The Entra token scope used when authenticating reports.</param>
/// <param name="Auth">The model reporting authentication profile.</param>
/// <param name="ReportsPerMinute">The validated per-model request budget.</param>
public sealed record HealthModelConfiguration(Uri DataPlaneUri, string ApiVersion, string TokenScope,
    AuthProfile Auth, int ReportsPerMinute);

/// <summary>Groups effective signals and defaults for one uniquely configured health model.</summary>
public sealed class ModelConfiguration
{
    internal ModelConfiguration(string variable, string name, HealthModelConfiguration healthModel,
        ConfigurationDefaults defaults, IEnumerable<SignalConfiguration> signals)
    {
        Variable = variable;
        Name = name;
        HealthModel = healthModel;
        Defaults = defaults;
        Signals = signals.ToImmutableArray();
    }

    /// <summary>Gets the source model environment variable for diagnostics.</summary>
    public string Variable { get; }
    /// <summary>Gets the supported model configuration schema version.</summary>
    public int SchemaVersion => 1;
    /// <summary>Gets the local model configuration name.</summary>
    public string Name { get; }
    /// <summary>Gets the target model's reporting settings.</summary>
    public HealthModelConfiguration HealthModel { get; }
    /// <summary>Gets model-level polling and expiry defaults.</summary>
    public ConfigurationDefaults Defaults { get; }
    /// <summary>Gets the immutable collection of signals to poll and report.</summary>
    public ImmutableArray<SignalConfiguration> Signals { get; }
}

/// <summary>Contains one signal's query, evaluation policy and fully resolved cadence and expiry.</summary>
public sealed class SignalConfiguration
{
    internal SignalConfiguration(string propertyPath, string entity, string name, string plugin, JsonElement query,
        EvaluationConfiguration evaluation, string? context, TimeSpan interval, int expiresInMinutes)
    {
        PropertyPath = propertyPath;
        Entity = entity;
        Name = name;
        Plugin = plugin;
        Query = query.Clone();
        Evaluation = evaluation;
        Context = context;
        Interval = interval;
        ExpiresInMinutes = expiresInMinutes;
    }

    /// <summary>Gets the signal's JSON path within its source model configuration.</summary>
    public string PropertyPath { get; }
    /// <summary>Gets the target entity name within the model.</summary>
    public string Entity { get; }
    /// <summary>Gets the signal name within that entity.</summary>
    public string Name { get; }
    /// <summary>Gets the named plugin instance used to query this signal.</summary>
    public string Plugin { get; }
    /// <summary>Gets the cloned, package-specific query object.</summary>
    public JsonElement Query { get; }
    /// <summary>Gets the threshold or state-map evaluation policy.</summary>
    public EvaluationConfiguration Evaluation { get; }
    /// <summary>Gets the optional report-context template.</summary>
    public string? Context { get; }
    /// <summary>Gets the effective interval after signal, plugin, model and host precedence.</summary>
    public TimeSpan Interval { get; }
    /// <summary>Gets the validated report lifetime in whole minutes.</summary>
    public int ExpiresInMinutes { get; }
}

/// <summary>Identifies the four health states published to a model.</summary>
public enum HealthState
{
    /// <summary>The signal meets its configured healthy criteria.</summary>
    Healthy,
    /// <summary>The signal meets its configured degraded criteria.</summary>
    Degraded,
    /// <summary>The signal meets its configured unhealthy criteria.</summary>
    Unhealthy,
    /// <summary>The signal's health cannot be determined.</summary>
    Unknown
}

/// <summary>Selects the numeric comparison used by a threshold rule.</summary>
public enum RuleOperator
{
    /// <summary>The value is strictly below the threshold.</summary>
    LessThan,
    /// <summary>The value is at or below the threshold.</summary>
    LessThanOrEqual,
    /// <summary>The value is strictly above the threshold.</summary>
    GreaterThan,
    /// <summary>The value is at or above the threshold.</summary>
    GreaterThanOrEqual,
    /// <summary>The value equals the threshold.</summary>
    Equal,
    /// <summary>The value differs from the threshold.</summary>
    NotEqual
}

/// <summary>Defines a numeric comparison for an unhealthy or degraded boundary.</summary>
/// <param name="Operator">The comparison applied to the observed value.</param>
/// <param name="Threshold">The finite value against which the observation is compared.</param>
public sealed record ThresholdRule(RuleOperator Operator, double Threshold);

/// <summary>Defines either numeric thresholds or a native-state map, plus the missing-data policy.</summary>
public sealed class EvaluationConfiguration
{
    internal EvaluationConfiguration(ThresholdRule? unhealthy, ThresholdRule? degraded,
        IReadOnlyDictionary<string, HealthState>? stateMap, HealthState noData)
    {
        Unhealthy = unhealthy;
        Degraded = degraded;
        StateMap = stateMap?.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
        NoData = noData;
    }

    /// <summary>Gets the unhealthy threshold, tested before degraded, or null for state-map evaluation.</summary>
    public ThresholdRule? Unhealthy { get; }
    /// <summary>Gets the optional degraded threshold.</summary>
    public ThresholdRule? Degraded { get; }
    /// <summary>Gets the case-insensitive native-state mapping, or null for threshold evaluation.</summary>
    public IReadOnlyDictionary<string, HealthState>? StateMap { get; }
    /// <summary>Gets the health state used when a successful query returns no data.</summary>
    public HealthState NoData { get; }
}
