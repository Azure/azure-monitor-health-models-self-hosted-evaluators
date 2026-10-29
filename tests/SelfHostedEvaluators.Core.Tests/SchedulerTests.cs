using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Threading.Channels;
using SelfHostedEvaluators.Core.Diagnostics;
using SelfHostedEvaluators.Core.Polling;

namespace SelfHostedEvaluators.Core.Tests;

public class SchedulerTests
{
    [TestCase(false)]
    [TestCase(true)]
    public async Task StartupGroupsAcrossModelsAndSplitsAtBatchCap(bool smallBatchFirst)
    {
        var time = PollingTestSupport.Clock();
        var config = PollingTestSupport.Snapshot(signals: 2, batchSize: 3, secondModel: true);
        var batches = Channel.CreateUnbounded<ScheduledBatch>();
        await using var scheduler = new Scheduler(time);
        using var stop = new CancellationTokenSource();
        var smallWritten = PollingTestSupport.Gate();
        var run = scheduler.RunAsync(config, async (batch, _) =>
        {
            if (smallBatchFirst && batch.Queries.Count == 3) await smallWritten.Task;
            batches.Writer.TryWrite(batch);
            if (batch.Queries.Count == 1) smallWritten.TrySetResult();
        }, stop.Token);
        try
        {
            var captured = new[]
            {
                await batches.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)),
                await batches.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5))
            };
            Assert.That(captured.Select(b => b.Queries.Count), Is.EquivalentTo(new[] { 3, 1 }));
            var expected = config.Models.SelectMany(m => m.Signals.Select(s =>
                new SelfHostedEvaluators.Plugins.Abstractions.SignalKey(m.HealthModel.DataPlaneUri, s.Entity, s.Name)));
            Assert.That(captured.SelectMany(b => b.Queries).Select(q => q.Key), Is.EquivalentTo(expected));
            Assert.That(captured.All(b => b.Window.ScheduledAt == PollingTestSupport.Start &&
                b.Window.Interval == TimeSpan.FromSeconds(10) && b.PluginName == "sink-one"), Is.True);
            Assert.That(captured.Single(b => b.Queries.Count == 3).Queries.Select(q => q.Key.ModelUri).Distinct().Count(), Is.EqualTo(2));
        }
        finally
        {
            smallWritten.TrySetResult();
            stop.Cancel();
            await run.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Test]
    public async Task OffsetsAreStableAlignedAndNotRelativeToStartup()
    {
        async Task<DateTimeOffset[]> Capture(DateTimeOffset start)
        {
            var time = PollingTestSupport.Clock();
            time.SetUtcNow(start);
            var batches = Channel.CreateUnbounded<ScheduledBatch>();
            await using var scheduler = new Scheduler(time);
            using var stop = new CancellationTokenSource();
            var run = scheduler.RunAsync(PollingTestSupport.Snapshot(), (batch, _) =>
            { batches.Writer.TryWrite(batch); return Task.CompletedTask; }, stop.Token);
            await batches.Reader.ReadAsync();
            await scheduler.DrainAsync(default);
            var ticks = new List<DateTimeOffset>();
            for (var i = 0; i < 2; i++)
            {
                time.Advance(TimeSpan.FromSeconds(10));
                ticks.Add((await batches.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5))).Window.ScheduledAt);
                await scheduler.DrainAsync(default);
            }
            stop.Cancel();
            await run;
            return ticks.ToArray();
        }
        var first = await Capture(PollingTestSupport.Start);
        var shifted = await Capture(PollingTestSupport.Start.AddSeconds(3));
        Assert.That(first[1] - first[0], Is.EqualTo(TimeSpan.FromSeconds(10)));
        Assert.That((first[0] - DateTimeOffset.UnixEpoch).Ticks % TimeSpan.FromSeconds(10).Ticks,
            Is.EqualTo((shifted[0] - DateTimeOffset.UnixEpoch).Ticks % TimeSpan.FromSeconds(10).Ticks));
    }

    [Test]
    public async Task OverrunsSkipTicksWithoutOverlapAndShutdownDoesNotWaitForBlockedCallback()
    {
        var time = PollingTestSupport.Clock();
        using var diagnostics = new RuntimeDiagnostics();
        using var metrics = new RuntimeMetricCapture(diagnostics);
        var entered = PollingTestSupport.Gate();
        var release = PollingTestSupport.Gate();
        var calls = 0;
        await using var scheduler = new Scheduler(time, diagnostics);
        using var stop = new CancellationTokenSource();
        var run = scheduler.RunAsync(PollingTestSupport.Snapshot(), async (_, _) =>
        { Interlocked.Increment(ref calls); entered.TrySetResult(); await release.Task; }, stop.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            time.Advance(TimeSpan.FromSeconds(35));
            await PollingTestSupport.Until(() => metrics.Total("evaluator.ticks.skipped") >= 3);
            Assert.That(calls, Is.EqualTo(1));
            stop.Cancel();
            await run.WaitAsync(TimeSpan.FromSeconds(5));
            using var drainStop = new CancellationTokenSource();
            var drain = scheduler.DrainAsync(drainStop.Token);
            Assert.That(drain.IsCompleted, Is.False);
            drainStop.Cancel();
            Assert.CatchAsync<OperationCanceledException>(async () => await drain);
        }
        finally { stop.Cancel(); release.TrySetResult(); }
        await scheduler.DrainAsync(default);
    }

    [Test]
    public async Task SynchronouslyBlockingCallbackCannotBlockOtherGroupsOrStop()
    {
        var env = EnvironmentConfigTests.Environment();
        env["SHE_MODEL_ONE"] = EnvironmentConfigTests.Model.Replace("\"query\":{}", "\"interval\":\"20s\",\"query\":{}");
        env["SHE_MODEL_TWO"] = EnvironmentConfigTests.Model.Replace("model-one", "model-two").Replace("model.test", "other.test");
        var config = new Core.Configuration.EnvironmentConfigSource().Load(env);
        using var gate = new ManualResetEventSlim();
        var entered = PollingTestSupport.Gate();
        var other = PollingTestSupport.Gate();
        await using var scheduler = new Scheduler(PollingTestSupport.Clock());
        using var stop = new CancellationTokenSource();
        var run = scheduler.RunAsync(config, (batch, _) =>
        {
            if (batch.Window.Interval == TimeSpan.FromSeconds(20)) { entered.TrySetResult(); gate.Wait(); }
            else other.TrySetResult();
            return Task.CompletedTask;
        }, stop.Token);
        try
        {
            await Task.WhenAll(entered.Task, other.Task).WaitAsync(TimeSpan.FromSeconds(5));
            stop.Cancel();
            await run.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { gate.Set(); stop.Cancel(); }
    }

    [Test]
    public async Task FailedTickIsObservedAndNextAlignedPollStillRuns()
    {
        var time = PollingTestSupport.Clock();
        using var diagnostics = new RuntimeDiagnostics();
        using var metrics = new RuntimeMetricCapture(diagnostics);
        var succeeded = PollingTestSupport.Gate();
        var attempts = 0;
        await using var scheduler = new Scheduler(time, diagnostics);
        using var stop = new CancellationTokenSource();
        var run = scheduler.RunAsync(PollingTestSupport.Snapshot(), (_, _) =>
        {
            if (Interlocked.Increment(ref attempts) == 1) throw new InvalidOperationException("not logged");
            succeeded.TrySetResult();
            return Task.CompletedTask;
        }, stop.Token);
        await PollingTestSupport.Until(() => metrics.Total("evaluator.schedule.callback_failures") == 1);
        await scheduler.DrainAsync(default);
        time.Advance(TimeSpan.FromSeconds(10));
        await succeeded.Task.WaitAsync(TimeSpan.FromSeconds(5));
        stop.Cancel();
        await run;
        Assert.That(attempts, Is.EqualTo(2));
    }
}

