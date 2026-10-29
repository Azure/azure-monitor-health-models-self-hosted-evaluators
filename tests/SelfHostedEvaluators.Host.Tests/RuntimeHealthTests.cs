using Microsoft.Extensions.Time.Testing;
using SelfHostedEvaluators.Core.Configuration;
using SelfHostedEvaluators.Core.Polling;

namespace SelfHostedEvaluators.Host.Tests;

[NonParallelizable]
public class RuntimeHealthTests
{
    [Test]
    public async Task InitialSchedulerHeartbeatAtMonotonicZeroIsLive()
    {
        var time = new FakeTimeProvider(DateTimeOffset.MinValue);
        Assert.That(time.GetTimestamp(), Is.Zero);
        using var fixture = new HostFixture();
        await using var scheduler = new Scheduler(time);
        using var stop = new CancellationTokenSource();
        var run = scheduler.RunAsync(new EnvironmentConfigSource().Load(fixture.Environment),
            (_, _) => Task.CompletedTask, stop.Token);
        var health = new RuntimeHealth(time);
        health.Activate(scheduler);
        try { Assert.That(health.Live, Is.True); }
        finally { stop.Cancel(); await run; }
    }

    [Test]
    public async Task HealthDetectsStuckInitializationAndUsesIndependentSchedulerHeartbeat()
    {
        var time = new FakeTimeProvider();
        var health = new RuntimeHealth(time);
        Assert.That(health.Live, Is.True);
        Assert.That(health.Ready, Is.False);
        time.Advance(RuntimeHealth.InitializationWindow + TimeSpan.FromSeconds(1));
        Assert.That(health.Live, Is.False);

        using var fixture = new HostFixture();
        fixture.Environment["SHE_DEFAULT_INTERVAL"] = "1d";
        var snapshot = new EnvironmentConfigSource().Load(fixture.Environment);
        await using var scheduler = new Scheduler(time);
        using var stop = new CancellationTokenSource();
        var run = scheduler.RunAsync(snapshot, (_, _) => Task.CompletedTask, stop.Token);
        health.Activate(scheduler);
        try
        {
            for (var i = 0; i < 30; i++)
            {
                time.Advance(TimeSpan.FromSeconds(1));
                await Task.Delay(2);
            }
            Assert.That(health.Live, Is.True, "A normal day-long poll interval is not a heartbeat failure.");
            stop.Cancel();
            await run;
            Assert.That(health.Live, Is.False, "A stopped scheduler must not be kept alive by an unrelated host timer.");
            health.Stop();
            Assert.That(health.Ready, Is.False);
        }
        finally { stop.Cancel(); await run; }
    }
}
