using System.Text.Json;
using SelfHostedEvaluators.Plugins.Abstractions;
using SelfHostedEvaluators.Plugins.Internal;

namespace SelfHostedEvaluators.Plugins.Static;

/// <summary>Registers the deterministic Static integration for controlled scenarios.</summary>
public sealed class StaticPackage : IPluginPackage
{
    /// <inheritdoc />
    public void Register(IPluginRegistrar registrar) => registrar.SetSinkPlugin<StaticPlugin>();
}

/// <summary>Validates configured static outcomes and creates clients that make no outbound requests.</summary>
public sealed class StaticPlugin : ISinkPlugin
{
    /// <summary>Gets the 100-query batch cap with no consumed HTTP endpoint names.</summary>
    public PluginCapabilities Capabilities { get; } = new(100, []);
    /// <inheritdoc />
    public ValidationResult ValidateSettings(JsonElement settings) =>
        PluginJson.Valid(PluginJson.Shape(settings, "delay") && PluginJson.OptionalDuration(settings, "delay"), "Static settings");

    /// <inheritdoc />
    public ValidationResult ValidateQuery(JsonElement query)
    {
        if (!PluginJson.Shape(query, "kind", "value", "nativeState", "detail", "reason", "code", "message", "retryAfter") ||
            !PluginJson.Text(query, "kind")) return PluginJson.Valid(false, "Static query");
        var valid = query.GetProperty("kind").GetString() switch
        {
            "observation" => PluginJson.Shape(query, "kind", "value", "nativeState", "detail") &&
                (!query.TryGetProperty("value", out var value) || PluginJson.Finite(value, out _)) &&
                PluginJson.OptionalText(query, "nativeState", 128) && PluginJson.OptionalText(query, "detail") &&
                (query.TryGetProperty("value", out _) || query.TryGetProperty("nativeState", out _)),
            "noData" => PluginJson.Shape(query, "kind", "reason") && PluginJson.Text(query, "reason"),
            "failure" => PluginJson.Shape(query, "kind", "code", "message", "retryAfter") &&
                PluginJson.Text(query, "code", 128) && PluginJson.Text(query, "message") && PluginJson.OptionalDuration(query, "retryAfter"),
            _ => false
        };
        return PluginJson.Valid(valid, "Static query");
    }

    /// <inheritdoc />
    public ValueTask<ISinkClient> CreateClientAsync(PluginInstanceContext context, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!ValidateSettings(context.Settings).IsValid) throw new ArgumentException("Invalid Static settings.");
        return ValueTask.FromResult<ISinkClient>(new StaticSinkClient(context));
    }
}
