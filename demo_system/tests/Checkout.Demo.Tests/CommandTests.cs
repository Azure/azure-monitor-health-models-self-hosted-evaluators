using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace Checkout.Demo.Tests;

[Parallelizable(ParallelScope.All)]
public class CommandTests
{
    [TestCase("healthy", HttpStatusCode.OK)]
    [TestCase("slow", HttpStatusCode.OK)]
    [TestCase("very-slow", HttpStatusCode.OK)]
    [TestCase("errors", HttpStatusCode.InternalServerError)]
    public async Task FaultCommandActuallySetsModeThenPrintsExactMarker(string mode, HttpStatusCode expected)
    {
        await using var api = await ApiFixture.StartAsync();
        await using var command = new RunningDemo(["fault", mode], Endpoint(api.Client.BaseAddress!));
        var result = await command.FinishAsync();
        using var checkout = await api.CheckoutAsync();
        Assert.Multiple(() =>
        {
            Assert.That(result.Code, Is.Zero, result.Output);
            Assert.That(result.Output.Trim(), Is.EqualTo($"DEMO_FAULT:{mode}"));
            Assert.That(checkout.StatusCode, Is.EqualTo(expected));
        });
    }

    [TestCase(500)]
    [TestCase(302)]
    public async Task UnsuccessfulOrRedirectingHttpNeverPrintsSuccess(int status)
    {
        var calls = 0;
        await using var server = await HttpStub.StartAsync(context =>
        {
            var call = Interlocked.Increment(ref calls);
            context.Response.StatusCode = call == 1 ? status : 200;
            context.Response.Headers.Location = "/redirected";
            return Task.CompletedTask;
        });
        await using var command = new RunningDemo(["fault", "healthy"], Endpoint(server.Address));
        var result = await command.FinishAsync();
        Assert.Multiple(() =>
        {
            Assert.That(result.Code, Is.Not.Zero);
            Assert.That(result.Output, Does.Not.Contain("DEMO_FAULT:"));
            Assert.That(calls, Is.EqualTo(1), "Internal commands must not follow HTTP redirects.");
        });
    }

    [Test]
    public async Task UnresponsiveInternalHttpIsBoundedAndFailsClosed()
    {
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = await HttpStub.StartAsync(async context =>
        {
            received.TrySetResult();
            await Task.Delay(TimeSpan.FromSeconds(30), context.RequestAborted);
        });
        var elapsed = Stopwatch.StartNew();
        await using var command = new RunningDemo(["fault", "healthy"], Endpoint(server.Address));
        await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var result = await command.FinishAsync();
        Assert.Multiple(() =>
        {
            Assert.That(result.Code, Is.Not.Zero);
            Assert.That(result.Output, Does.Not.Contain("DEMO_FAULT:"));
            Assert.That(elapsed.Elapsed, Is.LessThan(TimeSpan.FromSeconds(8)));
        });
    }

