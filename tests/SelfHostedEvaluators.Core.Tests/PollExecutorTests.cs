using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Time.Testing;
using SelfHostedEvaluators.Core.Auth;
using SelfHostedEvaluators.Core.Configuration;
using SelfHostedEvaluators.Core.Diagnostics;
using SelfHostedEvaluators.Core.Evaluation;
using SelfHostedEvaluators.Core.Packages;
using SelfHostedEvaluators.Core.Polling;
using SelfHostedEvaluators.Plugins.Abstractions;

namespace SelfHostedEvaluators.Core.Tests;

public class PollExecutorTests
{
    [Test]
    public async Task MatchesByFullKeyInOriginalOrderAcrossModels()
    {
        var config = PollingTestSupport.Snapshot(secondModel: true);
        var a = PollingTestSupport.Batch(config);
        var b = PollingTestSupport.Batch(config, 1);
        var batch = new ScheduledBatch(a.PluginName, a.Window, a.Queries.Concat(b.Queries).ToArray());
        var sink = new DelegateSink((queries, _, _) => Task.FromResult<IReadOnlyList<SignalResult>>(
            [new SignalResult.Observation(queries[1].Key, 22, null, PollingTestSupport.Start),
             new SignalResult.Observation(queries[0].Key, 11, null, PollingTestSupport.Start)]));
        await using var executor = Executor(config, sink);
        var results = await executor.ExecuteAsync(batch, default);
        Assert.That(results.Select(r => r.Key), Is.EqualTo(batch.Queries.Select(q => q.Key)));
        Assert.That(results.OfType<SignalResult.Observation>().Select(r => r.Value), Is.EqualTo(new[] { 11d, 22d }));
    }

    [TestCase("missing")]
    [TestCase("duplicate")]
    [TestCase("foreign-model")]
    [TestCase("foreign-entity")]
    [TestCase("null-list")]
    [TestCase("null-item")]
    [TestCase("null-key")]
    [TestCase("malformed")]
    [TestCase("nan")]
    [TestCase("infinity")]
    [TestCase("throw")]
    public async Task InvalidResponsesBecomePerSignalFailuresAndUnknown(string mode)
    {
        var config = PollingTestSupport.Snapshot();
        var batch = PollingTestSupport.Batch(config);
        var key = batch.Queries[0].Key;
        var observation = new SignalResult.Observation(key, 1, null, PollingTestSupport.Start);
        var sink = new DelegateSink((_, _, _) => Task.FromResult<IReadOnlyList<SignalResult>>(mode switch
        {
            "missing" => [],
            "duplicate" => [observation, observation],
            "foreign-model" => [observation with { Key = key with { ModelUri = new("https://other.test") } }],
            "foreign-entity" => [observation with { Key = key with { Entity = "other-entity" } }],
            "null-list" => null!,
            "null-item" => [null!],
            "null-key" => [observation with { Key = null! }],
            "malformed" => [new SignalResult.Failure(key, "", null!)],
            "nan" => [observation with { Value = double.NaN }],
            "infinity" => [observation with { Value = double.PositiveInfinity }],
            _ => throw new InvalidOperationException("secret must not leak")
        }));
        await using var executor = Executor(config, sink);
        var results = await executor.ExecuteAsync(batch, default);
        Assert.That(results, Has.Count.EqualTo(1));
        Assert.That(results[0], Is.TypeOf<SignalResult.Failure>());
        var evaluated = new Evaluator(new Redactor()).Evaluate(key, config.Models[0].Signals[0], results[0]);
        Assert.That(evaluated.State, Is.EqualTo(HealthState.Unknown));
        Assert.That(evaluated.AdditionalContext, Does.Not.Contain("secret must not leak"));
    }

