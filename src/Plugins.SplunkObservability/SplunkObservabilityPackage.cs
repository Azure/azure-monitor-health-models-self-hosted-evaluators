using System.Text.Json;
using SelfHostedEvaluators.Plugins.Abstractions;
using SelfHostedEvaluators.Plugins.Internal;

namespace SelfHostedEvaluators.Plugins.SplunkObservability;

/// <summary>Registers the bundled Splunk Observability integration.</summary>
public sealed class SplunkObservabilityPackage : IPluginPackage
{
    /// <inheritdoc />
    public void Register(IPluginRegistrar registrar) => registrar.SetSinkPlugin<SplunkObservabilityPlugin>();
}

/// <summary>Validates SignalFlow metric and detector queries and creates Splunk Observability clients.</summary>
public sealed class SplunkObservabilityPlugin : ISinkPlugin
{
    /// <summary>Gets the 50-query batch cap and required API and stream endpoints.</summary>
    public PluginCapabilities Capabilities { get; } = new(50, ["api", "stream"]);
    /// <inheritdoc />
    public ValidationResult ValidateSettings(JsonElement settings) => PluginJson.Valid(PluginJson.Shape(settings), "Splunk Observability settings");
    /// <inheritdoc />
    public ValidationResult ValidateQuery(JsonElement query) => SplunkObservabilityQueryValidator.Validate(query);
    /// <inheritdoc />
    public ValueTask<ISinkClient> CreateClientAsync(PluginInstanceContext context, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!ValidateSettings(context.Settings).IsValid) throw new ArgumentException("Invalid Splunk Observability settings.");
        return ValueTask.FromResult<ISinkClient>(new SplunkObservabilitySinkClient(context));
    }
}
