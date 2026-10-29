using SelfHostedEvaluators.Plugins.Abstractions;
using SelfHostedEvaluators.Plugins.Internal;

namespace SelfHostedEvaluators.Plugins.Static;

/// <summary>Produces configured observations, no-data results or failures without contacting an external source.</summary>
public sealed class StaticSinkClient : SingleQueryClientBase
{
    private readonly TimeSpan delay;
    private readonly TimeProvider time;
    /// <summary>Creates a static client with an optional cancellable delay.</summary>
    /// <param name="context">The validated Static settings, batch size and injected clock.</param>
    public StaticSinkClient(PluginInstanceContext context) : base(Math.Min(context.EffectiveBatchSize, 8))
    {
        time = context.TimeProvider;
        if (!new StaticPlugin().ValidateSettings(context.Settings).IsValid) throw new ArgumentException("Invalid Static settings.");
        if (context.Settings.TryGetProperty("delay", out var configured)) PluginJson.Duration(configured, out delay);
    }

    /// <inheritdoc />
    protected override async Task<SignalResult> QueryAsync(SignalQuery query, QueryWindow window, CancellationToken ct)
    {
        if (!new StaticPlugin().ValidateQuery(query.Query).IsValid)
            return new SignalResult.Failure(query.Key, "invalid_query", "Invalid Static query schema.");
        if (delay > TimeSpan.Zero) await Task.Delay(delay, time, ct).ConfigureAwait(false);
        var json = query.Query;
        if (json.GetProperty("kind").GetString() == "observation")
            return new SignalResult.Observation(query.Key, json.TryGetProperty("value", out var value) ? value.GetDouble() : null,
                json.TryGetProperty("nativeState", out var state) ? state.GetString() : null, window.ScheduledAt,
                json.TryGetProperty("detail", out var detail) ? detail.GetString() : null);
        if (json.GetProperty("kind").GetString() == "noData")
            return new SignalResult.NoData(query.Key, json.GetProperty("reason").GetString()!);
        TimeSpan? retry = null;
        if (json.TryGetProperty("retryAfter", out var retryJson) && PluginJson.Duration(retryJson, out var duration)) retry = duration;
        return new SignalResult.Failure(query.Key, json.GetProperty("code").GetString()!, json.GetProperty("message").GetString()!, retry);
    }
}