    [Test]
    public async Task PartialResultsFailOnlyMissingOrDuplicateFullKeys()
    {
        var config = PollingTestSupport.Snapshot(signals: 3);
        var batch = PollingTestSupport.Batch(config);
        var sink = new DelegateSink((q, _, _) => Task.FromResult<IReadOnlyList<SignalResult>>(
            [new SignalResult.Observation(q[1].Key, 5, null, PollingTestSupport.Start),
             new SignalResult.NoData(q[2].Key, "none"), new SignalResult.NoData(q[2].Key, "duplicate")]));
        await using var executor = Executor(config, sink);
        var results = await executor.ExecuteAsync(batch, default);
        Assert.That(results[0], Is.TypeOf<SignalResult.Failure>());
        Assert.That(results[1], Is.TypeOf<SignalResult.Observation>());
        Assert.That(results[2], Is.TypeOf<SignalResult.Failure>());
    }

    [Test]
    public async Task ExecutorRejectsOversizeBatchesWithoutInvokingPlugin()
    {
        var config = PollingTestSupport.Snapshot(signals: 2, batchSize: 1);
        var calls = 0;
        var sink = new DelegateSink((q, _, _) =>
        { Interlocked.Increment(ref calls); return Task.FromResult(PollingTestSupport.Observations(q)); });
        await using var executor = Executor(config, sink);
        var results = await executor.ExecuteAsync(PollingTestSupport.Batch(config), default);
        Assert.That(results, Has.Count.EqualTo(2));
        Assert.That(results.All(r => r is SignalResult.Failure), Is.True);
        Assert.That(calls, Is.Zero);
    }

    [Test]
    public async Task ConcurrencyIsSharedAcrossModelsAndIntervalGroups()
    {
        var config = PollingTestSupport.Snapshot(concurrency: 2, secondModel: true);
        var release = PollingTestSupport.Gate();
        var calls = 0;
        var active = 0;
        var peak = 0;
        var created = 0;
        var sink = new DelegateSink(async (queries, _, _) =>
        {
            var current = Interlocked.Increment(ref active);
            Interlocked.Increment(ref calls);
            Interlocked.Exchange(ref peak, Math.Max(peak, current));
            await release.Task;
            Interlocked.Decrement(ref active);
            return PollingTestSupport.Observations(queries);
        });
        await using var executor = new PollExecutor(config, (_, _) =>
        { Interlocked.Increment(ref created); return ValueTask.FromResult<ISinkClient>(sink); }, PollingTestSupport.Clock());
        var first = executor.ExecuteAsync(PollingTestSupport.Batch(config), default);
        var second = executor.ExecuteAsync(PollingTestSupport.Batch(config, 1, TimeSpan.FromSeconds(20)), default);
        Task<IReadOnlyList<SignalResult>> third;
        try
        {
            await PollingTestSupport.Until(() => Volatile.Read(ref calls) == 2);
            third = executor.ExecuteAsync(PollingTestSupport.Batch(config, interval: TimeSpan.FromSeconds(30)), default);
            Assert.That(third.IsCompleted, Is.False);
        }
        finally { release.TrySetResult(); }
        await Task.WhenAll(first, second, third).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(peak, Is.EqualTo(2));
        Assert.That(calls, Is.EqualTo(3));
        Assert.That(created, Is.EqualTo(1));
    }

