using System.Globalization;
using System.Diagnostics;
using System.Text;
using SelfHostedEvaluators.Core.Configuration;
using SelfHostedEvaluators.Core.Diagnostics;
using SelfHostedEvaluators.Plugins.Abstractions;

namespace SelfHostedEvaluators.Core.Polling;

/// <summary>Schedules immediate startup polls and aligned plugin/interval ticks without overlapping a group's work.</summary>
/// <param name="time">An optional clock for stable scheduling and heartbeat checks.</param>
/// <param name="diagnostics">Optional metrics for ticks, skipped work and callback failures.</param>
public sealed class Scheduler(TimeProvider? time = null, RuntimeDiagnostics? diagnostics = null) : IAsyncDisposable
{
    private static readonly ActivitySource Activities = new("SelfHostedEvaluators.Core.Polling");
    private readonly TimeProvider time = time ?? TimeProvider.System;
    private readonly RuntimeDiagnostics diagnostics = diagnostics ?? RuntimeDiagnostics.Shared;
    private readonly object sync = new();
    private readonly CancellationTokenSource shutdown = new();
    private readonly HashSet<Task> active = [];
    private Task? run;
    private Task? disposal;
    private long heartbeat;

    /// <summary>Checks that the scheduling loop is still running and has advanced within the allowed window.</summary>
    /// <param name="window">The maximum acceptable time since its last heartbeat.</param>
    /// <returns>Whether the scheduler has a recent heartbeat and an incomplete run task.</returns>
    public bool HasRecentHeartbeat(TimeSpan window) =>
        time.GetElapsedTime(Volatile.Read(ref heartbeat)) <= window &&
        run is { IsCompleted: false };

