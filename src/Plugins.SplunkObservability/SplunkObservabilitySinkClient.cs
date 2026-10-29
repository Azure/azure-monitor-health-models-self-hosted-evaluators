using System.Globalization;
using System.Text;
using System.Text.Json;
using SelfHostedEvaluators.Plugins.Abstractions;
using SelfHostedEvaluators.Plugins.Internal;

namespace SelfHostedEvaluators.Plugins.SplunkObservability;

/// <summary>Reads finite SignalFlow metric streams and paginated active-detector incident snapshots.</summary>
public sealed class SplunkObservabilitySinkClient : SingleQueryClientBase
{
    private readonly HttpClient api;
    private readonly HttpClient stream;
    private readonly TimeProvider time;

    /// <summary>Creates a client using the host-authenticated API and stream endpoints.</summary>
    /// <param name="context">The named transports, batch limit and observation clock.</param>
    public SplunkObservabilitySinkClient(PluginInstanceContext context) : base(Math.Min(context.EffectiveBatchSize, 8))
    {
        api = PluginJson.Endpoint(context, "api");
        stream = PluginJson.Endpoint(context, "stream");
        time = context.TimeProvider;
    }

    /// <inheritdoc />
    protected override async Task<SignalResult> QueryAsync(SignalQuery query, QueryWindow window, CancellationToken ct)
    {
        if (!SplunkObservabilityQueryValidator.Validate(query.Query).IsValid)
            return new SignalResult.Failure(query.Key, "invalid_query", "Invalid Splunk Observability query schema.");
        try
        {
            return query.Query.TryGetProperty("detectorId", out var id)
                ? await DetectorAsync(query, id.GetString()!, ct).ConfigureAwait(false)
                : await MetricAsync(query, window, ct).ConfigureAwait(false);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or ArgumentOutOfRangeException)
        {
            return PluginJson.Malformed(query.Key);
        }
    }

    private async Task<SignalResult> DetectorAsync(SignalQuery query, string id, CancellationToken ct)
    {
        var states = new List<(string State, DateTimeOffset At)>();
        var label = query.Query.TryGetProperty("detectLabel", out var configured) ? configured.GetString() : null;
        const int limit = 100;
        for (var offset = 0; offset < 10000; offset += limit)
        {
            using var response = await api.GetAsync($"v2/detector/{Uri.EscapeDataString(id)}/incidents?offset={offset}&limit={limit}", ct).ConfigureAwait(false);
            if (PluginJson.HttpFailure(response, query.Key, time) is { } failure) return failure;
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            var root = document.RootElement;
            foreach (var incident in root.EnumerateArray())
            {
                if (incident.GetProperty("detectorId").GetString() != id) return PluginJson.Malformed(query.Key);
                var state = incident.GetProperty("anomalyState").GetString();
                if (state is not ("ANOMALOUS" or "OK" or "MANUALLY_RESOLVED" or "STOPPED")) return PluginJson.Malformed(query.Key);
                var active = incident.GetProperty("active").GetBoolean();
                if (!active || label is not null && incident.GetProperty("detectLabel").GetString() != label) continue;
                DateTimeOffset? at = null;
                foreach (var entry in incident.GetProperty("events").EnumerateArray())
                {
                    var timestamp = DateTimeOffset.FromUnixTimeMilliseconds(entry.GetProperty("timestamp").GetInt64());
                    if (at is null || timestamp > at) at = timestamp;
                }
                states.Add((state, at ?? time.GetUtcNow()));
            }
            if (root.GetArrayLength() < limit)
            {
                if (states.Count == 0) return new SignalResult.NoData(query.Key, "No matching active detector incidents.");
                // Never infer an OK state from an empty list or hide mixed native states.
                if (states.Select(s => s.State).Distinct(StringComparer.Ordinal).Count() > 1)
                    return new SignalResult.Failure(query.Key, "ambiguous_state", "Matching incidents have different native states; select a detectLabel.");
                return new SignalResult.Observation(query.Key, null, states[0].State, states.Max(s => s.At));
            }
        }
        return new SignalResult.Failure(query.Key, "response_limit", "Detector incident pagination exceeded the supported bound.");
    }

