using System.Text.Json;
using SelfHostedEvaluators.Plugins.Abstractions;
using SelfHostedEvaluators.Plugins.Internal;

namespace SelfHostedEvaluators.Plugins.Datadog;

/// <summary>Validates the closed schemas for Datadog metric expressions and monitor identifiers.</summary>
public static class DatadogQueryValidator
{
    /// <summary>Checks that a query selects exactly one supported metric or monitor form.</summary>
    /// <param name="query">The sink-specific signal query object.</param>
    /// <returns>Success or a redacted schema error without echoing the query.</returns>
    public static ValidationResult Validate(JsonElement query)
    {
        var monitor = PluginJson.Shape(query, "monitorId") &&
            query.TryGetProperty("monitorId", out var id) && id.ValueKind == JsonValueKind.Number &&
            id.TryGetInt64(out var number) && number > 0;
        var metric = PluginJson.Shape(query, "metric", "window", "aggregation") &&
            PluginJson.Text(query, "metric") && PluginJson.OptionalDuration(query, "window") && PluginJson.Aggregation(query);
        return PluginJson.Valid(monitor || metric, "Datadog query");
    }
}
