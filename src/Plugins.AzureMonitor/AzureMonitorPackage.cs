using System.Text.Json;
using SelfHostedEvaluators.Plugins.Abstractions;
using SelfHostedEvaluators.Plugins.Internal;

namespace SelfHostedEvaluators.Plugins.AzureMonitor;

/// <summary>Registers the Azure Monitor workspace managed Prometheus integration.</summary>
public sealed class AzureMonitorPackage : IPluginPackage
{
    /// <inheritdoc />
    public void Register(IPluginRegistrar registrar) => registrar.SetSinkPlugin<AzureMonitorPlugin>();
}

/// <summary>Validates instant PromQL queries and creates independently configured workspace clients.</summary>
public sealed class AzureMonitorPlugin : ISinkPlugin
{
    /// <summary>Gets the 50-query batch cap and required default workspace endpoint.</summary>
    public PluginCapabilities Capabilities { get; } = new(50, ["default"]);
    /// <inheritdoc />
    public ValidationResult ValidateSettings(JsonElement settings) => PluginJson.Valid(PluginJson.Shape(settings), "Azure Monitor settings");
    /// <inheritdoc />
    public ValidationResult ValidateQuery(JsonElement query) => AzureMonitorQueryValidator.Validate(query);
    /// <inheritdoc />
    public ValueTask<ISinkClient> CreateClientAsync(PluginInstanceContext context, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!ValidateSettings(context.Settings).IsValid) throw new ArgumentException("Invalid Azure Monitor settings.");
        return ValueTask.FromResult<ISinkClient>(new AzureMonitorSinkClient(context));
    }
}
