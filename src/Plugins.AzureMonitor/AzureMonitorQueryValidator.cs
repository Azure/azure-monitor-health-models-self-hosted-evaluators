using System.Text.Json;
using SelfHostedEvaluators.Plugins.Abstractions;
using SelfHostedEvaluators.Plugins.Internal;

namespace SelfHostedEvaluators.Plugins.AzureMonitor;

/// <summary>Validates the closed schema for an instant PromQL expression.</summary>
public static class AzureMonitorQueryValidator
{
    /// <summary>Checks that a query contains only a nonempty, bounded PromQL expression.</summary>
    /// <param name="query">The sink-specific signal query object.</param>
    /// <returns>Success or a redacted schema error without echoing the expression.</returns>
    public static ValidationResult Validate(JsonElement query) =>
        PluginJson.Valid(PluginJson.Shape(query, "promql") && PluginJson.Text(query, "promql"), "Azure Monitor query");
}
