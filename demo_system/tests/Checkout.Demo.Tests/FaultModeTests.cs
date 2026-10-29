using System.Diagnostics;
using System.Net;
using System.Text;

namespace Checkout.Demo.Tests;

[Parallelizable(ParallelScope.All)]
public class FaultModeTests
{
    [TestCase("healthy", 50, HttpStatusCode.OK)]
    [TestCase("slow", 750, HttpStatusCode.OK)]
    [TestCase("very-slow", 1500, HttpStatusCode.OK)]
    [TestCase("errors", 0, HttpStatusCode.InternalServerError)]
    public async Task ManualModeControlsCheckoutButNotReadiness(string mode, int milliseconds, HttpStatusCode expected)
    {
        await using var api = await ApiFixture.StartAsync();
        using var fault = await api.FaultAsync(mode);
        Assert.That(fault.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var elapsed = Stopwatch.StartNew();
        using var checkout = await api.CheckoutAsync();
        elapsed.Stop();
        using var readiness = await api.Client.GetAsync("/healthz");
        Assert.Multiple(() =>
        {
            Assert.That(checkout.StatusCode, Is.EqualTo(expected));
            Assert.That(readiness.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(elapsed.Elapsed.TotalMilliseconds, Is.GreaterThanOrEqualTo(milliseconds - 20));
            Assert.That(elapsed.Elapsed.TotalMilliseconds, Is.LessThan(milliseconds + 3000));
        });
    }

    [Test]
    public async Task ErrorsPersistForEveryValidCheckoutUntilExplicitHealthyRecovery()
    {
        await using var api = await ApiFixture.StartAsync();
        using var fault = await api.FaultAsync("errors");
        using var first = await api.CheckoutAsync();
        using var second = await api.CheckoutAsync();
        using var invalid = await api.Client.PostAsync("/checkout",
            new StringContent("{}", Encoding.UTF8, "application/json"));
        using var recovery = await api.FaultAsync("healthy");
        using var recovered = await api.CheckoutAsync();
        var metrics = await api.MetricsAsync();
        Assert.Multiple(() =>
        {
            Assert.That(first.StatusCode, Is.EqualTo(HttpStatusCode.InternalServerError));
            Assert.That(second.StatusCode, Is.EqualTo(HttpStatusCode.InternalServerError));
            Assert.That(invalid.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(recovered.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(ApiFixture.Sample(metrics, """http_requests_total{status="500"}"""), Is.EqualTo(2));
            Assert.That(ApiFixture.Sample(metrics, """http_requests_total{status="400"}"""), Is.EqualTo(1));
            Assert.That(ApiFixture.Sample(metrics, """http_requests_total{status="200"}"""), Is.EqualTo(1));
            Assert.That(ApiFixture.Sample(metrics, "http_request_duration_seconds_count"), Is.EqualTo(4));
        });
    }

    [TestCase("unknown")]
    [TestCase("HEALTHY")]
    [TestCase("very_slow")]
    [TestCase("")]
    [TestCase("healthy ")]
    public async Task InvalidModeDoesNotResetExistingFault(string mode)
    {
        await using var api = await ApiFixture.StartAsync();
        using var fault = await api.FaultAsync("errors");
        using var invalid = await api.FaultAsync(mode);
        using var checkout = await api.CheckoutAsync();
        Assert.Multiple(() =>
        {
            Assert.That(invalid.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(checkout.StatusCode, Is.EqualTo(HttpStatusCode.InternalServerError));
        });
    }

    [TestCase("{}")]
    [TestCase("null")]
    [TestCase("[]")]
    [TestCase("{broken")]
    [TestCase("""{"mode":null}""")]
    [TestCase("""{"mode":42}""")]
    public async Task InvalidFaultBodyIsBoundedBadRequestWithoutChangingState(string body)
    {
        await using var api = await ApiFixture.StartAsync();
        using var fault = await api.FaultAsync("errors");
        using var invalid = await api.Client.PostAsync("/admin/fault",
            new StringContent(body, Encoding.UTF8, "application/json"));
        using var checkout = await api.CheckoutAsync();
        Assert.Multiple(() =>
        {
            Assert.That(invalid.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(checkout.StatusCode, Is.EqualTo(HttpStatusCode.InternalServerError));
        });
    }

    [Test]
    public async Task ManagementReadinessAndScrapesDoNotChangeAnyCheckoutMetric()
    {
        await using var api = await ApiFixture.StartAsync();
        var before = await api.MetricsAsync();
        using var fault = await api.FaultAsync("slow");
        using var invalid = await api.FaultAsync("unknown");
        using var health = await api.Client.GetAsync("/healthz");
        _ = await api.MetricsAsync();
        var after = await api.MetricsAsync();
        Assert.Multiple(() =>
        {
            Assert.That(fault.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(invalid.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(after, Is.EqualTo(before));
        });
    }

    [Test]
    public async Task NewApplicationStartsHealthyWithIndependentMetrics()
    {
        await using var first = await ApiFixture.StartAsync();
        using var fault = await first.FaultAsync("errors");
        using var failed = await first.CheckoutAsync();
        await using var second = await ApiFixture.StartAsync();
        using var checkout = await second.CheckoutAsync();
        var metrics = await second.MetricsAsync();
        Assert.Multiple(() =>
        {
            Assert.That(failed.StatusCode, Is.EqualTo(HttpStatusCode.InternalServerError));
            Assert.That(checkout.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(ApiFixture.Sample(metrics, """http_requests_total{status="200"}"""), Is.EqualTo(1));
            Assert.That(ApiFixture.Sample(metrics, """http_requests_total{status="500"}"""), Is.Zero);
        });
    }

    [Test]
    public async Task ConcurrentRequestsAndFaultUpdatesRetainExactBoundedTotals()
    {
        await using var api = await ApiFixture.StartAsync();
        var checkouts = Enumerable.Range(0, 40).Select(async _ =>
        {
            using var result = await api.CheckoutAsync();
            Assert.That(result.StatusCode, Is.AnyOf(HttpStatusCode.OK, HttpStatusCode.InternalServerError));
        });
        var management = Enumerable.Range(0, 40).Select(async index =>
        {
            using var result = await api.FaultAsync(index % 2 == 0 ? "errors" : "healthy");
            Assert.That(result.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        });
        await Task.WhenAll(checkouts.Concat(management));
        var metrics = await api.MetricsAsync();
        var successes = ApiFixture.Sample(metrics, """http_requests_total{status="200"}""");
        var errors = ApiFixture.Sample(metrics, """http_requests_total{status="500"}""");
        Assert.Multiple(() =>
        {
            Assert.That(successes + errors, Is.EqualTo(40));
            Assert.That(ApiFixture.Sample(metrics, """http_requests_total{status="400"}"""), Is.Zero);
            Assert.That(ApiFixture.Sample(metrics, "http_request_duration_seconds_count"), Is.EqualTo(40));
            Assert.That(metrics.Split('\n').Count(line => line.StartsWith("http_requests_total{", StringComparison.Ordinal)),
                Is.EqualTo(3));
        });
    }
}