internal sealed class RuntimeMetricCapture : IDisposable
{
    private readonly MeterListener listener = new();
    private readonly ConcurrentDictionary<string, long> totals = new();
    private readonly ConcurrentDictionary<string, int> samples = new();
    private readonly ConcurrentDictionary<string, byte> meterNames = new();
    private readonly ConcurrentDictionary<string, byte> tagNames = new();
    public IEnumerable<string> MeterNames => meterNames.Keys;
    public IEnumerable<string> TagNames => tagNames.Keys;
    public RuntimeMetricCapture(RuntimeDiagnostics diagnostics)
    {
        listener.InstrumentPublished = (instrument, source) =>
        { if (instrument.Meter == diagnostics.Meter) source.EnableMeasurementEvents(instrument); };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            meterNames.TryAdd(instrument.Meter.Name, 0);
            foreach (var tag in tags) tagNames.TryAdd(tag.Key, 0);
            totals.AddOrUpdate(instrument.Name, value, (_, current) => current + value);
        });
        listener.SetMeasurementEventCallback<double>((instrument, _, tags, _) =>
        {
            meterNames.TryAdd(instrument.Meter.Name, 0);
            foreach (var tag in tags) tagNames.TryAdd(tag.Key, 0);
            samples.AddOrUpdate(instrument.Name, 1, (_, current) => current + 1);
        });
        listener.Start();
    }
    public long Total(string name) => totals.GetValueOrDefault(name);
    public int Samples(string name) => samples.GetValueOrDefault(name);
    public void Dispose() => listener.Dispose();
}