    [TestCase("throw")]
    [TestCase("fault")]
    [TestCase("cancel")]
    [TestCase("null")]
    public async Task CompletedActivationFailureIsRetriedOnTheNextPoll(string failure)
    {
        var config = PollingTestSupport.Snapshot();
        var created = 0;
        var sink = new DelegateSink((q, _, _) => Task.FromResult(PollingTestSupport.Observations(q)));
        var executor = new PollExecutor(config, (_, _) =>
        {
            if (Interlocked.Increment(ref created) != 1) return ValueTask.FromResult<ISinkClient>(sink);
            return failure switch
            {
                "throw" => throw new InvalidOperationException("activation failed"),
                "fault" => ValueTask.FromException<ISinkClient>(new InvalidOperationException("activation failed")),
                "cancel" => ValueTask.FromCanceled<ISinkClient>(new CancellationToken(true)),
                _ => ValueTask.FromResult<ISinkClient>(null!)
            };
        }, PollingTestSupport.Clock());
        try
        {
            var batch = PollingTestSupport.Batch(config);
            Assert.That((await executor.ExecuteAsync(batch, default))[0], Is.TypeOf<SignalResult.Failure>());
            Assert.That((await executor.ExecuteAsync(batch, default))[0], Is.TypeOf<SignalResult.Observation>());
            Assert.That((await executor.ExecuteAsync(batch, default))[0], Is.TypeOf<SignalResult.Observation>());
            Assert.That(created, Is.EqualTo(2));
            Assert.That(sink.Disposals, Is.Zero);
        }
        finally { await executor.DisposeAsync(); }
        Assert.That(sink.Disposals, Is.EqualTo(1));
    }

    [Test]
    public async Task ConcurrentGroupsShareOneRecoveryActivationAndDisposeItOnce()
    {
        var config = PollingTestSupport.Snapshot(secondModel: true);
        var created = 0;
        var activationEntered = PollingTestSupport.Gate();
        var release = new TaskCompletionSource<ISinkClient>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sink = new DelegateSink((q, _, _) => Task.FromResult(PollingTestSupport.Observations(q)));
        using var diagnostics = new RuntimeDiagnostics();
        using var metrics = new RuntimeMetricCapture(diagnostics);
        var executor = new PollExecutor(config, (_, _) =>
        {
            if (Interlocked.Increment(ref created) == 1)
                return ValueTask.FromException<ISinkClient>(new InvalidOperationException("activation failed"));
            activationEntered.TrySetResult();
            return new(release.Task);
        }, PollingTestSupport.Clock(), diagnostics);
        try
        {
            Assert.That((await executor.ExecuteAsync(PollingTestSupport.Batch(config), default))[0],
                Is.TypeOf<SignalResult.Failure>());
            var first = executor.ExecuteAsync(PollingTestSupport.Batch(config), default);
            var second = executor.ExecuteAsync(PollingTestSupport.Batch(config, 1, TimeSpan.FromSeconds(20)), default);
            await Task.WhenAny(activationEntered.Task, Task.WhenAll(first, second)).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(activationEntered.Task.IsCompleted, Is.True, "A later poll must start a recovery activation.");
            await PollingTestSupport.Until(() => metrics.Total("evaluator.poll.inflight") == 2);
            Assert.That(created, Is.EqualTo(2));
            Assert.That(first.IsCompleted, Is.False);
            Assert.That(second.IsCompleted, Is.False);
            release.TrySetResult(sink);
            var results = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(results.All(r => r[0] is SignalResult.Observation), Is.True);
            Assert.That(created, Is.EqualTo(2));
            Assert.That(sink.Disposals, Is.Zero);
        }
        finally
        {
            release.TrySetResult(sink);
            await executor.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.That(sink.Disposals, Is.EqualTo(1));
    }

    [TestCase("30s", 10)]
    [TestCase("2s", 2)]
    public async Task TimeoutIsIntervalBoundedCancelsUnderlyingAndQuarantinesWithoutEarlyDisposal(string timeout, int seconds)
    {
        var time = PollingTestSupport.Clock();
        var config = PollingTestSupport.Snapshot(timeout: timeout, concurrency: 1);
        var entered = PollingTestSupport.Gate();
        var cancelled = PollingTestSupport.Gate();
        var finish = new TaskCompletionSource<IReadOnlyList<SignalResult>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var sink = new DelegateSink((_, _, ct) =>
        {
            Interlocked.Increment(ref calls);
            ct.Register(() => cancelled.TrySetResult());
            entered.TrySetResult();
            return finish.Task;
        });
        using var diagnostics = new RuntimeDiagnostics();
        using var metrics = new RuntimeMetricCapture(diagnostics);
        var executor = Executor(config, sink, time, diagnostics);
        var first = executor.ExecuteAsync(PollingTestSupport.Batch(config), default);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            time.Advance(TimeSpan.FromSeconds(seconds));
            var results = await first.WaitAsync(TimeSpan.FromSeconds(5));
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(results[0], Is.TypeOf<SignalResult.Failure>());
            Assert.That(((SignalResult.Failure)results[0]).Code, Is.EqualTo("timeout"));
            for (var i = 0; i < 10; i++)
                Assert.That((await executor.ExecuteAsync(PollingTestSupport.Batch(config), default))[0],
                    Is.TypeOf<SignalResult.Failure>());
            Assert.That(calls, Is.EqualTo(1));
            Assert.That(metrics.Total("evaluator.poll.hangs"), Is.Zero);
            time.Advance(TimeSpan.FromSeconds(5));
            await PollingTestSupport.Until(() => metrics.Total("evaluator.poll.hangs") == 1);
            Assert.That(metrics.Total("evaluator.poll.hangs"), Is.EqualTo(1));
            Assert.That(metrics.Total("evaluator.poll.inflight"), Is.EqualTo(1));
            var dispose = executor.DisposeAsync().AsTask();
            Assert.That(dispose.IsCompleted, Is.False);
            Assert.That(sink.Disposals, Is.Zero);
        }
        finally { finish.TrySetException(new InvalidOperationException("late plugin failure")); }
        await executor.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(sink.Disposals, Is.EqualTo(1));
        Assert.That(metrics.Total("evaluator.poll.inflight"), Is.Zero);
    }

