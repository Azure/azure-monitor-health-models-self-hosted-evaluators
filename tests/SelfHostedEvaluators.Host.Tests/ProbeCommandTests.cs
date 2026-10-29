using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Time.Testing;
using SelfHostedEvaluators.Core.Configuration;
using SelfHostedEvaluators.Core.Polling;

namespace SelfHostedEvaluators.Host.Tests;

[NonParallelizable]
public class ProbeCommandTests
{
    [Test]
    public async Task UnknownProbeIsBoundedAndDoesNotLoadInvalidConfiguration()
    {
        using var fixture = new HostFixture();
        fixture.Environment["SHE_PACKAGES_DIR"] = "not-a-real-directory";
        var result = await fixture.Cli("probe", "unknown");
        Assert.Multiple(() =>
        {
            Assert.That(result.Code, Is.Not.Zero);
            Assert.That(result.Output, Does.Not.Contain("SHE_PACKAGES_DIR"));
            Assert.That(result.Output, Does.Not.Contain("NotSupportedException"));
        });
    }

    [TestCase(200, 0)]
    [TestCase(503, 1)]
    [TestCase(302, 1)]
    public async Task ProbeUsesActualLoopbackHttpAndCliExitCodes(int status, int expected)
    {
        using var server = new TcpListener(IPAddress.Loopback, 8080);
        server.Start();
        using var fixture = new HostFixture();
        fixture.Environment["SHE_PACKAGES_DIR"] = "missing";
        var request = Task.Run(async () =>
        {
            using var client = await server.AcceptTcpClientAsync();
            var buffer = new byte[4096];
            var count = await client.GetStream().ReadAsync(buffer);
            Assert.That(System.Text.Encoding.ASCII.GetString(buffer, 0, count), Does.StartWith("GET /healthz/ready "));
            await client.GetStream().WriteAsync(System.Text.Encoding.ASCII.GetBytes(
                $"HTTP/1.1 {status} Result\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"));
        });
        var result = await fixture.Cli("probe", "ready");
        await request.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(result.Code, Is.EqualTo(expected), result.Output);
        Assert.That(result.Output, status == 200
            ? Does.Contain("Evaluator probe ready succeeded.")
            : Does.Not.Contain("succeeded."));
    }

    [Test]
    public async Task ProbeTimesOutWithoutReadingBodiesOrFollowingRedirects()
    {
        using var server = new TcpListener(IPAddress.Loopback, 8080);
        server.Start();
        var result = new ProbeCommand().ExecuteAsync("live", CancellationToken.None);
        using var client = await server.AcceptTcpClientAsync();
        Assert.That(await result.WaitAsync(TimeSpan.FromSeconds(4)), Is.EqualTo(1));
    }

    [Test]
    public async Task RefusedConnectionAndCancellationFailClosed()
    {
        Assert.That(await new ProbeCommand().ExecuteAsync("live", CancellationToken.None), Is.EqualTo(1));
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        Assert.That(await new ProbeCommand().ExecuteAsync("ready", stop.Token), Is.EqualTo(1));
    }

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
