using System.Threading.RateLimiting;
using SelfHostedEvaluators.Core.Configuration;
using SelfHostedEvaluators.Core.Diagnostics;

namespace SelfHostedEvaluators.Core.Auth;

/// <summary>Enforces a plugin's replenishing outbound request budget and shared Retry-After pauses.</summary>
/// <remarks>Acquire exactly one permit per request. A lease is not a batch-concurrency permit.</remarks>
public sealed class PluginRequestLimiter : RateLimiter
{
    private readonly object sync = new();
    private readonly RateLimitOptions options;
    private readonly TimeProvider time;
    private readonly CancellationTokenSource shutdown = new();
    private DateTimeOffset refillAt;
    private DateTimeOffset pausedUntil;
    private int available;
    private long successful;
    private long failed;
    private bool disposed;
    private readonly RuntimeDiagnostics? diagnostics;
    private readonly string? plugin;

    /// <summary>Creates a limiter with an initially full request budget.</summary>
    /// <param name="options">The positive request count and replenishment period.</param>
    /// <param name="time">An optional clock, defaulting to system time.</param>
    /// <param name="diagnostics">Optional metrics for time spent waiting.</param>
    /// <param name="plugin">The plugin name to attach to wait metrics.</param>
    public PluginRequestLimiter(RateLimitOptions options, TimeProvider? time = null,
        RuntimeDiagnostics? diagnostics = null, string? plugin = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.Requests);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.Per, TimeSpan.Zero);
        this.options = options;
        this.time = time ?? TimeProvider.System;
        this.diagnostics = diagnostics;
        this.plugin = plugin;
        available = options.Requests;
        refillAt = this.time.GetUtcNow() + options.Per;
    }

    /// <summary>Gets null because this limiter does not track idle duration.</summary>
    public override TimeSpan? IdleDuration => null;
    /// <summary>Defers new requests until at least the requested delay has elapsed.</summary>
    /// <param name="delay">A positive pause; nonpositive delays leave the current pause unchanged.</param>
    public void Pause(TimeSpan delay)
    {
        if (delay <= TimeSpan.Zero) return;
        lock (sync)
        {
            var until = time.GetUtcNow() + delay;
            if (until > pausedUntil) pausedUntil = until;
        }
    }

    private TimeSpan Delay()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var now = time.GetUtcNow();
        if (now >= refillAt) { available = options.Requests; refillAt = now + options.Per; }
        if (now < pausedUntil) return pausedUntil - now;
        return available > 0 ? TimeSpan.Zero : refillAt - now;
    }

    /// <inheritdoc />
    protected override RateLimitLease AttemptAcquireCore(int permitCount)
    {
        if (permitCount != 1) throw new ArgumentOutOfRangeException(nameof(permitCount), "Acquire one permit per outbound request.");
        lock (sync)
        {
            if (Delay() > TimeSpan.Zero) { failed++; return new Lease(false); }
            available--;
            successful++;
            return new Lease(true);
        }
    }

    internal ValueTask<RateLimitLease> AcquireWithinAsync(TimeSpan remaining, CancellationToken ct) =>
        AcquireCoreAsync(1, ct, time.GetUtcNow() + remaining);

    /// <inheritdoc />
    protected override ValueTask<RateLimitLease> AcquireAsyncCore(int permitCount, CancellationToken ct) =>
        AcquireCoreAsync(permitCount, ct, null);

    private async ValueTask<RateLimitLease> AcquireCoreAsync(int permitCount, CancellationToken ct, DateTimeOffset? deadline)
    {
        if (permitCount != 1) throw new ArgumentOutOfRangeException(nameof(permitCount), "Acquire one permit per outbound request.");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, shutdown.Token);
        var started = time.GetTimestamp();
        try
        {
            while (true)
            {
                linked.Token.ThrowIfCancellationRequested();
                TimeSpan delay;
                lock (sync)
                {
                    delay = Delay();
                    if (deadline is { } end && (end <= time.GetUtcNow() || delay >= end - time.GetUtcNow()))
                    { failed++; return new Lease(false); }
                    if (delay == TimeSpan.Zero) { available--; successful++; return new Lease(true); }
                }
                await Task.Delay(delay, time, linked.Token).ConfigureAwait(false);
            }
        }
        finally { if (plugin is not null) diagnostics?.SinkRateLimitWait(plugin, time.GetElapsedTime(started)); }
    }

    /// <summary>Gets a synchronized snapshot of available permits and lease outcomes.</summary>
    public override RateLimiterStatistics GetStatistics()
    {
        lock (sync) return new()
        { CurrentAvailablePermits = available, CurrentQueuedCount = 0, TotalSuccessfulLeases = successful, TotalFailedLeases = failed };
    }

    /// <summary>Marks the limiter disposed and cancels pending request acquisitions.</summary>
    /// <param name="disposing">Whether managed resources are being disposed.</param>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            lock (sync) { if (disposed) return; disposed = true; }
            shutdown.Cancel();
        }
        base.Dispose(disposing);
    }

    private sealed class Lease(bool acquired) : RateLimitLease
    {
        public override bool IsAcquired => acquired;
        public override IEnumerable<string> MetadataNames => [];
        public override bool TryGetMetadata(string metadataName, out object? metadata) { metadata = null; return false; }
    }
}
