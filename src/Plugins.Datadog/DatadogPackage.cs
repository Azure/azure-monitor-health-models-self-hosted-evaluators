using System.Text.Json;
using SelfHostedEvaluators.Plugins.Abstractions;
using SelfHostedEvaluators.Plugins.Internal;

namespace SelfHostedEvaluators.Plugins.Datadog;

/// <summary>Registers the bundled Datadog integration.</summary>
public sealed class DatadogPackage : IPluginPackage
{
    /// <inheritdoc />
    public void Register(IPluginRegistrar registrar) => registrar.SetSinkPlugin<DatadogPlugin>();
}

/// <summary>Validates Datadog metric and monitor queries and creates independently configured clients.</summary>
public sealed class DatadogPlugin : ISinkPlugin
{
    /// <summary>Gets the 50-query batch cap and required default endpoint.</summary>
    public PluginCapabilities Capabilities { get; } = new(50, ["default"]);
    /// <inheritdoc />
    public ValidationResult ValidateSettings(JsonElement settings) => PluginJson.Valid(PluginJson.Shape(settings), "Datadog settings");
    /// <inheritdoc />
    public ValidationResult ValidateQuery(JsonElement query) => DatadogQueryValidator.Validate(query);
    /// <inheritdoc />
    public ValueTask<ISinkClient> CreateClientAsync(PluginInstanceContext context, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!ValidateSettings(context.Settings).IsValid) throw new ArgumentException("Invalid Datadog settings.");
        return ValueTask.FromResult<ISinkClient>(new DatadogSinkClient(context));
    }
}