    private async Task<SignalResult> MetricAsync(SignalQuery query, QueryWindow window, CancellationToken ct)
    {
        var start = window.ScheduledAt - PluginJson.Lookback(query.Query, window);
        var path = $"v2/signalflow/execute?start={start.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)}" +
            $"&stop={window.ScheduledAt.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)}";
        if (query.Query.TryGetProperty("resolution", out var resolution))
        {
            PluginJson.Duration(resolution, out var duration);
            path += $"&resolution={((long)duration.TotalMilliseconds).ToString(CultureInfo.InvariantCulture)}";
        }
        var metric = query.Query.GetProperty("metric").GetString()!.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("'", "\\'", StringComparison.Ordinal);
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent($"data('{metric}').publish()", Encoding.UTF8, "text/plain")
        };
        request.Headers.Accept.ParseAdd("text/event-stream");
        using var response = await stream.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (PluginJson.HttpFailure(response, query.Key, time) is { } failure) return failure;
        if (response.Content.Headers.ContentType?.MediaType != "text/event-stream") return PluginJson.Malformed(query.Key);
        await using var content = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(content);
        var accumulator = new SampleAccumulator(start, window.ScheduledAt);
        var eventName = "";
        var payload = new StringBuilder();
        var characters = 0;
        while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
        {
            characters += line.Length;
            if (characters > 8 * 1024 * 1024) return new SignalResult.Failure(query.Key, "response_limit", "SignalFlow stream exceeded the supported bound.");
            if (line.Length == 0)
            {
                if (payload.Length > 0)
                {
                    using var document = JsonDocument.Parse(payload.ToString());
                    var root = document.RootElement;
                    if (eventName == "error") return new SignalResult.Failure(query.Key, "stream_error", "SignalFlow reported a computation error.");
                    if (eventName == "control-message")
                    {
                        var control = root.GetProperty("event").GetString();
                        if (control == "CHANNEL_ABORT") return new SignalResult.Failure(query.Key, "stream_aborted", "SignalFlow aborted the computation.");
                        _ = DateTimeOffset.FromUnixTimeMilliseconds(root.GetProperty("timestampMs").GetInt64());
                        if (control == "END_OF_CHANNEL") return accumulator.Result(query.Key, PluginJson.AggregationName(query.Query));
                        if (control is not ("STREAM_START" or "JOB_START" or "JOB_PROGRESS")) return PluginJson.Malformed(query.Key);
                    }
                    else if (eventName == "data")
                    {
                        var timestamp = root.GetProperty("logicalTimestampMs");
                        _ = DateTimeOffset.FromUnixTimeMilliseconds(timestamp.GetInt64());
                        foreach (var datum in root.GetProperty("data").EnumerateArray())
                        {
                            if (!PluginJson.Text(datum, "tsId", 256)) return PluginJson.Malformed(query.Key);
                            accumulator.Add(timestamp, datum.GetProperty("value"));
                        }
                    }
                    else if (!ValidIgnoredEnvelope(eventName, root))
                        return PluginJson.Malformed(query.Key);
                }
                eventName = "";
                payload.Clear();
            }
            else if (line.StartsWith("event:", StringComparison.Ordinal)) eventName = line[6..].TrimStart(' ');
            else if (line.StartsWith("data:", StringComparison.Ordinal)) payload.Append(line[5..].TrimStart(' ')).Append('\n');
        }
        return new SignalResult.Failure(query.Key, "incomplete_stream", "SignalFlow closed before END_OF_CHANNEL.");
    }

    private static bool ValidIgnoredEnvelope(string kind, JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return false;
        if (kind == "message")
        {
            _ = DateTimeOffset.FromUnixTimeMilliseconds(root.GetProperty("logicalTimestampMs").GetInt64());
            return root.GetProperty("message").ValueKind == JsonValueKind.Object;
        }
        if (kind is not ("metadata" or "event" or "expired-tsid") || !PluginJson.Text(root, "tsId", 256))
            return false;
        if (kind == "expired-tsid") return true;
        if (root.GetProperty("properties").ValueKind != JsonValueKind.Object) return false;
        if (kind == "metadata") return true;
        _ = DateTimeOffset.FromUnixTimeMilliseconds(root.GetProperty("timestampMs").GetInt64());
        return root.GetProperty("metadata").ValueKind == JsonValueKind.Object;
    }
}
