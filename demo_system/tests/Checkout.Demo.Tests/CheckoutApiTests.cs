using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Checkout.Demo.Tests;

[Parallelizable(ParallelScope.All)]
public class CheckoutApiTests
{
    [Test]
    public async Task HealthyCheckoutReturnsSyntheticOrderAfterSmallDelay()
    {
        await using var api = await ApiFixture.StartAsync();
        var elapsed = Stopwatch.StartNew();
        using var response = await api.CheckoutAsync();
        elapsed.Stop();
        var order = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(Guid.Parse(order.GetProperty("orderId").GetString()!), Is.Not.EqualTo(Guid.Empty));
            Assert.That(order.GetProperty("productId").GetString(), Is.EqualTo("demo-item"));
            Assert.That(order.GetProperty("quantity").GetInt32(), Is.EqualTo(1));
            Assert.That(order.GetProperty("status").GetString(), Is.EqualTo("confirmed"));
            Assert.That(elapsed.Elapsed, Is.GreaterThanOrEqualTo(TimeSpan.FromMilliseconds(30)));
            Assert.That(elapsed.Elapsed, Is.LessThan(TimeSpan.FromSeconds(3)));
        });
    }

    [TestCase("{}")]
    [TestCase("null")]
    [TestCase("[]")]
    [TestCase("{broken")]
    [TestCase("""{"productId":"demo-item"}""")]
    [TestCase("""{"quantity":1}""")]
    [TestCase("""{"productId":null,"quantity":1}""")]
    [TestCase("""{"productId":"","quantity":1}""")]
    [TestCase("""{"productId":"  ","quantity":1}""")]
    [TestCase("""{"productId":"demo-item","quantity":0}""")]
    [TestCase("""{"productId":"demo-item","quantity":-1}""")]
    [TestCase("""{"productId":"demo-item","quantity":101}""")]
    [TestCase("""{"productId":"demo-item","quantity":2147483648}""")]
    [TestCase("""{"productId":"demo-item","quantity":1.5}""")]
    [TestCase("""{"productId":"demo-item","quantity":"1"}""")]
    public async Task InvalidCheckoutIsExplicitBadRequest(string json)
    {
        await using var api = await ApiFixture.StartAsync();
        using var response = await api.Client.PostAsync("/checkout",
            new StringContent(json, Encoding.UTF8, "application/json"));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        var error = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.That(error.GetProperty("error").GetString(), Is.Not.Empty);
    }

    [Test]
    public async Task ProductLengthIsBoundedAndUpperValidQuantityIsAccepted()
    {
        await using var api = await ApiFixture.StartAsync();
        using var invalid = await api.Client.PostAsJsonAsync("/checkout",
            new { productId = new string('x', 101), quantity = 1 });
        using var valid = await api.Client.PostAsJsonAsync("/checkout",
            new { productId = new string('x', 100), quantity = 100 });
        Assert.Multiple(() =>
        {
            Assert.That(invalid.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(valid.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        });
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task OversizedKnownOrChunkedBodyIsExplicitBadRequest(bool chunked)
    {
        await using var api = await ApiFixture.StartAsync();
        var body = "{\"productId\":\"" + new string('x', 5000) + "\",\"quantity\":1}";
        using HttpContent content = chunked
            ? new ChunkedJsonContent(body)
            : new StringContent(body, Encoding.UTF8, "application/json");
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var response = await api.Client.PostAsync("/checkout", content);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [Test]
    public async Task NonJsonCheckoutIsExplicitBadRequest()
    {
        await using var api = await ApiFixture.StartAsync();
        using var response = await api.Client.PostAsync("/checkout",
            new StringContent("""{"productId":"demo-item","quantity":1}"""));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [Test]
    public async Task ReadinessAndScrapesDoNotInventCheckoutActivityOrProcessCollectors()
    {
        await using var api = await ApiFixture.StartAsync();
        using var health = await api.Client.GetAsync("/healthz");
        _ = await api.MetricsAsync();
        var metrics = await api.MetricsAsync();
        Assert.Multiple(() =>
        {
            Assert.That(health.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(ApiFixture.Sample(metrics, """http_requests_total{status="200"}"""), Is.Zero);
            Assert.That(ApiFixture.Sample(metrics, """http_requests_total{status="400"}"""), Is.Zero);
            Assert.That(ApiFixture.Sample(metrics, """http_requests_total{status="500"}"""), Is.Zero);
            Assert.That(ApiFixture.Sample(metrics, "http_request_duration_seconds_count"), Is.Zero);
            Assert.That(ApiFixture.Sample(metrics, "checkout_last_request_timestamp_seconds"), Is.Zero);
            Assert.That(metrics, Does.Not.Contain("process_").And.Not.Contain("dotnet_"));
        });
    }

    [Test]
    public async Task OnlyCheckoutRecordsStatusLatencyBucketsAndReceiptTimestamp()
    {
        await using var api = await ApiFixture.StartAsync();
        var before = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000d;
        using var invalid = await api.Client.PostAsync("/checkout",
            new StringContent("{broken", Encoding.UTF8, "application/json"));
        using var valid = await api.CheckoutAsync();
        using var health = await api.Client.GetAsync("/healthz");
        var after = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000d;
        var metrics = await api.MetricsAsync();
        Assert.Multiple(() =>
        {
            Assert.That(ApiFixture.Sample(metrics, """http_requests_total{status="200"}"""), Is.EqualTo(1));
            Assert.That(ApiFixture.Sample(metrics, """http_requests_total{status="400"}"""), Is.EqualTo(1));
            Assert.That(ApiFixture.Sample(metrics, """http_requests_total{status="500"}"""), Is.Zero);
            Assert.That(ApiFixture.Sample(metrics, "http_request_duration_seconds_count"), Is.EqualTo(2));
            Assert.That(ApiFixture.Sample(metrics, "http_request_duration_seconds_sum"), Is.GreaterThan(0.03));
            Assert.That(ApiFixture.Sample(metrics, "checkout_last_request_timestamp_seconds"),
                Is.InRange(before, after));
            foreach (var bucket in new[] { "0.1", "0.25", "0.5", "1", "2", "5", "+Inf" })
                Assert.That(metrics, Does.Contain($$"""http_request_duration_seconds_bucket{le="{{bucket}}"} """));
        });
    }
}
