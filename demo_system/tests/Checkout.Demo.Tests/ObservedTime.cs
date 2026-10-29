using Microsoft.Extensions.Time.Testing;

namespace Checkout.Demo.Tests;

internal sealed class ObservedTime : TimeProvider, IDisposable
{
    private readonly FakeTimeProvider _clock = new();
    private readonly SemaphoreSlim _timers = new(0);
    public override long TimestampFrequency => _clock.TimestampFrequency;
    public override DateTimeOffset GetUtcNow() => _clock.GetUtcNow();
    public override long GetTimestamp() => _clock.GetTimestamp();
    public override TimeZoneInfo LocalTimeZone => _clock.LocalTimeZone;
    public void Advance(TimeSpan interval) => _clock.Advance(interval);

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = _clock.CreateTimer(callback, state, dueTime, period);
        _timers.Release();
        return timer;
    }

    public async Task WaitForTimerAsync()
    {
        Assert.That(await _timers.WaitAsync(TimeSpan.FromSeconds(3)), Is.True, "The next fake-time delay was not scheduled.");
    }

    public void Dispose() => _timers.Dispose();
}
