using System.Globalization;
using System.Text.Json;
using SelfHostedEvaluators.Plugins.Abstractions;
using SelfHostedEvaluators.Plugins.Internal;

namespace SelfHostedEvaluators.Plugins.Datadog;

/// <summary>Queries Datadog metric windows and monitor states through the authenticated default endpoint.</summary>
public sealed class DatadogSinkClient : SingleQueryClientBase
{
    private readonly HttpClient http;
    private readonly TimeProvider time;
    /// <summary>Creates a client with at most eight concurrent single-query calls per batch.</summary>
    /// <param name="context">The host-owned default HTTP client and observation clock.</param>
    public DatadogSinkClient(PluginInstanceContext context) : base(Math.Min(context.EffectiveBatchSize, 8))
    {
        http = PluginJson.Endpoint(context, "default");
        time = context.TimeProvider;
    }

    /// <inheritdoc />
    protected override async Task<SignalResult> QueryAsync(SignalQuery query, QueryWindow window, CancellationToken ct)
    {
        if (!DatadogQueryValidator.Validate(query.Query).IsValid)
            return new SignalResult.Failure(query.Key, "invalid_query", "Invalid Datadog query schema.");
        var monitor = query.Query.TryGetProperty("monitorId", out var id);
        var lookback = monitor ? TimeSpan.Zero : PluginJson.Lookback(query.Query, window);
        var path = monitor
            ? "api/v1/monitor/" + id.GetInt64().ToString(CultureInfo.InvariantCulture)
            : $"api/v1/query?from={(window.ScheduledAt - lookback).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)}" +
              $"&to={window.ScheduledAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)}" +
              $"&query={Uri.EscapeDataString(query.Query.GetProperty("metric").GetString()!)}";
        using var response = await http.GetAsync(path, ct).ConfigureAwait(false);
        if (PluginJson.HttpFailure(response, query.Key, time) is { } failure) return failure;
        try
        {
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            var root = document.RootElement;
            if (monitor)
            {
                if (root.GetProperty("id").GetInt64() != id.GetInt64()) return PluginJson.Malformed(query.Key);
                var state = root.GetProperty("overall_state");
                if (state.ValueKind == JsonValueKind.Null) return new SignalResult.NoData(query.Key, "The monitor returned no state.");
                var text = state.GetString();
                if (text is not ("Alert" or "Ignored" or "No Data" or "OK" or "Skipped" or "Unknown" or "Warn"))
                    return PluginJson.Malformed(query.Key);
                return new SignalResult.Observation(query.Key, null, text, time.GetUtcNow());
            }
            if (root.GetProperty("status").GetString() != "ok") return PluginJson.Malformed(query.Key);
            var accumulator = new SampleAccumulator(window.ScheduledAt - lookback, window.ScheduledAt);
            foreach (var series in root.GetProperty("series").EnumerateArray())
                foreach (var point in series.GetProperty("pointlist").EnumerateArray())
                {
                    if (point.GetArrayLength() != 2) return PluginJson.Malformed(query.Key);
                    accumulator.Add(point[0], point[1]);
                }
            return accumulator.Result(query.Key, PluginJson.AggregationName(query.Query));
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or ArgumentOutOfRangeException)
        {
            return PluginJson.Malformed(query.Key);
        }
    }
}
