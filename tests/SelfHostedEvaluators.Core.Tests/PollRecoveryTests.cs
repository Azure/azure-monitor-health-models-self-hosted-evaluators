using System.Net;
using System.Net.Http.Headers;
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using SelfHostedEvaluators.Core.Auth;
using SelfHostedEvaluators.Core.Configuration;
using SelfHostedEvaluators.Core.Diagnostics;
using SelfHostedEvaluators.Core.Polling;
using SelfHostedEvaluators.Plugins.Abstractions;

namespace SelfHostedEvaluators.Core.Tests;

public class PollRecoveryTests
{
    [TestCase(false)]
    [TestCase(true)]
    public async Task CancelledCallsRecoverOnlyAfterAllActualWorkCompletesAcrossModels(bool exceedsGrace)
    {
        var time = PollingTestSupport.Clock();
        var config = PollingTestSupport.Snapshot(concurrency: 2, timeout: "2s", secondModel: true);
        var entered = new[] { PollingTestSupport.Gate(), PollingTestSupport.Gate() };
        var cancelled = new[] { PollingTestSupport.Gate(), PollingTestSupport.Gate() };
        var finish = new[] { PollingTestSupport.Gate(), PollingTestSupport.Gate() };
        var calls = 0;
        var sink = new DelegateSink(async (queries, _, ct) =>
        {
            var index = Interlocked.Increment(ref calls) - 1;
            if (index < 2)
            {
                using var registration = ct.Register(() => cancelled[index].TrySetResult());
                entered[index].TrySetResult();
                await cancelled[index].Task;
                await finish[index].Task; // Cooperative asynchronous cleanup, not a hung call.
                ct.ThrowIfCancellationRequested();
            }
            return PollingTestSupport.Observations(queries);
        });
        using var diagnostics = new RuntimeDiagnostics();
        using var metrics = new RuntimeMetricCapture(diagnostics);
        var logs = new CaptureLogger();
        var executor = new PollExecutor(config, (_, _) => ValueTask.FromResult<ISinkClient>(sink), time, diagnostics, logs);
        try
        {
            var first = executor.ExecuteAsync(PollingTestSupport.Batch(config), default);
            var second = executor.ExecuteAsync(PollingTestSupport.Batch(config, 1), default);
            await Task.WhenAll(entered.Select(g => g.Task)).WaitAsync(TimeSpan.FromSeconds(5));
            time.Advance(TimeSpan.FromSeconds(2));
            var results = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(results.All(r => r[0] is SignalResult.Failure { Code: "timeout" }), Is.True);
            await Task.WhenAll(cancelled.Select(g => g.Task)).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(metrics.Total("evaluator.poll.hangs"), Is.Zero, "Cancellation scheduling is not evidence of a hang.");
            Assert.That(metrics.Total("evaluator.poll.inflight"), Is.EqualTo(2), "Permits still belong to actual calls.");
            if (exceedsGrace)
            {
                time.Advance(TimeSpan.FromSeconds(5));
                await PollingTestSupport.Until(() => metrics.Total("evaluator.poll.hangs") == 2);
                Assert.That(metrics.Total("evaluator.poll.quarantines"), Is.EqualTo(1));
            }
            finish[0].TrySetResult();
            await PollingTestSupport.Until(() => metrics.Total("evaluator.poll.inflight") == 1);
            Assert.That((await executor.ExecuteAsync(PollingTestSupport.Batch(config), default))[0],
                Is.TypeOf<SignalResult.Failure>(), "One completion must not recover another outstanding unsafe call.");
            Assert.That(calls, Is.EqualTo(2));
            finish[1].TrySetResult();
            await executor.DrainAsync(default).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That((await executor.ExecuteAsync(PollingTestSupport.Batch(config, 1), default))[0],
                Is.TypeOf<SignalResult.Observation>());
            Assert.That(calls, Is.EqualTo(3));
            Assert.That(logs.Messages.Count, Is.EqualTo(exceedsGrace ? 2 : 0));
            if (exceedsGrace)
            {
                Assert.That(logs.Messages.First(), Does.Contain("quarantined"));
                Assert.That(logs.Messages.Last(), Does.Contain("recovered"));
            }
        }
        finally
        {
            foreach (var gate in finish) gate.TrySetResult();
            await executor.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.That(sink.Disposals, Is.EqualTo(1));
    }

    [TestCase("pause")]
    [TestCase("refill")]
    [TestCase("retry-delta")]
    [TestCase("retry-date")]
    [TestCase("retry-remaining")]
    public async Task OverBudgetHttpWaitFailsPromptlyWithoutTimeoutOrQuarantine(string mode)
    {
        var time = PollingTestSupport.Clock();
        var config = PollingTestSupport.Snapshot(timeout: "30s", interval: "10s");
        using var auth = new AuthRig();
        var limiterWait = mode is "pause" or "refill";
        using var limiter = new PluginRequestLimiter(new(mode == "refill" ? 1 : 100, TimeSpan.FromMinutes(1)), time);
        if (mode == "refill") { using var consumed = limiter.AttemptAcquire(); }
        if (mode == "pause") limiter.Pause(TimeSpan.FromSeconds(20));
        else auth.Transport.Respond = _ => new(HttpStatusCode.TooManyRequests)
        {
            Headers = { RetryAfter = mode == "retry-date"
                ? new RetryConditionHeaderValue(time.GetUtcNow().AddSeconds(20))
                : new RetryConditionHeaderValue(TimeSpan.FromSeconds(mode == "retry-remaining" ? 5 : 20)) }
        };
        var registry = new AuthRegistry(auth.Secrets, auth.Credentials, auth.Redactor, time, auth.Transport);
        using var http = await registry.CreateClientAsync(new NoneAuthProfile(), new("https://sink.test"),
            limiter, TimeSpan.FromSeconds(30), default);
        var sink = new DelegateSink(async (queries, _, ct) =>
        {
            if (mode == "retry-remaining") time.Advance(TimeSpan.FromSeconds(8));
            try
            {
                using var response = await http.GetAsync("/", ct);
                return queries.Select(q => (SignalResult)new SignalResult.Failure(q.Key,
                    $"http_{(int)response.StatusCode}", "Sink rejected the request.")).ToArray();
            }
            catch (AuthenticationFailureException error)
            {
                return queries.Select(q => (SignalResult)new SignalResult.Failure(q.Key, error.Code, "Request failed.")).ToArray();
            }
        });
        using var diagnostics = new RuntimeDiagnostics();
        using var metrics = new RuntimeMetricCapture(diagnostics);
        await using var executor = new PollExecutor(config, (_, _) => ValueTask.FromResult<ISinkClient>(sink), time, diagnostics);
        var pending = executor.ExecuteAsync(PollingTestSupport.Batch(config), default);
        // No virtual time advances: an expected wait longer than the effective batch budget fails immediately.
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.That(result[0], Is.TypeOf<SignalResult.Failure>());
        Assert.That(((SignalResult.Failure)result[0]).Code, Is.EqualTo(limiterWait ? "request_rate_limited" : "http_429"));
        Assert.That(auth.Transport.Calls, Is.EqualTo(limiterWait ? 0 : 1));
        using var paused = limiter.AttemptAcquire();
        Assert.That(paused.IsAcquired, Is.False);
        await executor.DrainAsync(default).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(metrics.Total("evaluator.poll.hangs"), Is.Zero);
        Assert.That(metrics.Total("evaluator.poll.quarantines"), Is.Zero);
    }

    [Test]
    public async Task NativeIdentityExchangeDoesNotSleepPastTheEffectivePollBudget()
    {
        var time = PollingTestSupport.Clock();
        var config = PollingTestSupport.Snapshot(timeout: "30s", interval: "10s");
        using var rig = new AuthRig();
        using var limiter = new PluginRequestLimiter(new(100, TimeSpan.FromMinutes(1)), time);
        rig.Transport.Respond = _ => new(HttpStatusCode.TooManyRequests)
        { Headers = { RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMinutes(5)) } };
        using var sdk = new AzureSdkCredentialSource(rig.Transport);
        var credentials = new AzureCredentialFactory(rig.Secrets, sdk, time, rig.Redactor);
        var registry = new AuthRegistry(rig.Secrets, credentials, rig.Redactor, time, rig.Transport);
        using var http = await registry.CreateClientAsync(new EntraIdAuthProfile(
            new ServicePrincipalSecretAuthProfile("11111111-1111-1111-1111-111111111111",
                "22222222-2222-2222-2222-222222222222", new("kv://vault/key")), "https://sink.test/.default"),
            new("https://sink.test"), limiter, TimeSpan.FromSeconds(30), default);
        var sink = new DelegateSink(async (queries, _, ct) =>
        {
            using var response = await http.GetAsync("/", ct);
            return PollingTestSupport.Observations(queries);
        });
        await using var executor = new PollExecutor(config, (_, _) => ValueTask.FromResult<ISinkClient>(sink), time);
        var results = await executor.ExecuteAsync(PollingTestSupport.Batch(config), default).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.That(results[0], Is.TypeOf<SignalResult.Failure>());
        Assert.That(((SignalResult.Failure)results[0]).Code, Is.Not.EqualTo("timeout"));
        Assert.That(rig.Transport.Calls, Is.EqualTo(1), "Only the injected identity transport is invoked, without retries.");
    }

