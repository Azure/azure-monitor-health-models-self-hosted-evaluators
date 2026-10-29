namespace SelfHostedEvaluators.Core.Reporting;

internal sealed class ModelRequestBudget(int capacity, TimeProvider time)
{
    private readonly object sync = new();
    private double tokens = capacity;
    private long last = time.GetTimestamp();
    private DateTimeOffset pausedUntil;

    public void Pause(TimeSpan delay)
    {
        lock (sync)
        {
            var now = time.GetUtcNow();
            var until = now + (delay < DateTimeOffset.MaxValue - now ? delay : DateTimeOffset.MaxValue - now);
            if (until > pausedUntil) pausedUntil = until;
        }
    }

    public async ValueTask AcquireAsync(CancellationToken ct, DateTimeOffset? deadline = null)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            TimeSpan delay;
            lock (sync)
            {
                var now = time.GetUtcNow();
                if (deadline <= now) throw new OperationCanceledException("Request deadline elapsed.");
                var timestamp = time.GetTimestamp();
                tokens = Math.Min(capacity, tokens + time.GetElapsedTime(last, timestamp).TotalMinutes * capacity);
                last = timestamp;
                var pause = pausedUntil - now;
                if (pause <= TimeSpan.Zero && tokens >= 1) { tokens--; return; }
                var refill = tokens >= 1 ? TimeSpan.Zero :
                    TimeSpan.FromTicks(Math.Max(1, (long)Math.Ceiling((1 - tokens) * TimeSpan.TicksPerMinute / capacity)));
                delay = pause > refill ? pause : refill;
                if (deadline is { } expires && delay > expires - now) delay = expires - now;
            }
            // Long HTTP-date waits are sliced because timers have a finite due-time range.
            await Task.Delay(delay > TimeSpan.FromDays(1) ? TimeSpan.FromDays(1) : delay, time, ct).ConfigureAwait(false);
        }
    }
}
