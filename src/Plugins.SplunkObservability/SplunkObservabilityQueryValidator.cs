using System.Text.Json;
using System.Text.RegularExpressions;
using SelfHostedEvaluators.Plugins.Abstractions;
using SelfHostedEvaluators.Plugins.Internal;

namespace SelfHostedEvaluators.Plugins.SplunkObservability;

/// <summary>Validates the closed schemas for SignalFlow metric selectors and detector incident queries.</summary>
public static class SplunkObservabilityQueryValidator
{
    /// <summary>Checks the supported metric or detector form, including optional durations and aggregation.</summary>
    /// <param name="query">The sink-specific signal query object.</param>
    /// <returns>Success or a redacted schema error without echoing the query.</returns>
    public static ValidationResult Validate(JsonElement query)
    {
        var detector = PluginJson.Shape(query, "detectorId", "detectLabel") && PluginJson.Text(query, "detectorId", 256) &&
            Regex.IsMatch(query.GetProperty("detectorId").GetString()!, @"^[a-zA-Z0-9_-]+\z", RegexOptions.CultureInvariant) &&
            PluginJson.OptionalText(query, "detectLabel", 256);
        var metric = PluginJson.Shape(query, "metric", "window", "resolution", "aggregation") && PluginJson.Text(query, "metric") &&
            PluginJson.OptionalDuration(query, "window") && PluginJson.OptionalDuration(query, "resolution") && PluginJson.Aggregation(query);
        return PluginJson.Valid(detector || metric, "Splunk Observability query");
    }
}