    [Test]
    public async Task SynchronousPluginInvocationIsExternallyBounded()
    {
        var time = PollingTestSupport.Clock();
        var config = PollingTestSupport.Snapshot(timeout: "2s");
        using var unblock = new ManualResetEventSlim();
        var entered = PollingTestSupport.Gate();
        var sink = new DelegateSink((queries, _, _) =>
        { entered.TrySetResult(); unblock.Wait(); return Task.FromResult(PollingTestSupport.Observations(queries)); });
        var executor = Executor(config, sink, time);
        try
        {
            var pending = executor.ExecuteAsync(PollingTestSupport.Batch(config), default);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            time.Advance(TimeSpan.FromSeconds(2));
            Assert.That((await pending.WaitAsync(TimeSpan.FromSeconds(5)))[0], Is.TypeOf<SignalResult.Failure>());
            Assert.That(sink.Disposals, Is.Zero);
        }
        finally { unblock.Set(); }
        await executor.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Test]
    public async Task TimeoutFailsAllSignalsEvenWhenCancellationCallbackBlocks()
    {
        var time = PollingTestSupport.Clock();
        var config = PollingTestSupport.Snapshot(signals: 3, timeout: "2s");
        using var unblock = new ManualResetEventSlim();
        var entered = PollingTestSupport.Gate();
        var cancellationEntered = PollingTestSupport.Gate();
        var finish = new TaskCompletionSource<IReadOnlyList<SignalResult>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sink = new DelegateSink((_, _, ct) =>
        {
            ct.Register(() => { cancellationEntered.TrySetResult(); unblock.Wait(); });
            entered.TrySetResult();
            return finish.Task;
        });
        var executor = Executor(config, sink, time);
        try
        {
            var pending = executor.ExecuteAsync(PollingTestSupport.Batch(config), default);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            time.Advance(TimeSpan.FromSeconds(2));
            var results = await pending.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(results, Has.Count.EqualTo(3));
            Assert.That(results.All(r => r is SignalResult.Failure { Code: "timeout" }), Is.True);
            await cancellationEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            finish.TrySetResult([]);
            Assert.That(executor.DisposeAsync().IsCompleted, Is.False);
            Assert.That(sink.Disposals, Is.Zero);
        }
        finally { unblock.Set(); finish.TrySetResult([]); }
        await executor.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(sink.Disposals, Is.EqualTo(1));
    }