    [Test]
    public async Task CompletedTaskDoesNotReleasePermitOrRecoverWhileCancellationCallbackStillUsesResources()
    {
        var time = PollingTestSupport.Clock();
        var config = PollingTestSupport.Snapshot(concurrency: 1, timeout: "2s");
        var entered = PollingTestSupport.Gate();
        var callback = PollingTestSupport.Gate();
        var returned = PollingTestSupport.Gate();
        var finish = PollingTestSupport.Gate();
        using var unblock = new ManualResetEventSlim();
        var calls = 0;
        var sink = new DelegateSink(async (q, _, ct) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                ct.Register(() => { callback.TrySetResult(); unblock.Wait(); });
                entered.TrySetResult();
                await finish.Task;
                returned.TrySetResult();
            }
            return PollingTestSupport.Observations(q);
        });
        using var diagnostics = new RuntimeDiagnostics();
        using var metrics = new RuntimeMetricCapture(diagnostics);
        var executor = new PollExecutor(config, (_, _) => ValueTask.FromResult<ISinkClient>(sink), time, diagnostics);
        try
        {
            var pending = executor.ExecuteAsync(PollingTestSupport.Batch(config), default);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            time.Advance(TimeSpan.FromSeconds(2));
            await pending.WaitAsync(TimeSpan.FromSeconds(5));
            await callback.Task.WaitAsync(TimeSpan.FromSeconds(5));
            finish.TrySetResult();
            await returned.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(executor.DrainAsync(default).IsCompleted, Is.False);
            Assert.That(metrics.Total("evaluator.poll.inflight"), Is.EqualTo(1));
            Assert.That((await executor.ExecuteAsync(PollingTestSupport.Batch(config), default))[0],
                Is.TypeOf<SignalResult.Failure>());
            Assert.That(calls, Is.EqualTo(1));
            Assert.That(sink.Disposals, Is.Zero);
            unblock.Set();
            await executor.DrainAsync(default).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That((await executor.ExecuteAsync(PollingTestSupport.Batch(config), default))[0],
                Is.TypeOf<SignalResult.Observation>());
        }
        finally
        {
            finish.TrySetResult();
            unblock.Set();
            await executor.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.That(sink.Disposals, Is.EqualTo(1));
    }

    private sealed class CaptureLogger : ILogger
    {
        public ConcurrentQueue<string> Messages { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> formatter)
        {
            Assert.That(level, Is.EqualTo(LogLevel.Warning));
            Assert.That(error, Is.Null);
            Messages.Enqueue(formatter(state, error));
        }
    }
}
