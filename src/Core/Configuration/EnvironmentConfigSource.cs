using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SelfHostedEvaluators.Core.Configuration;

/// <summary>Parses a copied process environment into strict, immutable host and model configuration.</summary>
public sealed class EnvironmentConfigSource
{
    /// <summary>Parses host options alone, without loading packages or requiring plugin and model entries.</summary>
    /// <param name="environment">Environment values whose names are interpreted case-sensitively.</param>
    /// <returns>Validated host settings with omitted options defaulted.</returns>
    public HostOptions LoadHostOptions(IReadOnlyDictionary<string, string> environment) =>
        ReadHost(environment.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal));

    private static readonly HashSet<string> HostKeys = new(StringComparer.Ordinal)
    {
        "SHE_PACKAGES_DIR", "SHE_DEFAULT_INTERVAL", "SHE_DEFAULT_TIMEOUT", "SHE_DEFAULT_TTL_MULTIPLIER",
        "SHE_REPORTS_PER_MINUTE", "SHE_SECRET_CACHE_TTL", "SHE_SHUTDOWN_TIMEOUT", "SHE_LOG_LEVEL"
    };

    /// <summary>Parses and validates the entire environment without resolving secrets or contacting services.</summary>
    /// <param name="environment">The complete environment, copied before validation.</param>
    /// <returns>An immutable snapshot with resolved signal intervals and expiry values.</returns>
    /// <exception cref="ConfigurationException">A reserved variable, schema, reference or budget is invalid.</exception>
    public ConfigSnapshot Load(IReadOnlyDictionary<string, string> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        // Copy with ordinal keys even when the caller supplied a case-insensitive dictionary.
        var env = environment.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        var errors = new List<ConfigurationDiagnostic>();
        var plugins = new List<PluginConfiguration>();
        var models = new List<ModelConfiguration>();
        var warnings = new List<ConfigurationDiagnostic>();
        foreach (var key in env.Keys.Where(k => k.StartsWith("SHE_", StringComparison.Ordinal)))
        {
            if (HostKeys.Contains(key)) continue;
            var prefix = key.StartsWith("SHE_PLUGIN_", StringComparison.Ordinal) ? "SHE_PLUGIN_" :
                key.StartsWith("SHE_MODEL_", StringComparison.Ordinal) ? "SHE_MODEL_" : null;
            if (prefix is null || !Regex.IsMatch(key[prefix.Length..], @"^[A-Z][A-Z0-9_]*\z", RegexOptions.CultureInvariant))
                errors.Add(new(key, "$"));
        }
        var host = ReadHost(env);
        foreach (var entry in env.Where(p => p.Key.StartsWith("SHE_PLUGIN_", StringComparison.Ordinal)).OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            try
            {
                using var document = StrictJson.Parse(entry.Value, entry.Key);
                plugins.Add(ReadPlugin(new(document.RootElement, entry.Key), host));
            }
            catch (ConfigurationException error) { errors.AddRange(error.Diagnostics); }
        }
        foreach (var entry in env.Where(p => p.Key.StartsWith("SHE_MODEL_", StringComparison.Ordinal)).OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            try
            {
                using var document = StrictJson.Parse(entry.Value, entry.Key);
                models.Add(ReadModel(new(document.RootElement, entry.Key), host, plugins, warnings));
            }
            catch (ConfigurationException error) { errors.AddRange(error.Diagnostics); }
        }
        if (plugins.Count == 0) errors.Add(new("SHE_PLUGIN_<KEY>", "$"));
        if (models.Count == 0) errors.Add(new("SHE_MODEL_<KEY>", "$"));
        CheckUnique(plugins.Select(p => (p.Name, p.Variable, "$.name")), errors);
        CheckUnique(models.Select(m => (m.Name, m.Variable, "$.name")), errors);
        CheckUnique(models.Select(m => (NormalizeModelUri(m.HealthModel.DataPlaneUri), m.Variable, "$.healthModel.dataPlaneUri")), errors);
        if (errors.Count > 0) throw new ConfigurationException(errors);
        return new(host, plugins, models, warnings);
    }

    private static void CheckUnique(IEnumerable<(string Value, string Variable, string Path)> values, List<ConfigurationDiagnostic> errors)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in values)
            if (!seen.Add(item.Value)) errors.Add(new(item.Variable, item.Path));
    }

    internal static string NormalizeModelUri(Uri uri) => uri.AbsoluteUri.TrimEnd('/');

    private static HostOptions ReadHost(Dictionary<string, string> env)
    {
        string Text(string key, string fallback)
        {
            if (!env.TryGetValue(key, out var text)) return fallback;
            if (string.IsNullOrWhiteSpace(text)) throw ConfigurationException.At(key, "$");
            return text;
        }
        string? Optional(string key) => env.ContainsKey(key) ? Text(key, "") : null;
        TimeSpan Duration(string key, string fallback) => StrictJson.ParseDuration(Text(key, fallback), key, "$");
        int Integer(string key, string fallback, int max)
        {
            if (!int.TryParse(Text(key, fallback), NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value <= 0 || value > max)
                throw ConfigurationException.At(key, "$");
            return value;
        }
        var packages = Text("SHE_PACKAGES_DIR", "/app/packages");
        if (packages.Contains("://", StringComparison.Ordinal) || packages.IndexOf('\0') >= 0)
            throw ConfigurationException.At("SHE_PACKAGES_DIR", "$");
        var interval = StrictJson.Interval(Duration("SHE_DEFAULT_INTERVAL", "1m"), "SHE_DEFAULT_INTERVAL", "$");
        if (!double.TryParse(Text("SHE_DEFAULT_TTL_MULTIPLIER", "3"), NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out var multiplier) || !double.IsFinite(multiplier) || multiplier <= 0)
            throw ConfigurationException.At("SHE_DEFAULT_TTL_MULTIPLIER", "$");
        var logLevel = Text("SHE_LOG_LEVEL", "Information");
        if (!new[] { "Trace", "Debug", "Information", "Warning", "Error", "Critical", "None" }.Contains(logLevel, StringComparer.Ordinal))
            throw ConfigurationException.At("SHE_LOG_LEVEL", "$");
        var clientId = Optional("AZURE_CLIENT_ID");
        if (clientId is not null && !Guid.TryParseExact(clientId, "D", out _)) throw ConfigurationException.At("AZURE_CLIENT_ID", "$");
        var connection = Optional("APPLICATIONINSIGHTS_CONNECTION_STRING");
        if (connection is not null && clientId is null) throw ConfigurationException.At("AZURE_CLIENT_ID", "$");
        return new(packages, interval, Duration("SHE_DEFAULT_TIMEOUT", "30s"), multiplier,
            Integer("SHE_REPORTS_PER_MINUTE", "5000", 5999), Duration("SHE_SECRET_CACHE_TTL", "5m"),
            Duration("SHE_SHUTDOWN_TIMEOUT", "5s"), logLevel, clientId,
            connection, Optional("AZURE_FEDERATED_TOKEN_FILE"));
    }

    private static PluginConfiguration ReadPlugin(StrictJson json, HostOptions host)
    {
        json.Shape("schemaVersion", "name", "package", "packageVersion", "endpoints", "auth", "batch",
            "rateLimit", "concurrency", "timeout", "defaults", "settings");
        json.Child("schemaVersion").Integer(1, 1);
        var name = json.Child("name").Name();
        var package = json.Text("package");
        if (!Regex.IsMatch(package, @"^[a-zA-Z0-9][a-zA-Z0-9-]*\z", RegexOptions.CultureInvariant)) throw json.Error("package");
        var version = json.OptionalText("packageVersion");
        if (version is not null && !Packages.PackageVersionConstraint.IsValid(version)) throw json.Error("packageVersion");
        var endpoints = json.Child("endpoints").ShapeMap();
        var endpointMap = new Dictionary<string, Uri>(StringComparer.Ordinal);
        foreach (var property in endpoints.Element.EnumerateObject())
        {
            if (!Regex.IsMatch(property.Name, @"^[a-zA-Z][a-zA-Z0-9_-]*\z", RegexOptions.CultureInvariant)) throw endpoints.Error();
            endpointMap.Add(property.Name, endpoints.Child(property.Name).Https());
        }
        if (endpointMap.Count == 0) throw json.Error("endpoints");
        var auth = AuthProfileParser.Parse(json.Child("auth"), host);
        if (auth is AzureAuthProfile) throw json.Error("auth");
        var batch = json.Has("batch") ? json.Child("batch").Shape("maxSize").Child("maxSize").Integer() : 1;
        var rate = new RateLimitOptions(1, TimeSpan.FromSeconds(1));
        if (json.Has("rateLimit"))
        {
            var limits = json.Child("rateLimit").Shape("requests", "per");
            rate = new(limits.Child("requests").Integer(), limits.Child("per").Duration());
        }
        var concurrency = json.Has("concurrency") ? json.Child("concurrency").Integer() : 1;
        var timeout = json.Has("timeout") ? json.Child("timeout").Duration() : host.DefaultTimeout;
        var defaults = json.Has("defaults") ? ReadDefaults(json.Child("defaults"), true) : new ConfigurationDefaults();
        using var empty = JsonDocument.Parse("{}");
        var settings = json.Has("settings") ? json.Child("settings").ShapeMap().Element : empty.RootElement;
        return new(json.Variable, name, package, version, endpointMap, auth, new(batch), rate, concurrency, timeout, defaults, settings);
    }

    private static ConfigurationDefaults ReadDefaults(StrictJson json, bool plugin = false)
    {
        json.Shape(plugin ? ["interval"] : ["interval", "ttlMultiplier", "expiresInMinutes"]);
        var interval = json.Has("interval") ? StrictJson.Interval(json.Child("interval").Duration(), json.Variable, json.Path + ".interval") : (TimeSpan?)null;
        var multiplier = json.Has("ttlMultiplier") ? json.Child("ttlMultiplier").Multiplier() : (double?)null;
        var expires = json.Has("expiresInMinutes") ? json.Child("expiresInMinutes").Integer(1, 10080) : (int?)null;
        return new(interval, multiplier, expires);
    }

    private static ModelConfiguration ReadModel(StrictJson json, HostOptions host, List<PluginConfiguration> plugins,
        List<ConfigurationDiagnostic> warnings)
    {
        json.Shape("schemaVersion", "name", "healthModel", "defaults", "signals");
        json.Child("schemaVersion").Integer(1, 1);
        var name = json.Child("name").Name();
        var health = json.Child("healthModel").Shape("dataPlaneUri", "apiVersion", "auth", "tokenScope", "reportsPerMinute");
        var uri = health.Child("dataPlaneUri").Https();
        var auth = health.Has("auth") ? AuthProfileParser.Parse(health.Child("auth"), host) : new ManagedIdentityAuthProfile(host.AzureClientId);
        if (auth is not AzureAuthProfile and not CustomAuthProfile) throw health.Error("auth");
        var apiVersion = health.OptionalText("apiVersion") ?? "2026-06-01-preview";
        if (!new[] { "2026-06-01-preview", "2026-10-01-preview" }.Contains(apiVersion, StringComparer.Ordinal)) throw health.Error("apiVersion");
        var scope = health.Has("tokenScope") ? health.Child("tokenScope").Https().OriginalString :
            "https://data.cloudhealth.azure.com/.default";
        var budget = health.Has("reportsPerMinute") ? health.Child("reportsPerMinute").Integer(1, 5999) : host.ReportsPerMinute;
        var defaults = json.Has("defaults") ? ReadDefaults(json.Child("defaults")) : new ConfigurationDefaults();
        var signals = new List<SignalConfiguration>();
        var keys = new HashSet<(string, string)>();
        foreach (var signal in json.Child("signals").Items())
        {
            signal.Shape("entity", "name", "plugin", "query", "evaluation", "interval", "ttlMultiplier", "expiresInMinutes", "context");
            var entity = signal.Child("entity").Name();
            var signalName = signal.Child("name").Name();
            if (!keys.Add((entity, signalName))) throw signal.Error("name");
            var pluginName = signal.Child("plugin").Name();
            var plugin = plugins.FirstOrDefault(p => p.Name == pluginName);
            var interval = signal.Has("interval") ? signal.Child("interval").Duration() :
                plugin?.Defaults.Interval ?? defaults.Interval ?? host.DefaultInterval;
            StrictJson.Interval(interval, signal.Variable, signal.Path + ".interval");
            var explicitTtl = signal.Has("expiresInMinutes") ? signal.Child("expiresInMinutes").Integer(1, 10080) :
                signal.Has("ttlMultiplier") ? null : defaults.ExpiresInMinutes;
            var multiplier = signal.Has("ttlMultiplier") ? signal.Child("ttlMultiplier").Multiplier() :
                defaults.TtlMultiplier ?? host.DefaultTtlMultiplier;
            var ttl = ValidateTtl(interval, multiplier, explicitTtl, signal.Variable, signal.Path + (explicitTtl is null ? ".ttlMultiplier" : ".expiresInMinutes"));
            var query = signal.Child("query").ShapeMap().Element;
            var evaluation = ReadEvaluation(signal.Child("evaluation"));
            var context = signal.OptionalText("context");
            if (context is not null)
            {
                var tokens = Regex.Matches(context, @"\{\{(.*?)\}\}");
                if (tokens.Any(t => !new[] { "value", "state", "healthState", "detail", "observedAt" }.Contains(t.Groups[1].Value, StringComparer.Ordinal)) ||
                    Regex.Replace(context, @"\{\{(.*?)\}\}", "").Contains("{{", StringComparison.Ordinal) ||
                    Regex.Replace(context, @"\{\{(.*?)\}\}", "").Contains("}}", StringComparison.Ordinal))
                    throw signal.Error("context");
            }
            signals.Add(new(signal.Path, entity, signalName, pluginName, query, evaluation, context, interval, ttl));
        }
        if (ExceedsReportBudget(signals, budget)) throw health.Error("reportsPerMinute");
        foreach (var group in signals.GroupBy(s => s.Entity).Where(g => g.Count() > 50))
            warnings.Add(new(json.Variable, "$.signals"));
        return new(json.Variable, name, new(uri, apiVersion, scope, auth, budget), defaults, signals);
    }

    private static bool ExceedsReportBudget(IEnumerable<SignalConfiguration> signals, int budget)
    {
        // Rounded division can reject exact equality; accumulate reduced rational rates instead.
        BigInteger numerator = 0;
        BigInteger denominator = 1;
        foreach (var signal in signals)
        {
            var common = BigInteger.GreatestCommonDivisor(denominator, signal.Interval.Ticks);
            var factor = signal.Interval.Ticks / common;
            numerator = numerator * factor + TimeSpan.TicksPerMinute * (denominator / common);
            denominator *= factor;
            if (numerator > budget * denominator) return true;
            common = BigInteger.GreatestCommonDivisor(numerator, denominator);
            numerator /= common;
            denominator /= common;
        }
        return false;
    }

    private static EvaluationConfiguration ReadEvaluation(StrictJson json)
    {
        json.Shape("unhealthy", "degraded", "stateMap", "noData");
        var noData = json.Has("noData") ? json.Child("noData").EnumValue<HealthState>() : HealthState.Unknown;
        if (json.Has("stateMap"))
        {
            if (json.Has("unhealthy") || json.Has("degraded")) throw json.Error("stateMap");
            var map = json.Child("stateMap").ShapeMap();
            var states = new Dictionary<string, HealthState>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in map.Element.EnumerateObject())
            {
                if (string.IsNullOrWhiteSpace(property.Name) ||
                    !states.TryAdd(property.Name, map.Child(property.Name).EnumValue<HealthState>())) throw map.Error();
            }
            if (states.Count == 0) throw map.Error();
            return new(null, null, states, noData);
        }
        return new(ReadRule(json.Child("unhealthy")), json.Has("degraded") ? ReadRule(json.Child("degraded")) : null, null, noData);
    }

    private static ThresholdRule ReadRule(StrictJson json)
    {
        json.Shape("operator", "threshold");
        return new(json.Child("operator").EnumValue<RuleOperator>(), json.Child("threshold").Number());
    }

    private static int ValidateTtl(TimeSpan interval, double multiplier, int? explicitTtl, string variable, string path)
    {
        try { return Evaluation.TtlCalculator.Calculate(interval, multiplier, explicitTtl); }
        catch (ArgumentOutOfRangeException) { throw ConfigurationException.At(variable, path); }
    }
}
