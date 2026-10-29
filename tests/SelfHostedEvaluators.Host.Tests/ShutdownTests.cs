using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using SelfHostedEvaluators.Core.Auth;
using SelfHostedEvaluators.Core.Configuration;
using SelfHostedEvaluators.Core.Polling;
using SelfHostedEvaluators.Plugins.Abstractions;

namespace SelfHostedEvaluators.Host.Tests;

[NonParallelizable]
public class ShutdownTests
{
    [Test]
    public async Task PreinitializationAndPollingShareExactlyOneOwnedClient()
    {
        using var fixture = new HostFixture();
        var snapshot = new EnvironmentConfigSource().Load(fixture.Environment);
        var client = new CountingClient();
        var creations = 0;
        await using (var executor = new PollExecutor(snapshot, (_, _) =>
        {
            creations++;
            return ValueTask.FromResult<ISinkClient>(client);
        }))
        {
            await executor.InitializeAsync(["static-one"], CancellationToken.None);
            var signal = snapshot.Models[0].Signals[0];
            var key = new SignalKey(snapshot.Models[0].HealthModel.DataPlaneUri, signal.Entity, signal.Name);
            await executor.ExecuteAsync(new("static-one", new(DateTimeOffset.UtcNow, signal.Interval),
                [new(key, signal.Query)]), CancellationToken.None);
            Assert.Multiple(() =>
            {
                Assert.That(creations, Is.EqualTo(1));
                Assert.That(client.Calls, Is.EqualTo(1));
                Assert.That(client.Disposed, Is.False);
            });
        }
        Assert.That(client.Disposed, Is.True);
    }

    [Test]
    public async Task PollCompletionAndPublisherFlushUseOneRemainingBudget()
    {
        using var fixture = new HostFixture();
        using var offline = new OfflineRuntime();
        fixture.Environment["SHE_SHUTDOWN_TIMEOUT"] = "2s";
        fixture.Environment["SHE_PLUGIN_STATIC"] = fixture.Environment["SHE_PLUGIN_STATIC"]
            .Replace("\"auth\":", "\"settings\":{\"delay\":\"1s\"},\"auth\":");
        var polling = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        long dropped = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (i, l) => { if (ReferenceEquals(i.Meter, offline.Diagnostics.Meter)) l.EnableMeasurementEvents(i); };
        listener.SetMeasurementEventCallback<long>((i, value, tags, _) =>
        {
            if (i.Name == "evaluator.poll.inflight" && value == 1) polling.TrySetResult();
            if (i.Name == "evaluator.reports.dropped" && tags.ToArray().Any(t => Equals(t.Value, "shutdown")))
                Interlocked.Add(ref dropped, value);
        });
        listener.Start();
        offline.Transport.Send = async (request, ct) =>
        {
            if (request.Method == HttpMethod.Post) await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return new(HttpStatusCode.OK) { Content = new StringContent("""{"signals":[]}""") };
        };
        using var stop = new CancellationTokenSource();
        var run = new HostRunner(offline.Dependencies(fixture)).RunAsync(["run"], stop.Token);
        try
        {
            await polling.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var clock = Stopwatch.StartNew();
            stop.Cancel();
            Assert.That(await run.WaitAsync(TimeSpan.FromSeconds(5)), Is.Zero);
            Assert.Multiple(() =>
            {
                Assert.That(clock.Elapsed, Is.LessThan(TimeSpan.FromSeconds(2.7)), "Flush must not start a new two-second budget.");
                Assert.That(offline.Transport.Requests.Single(r => r.Method == "POST").Body, Does.Contain("\"healthState\":\"Healthy\""));
                Assert.That(dropped, Is.EqualTo(1));
            });
        }
        finally
        {
            stop.Cancel();
            await run.WaitAsync(TimeSpan.FromSeconds(5));
            await OfflineRuntime.Until(() => offline.Transport.Disposals > 0);
        }
    }

    [Test]
    public async Task HungInitializationIsBoundedAndNeverDisposesItsResourcesEarly()
    {
        using var fixture = new HostFixture();
        using var offline = new OfflineRuntime();
        var vault = new BlockingVault();
        fixture.Environment["SHE_PLUGIN_STATIC"] = fixture.Environment["SHE_PLUGIN_STATIC"]
            .Replace("\"type\":\"none\"", "\"type\":\"bearer\",\"token\":\"kv://vault-one/token\"");
        using var stop = new CancellationTokenSource();
        var run = new HostRunner(offline.Dependencies(fixture, vault)).RunAsync([], stop.Token);
        try
        {
            await vault.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.That(await new ProbeCommand().ExecuteAsync("ready", CancellationToken.None), Is.EqualTo(1));
            var clock = Stopwatch.StartNew();
            stop.Cancel();
            Assert.That(await run.WaitAsync(TimeSpan.FromSeconds(4)), Is.Zero);
            Assert.Multiple(() =>
            {
                Assert.That(clock.Elapsed, Is.LessThan(TimeSpan.FromSeconds(1.8)));
                Assert.That(vault.Disposed, Is.False);
                Assert.That(offline.Transport.Requests, Is.Empty);
            });
        }
        finally
        {
            stop.Cancel();
            vault.Release.TrySetResult("late-private-token");
            await run.WaitAsync(TimeSpan.FromSeconds(4));
            await OfflineRuntime.Until(() => vault.Disposed);
        }
    }

    [Test]
    public async Task BlockedWarningChecksNeverGateReadinessOrForceEarlyDisposal()
    {
        using var fixture = new HostFixture();
        using var offline = new OfflineRuntime();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        offline.Transport.Send = (request, _) =>
        {
            if (request.Method == HttpMethod.Get)
            {
                entered.TrySetResult();
                release.Task.GetAwaiter().GetResult();
            }
            return Task.FromResult(new HttpResponseMessage(request.Method == HttpMethod.Post ? HttpStatusCode.Accepted : HttpStatusCode.OK)
            { Content = new StringContent("""{"signals":[]}""") });
        };
        using var stop = new CancellationTokenSource();
        var run = new HostRunner(offline.Dependencies(fixture)).RunAsync([], stop.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.That(await new ProbeCommand().ExecuteAsync("ready", CancellationToken.None), Is.Zero);
            stop.Cancel();
            Assert.That(await run.WaitAsync(TimeSpan.FromSeconds(3)), Is.Zero);
            Assert.That(offline.Transport.Disposals, Is.Zero, "Actual blocked requests still own the model context.");
        }
        finally
        {
            stop.Cancel();
            release.TrySetResult();
            await run.WaitAsync(TimeSpan.FromSeconds(4));
            await OfflineRuntime.Until(() => offline.Transport.Disposals > 0);
        }
    }

    private sealed class BlockingVault : IKeyVaultSource, IDisposable
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<string> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Disposed;
        public async ValueTask<string> GetSecretAsync(KeyVaultReference reference, CancellationToken ct)
        {
            Entered.TrySetResult();
            return await Release.Task;
        }
        public void Dispose() => Disposed = true;
    }

    private sealed class CountingClient : ISinkClient
    {
        public int Calls;
        public bool Disposed;
        public Task<IReadOnlyList<SignalResult>> QueryBatchAsync(IReadOnlyList<SignalQuery> batch, QueryWindow window, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult<IReadOnlyList<SignalResult>>(batch.Select(q =>
                (SignalResult)new SignalResult.Observation(q.Key, 5, null, window.ScheduledAt)).ToArray());
        }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
}