    [Test]
    public async Task QueuedTimeoutDoesNotQuarantineAHealthyPlugin()
    {
        var time = PollingTestSupport.Clock();
        var config = PollingTestSupport.Snapshot(concurrency: 1, timeout: "30s", interval: "1m");
        var entered = PollingTestSupport.Gate();
        var release = PollingTestSupport.Gate();
        var calls = 0;
        var sink = new DelegateSink(async (q, _, _) =>
        {
            if (Interlocked.Increment(ref calls) == 1) { entered.TrySetResult(); await release.Task; }
            return PollingTestSupport.Observations(q);
        });
        await using var executor = Executor(config, sink, time);
        var first = executor.ExecuteAsync(PollingTestSupport.Batch(config), default);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var queued = executor.ExecuteAsync(PollingTestSupport.Batch(config, interval: TimeSpan.FromSeconds(10)), default);
            time.Advance(TimeSpan.FromSeconds(10));
            Assert.That((await queued.WaitAsync(TimeSpan.FromSeconds(5)))[0], Is.TypeOf<SignalResult.Failure>());
        }
        finally { release.TrySetResult(); }
        Assert.That((await first.WaitAsync(TimeSpan.FromSeconds(5)))[0], Is.TypeOf<SignalResult.Observation>());
        Assert.That((await executor.ExecuteAsync(PollingTestSupport.Batch(config), default))[0],
            Is.TypeOf<SignalResult.Observation>());
        Assert.That(calls, Is.EqualTo(2));
    }

    [Test]
    public async Task SingleQueryUnderlyingWorkersRemainOwnedAfterExternalTimeout()
    {
        var time = PollingTestSupport.Clock();
        var config = PollingTestSupport.Snapshot(timeout: "2s");
        var sink = new UncooperativeSingleSink();
        var executor = Executor(config, sink, time);
        var pending = executor.ExecuteAsync(PollingTestSupport.Batch(config), default);
        try
        {
            await sink.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            time.Advance(TimeSpan.FromSeconds(2));
            await pending.WaitAsync(TimeSpan.FromSeconds(5));
            var disposal = executor.DisposeAsync().AsTask();
            Assert.That(disposal.IsCompleted, Is.False);
            Assert.That(sink.Disposed, Is.False);
        }
        finally { sink.Release.TrySetException(new InvalidOperationException("late")); }
        await executor.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(sink.Disposed, Is.True);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task ActivationHangsAreBoundedAndOnlyOneActivationIsRetained(bool failEventually)
    {
        var time = PollingTestSupport.Clock();
        var config = PollingTestSupport.Snapshot(timeout: "2s");
        var entered = PollingTestSupport.Gate();
        var finish = new TaskCompletionSource<ISinkClient>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var sink = new DelegateSink((q, _, _) => Task.FromResult(PollingTestSupport.Observations(q)));
        var executor = new PollExecutor(config, (_, _) =>
        { Interlocked.Increment(ref calls); entered.TrySetResult(); return new(finish.Task); }, time);
        try
        {
            var pending = executor.ExecuteAsync(PollingTestSupport.Batch(config), default);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            time.Advance(TimeSpan.FromSeconds(2));
            Assert.That((await pending.WaitAsync(TimeSpan.FromSeconds(5)))[0], Is.TypeOf<SignalResult.Failure>());
            await executor.ExecuteAsync(PollingTestSupport.Batch(config), default);
            Assert.That(calls, Is.EqualTo(1));
            Assert.That(executor.DrainAsync(default).IsCompleted, Is.False);
            if (failEventually)
            {
                finish.TrySetException(new InvalidOperationException("late activation failure"));
                await executor.DrainAsync(default).WaitAsync(TimeSpan.FromSeconds(5));
                Assert.That((await executor.ExecuteAsync(PollingTestSupport.Batch(config), default))[0],
                    Is.TypeOf<SignalResult.Failure>());
                Assert.That(calls, Is.EqualTo(2), "Completed failed activations can be retried after recovery.");
            }
            else Assert.That(executor.DisposeAsync().IsCompleted, Is.False);
            Assert.That(sink.Disposals, Is.Zero);
        }
        finally
        {
            finish.TrySetResult(sink);
            await executor.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.That(sink.Disposals, Is.EqualTo(failEventually ? 0 : 1));
    }

    [Test]
    public async Task CancellationStopsWaitersButAllowsLaterDrain()
    {
        var config = PollingTestSupport.Snapshot(concurrency: 1);
        var entered = PollingTestSupport.Gate();
        var finish = PollingTestSupport.Gate();
        var sink = new DelegateSink(async (q, _, _) =>
        { entered.TrySetResult(); await finish.Task; return PollingTestSupport.Observations(q); });
        var executor = Executor(config, sink);
        using var stop = new CancellationTokenSource();
        var first = executor.ExecuteAsync(PollingTestSupport.Batch(config), stop.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var queued = executor.ExecuteAsync(PollingTestSupport.Batch(config), stop.Token);
            stop.Cancel();
            Assert.That((await first.WaitAsync(TimeSpan.FromSeconds(5)))[0], Is.TypeOf<SignalResult.Failure>());
            Assert.That((await queued.WaitAsync(TimeSpan.FromSeconds(5)))[0], Is.TypeOf<SignalResult.Failure>());
            using var drainStop = new CancellationTokenSource();
            var drain = executor.DrainAsync(drainStop.Token);
            Assert.That(drain.IsCompleted, Is.False);
            drainStop.Cancel();
            Assert.CatchAsync<OperationCanceledException>(async () => await drain);
        }
        finally { finish.TrySetResult(); stop.Cancel(); }
        await executor.DrainAsync(default);
        await executor.DisposeAsync();
    }

    [Test]
    public async Task UnavailablePluginIsUnknownInRuntimeButRejectedByDeploymentValidation()
    {
        var config = PollingTestSupport.Snapshot();
        using var fixture = new PackageFixture();
        using var catalog = PackageCatalog.Load(fixture.Root);
        Assert.DoesNotThrow(() => ConfigurationValidator.Validate(config, catalog, ConfigurationValidationMode.Runtime));
        Assert.Throws<ConfigurationException>(() =>
            ConfigurationValidator.Validate(config, catalog, ConfigurationValidationMode.Deployment));
        await using var executor = new PollExecutor(config, (_, _) =>
            ValueTask.FromException<ISinkClient>(new InvalidOperationException("unavailable")));
        var result = (await executor.ExecuteAsync(PollingTestSupport.Batch(config), default))[0];
        Assert.That(new Evaluator(new Redactor()).Evaluate(result.Key, config.Models[0].Signals[0], result).State,
            Is.EqualTo(HealthState.Unknown));
    }

    [Test]
    public async Task MissingPluginEntryStillProducesUnknown()
    {
        var env = EnvironmentConfigTests.Environment();
        env["SHE_MODEL_ONE"] = EnvironmentConfigTests.Model.Replace("sink-one", "sink-absent");
        var config = new EnvironmentConfigSource().Load(env);
        var batch = PollingTestSupport.Batch(config);
        batch = new("sink-absent", batch.Window, batch.Queries);
        await using var executor = new PollExecutor(config, (_, _) => throw new AssertionException("Must not activate."));
        var result = (await executor.ExecuteAsync(batch, default))[0];
        Assert.That(result, Is.TypeOf<SignalResult.Failure>());
        Assert.That(new Evaluator(new Redactor()).Evaluate(result.Key, config.Models[0].Signals[0], result).State,
            Is.EqualTo(HealthState.Unknown));
    }

    [Test]
    public async Task HealthyPluginRemainsUsableWhileAnotherIsQuarantined()
    {
        var env = EnvironmentConfigTests.Environment();
        env["SHE_PLUGIN_TWO"] = EnvironmentConfigTests.Plugin.Replace("sink-one", "sink-two");
        var config = new EnvironmentConfigSource().Load(env);
        var time = PollingTestSupport.Clock();
        var entered = PollingTestSupport.Gate();
        var finish = new TaskCompletionSource<IReadOnlyList<SignalResult>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocked = new DelegateSink((_, _, _) => { entered.TrySetResult(); return finish.Task; });
        var healthy = new DelegateSink((q, _, _) => Task.FromResult(PollingTestSupport.Observations(q)));
        var executor = new PollExecutor(config, (plugin, _) => ValueTask.FromResult<ISinkClient>(
            plugin.Name == "sink-one" ? blocked : healthy), time);
        try
        {
            var batch = PollingTestSupport.Batch(config);
            var pending = executor.ExecuteAsync(batch, default);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            time.Advance(TimeSpan.FromSeconds(30));
            await pending.WaitAsync(TimeSpan.FromSeconds(5));
            var independent = await executor.ExecuteAsync(new("sink-two", batch.Window, batch.Queries), default);
            Assert.That(independent[0], Is.TypeOf<SignalResult.Observation>());
        }
        finally { finish.TrySetResult([]); }
        await executor.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Test]
    public async Task SharedDiagnosticsExposePipelineCountsAndDurationsWithoutSignalTags()
    {
        var time = PollingTestSupport.Clock();
        var config = PollingTestSupport.Snapshot(signals: 2);
        using var diagnostics = new RuntimeDiagnostics();
        using var metrics = new RuntimeMetricCapture(diagnostics);
        var evaluated = PollingTestSupport.Gate();
        var sink = new DelegateSink((q, _, _) => Task.FromResult(PollingTestSupport.Observations(q)));
        await using var executor = Executor(config, sink, time, diagnostics);
        await using var scheduler = new Scheduler(time, diagnostics);
        var evaluator = new Evaluator(new Redactor(), time, diagnostics);
        using var stop = new CancellationTokenSource();
        var run = scheduler.RunAsync(config, async (batch, ct) =>
        {
            var results = await executor.ExecuteAsync(batch, ct);
            foreach (var result in results)
                evaluator.Evaluate(result.Key, config.Models[0].Signals.Single(s => s.Name == result.Key.Name), result);
            evaluated.TrySetResult();
        }, stop.Token);
        await evaluated.Task.WaitAsync(TimeSpan.FromSeconds(5));
        stop.Cancel();
        await run;
        await scheduler.DrainAsync(default);
        await executor.DrainAsync(default);
        Assert.Multiple(() =>
        {
            Assert.That(metrics.MeterNames, Is.EquivalentTo(new[] { "SelfHostedEvaluators" }));
            Assert.That(metrics.TagNames, Is.SubsetOf(new[] { "plugin", "outcome", "state" }));
            Assert.That(metrics.Total("evaluator.ticks"), Is.EqualTo(1));
            Assert.That(metrics.Total("evaluator.polls"), Is.EqualTo(1));
            Assert.That(metrics.Total("evaluator.evaluations"), Is.EqualTo(2));
            Assert.That(metrics.Total("evaluator.poll.inflight"), Is.Zero);
            Assert.That(metrics.Samples("evaluator.schedule.lag"), Is.EqualTo(1));
            Assert.That(metrics.Samples("evaluator.poll.duration"), Is.EqualTo(1));
            Assert.That(metrics.Samples("evaluator.evaluation.duration"), Is.EqualTo(2));
        });
    }

    [Test]
    public async Task OutboundAttemptsAreRateLimitedWithoutChargingBatchEntry()
    {
        var time = PollingTestSupport.Clock();
        var config = PollingTestSupport.Snapshot(interval: "1m", timeout: "30s");
        using var auth = new AuthRig();
        using var limiter = new PluginRequestLimiter(new(1, TimeSpan.FromSeconds(5)), time);
        var registry = new AuthRegistry(auth.Secrets, auth.Credentials, auth.Redactor, time, auth.Transport);
        using var http = await registry.CreateClientAsync(new NoneAuthProfile(), new("https://sink.test"),
            limiter, TimeSpan.FromSeconds(30), default);
        var sink = new DelegateSink(async (q, _, ct) =>
        {
            using var a = await http.GetAsync("/a", ct);
            using var b = await http.GetAsync("/b", ct);
            return PollingTestSupport.Observations(q);
        });
        await using var executor = Executor(config, sink, time);
        var pending = executor.ExecuteAsync(PollingTestSupport.Batch(config), default);
        await PollingTestSupport.Until(() => auth.Transport.Calls == 1);
        Assert.That(pending.IsCompleted, Is.False);
        time.Advance(TimeSpan.FromSeconds(5));
        Assert.That((await pending.WaitAsync(TimeSpan.FromSeconds(5)))[0], Is.TypeOf<SignalResult.Observation>());
        Assert.That(limiter.GetStatistics()!.TotalSuccessfulLeases, Is.EqualTo(2));
    }

    [Test]
    public async Task RetryAfterPausesOnlyThe429PluginAndEveryRetryCounts()
    {
        var time = PollingTestSupport.Clock();
        using var auth = new AuthRig();
        var transportA = new FakeTransport();
        var transportB = new FakeTransport();
        transportA.Respond = _ => transportA.Calls == 1
            ? new(HttpStatusCode.TooManyRequests) { Headers = { RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(5)) } }
            : new(HttpStatusCode.OK);
        using var aLimiter = new PluginRequestLimiter(new(100, TimeSpan.FromMinutes(1)), time);
        using var bLimiter = new PluginRequestLimiter(new(100, TimeSpan.FromMinutes(1)), time);
        var aRegistry = new AuthRegistry(auth.Secrets, auth.Credentials, auth.Redactor, time, transportA);
        var bRegistry = new AuthRegistry(auth.Secrets, auth.Credentials, auth.Redactor, time, transportB);
        using var a = await aRegistry.CreateClientAsync(new NoneAuthProfile(), new("https://a.test"), aLimiter, TimeSpan.FromSeconds(30), default);
        using var b = await bRegistry.CreateClientAsync(new NoneAuthProfile(), new("https://b.test"), bLimiter, TimeSpan.FromSeconds(30), default);
        var waiting = a.GetAsync("/");
        await PollingTestSupport.Until(() => transportA.Calls == 1);
        using var independent = await b.GetAsync("/").WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(independent.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        using (var paused = aLimiter.AttemptAcquire()) Assert.That(paused.IsAcquired, Is.False);
        Assert.That(waiting.IsCompleted, Is.False);
        time.Advance(TimeSpan.FromSeconds(5));
        using var retried = await waiting.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(retried.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(aLimiter.GetStatistics()!.TotalSuccessfulLeases, Is.EqualTo(2));
        Assert.That(bLimiter.GetStatistics()!.TotalSuccessfulLeases, Is.EqualTo(1));
    }

    private static PollExecutor Executor(ConfigSnapshot config, ISinkClient sink, FakeTimeProvider? time = null,
        RuntimeDiagnostics? diagnostics = null) =>
        new(config, (_, _) => ValueTask.FromResult(sink), time ?? PollingTestSupport.Clock(), diagnostics);

    private sealed class UncooperativeSingleSink() : SingleQueryClientBase(1)
    {
        public TaskCompletionSource Entered { get; } = PollingTestSupport.Gate();
        public TaskCompletionSource<SignalResult> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Disposed;
        protected override Task<SignalResult> QueryAsync(SignalQuery query, QueryWindow window, CancellationToken ct)
        { Entered.TrySetResult(); return Release.Task; }
        public override ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
}
