using System.Collections.Immutable;
using SelfHostedEvaluators.Plugins.Abstractions;

namespace SelfHostedEvaluators.Core.Polling;

/// <summary>Holds an immutable, nonempty set of uniquely keyed queries for one plugin tick.</summary>
public sealed record ScheduledBatch
{
    /// <summary>Creates a batch after validating the plugin, interval and uniqueness of full signal keys.</summary>
    /// <param name="pluginName">The configured plugin instance to execute.</param>
    /// <param name="window">The scheduled anchor and positive polling interval.</param>
    /// <param name="queries">Nonempty queries with distinct full model/entity/name keys.</param>
    public ScheduledBatch(string pluginName, QueryWindow window, IReadOnlyList<SignalQuery> queries)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginName);
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(queries);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(window.Interval, TimeSpan.Zero);
        if (queries.Count == 0 || queries.Any(q => q is null || q.Key is null) ||
            queries.Select(q => q.Key).Distinct().Count() != queries.Count)
            throw new ArgumentException("A batch requires unique full signal keys.", nameof(queries));
        PluginName = pluginName;
        Window = window;
        Queries = queries.ToImmutableArray();
    }

    /// <summary>Gets the configured plugin instance name.</summary>
    public string PluginName { get; }
    /// <summary>Gets the scheduled query-window anchor and interval.</summary>
    public QueryWindow Window { get; }
    /// <summary>Gets the copied query list in execution order.</summary>
    public IReadOnlyList<SignalQuery> Queries { get; }
}