    /// <summary>Runs one startup snapshot, grouping signals by plugin and interval and splitting configured batches.</summary>
    /// <param name="snapshot">The immutable snapshot to schedule for this scheduler's lifetime.</param>
    /// <param name="onTick">The callback invoked for each scheduled batch.</param>
    /// <param name="ct">Stops scheduling and requests callback cancellation.</param>
    /// <returns>The scheduling-loop lifetime; use DrainAsync to observe outstanding callbacks.</returns>
    public Task RunAsync(ConfigSnapshot snapshot, Func<ScheduledBatch, CancellationToken, Task> onTick, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(onTick);
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposal is not null, this);
            if (run is not null) throw new InvalidOperationException("A scheduler runs one startup snapshot.");
            run = RunCoreAsync(snapshot, onTick, ct);
            return run;
        }
    }

    private async Task RunCoreAsync(ConfigSnapshot snapshot, Func<ScheduledBatch, CancellationToken, Task> onTick, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, shutdown.Token);
        var sizes = snapshot.Plugins.ToDictionary(p => p.Name, p => p.Batch.MaxSize, StringComparer.Ordinal);
        var groups = snapshot.Models.SelectMany(m => m.Signals.Select(s => new
        {
            s.Plugin, s.Interval, Query = new SignalQuery(new(m.HealthModel.DataPlaneUri, s.Entity, s.Name), s.Query)
        })).GroupBy(s => (s.Plugin, s.Interval));
        var loops = groups.Select(g => RunGroupAsync(g.Key.Plugin, g.Key.Interval,
            g.Select(s => s.Query).Chunk(sizes.GetValueOrDefault(g.Key.Plugin, 1)).ToArray(), onTick, linked.Token)).ToArray();
        try
        {
            while (!linked.IsCancellationRequested)
            {
                foreach (var loop in loops.Where(l => l.IsCompleted))
                    await loop.ConfigureAwait(false);
                Volatile.Write(ref heartbeat, time.GetTimestamp());
                await Task.Delay(TimeSpan.FromSeconds(1), time, linked.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
        finally
        {
            await linked.CancelAsync().ConfigureAwait(false);
            await Task.WhenAll(loops).ConfigureAwait(false);
        }
    }

    private async Task RunGroupAsync(string plugin, TimeSpan interval, SignalQuery[][] batches,
        Func<ScheduledBatch, CancellationToken, Task> onTick, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return;
        Task tick = StartTick(time.GetUtcNow());
        var next = NextTick(plugin, interval, time.GetUtcNow());
        try
        {
            while (true)
            {
                var delay = next - time.GetUtcNow();
                if (delay > TimeSpan.Zero) await Task.Delay(delay, time, ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                var now = time.GetUtcNow();
                var missed = Math.Max(0, (now - next).Ticks / interval.Ticks);
                if (missed > 0)
                {
                    diagnostics.Skip(plugin, missed);
                    next += TimeSpan.FromTicks(missed * interval.Ticks);
                }
                if (tick.IsCompleted) tick = StartTick(next);
                else diagnostics.Skip(plugin, 1);
                next += interval;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }

        Task StartTick(DateTimeOffset scheduledAt)
        {
            diagnostics.Tick(plugin, time.GetUtcNow() - scheduledAt);
            var parent = Activity.Current;
            var tickSpan = Activities.StartActivity("group.tick");
            tickSpan?.SetTag("plugin", plugin);
            // Offload the invocation itself, not just its await: a plugin/callback can block before returning a task.
            Task task;
            try
            {
                task = CompleteTickAsync(Task.WhenAll(batches.Select(batch => Task.Run(async () =>
                {
                    try
                    {
                        using var batchSpan = Activities.StartActivity("poll.batch");
                        batchSpan?.SetTag("plugin", plugin);
                        ct.ThrowIfCancellationRequested();
                        await onTick(new(plugin, new(scheduledAt, interval), batch), ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
                    catch (Exception) { diagnostics.CallbackFailed(plugin); }
                }))), tickSpan);
            }
            finally { Activity.Current = parent; }
            lock (sync) active.Add(task);
            _ = task.ContinueWith(completed => { lock (sync) active.Remove(completed); },
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return task;
        }
    }

    private static async Task CompleteTickAsync(Task task, Activity? span)
    {
        try { await task.ConfigureAwait(false); }
        finally { span?.Dispose(); }
    }

    private static DateTimeOffset NextTick(string plugin, TimeSpan interval, DateTimeOffset now)
    {
        // FNV-1a is stable across processes, unlike string.GetHashCode().
        ulong hash = 14695981039346656037;
        foreach (var b in Encoding.UTF8.GetBytes(plugin + ":" + interval.Ticks.ToString(CultureInfo.InvariantCulture)))
            hash = unchecked((hash ^ b) * 1099511628211);
        var offset = (long)(hash % (ulong)interval.Ticks);
        var elapsed = (now - DateTimeOffset.UnixEpoch).Ticks;
        var floor = elapsed - ((elapsed % interval.Ticks + interval.Ticks) % interval.Ticks);
        var target = DateTimeOffset.UnixEpoch + TimeSpan.FromTicks(floor + offset);
        return target > now ? target : target + interval;
    }

    /// <summary>Waits for the callbacks active when draining begins, without forcibly terminating them.</summary>
    /// <param name="ct">Cancels only the wait for callbacks to finish.</param>
    public Task DrainAsync(CancellationToken ct)
    {
        lock (sync) return Task.WhenAll(active.ToArray()).WaitAsync(ct);
    }

    /// <summary>Stops scheduling and waits for all tracked callbacks before disposing shutdown resources.</summary>
    public ValueTask DisposeAsync()
    {
        lock (sync)
        {
            disposal ??= DisposeCoreAsync();
            return new(disposal);
        }
    }

    private async Task DisposeCoreAsync()
    {
        await shutdown.CancelAsync().ConfigureAwait(false);
        if (run is not null) await run.ConfigureAwait(false);
        await DrainAsync(CancellationToken.None).ConfigureAwait(false);
        shutdown.Dispose();
    }
}
