using System.Text.Json;

namespace SelfHostedEvaluators.Plugins.Abstractions;

/// <summary>Identifies a signal across models and entities; the signal name alone is not unique.</summary>
/// <param name="ModelUri">The target health model's data-plane URI.</param>
/// <param name="Entity">The existing model entity name.</param>
/// <param name="Name">The signal name within that entity.</param>
public sealed record SignalKey(Uri ModelUri, string Entity, string Name);

/// <summary>Pairs a full signal identity with a cloned, sink-specific query.</summary>
public sealed record SignalQuery
{
    /// <summary>Creates a query that outlives the source JSON document.</summary>
    /// <param name="key">The complete target signal identity.</param>
    /// <param name="query">The validated query object to clone.</param>
    public SignalQuery(SignalKey key, JsonElement query)
    {
        ArgumentNullException.ThrowIfNull(key);
        Key = key;
        Query = query.Clone();
    }

    /// <summary>Gets the complete identity to preserve in the query result.</summary>
    public SignalKey Key { get; }
    /// <summary>Gets the query clone interpreted by the selected sink.</summary>
    public JsonElement Query { get; }
}

/// <summary>Describes a scheduled poll, independently of when the outbound request actually starts.</summary>
/// <param name="ScheduledAt">The scheduled tick used as the query-window anchor.</param>
/// <param name="Interval">The effective polling interval; the sink chooses its lookback policy.</param>
public sealed record QueryWindow(DateTimeOffset ScheduledAt, TimeSpan Interval);

/// <summary>Represents an observation, missing data or a query failure for one complete signal key.</summary>
/// <param name="Key">The identity of the input query.</param>
public abstract record SignalResult(SignalKey Key)
{
    /// <summary>Carries a numeric value, vendor-native state or both for evaluation.</summary>
    /// <param name="Key">The identity of the input query.</param>
    /// <param name="Value">A finite numeric sample, or null when unavailable.</param>
    /// <param name="NativeState">A vendor state for the configured state map, or null.</param>
    /// <param name="ObservedAt">The observation timestamp, not necessarily the request time.</param>
    /// <param name="Detail">Optional redacted context; do not include raw vendor payloads or credentials.</param>
    public sealed record Observation(
        SignalKey Key,
        double? Value,
        string? NativeState,
        DateTimeOffset ObservedAt,
        string? Detail = null) : SignalResult(Key);

    /// <summary>Indicates that the query succeeded without a usable observation.</summary>
    /// <param name="Key">The identity of the input query.</param>
    /// <param name="Reason">A redacted explanation of the missing data.</param>
    public sealed record NoData(SignalKey Key, string Reason) : SignalResult(Key);

    /// <summary>Indicates a query failure, which the engine evaluates as Unknown.</summary>
    /// <param name="Key">The identity of the input query.</param>
    /// <param name="Code">A stable failure category suitable for diagnostics.</param>
    /// <param name="Message">A redacted explanation without exception or credential payloads.</param>
    /// <param name="RetryAfter">An optional vendor-requested delay before another poll.</param>
    public sealed record Failure(
        SignalKey Key,
        string Code,
        string Message,
        TimeSpan? RetryAfter = null) : SignalResult(Key);
}
