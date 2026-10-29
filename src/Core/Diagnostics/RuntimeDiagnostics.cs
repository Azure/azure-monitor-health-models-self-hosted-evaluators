using System.Diagnostics.Metrics;

namespace SelfHostedEvaluators.Core.Diagnostics;

/// <summary>Owns the meter and instruments for host startup, polling, evaluation and report delivery.</summary>
public sealed class RuntimeDiagnostics : IDisposable
{
    /// <summary>The meter name used to subscribe to evaluator runtime metrics.</summary>
    public const string MeterName = "SelfHostedEvaluators";
    /// <summary>Gets the process-wide default diagnostics instance.</summary>
    public static RuntimeDiagnostics Shared { get; } = new();
    /// <summary>Gets the owned meter used to publish runtime instruments.</summary>
    public Meter Meter { get; } = new(MeterName, "1.0.0");
    private readonly Counter<long> ticks;
    private readonly Counter<long> skipped;
    private readonly Histogram<double> lag;
    private readonly Counter<long> batches;
    private readonly Counter<long> hangs;
    private readonly Counter<long> quarantines;
    private readonly UpDownCounter<long> inflight;
    private readonly Histogram<double> pollDuration;
    private readonly Histogram<double> underlyingDuration;
    private readonly Counter<long> evaluations;
    private readonly Histogram<double> evaluationDuration;
    private readonly Counter<long> callbackFailures;
    private readonly Counter<long> publishes;
    private readonly Counter<long> superseded;
    private readonly Counter<long> dropped;
    private readonly Histogram<double> rateLimitWait;
    private readonly Counter<long> checkWarnings;
    private readonly Counter<long> configLoads;
    private readonly Counter<long> packageLoads;
    private readonly Counter<long> hostStarts;
    private readonly ObservableGauge<long> activeSignals;
    private long signalCount;

    /// <summary>Creates runtime counters, duration histograms and the active-signal gauge.</summary>
    public RuntimeDiagnostics()
    {
        ticks = Meter.CreateCounter<long>("evaluator.ticks");
        skipped = Meter.CreateCounter<long>("evaluator.ticks.skipped");
        lag = Meter.CreateHistogram<double>("evaluator.schedule.lag", "ms");
        callbackFailures = Meter.CreateCounter<long>("evaluator.schedule.callback_failures");
        batches = Meter.CreateCounter<long>("evaluator.polls");
        hangs = Meter.CreateCounter<long>("evaluator.poll.hangs");
        quarantines = Meter.CreateCounter<long>("evaluator.poll.quarantines");
        inflight = Meter.CreateUpDownCounter<long>("evaluator.poll.inflight");
        pollDuration = Meter.CreateHistogram<double>("evaluator.poll.duration", "ms");
        underlyingDuration = Meter.CreateHistogram<double>("evaluator.poll.underlying_duration", "ms");
        evaluations = Meter.CreateCounter<long>("evaluator.evaluations");
        evaluationDuration = Meter.CreateHistogram<double>("evaluator.evaluation.duration", "ms");
        publishes = Meter.CreateCounter<long>("evaluator.publishes");
        superseded = Meter.CreateCounter<long>("evaluator.reports.superseded");
        dropped = Meter.CreateCounter<long>("evaluator.reports.dropped");
        rateLimitWait = Meter.CreateHistogram<double>("evaluator.ratelimit.wait", "ms");
        checkWarnings = Meter.CreateCounter<long>("evaluator.checks.warnings");
        configLoads = Meter.CreateCounter<long>("evaluator.config.loads");
        packageLoads = Meter.CreateCounter<long>("evaluator.package.loads");
        hostStarts = Meter.CreateCounter<long>("evaluator.host.starts");
        activeSignals = Meter.CreateObservableGauge("evaluator.signals.active", () => Volatile.Read(ref signalCount));
    }

    /// <summary>Records whether a complete configuration snapshot was accepted.</summary>
    /// <param name="accepted">True for a successful load, false for rejection.</param>
    public void ConfigLoaded(bool accepted) => configLoads.Add(1, Tag("outcome", accepted ? "success" : "failure"));
    /// <summary>Records a package load outcome by package identifier.</summary>
    /// <param name="package">The package identifier, without configuration or credential values.</param>
    /// <param name="accepted">Whether loading succeeded.</param>
    public void PackageLoaded(string package, bool accepted) =>
        packageLoads.Add(1, new("package", package), new("outcome", accepted ? "success" : "failure"));
    /// <summary>Records completion of runtime host initialization.</summary>
    public void HostStarted() => hostStarts.Add(1);
    /// <summary>Sets the number of signals represented by the active startup snapshot.</summary>
    /// <param name="count">The active signal count.</param>
    public void SetActiveSignals(long count) => Interlocked.Exchange(ref signalCount, count);
    internal void Tick(string plugin, TimeSpan lateness)
    {
        ticks.Add(1, Tag("plugin", plugin));
        lag.Record(Math.Max(0, lateness.TotalMilliseconds), Tag("plugin", plugin));
    }
    internal void Skip(string plugin, long count) => skipped.Add(count, Tag("plugin", plugin));
    internal void CallbackFailed(string plugin) => callbackFailures.Add(1, Tag("plugin", plugin));
    internal void Hang(string plugin) => hangs.Add(1, Tag("plugin", plugin));
    internal void Quarantine(string plugin) => quarantines.Add(1, Tag("plugin", plugin));
    internal void Inflight(string plugin, long delta) => inflight.Add(delta, Tag("plugin", plugin));
    internal void UnderlyingFinished(string plugin, TimeSpan duration) =>
        underlyingDuration.Record(duration.TotalMilliseconds, Tag("plugin", plugin));
    internal void PollFinished(string plugin, string outcome, TimeSpan duration)
    {
        batches.Add(1, new("plugin", plugin), new("outcome", outcome));
        pollDuration.Record(duration.TotalMilliseconds, new("plugin", plugin), new("outcome", outcome));
    }
    internal void Evaluated(string state, TimeSpan duration)
    {
        evaluations.Add(1, Tag("state", state));
        evaluationDuration.Record(duration.TotalMilliseconds, Tag("state", state));
    }
    internal void Published(string model, string status) =>
        publishes.Add(1, new("model", model), new("status", status));
    internal void Superseded(string model) => superseded.Add(1, Tag("model", model));
    internal void Dropped(string model, string outcome) =>
        dropped.Add(1, new("model", model), new("outcome", outcome));
    internal void RateLimitWait(string model, TimeSpan duration) =>
        rateLimitWait.Record(duration.TotalMilliseconds, Tag("model", model));
    internal void SinkRateLimitWait(string plugin, TimeSpan duration) =>
        rateLimitWait.Record(duration.TotalMilliseconds, Tag("plugin", plugin));
    internal void CheckWarning(string model, string outcome) =>
        checkWarnings.Add(1, new("model", model), new("outcome", outcome));
    private static KeyValuePair<string, object?> Tag(string name, string value) => new(name, value);
    /// <summary>Disposes the owned meter and stops publishing its instruments.</summary>
    public void Dispose() => Meter.Dispose();
}
