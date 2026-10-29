using System.Globalization;
using System.Text.Json;
using SelfHostedEvaluators.Plugins.Abstractions;
using SelfHostedEvaluators.Plugins.Internal;

namespace SelfHostedEvaluators.Plugins.AzureMonitor;

/// <summary>Executes instant PromQL queries through a host-authenticated Azure Monitor workspace endpoint.</summary>
public sealed class AzureMonitorSinkClient : SingleQueryClientBase
{
    private readonly HttpClient http;
    private readonly TimeProvider time;

    /// <summary>Creates a client with at most eight concurrent instant queries per batch.</summary>
    /// <param name="context">The host-owned default HTTP client and observation clock; neither is disposed by this client.</param>
    public AzureMonitorSinkClient(PluginInstanceContext context) : base(Math.Min(context.EffectiveBatchSize, 8))
    {
        http = PluginJson.Endpoint(context, "default");
        time = context.TimeProvider;
    }

    /// <inheritdoc />
    protected override async Task<SignalResult> QueryAsync(SignalQuery query, QueryWindow window, CancellationToken ct)
    {
        if (!AzureMonitorQueryValidator.Validate(query.Query).IsValid)
            return new SignalResult.Failure(query.Key, "invalid_query", "Invalid Azure Monitor query schema.");

        var seconds = (window.ScheduledAt - DateTimeOffset.UnixEpoch).Ticks / (decimal)TimeSpan.TicksPerSecond;
        using var content = new FormUrlEncodedContent(
        [
            new("query", query.Query.GetProperty("promql").GetString()!),
            new("time", seconds.ToString("0.#######", CultureInfo.InvariantCulture))
        ]);
        using var response = await http.PostAsync("api/v1/query", content, ct).ConfigureAwait(false);
        if (PluginJson.HttpFailure(response, query.Key, time) is { } failure) return failure;

        try
        {
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            var root = document.RootElement;
            if (root.GetProperty("status").GetString() != "success") return PluginJson.Malformed(query.Key);
            foreach (var field in new[] { "warnings", "infos" })
                if (root.TryGetProperty(field, out var annotations) && annotations.GetArrayLength() != 0)
                    return new SignalResult.Failure(query.Key, "query_annotation",
                        "The query returned annotations; potentially incomplete results cannot be evaluated.");

            var data = root.GetProperty("data");
            var result = data.GetProperty("result");
            JsonElement sample;
            switch (data.GetProperty("resultType").GetString())
            {
                case "scalar":
                    sample = result;
                    break;
                case "vector":
                    var count = result.GetArrayLength();
                    if (count == 0) return new SignalResult.NoData(query.Key, "The PromQL query returned no series.");
                    if (count != 1)
                        return new SignalResult.Failure(query.Key, "multiple_series",
                            "The query returned multiple series. Aggregate or select a single series in PromQL.");
                    var series = result[0];
                    var metric = series.GetProperty("metric");
                    if (metric.ValueKind != JsonValueKind.Object ||
                        metric.EnumerateObject().Any(label => label.Value.ValueKind != JsonValueKind.String) ||
                        series.TryGetProperty("histogram", out _))
                        return PluginJson.Malformed(query.Key);
                    sample = series.GetProperty("value");
                    break;
                default:
                    return PluginJson.Malformed(query.Key);
            }

            if (sample.GetArrayLength() != 2 ||
                sample[0].ValueKind != JsonValueKind.Number || !sample[0].TryGetDecimal(out var timestamp) ||
                sample[1].ValueKind != JsonValueKind.String ||
                !double.TryParse(sample[1].GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ||
                !double.IsFinite(value))
                return PluginJson.Malformed(query.Key);

            var observedAt = DateTimeOffset.UnixEpoch.AddTicks((long)(timestamp * TimeSpan.TicksPerSecond));
            return observedAt <= window.ScheduledAt
                ? new SignalResult.Observation(query.Key, value, null, observedAt)
                : PluginJson.Malformed(query.Key);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or
            FormatException or ArgumentOutOfRangeException or OverflowException)
        {
            return PluginJson.Malformed(query.Key);
        }
    }
}
