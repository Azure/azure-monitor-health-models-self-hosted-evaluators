using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SelfHostedEvaluators.Core.Polling;

namespace SelfHostedEvaluators.Host;

/// <summary>Tracks startup, scheduler heartbeat and shutdown for local readiness and liveness probes.</summary>
/// <param name="time">The clock used to measure startup and heartbeat age.</param>
public sealed class RuntimeHealth(TimeProvider time)
{
    /// <summary>The maximum startup duration during which liveness can succeed before scheduler activation.</summary>
    public static readonly TimeSpan InitializationWindow = TimeSpan.FromMinutes(2);
    /// <summary>The maximum scheduler-heartbeat age accepted by the liveness probe.</summary>
    public static readonly TimeSpan HeartbeatWindow = TimeSpan.FromSeconds(15);
    private readonly long started = time.GetTimestamp();
    private Scheduler? scheduler;
    private int stopping;
    /// <summary>Gets whether initialization has activated the scheduler and shutdown has not begun.</summary>
    public bool Ready => Volatile.Read(ref stopping) == 0 && Volatile.Read(ref scheduler) is not null;
    /// <summary>Gets whether startup is within its allowance or the active scheduler has a recent heartbeat.</summary>
    public bool Live => Volatile.Read(ref stopping) == 0 &&
        (Volatile.Read(ref scheduler) is { } active ? active.HasRecentHeartbeat(HeartbeatWindow) :
            time.GetElapsedTime(started) <= InitializationWindow);
    /// <summary>Marks initialization complete by attaching the running scheduler.</summary>
    /// <param name="active">The scheduler used for subsequent heartbeat checks.</param>
    public void Activate(Scheduler active) => Volatile.Write(ref scheduler, active);
    /// <summary>Marks the runtime stopping so both probes fail immediately.</summary>
    public void Stop() => Interlocked.Exchange(ref stopping, 1);
}

internal sealed class HealthEndpoints(RuntimeHealth health, ILoggerFactory logs, HostDependencies dependencies) : IHostedService, IAsyncDisposable
{
    private WebApplication? app;

    public async Task StartAsync(CancellationToken ct)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [] });
        builder.Configuration.Sources.Clear();
        builder.Logging.ClearProviders();
        builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = Timeout.InfiniteTimeSpan);
        builder.Services.AddSingleton(logs);
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, dependencies.ProbePort));
        app = builder.Build();
        app.MapGet("/healthz/ready", (HttpContext context) =>
            context.Response.StatusCode = health.Ready ? 200 : 503);
        app.MapGet("/healthz/live", (HttpContext context) =>
            context.Response.StatusCode = health.Live ? 200 : 503);
        await app.StartAsync(ct).ConfigureAwait(false);
    }

    public Task StopAsync(CancellationToken ct) => app?.StopAsync(ct) ?? Task.CompletedTask;
    public async ValueTask DisposeAsync()
    {
        if (app is not null) await app.DisposeAsync().ConfigureAwait(false);
    }
}
