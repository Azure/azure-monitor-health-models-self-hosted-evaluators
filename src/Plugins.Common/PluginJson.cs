using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using SelfHostedEvaluators.Plugins.Abstractions;

namespace SelfHostedEvaluators.Plugins.Internal;

// Linked into each package; no extra shared assembly or Core dependency.
internal static class PluginJson
{
    internal static bool Shape(JsonElement json, params string[] fields)
    {
        if (json.ValueKind != JsonValueKind.Object) return false;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return json.EnumerateObject().All(p => fields.Contains(p.Name, StringComparer.Ordinal) && seen.Add(p.Name));
    }

    internal static bool Text(JsonElement json, string name, int limit = 4096) =>
        json.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String &&
        value.GetString() is { } text && !string.IsNullOrWhiteSpace(text) && text.Length <= limit && !text.Any(char.IsControl);

    internal static bool OptionalText(JsonElement json, string name, int limit = 4096) =>
        !json.TryGetProperty(name, out _) || Text(json, name, limit);

    internal static bool Duration(JsonElement value, out TimeSpan duration)
    {
        duration = default;
        if (value.ValueKind != JsonValueKind.String) return false;
        var match = Regex.Match(value.GetString()!, @"^([1-9][0-9]{0,5})(s|m|h|d)\z", RegexOptions.CultureInvariant);
        if (!match.Success) return false;
        var seconds = long.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) * (match.Groups[2].Value switch
        {
            "s" => 1L, "m" => 60L, "h" => 3600L, _ => 86400L
        });
        if (seconds > 86400) return false;
        duration = TimeSpan.FromSeconds(seconds);
        return true;
    }

    internal static bool OptionalDuration(JsonElement json, string name) =>
        !json.TryGetProperty(name, out var value) || Duration(value, out _);

    internal static TimeSpan Lookback(JsonElement json, QueryWindow window)
    {
        var result = window.Interval;
        if (json.TryGetProperty("window", out var duration) && !Duration(duration, out result))
            throw new ArgumentException("Invalid window.");
        if (result <= TimeSpan.Zero || result > TimeSpan.FromDays(1)) throw new ArgumentException("Invalid window.");
        return result;
    }

    internal static bool Aggregation(JsonElement json) =>
        !json.TryGetProperty("aggregation", out var aggregation) ||
        aggregation.ValueKind == JsonValueKind.String && aggregation.GetString() is "last" or "mean" or "min" or "max" or "sum";

    internal static string AggregationName(JsonElement json) =>
        json.TryGetProperty("aggregation", out var value) ? value.GetString()! : "last";

    internal static ValidationResult Valid(bool valid, string schema) =>
        valid ? ValidationResult.Success : ValidationResult.Failed($"Invalid {schema} schema.");

    internal static HttpClient Endpoint(PluginInstanceContext context, string name)
    {
        if (!context.HttpClients.TryGetValue(name, out var client) ||
            client.BaseAddress is not { Scheme: "https", AbsolutePath: "/" } uri ||
            !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new ArgumentException($"A root HTTPS '{name}' endpoint is required.");
        return client;
    }

    internal static SignalResult.Failure Malformed(SignalKey key) =>
        new(key, "invalid_response", "The sink returned an invalid response.");

    internal static SignalResult.Failure? HttpFailure(HttpResponseMessage response, SignalKey key, TimeProvider time)
    {
        if (response.IsSuccessStatusCode) return null;
        TimeSpan? retry = null;
        var header = response.Headers.RetryAfter;
        if (header?.Delta is { } delta) retry = delta;
        else if (header?.Date is { } date) retry = date - time.GetUtcNow();
        if (retry < TimeSpan.Zero) retry = TimeSpan.Zero;
        return new(key, $"http_{(int)response.StatusCode}", "The sink rejected the request.", retry);
    }

    internal static bool Finite(JsonElement value, out double number)
    {
        number = default;
        return value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out number) && double.IsFinite(number);
    }
}

internal sealed class SampleAccumulator(DateTimeOffset start, DateTimeOffset end)
{
    private readonly List<(DateTimeOffset Time, double Value)> samples = [];

    internal void Add(JsonElement timestamp, JsonElement value)
    {
        if (timestamp.ValueKind != JsonValueKind.Number || !timestamp.TryGetDecimal(out var numericTime) ||
            numericTime != decimal.Truncate(numericTime) || numericTime < long.MinValue || numericTime > long.MaxValue)
            throw new JsonException();
        var milliseconds = (long)numericTime;
        var time = DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
        if (value.ValueKind == JsonValueKind.Null) return;
        if (!PluginJson.Finite(value, out var number)) throw new JsonException();
        if (time >= start && time <= end) samples.Add((time, number));
    }

    internal SignalResult Result(SignalKey key, string aggregation)
    {
        if (samples.Count == 0) return new SignalResult.NoData(key, "No numeric samples in the requested window.");
        // Sorting also makes ties independent of response/series order.
        var ordered = samples.OrderBy(p => p.Time).ThenBy(p => p.Value).ToArray();
        var latest = ordered[^1].Time;
        var value = aggregation switch
        {
            "last" => ordered[^1].Value,
            "min" => ordered.Min(p => p.Value),
            "max" => ordered.Max(p => p.Value),
            "sum" => ordered.Sum(p => p.Value),
            "mean" => ordered.Sum(p => p.Value / ordered.Length),
            _ => throw new ArgumentException("Invalid aggregation.")
        };
        return double.IsFinite(value) ? new SignalResult.Observation(key, value, null, latest) : PluginJson.Malformed(key);
    }
}
