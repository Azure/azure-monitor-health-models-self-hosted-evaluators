namespace Checkout.Demo;

public static class TrafficRunner
{
    public static void ValidateInterval(TimeSpan interval)
    {
        if (interval < TimeSpan.FromSeconds(1) || interval.TotalMilliseconds > uint.MaxValue - 1)
            throw new ArgumentOutOfRangeException(nameof(interval),
                "The interval must be at least one second and fit within a single timer delay.");
    }

    public static async Task RunAsync(Func<CancellationToken, Task> send, TimeSpan interval,
        TimeProvider time, CancellationToken ct, ILogger? logger = null)
    {
        ValidateInterval(interval);
        ArgumentNullException.ThrowIfNull(send);
        ArgumentNullException.ThrowIfNull(time);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await send(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (HttpRequestException exception) { Report(exception); }
            catch (OperationCanceledException exception) { Report(exception); }

            try
            {
                await Task.Delay(interval, time, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
        }

        void Report(Exception exception)
        {
            if (logger is not null)
                logger.LogWarning(exception, "Checkout attempt failed; waiting {IntervalSeconds} seconds.",
                    interval.TotalSeconds);
            else
                Console.Error.WriteLine($"Checkout attempt failed: {exception.GetType().Name}: {exception.Message}");
        }
    }
}
