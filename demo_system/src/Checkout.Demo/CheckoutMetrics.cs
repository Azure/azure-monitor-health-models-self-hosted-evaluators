using Prometheus;

namespace Checkout.Demo;

public sealed class CheckoutMetrics
{
    private readonly CollectorRegistry _registry = Metrics.NewCustomRegistry();
    private readonly Counter _requests;
    private readonly Histogram _duration;
    private readonly Gauge _lastRequest;
    private readonly TimeProvider _time;
    private readonly object _receiptLock = new();

    public CheckoutMetrics(TimeProvider time)
    {
        _time = time;
        var factory = Metrics.WithCustomRegistry(_registry);
        _requests = factory.CreateCounter("http_requests_total", "Completed checkout requests by HTTP status.",
            new CounterConfiguration { LabelNames = ["status"] });
        foreach (var status in new[] { "200", "400", "500" })
            _requests.WithLabels(status).Inc(0);
        _duration = factory.CreateHistogram("http_request_duration_seconds", "Checkout request duration in seconds.",
            new HistogramConfiguration { Buckets = [0.1, 0.25, 0.5, 1, 2, 5] });
        _lastRequest = factory.CreateGauge("checkout_last_request_timestamp_seconds",
            "UTC Unix timestamp of the most recently received checkout; zero before any traffic.");
    }

    public void RecordReceived()
    {
        lock (_receiptLock)
            _lastRequest.Set(_time.GetUtcNow().ToUnixTimeMilliseconds() / 1000d);
    }

    public void RecordCompleted(int status, TimeSpan duration)
    {
        var label = status switch
        {
            200 => "200",
            400 => "400",
            500 => "500",
            _ => throw new ArgumentOutOfRangeException(nameof(status))
        };
        _requests.WithLabels(label).Inc();
        _duration.Observe(duration.TotalSeconds);
    }

    public Task ExportAsync(Stream destination, CancellationToken ct = default) =>
        _registry.CollectAndExportAsTextAsync(destination, ct);
}