    [Test]
    public async Task RefusedConnectionFailsWithoutSuccessMarker()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        await using var command = new RunningDemo(["fault", "healthy"], Endpoint(new Uri($"http://127.0.0.1:{port}")));
        var result = await command.FinishAsync();
        Assert.Multiple(() =>
        {
            Assert.That(result.Code, Is.Not.Zero);
            Assert.That(result.Output, Does.Not.Contain("DEMO_FAULT:"));
        });
    }

    [TestCase("unknown")]
    [TestCase("probe")]
    [TestCase("fault")]
    [TestCase("fault", "UNKNOWN")]
    [TestCase("fault", "healthy", "extra")]
    [TestCase("traffic", "extra")]
    public async Task InvalidCommandsExitWithoutCallingHttp(params string[] args)
    {
        var calls = 0;
        await using var server = await HttpStub.StartAsync(_ =>
        {
            Interlocked.Increment(ref calls);
            return Task.CompletedTask;
        });
        await using var command = new RunningDemo(args, Endpoint(server.Address));
        var result = await command.FinishAsync();
        Assert.Multiple(() =>
        {
            Assert.That(result.Code, Is.Not.Zero);
            Assert.That(result.Output, Does.Not.Contain("DEMO_FAULT:"));
            Assert.That(calls, Is.Zero);
        });
    }

    [TestCase("0")]
    [TestCase("0.5")]
    [TestCase("invalid")]
    public async Task TrafficRejectsUnsafeConfigurationBeforeSending(string interval)
    {
        var calls = 0;
        await using var server = await HttpStub.StartAsync(_ =>
        {
            Interlocked.Increment(ref calls);
            return Task.CompletedTask;
        });
        var environment = Endpoint(server.Address);
        environment["DEMO_INTERVAL_SECONDS"] = interval;
        await using var command = new RunningDemo(["traffic"], environment);
        var result = await command.FinishAsync();
        Assert.Multiple(() =>
        {
            Assert.That(result.Code, Is.Not.Zero);
            Assert.That(calls, Is.Zero);
            Assert.That(result.Output, Does.Contain("interval").IgnoreCase.Or.Contain("DEMO_INTERVAL_SECONDS"));
        });
    }

    [Test]
    public async Task TrafficExecutableSendsSyntheticCheckoutAndPacesAfterHttpFailures()
    {
        var attempts = new ConcurrentQueue<(long Timestamp, string Method, string Path, string Body)>();
        var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = await HttpStub.StartAsync(async context =>
        {
            using var reader = new StreamReader(context.Request.Body);
            var body = await reader.ReadToEndAsync();
            attempts.Enqueue((Stopwatch.GetTimestamp(), context.Request.Method, context.Request.Path.Value!, body));
            context.Response.StatusCode = 500;
            if (attempts.Count == 2) second.TrySetResult();
        });
        var environment = Endpoint(server.Address);
        environment["DEMO_INTERVAL_SECONDS"] = "1";
        await using var command = new RunningDemo(["traffic"], environment);
        await second.Task.WaitAsync(TimeSpan.FromSeconds(7));
        var received = attempts.ToArray();
        Assert.Multiple(() =>
        {
            Assert.That(received, Has.Length.EqualTo(2));
            Assert.That(Stopwatch.GetElapsedTime(received[0].Timestamp, received[1].Timestamp),
                Is.GreaterThanOrEqualTo(TimeSpan.FromSeconds(1)));
            Assert.That(received[0].Method, Is.EqualTo("POST"));
            Assert.That(received[0].Path, Is.EqualTo("/checkout"));
        });
        using var json = JsonDocument.Parse(received[0].Body);
        Assert.Multiple(() =>
        {
            Assert.That(json.RootElement.GetProperty("productId").GetString(), Is.EqualTo("demo-item"));
            Assert.That(json.RootElement.GetProperty("quantity").GetInt32(), Is.EqualTo(1));
        });
        Assert.That(command.Output, Does.Contain("Checkout attempt failed").And.Contain("500"));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task ApiCommandAndDefaultCommandBothStartRealKestrel(bool explicitApi)
    {
        string[] args = explicitApi ? ["api", "--urls", "http://127.0.0.1:0"] : [];
        await using var command = new RunningDemo(args, new Dictionary<string, string>
        {
            ["ASPNETCORE_URLS"] = "http://127.0.0.1:0"
        });
        var address = await command.AddressAsync();
        using var client = new HttpClient { BaseAddress = address, Timeout = TimeSpan.FromSeconds(3) };
        using var result = await client.GetAsync("/healthz");
        Assert.That(result.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    [Test]
    public async Task ApplicationHasPort8080DefaultWithoutBindingSharedPort()
    {
        await using var app = CheckoutApplication.Create([]);
        Assert.That(app.Configuration["urls"], Is.EqualTo("http://0.0.0.0:8080"));
    }

    [Test]
    public async Task TrafficCommandCancellationBeforeStartupExitsCleanly()
    {
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        Assert.That(await DemoCommand.RunAsync(["traffic"], stop.Token), Is.Zero);
    }

    private static Dictionary<string, string> Endpoint(Uri address) => new() { ["DEMO_API_URL"] = address.ToString() };
}
