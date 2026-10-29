using System.Text;
using Microsoft.Extensions.Time.Testing;

namespace Checkout.Demo.Tests;

[Parallelizable(ParallelScope.All)]
public class CheckoutMetricsTests
{
    [Test]
    public async Task ReceiptUsesInjectedUtcClockBeforeCompletionAndRegistriesAreIndependent()
    {
        var time = new FakeTimeProvider(DateTimeOffset.FromUnixTimeSeconds(1_700_000_000));
        var first = new CheckoutMetrics(time);
        var second = new CheckoutMetrics(time);
        first.RecordReceived();
        var receipt = await ExportAsync(first);
        time.Advance(TimeSpan.FromSeconds(2));
        first.RecordReceived();
        first.RecordCompleted(500, TimeSpan.FromMilliseconds(1500));
        var completed = await ExportAsync(first);
        var untouched = await ExportAsync(second);
        Assert.Multiple(() =>
        {
            Assert.That(ApiFixture.Sample(receipt, "checkout_last_request_timestamp_seconds"), Is.EqualTo(1_700_000_000));
            Assert.That(ApiFixture.Sample(receipt, "http_request_duration_seconds_count"), Is.Zero);
            Assert.That(ApiFixture.Sample(completed, "checkout_last_request_timestamp_seconds"), Is.EqualTo(1_700_000_002));
            Assert.That(ApiFixture.Sample(completed, """http_requests_total{status="500"}"""), Is.EqualTo(1));
            Assert.That(ApiFixture.Sample(completed, """http_request_duration_seconds_bucket{le="1"}"""), Is.Zero);
            Assert.That(ApiFixture.Sample(completed, """http_request_duration_seconds_bucket{le="2"}"""), Is.EqualTo(1));
            Assert.That(ApiFixture.Sample(completed, "http_request_duration_seconds_sum"), Is.EqualTo(1.5));
            Assert.That(ApiFixture.Sample(untouched, "checkout_last_request_timestamp_seconds"), Is.Zero);
            Assert.That(ApiFixture.Sample(untouched, """http_requests_total{status="500"}"""), Is.Zero);
        });
    }

    [Test]
    public async Task ConcurrentRecordingAndScrapingDoNotLoseCompletedRequests()
    {
        var metrics = new CheckoutMetrics(TimeProvider.System);
        var recording = Task.Run(() => Parallel.For(0, 1000, _ =>
        {
            metrics.RecordReceived();
            metrics.RecordCompleted(200, TimeSpan.FromMilliseconds(50));
        }));
        for (var i = 0; i < 10; i++) _ = await ExportAsync(metrics);
        await recording;
        var exported = await ExportAsync(metrics);
        Assert.Multiple(() =>
        {
            Assert.That(ApiFixture.Sample(exported, """http_requests_total{status="200"}"""), Is.EqualTo(1000));
            Assert.That(ApiFixture.Sample(exported, "http_request_duration_seconds_count"), Is.EqualTo(1000));
            Assert.That(ApiFixture.Sample(exported, """http_request_duration_seconds_bucket{le="0.1"}"""), Is.EqualTo(1000));
            Assert.That(ApiFixture.Sample(exported, "http_request_duration_seconds_sum"), Is.EqualTo(50).Within(0.00001));
        });
    }

    private static async Task<string> ExportAsync(CheckoutMetrics metrics)
    {
        await using var stream = new MemoryStream();
        await metrics.ExportAsync(stream);
        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
