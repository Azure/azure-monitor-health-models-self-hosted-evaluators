using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Checkout.Demo.Tests;

[Parallelizable(ParallelScope.All)]
public class TrafficRunnerTests
{
    [TestCase(0)]
    [TestCase(-1)]
    [TestCase(0.999)]
    [TestCase(4294968)]
    public void UnsafeIntervalIsRejectedBeforeAnySend(double seconds)
    {
        var calls = 0;
        var interval = TimeSpan.FromSeconds(seconds);
        Assert.Throws<ArgumentOutOfRangeException>(() => TrafficRunner.ValidateInterval(interval));
        Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => TrafficRunner.RunAsync(_ =>
        {
            calls++;
            return Task.CompletedTask;
        }, interval, TimeProvider.System, CancellationToken.None));
        Assert.That(calls, Is.Zero);
    }

    [TestCase(null, 5)]
    [TestCase("1", 1)]
    public async Task DefaultAndMinimumIntervalsWaitFullyAfterCompletion(string? setting, int seconds)
    {
        using var time = new ObservedTime();
        using var stop = new CancellationTokenSource();
        var options = TrafficOptions.Parse(setting, null);
        var calls = 0;
        var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = TrafficRunner.RunAsync(_ =>
        {
            if (Interlocked.Increment(ref calls) == 2) second.TrySetResult();
            return Task.CompletedTask;
        }, options.Interval, time, stop.Token);
        try
        {
            await time.WaitForTimerAsync();
            Assert.That(calls, Is.EqualTo(1));
            time.Advance(TimeSpan.FromSeconds(seconds) - TimeSpan.FromMilliseconds(1));
            Assert.That(calls, Is.EqualTo(1));
            time.Advance(TimeSpan.FromMilliseconds(1));
            await second.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.That(calls, Is.EqualTo(2));
        }
        finally { stop.Cancel(); await run; }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task HttpFailureOrTimeoutIsVisibleAndWaitsBeforeNextAttempt(bool timeout)
    {
        using var time = new ObservedTime();
        using var stop = new CancellationTokenSource();
        var log = new RecordingLogger();
        var calls = 0;
        var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = TrafficRunner.RunAsync(_ =>
        {
            if (Interlocked.Increment(ref calls) == 1)
                return Task.FromException(timeout
                    ? new TaskCanceledException("request timed out")
                    : new HttpRequestException("HTTP 500"));
            second.TrySetResult();
            return Task.CompletedTask;
        }, TimeSpan.FromSeconds(1), time, stop.Token, log);
        try
        {
            await time.WaitForTimerAsync();
            Assert.That(log.Messages, Has.Count.EqualTo(1));
            Assert.That(log.Messages.Single(), Does.Contain(timeout ? "request timed out" : "HTTP 500"));
            time.Advance(TimeSpan.FromMilliseconds(999));
            Assert.That(calls, Is.EqualTo(1));
            time.Advance(TimeSpan.FromMilliseconds(1));
            await second.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.That(calls, Is.EqualTo(2));
        }
        finally { stop.Cancel(); await run; }
    }

    [Test]
    public async Task SlowSendsAreSerializedAndMissedIntervalsNeverCatchUp()
    {
        using var time = new ObservedTime();
        using var stop = new CancellationTokenSource();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var third = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var active = 0;
        var maximumActive = 0;
        var run = TrafficRunner.RunAsync(async ct =>
        {
            maximumActive = Math.Max(maximumActive, Interlocked.Increment(ref active));
            var call = Interlocked.Increment(ref calls);
            try
            {
                if (call == 1) await release.Task.WaitAsync(ct);
                if (call == 2) second.TrySetResult();
                if (call == 3) third.TrySetResult();
            }
            finally { Interlocked.Decrement(ref active); }
        }, TimeSpan.FromSeconds(1), time, stop.Token);
        try
        {
            Assert.That(calls, Is.EqualTo(1));
            time.Advance(TimeSpan.FromSeconds(60));
            Assert.That(calls, Is.EqualTo(1));
            release.SetResult();
            await time.WaitForTimerAsync();
            time.Advance(TimeSpan.FromMilliseconds(999));
            Assert.That(calls, Is.EqualTo(1));
            time.Advance(TimeSpan.FromMilliseconds(1));
            await second.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await time.WaitForTimerAsync();
            time.Advance(TimeSpan.FromHours(1));
            await third.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await time.WaitForTimerAsync();
            Assert.Multiple(() =>
            {
                Assert.That(calls, Is.EqualTo(3));
                Assert.That(maximumActive, Is.EqualTo(1));
            });
        }
        finally { stop.Cancel(); await run; }
    }

    [Test]
    public async Task SlowTimeoutStartsItsIntervalOnlyAfterTimeoutCompletes()
    {
        using var time = new ObservedTime();
        using var stop = new CancellationTokenSource();
        var calls = 0;
        var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = TrafficRunner.RunAsync(async ct =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                await Task.Delay(TimeSpan.FromSeconds(10), time, ct);
                throw new TaskCanceledException("request timed out");
            }
            second.TrySetResult();
        }, TimeSpan.FromSeconds(1), time, stop.Token, new RecordingLogger());
        try
        {
            await time.WaitForTimerAsync();
            time.Advance(TimeSpan.FromSeconds(9));
            Assert.That(calls, Is.EqualTo(1));
            time.Advance(TimeSpan.FromSeconds(1));
            await time.WaitForTimerAsync();
            time.Advance(TimeSpan.FromMilliseconds(999));
            Assert.That(calls, Is.EqualTo(1));
            time.Advance(TimeSpan.FromMilliseconds(1));
            await second.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.That(calls, Is.EqualTo(2));
        }
        finally { stop.Cancel(); await run; }
    }

    [Test]
    public async Task AlreadyCancelledRunnerSendsNothing()
    {
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        var calls = 0;
        await TrafficRunner.RunAsync(_ =>
        {
            calls++;
            return Task.CompletedTask;
        }, TimeSpan.FromSeconds(1), TimeProvider.System, stop.Token);
        Assert.That(calls, Is.Zero);
    }

    [Test]
    public async Task CancellationDuringDelayExitsCleanlyWithoutAnotherSend()
    {
        using var time = new ObservedTime();
        using var stop = new CancellationTokenSource();
        var calls = 0;
        var run = TrafficRunner.RunAsync(_ =>
        {
            calls++;
            return Task.CompletedTask;
        }, TimeSpan.FromSeconds(1), time, stop.Token);
        await time.WaitForTimerAsync();
        stop.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(3));
        time.Advance(TimeSpan.FromHours(1));
        Assert.That(calls, Is.EqualTo(1));
    }

    [Test]
    public async Task CancellationDuringSendIsPassedThroughAndExitsCleanly()
    {
        using var time = new ObservedTime();
        using var stop = new CancellationTokenSource();
        var calls = 0;
        var run = TrafficRunner.RunAsync(async ct =>
        {
            calls++;
            await Task.Delay(TimeSpan.FromHours(1), time, ct);
        }, TimeSpan.FromSeconds(1), time, stop.Token);
        await time.WaitForTimerAsync();
        stop.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(3));
        time.Advance(TimeSpan.FromHours(2));
        Assert.That(calls, Is.EqualTo(1));
    }

    [Test]
    public void ProgrammingErrorsAreNotSwallowedOrRetried()
    {
        var calls = 0;
        Assert.ThrowsAsync<InvalidOperationException>(() => TrafficRunner.RunAsync(_ =>
        {
            calls++;
            throw new InvalidOperationException("bug");
        }, TimeSpan.FromSeconds(1), TimeProvider.System, CancellationToken.None));
        Assert.That(calls, Is.EqualTo(1));
    }

    private sealed class RecordingLogger : ILogger
    {
        public ConcurrentQueue<string> Messages { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Messages.Enqueue(formatter(state, exception) + " " + exception?.Message);
    }
}
