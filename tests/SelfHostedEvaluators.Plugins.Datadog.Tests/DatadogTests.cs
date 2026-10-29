using SelfHostedEvaluators.Plugins.Abstractions;
using SelfHostedEvaluators.Plugins.Testing;

namespace SelfHostedEvaluators.Plugins.Datadog.Tests;

public class DatadogTests : SinkClientContractFixture
{
    [Test]
    public void DatadogPackageManifestAndDependencyBoundary() => AssertPackage(new DatadogPackage(), "datadog", "default");
    private static ISinkPlugin Sink => Plugin("Datadog");
    protected override async ValueTask<SinkClientScenario> CreateScenarioAsync() => await DatadogScenario.Create();
    private const string Metric = """{"metric":"p95:trace.http.request{service:checkout}","window":"5m"}""";

    [TestCase(Metric, true)]
    [TestCase("""{"monitorId":12345678}""", true)]
    [TestCase("""{"metric":"avg:x{*}"}""", true)]
    [TestCase("""{"metric":"x","aggregation":"max","window":"1d"}""", true)]
    [TestCase("""{"monitorId":0}""", false)]
    [TestCase("""{"monitorId":"123"}""", false)]
    [TestCase("""{"monitorId":1,"window":"5m"}""", false)]
    [TestCase("""{"monitorId":1,"metric":"x"}""", false)]
    [TestCase("""{"metric":""}""", false)]
    [TestCase("""{"metric":"x","window":"0s"}""", false)]
    [TestCase("""{"metric":"x","window":"1.5m"}""", false)]
    [TestCase("""{"metric":"x","window":"2d"}""", false)]
    [TestCase("""{"metric":"x","aggregation":"random"}""", false)]
    [TestCase("""{"metric":"x","metric":"y"}""", false)]
    [TestCase("""{"metric":"x","token":"secret"}""", false)]
    [TestCase("[]", false)]
    public void DatadogQuerySchema(string json, bool valid) => Assert.That(Sink.ValidateQuery(Json(json)).IsValid, Is.EqualTo(valid));

    [TestCase("{} ", true)]
    [TestCase("""{"apiKey":"secret"}""", false)]
    [TestCase("null", false)]
    public void DatadogSettings(string json, bool valid) => Assert.That(Sink.ValidateSettings(Json(json)).IsValid, Is.EqualTo(valid));

    [Test]
    public async Task DatadogMetricUsesScheduledWindowEncodingHeadersAndLastPoint()
    {
        await using var rig = new PluginTestContext();
        PluginTestContext.Stub(rig.Api, "/api/v1/query",
            """{"status":"ok","series":[{"pointlist":[[1699999800000,5],[1699999900000,null],[1699999990000,42]]}]}""");
        await using var client = await Sink.CreateClientAsync(rig.Context(new() { ["default"] = rig.Http(rig.Api, "Datadog") }), default);
        var result = (SignalResult.Observation)(await client.QueryBatchAsync([new(Key(), Json(Metric))], Window, default)).Single();
        Assert.That(result.Value, Is.EqualTo(42));
        Assert.That(result.ObservedAt, Is.EqualTo(DateTimeOffset.FromUnixTimeMilliseconds(1699999990000)));
        var request = rig.Api.LogEntries.Single().RequestMessage!;
        Assert.That(request.Query!["from"].First(), Is.EqualTo("1699999700"));
        Assert.That(request.Query["to"].First(), Is.EqualTo("1700000000"));
        Assert.That(request.Query["query"].First(), Is.EqualTo("p95:trace.http.request{service:checkout}"));
        Assert.That(request.Headers!["DD-API-KEY"].Single(), Is.EqualTo("eu-api"));
        Assert.That(request.Headers["DD-APPLICATION-KEY"].Single(), Is.EqualTo("eu-app"));
    }

    [Test]
    public async Task DatadogAcceptsIntegralFloatingPointMilliseconds()
    {
        await using var rig = new PluginTestContext();
        PluginTestContext.Stub(rig.Api, "/api/v1/query",
            """{"status":"ok","series":[{"pointlist":[[1699999990000.0,42]]}]}""");
        await using var client = await Sink.CreateClientAsync(rig.Context(new() { ["default"] = rig.Http(rig.Api, "Datadog") }), default);
        var result = (SignalResult.Observation)(await client.QueryBatchAsync([new(Key(), Json(Metric))], Window, default)).Single();
        Assert.That(result.Value, Is.EqualTo(42));
        Assert.That(result.ObservedAt, Is.EqualTo(DateTimeOffset.FromUnixTimeMilliseconds(1699999990000)));
    }

    [TestCase("last", 9)]
    [TestCase("mean", 5)]
    [TestCase("min", 1)]
    [TestCase("max", 9)]
    [TestCase("sum", 15)]
    public async Task DatadogAggregationIsExplicitAndIntervalIsDefaultWindow(string aggregation, double expected)
    {
        await using var rig = new PluginTestContext();
        PluginTestContext.Stub(rig.Api, "/api/v1/query",
            """{"status":"ok","series":[{"pointlist":[[1699999900000,1],[1699999990000,5]]},{"pointlist":[[1699999990000,9]]}]}""");
        await using var client = await Sink.CreateClientAsync(rig.Context(new() { ["default"] = rig.Http(rig.Api, "Datadog") }), default);
        var result = (SignalResult.Observation)(await client.QueryBatchAsync([new(Key(), Json($$"""{"metric":"avg:x{*}","aggregation":"{{aggregation}}"}"""))], Window, default)).Single();
        Assert.That(result.Value, Is.EqualTo(expected));
        Assert.That(rig.Api.LogEntries.Single().RequestMessage!.Query!["from"].First(), Is.EqualTo("1699999880"));
    }

    [TestCase("OK")]
    [TestCase("Warn")]
    [TestCase("Alert")]
    [TestCase("No Data")]
    [TestCase("Ignored")]
    [TestCase("Skipped")]
    [TestCase("Unknown")]
    public async Task DatadogMonitorPreservesNativeState(string state)
    {
        await using var rig = new PluginTestContext();
        PluginTestContext.Stub(rig.Api, "/api/v1/monitor/12345678", $$"""{"id":12345678,"overall_state":"{{state}}","modified":"2020-01-01T00:00:00Z"}""");
        await using var client = await Sink.CreateClientAsync(rig.Context(new() { ["default"] = rig.Http(rig.Api, "Datadog") }), default);
        var result = (SignalResult.Observation)(await client.QueryBatchAsync([new(Key(), Json("""{"monitorId":12345678}"""))], Window, default)).Single();
        Assert.That(result.NativeState, Is.EqualTo(state));
        Assert.That(result.Value, Is.Null);
        Assert.That(result.ObservedAt, Is.GreaterThan(DateTimeOffset.UtcNow.AddSeconds(-10)), "modified is configuration time, not observation time");
    }

    [TestCase("""{"status":"ok","series":[]}""", true)]
    [TestCase("""{"status":"ok","series":[{"pointlist":[[1699999990000,null]]}]}""", true)]
    [TestCase("""{"status":"ok","series":[{"pointlist":[[1600000000000,1]]}]}""", true)]
    [TestCase("not-json", false)]
    [TestCase("{}", false)]
    [TestCase("""{"status":"error","error":"secret"}""", false)]
    [TestCase("""{"status":"ok","series":[{"pointlist":[[1699999990000,1e999]]}]}""", false)]
    [TestCase("""{"status":"ok","series":[{"pointlist":[[1699999990000,"NaN"]]}]}""", false)]
    [TestCase("""{"status":"ok","series":[{"pointlist":[["bad",1]]}]}""", false)]
    [TestCase("""{"status":"ok","series":[{"pointlist":[[1699999990000]]}]}""", false)]
    public async Task DatadogNoDataAndMalformedResponses(string body, bool noData)
    {
        await using var rig = new PluginTestContext();
        PluginTestContext.Stub(rig.Api, "/api/v1/query", body);
        await using var client = await Sink.CreateClientAsync(rig.Context(new() { ["default"] = rig.Http(rig.Api, "Datadog") }), default);
        var result = (await client.QueryBatchAsync([new(Key(), Json(Metric))], Window, default)).Single();
        Assert.That(result.GetType(), Is.EqualTo(noData ? typeof(SignalResult.NoData) : typeof(SignalResult.Failure)));
        if (result is SignalResult.Failure failure) Assert.That(failure.Message, Does.Not.Contain("secret"));
    }

    [TestCase(401)]
    [TestCase(403)]
    [TestCase(429)]
    [TestCase(500)]
    [TestCase(503)]
    public async Task DatadogErrorsAreExplicit(int status)
    {
        await using var rig = new PluginTestContext();
        PluginTestContext.Stub(rig.Api, "/api/v1/query", "secret", status);
        await using var client = await Sink.CreateClientAsync(rig.Context(new() { ["default"] = rig.Http(rig.Api, "Datadog") }), default);
        var failure = (SignalResult.Failure)(await client.QueryBatchAsync([new(Key(), Json(Metric))], Window, default)).Single();
        Assert.That(failure.Code, Is.EqualTo($"http_{status}"));
        Assert.That(failure.Message, Does.Not.Contain("secret"));
        if (status == 429) Assert.That(failure.RetryAfter, Is.EqualTo(TimeSpan.FromSeconds(7)));
    }

    [Test]
    public async Task DatadogInstancesNeverShareSiteClientsOrCredentials()
    {
        await using var rig = new PluginTestContext();
        PluginTestContext.Stub(rig.Api, "/api/v1/query", """{"status":"ok","series":[]}""");
        PluginTestContext.Stub(rig.Stream, "/api/v1/query", """{"status":"ok","series":[]}""");
        await using var eu = await Sink.CreateClientAsync(rig.Context(new() { ["default"] = rig.Http(rig.Api, "Datadog", "eu") }), default);
        await using var us = await Sink.CreateClientAsync(rig.Context(new() { ["default"] = rig.Http(rig.Stream, "Datadog", "us") }), default);
        await eu.QueryBatchAsync([new(Key(), Json(Metric))], Window, default);
        await us.QueryBatchAsync([new(Key(), Json(Metric))], Window, default);
        Assert.That(rig.Api.LogEntries.Single().RequestMessage!.Headers!["DD-API-KEY"].Single(), Is.EqualTo("eu-api"));
        Assert.That(rig.Stream.LogEntries.Single().RequestMessage!.Headers!["DD-API-KEY"].Single(), Is.EqualTo("us-api"));
    }

    [TestCase("""{"id":12345678,"overall_state":null}""", true)]
    [TestCase("""{"id":12345678,"overall_state":"made_up"}""", false)]
    [TestCase("""{"id":999,"overall_state":"OK"}""", false)]
    [TestCase("""{"id":12345678}""", false)]
    public async Task DatadogMonitorCannotInventNativeStates(string body, bool noData)
    {
        await using var rig = new PluginTestContext();
        PluginTestContext.Stub(rig.Api, "/api/v1/monitor/12345678", body);
        await using var client = await Sink.CreateClientAsync(rig.Context(new() { ["default"] = rig.Http(rig.Api, "Datadog") }), default);
        var result = (await client.QueryBatchAsync([new(Key(), Json("""{"monitorId":12345678}"""))], Window, default)).Single();
        Assert.That(result.GetType(), Is.EqualTo(noData ? typeof(SignalResult.NoData) : typeof(SignalResult.Failure)));
    }

    [Test]
    public async Task DatadogInvalidQueryDoesNotMakeRequestAndDisposeDoesNotOwnHttp()
    {
        await using var rig = new PluginTestContext();
        var http = rig.Http(rig.Api, "Datadog");
        var client = await Sink.CreateClientAsync(rig.Context(new() { ["default"] = http }), default);
        Assert.That((await client.QueryBatchAsync([new(Key(), Json("""{"metric":"x","secret":"bad"}"""))], Window, default)).Single(),
            Is.TypeOf<SignalResult.Failure>());
        Assert.That(rig.Api.LogEntries, Is.Empty);
        await client.DisposeAsync();
        PluginTestContext.Stub(rig.Api, "/still-owned", "{}");
        using var response = await http.GetAsync("still-owned");
        Assert.That(response.IsSuccessStatusCode, Is.True);
    }
}

internal sealed class DatadogScenario : SinkClientScenario
{
    private readonly PluginTestContext rig = new();
    private ISinkClient client = null!;
    public override ISinkClient Client => client;
    internal static async Task<DatadogScenario> Create()
    {
        var scenario = new DatadogScenario();
        scenario.client = await SinkClientContractFixture.Plugin("Datadog").CreateClientAsync(
            scenario.rig.Context(new() { ["default"] = scenario.rig.Http(scenario.rig.Api, "Datadog") }), default);
        return scenario;
    }
    public override SignalQuery Query(SignalKey key, ContractOutcome outcome)
    {
        if (outcome == ContractOutcome.Timeout) rig.SetTimeout(TimeSpan.FromMilliseconds(200));
        var id = (int)outcome + 1;
        PluginTestContext.Stub(rig.Api, $"/api/v1/monitor/{id}", outcome switch
        {
            ContractOutcome.NoData => """{"id":2,"overall_state":null}""",
            _ => $$"""{"id":{{id}},"overall_state":"OK"}"""
        }, outcome == ContractOutcome.Failure ? 429 : 200,
            delayMs: outcome is ContractOutcome.Delayed or ContractOutcome.Timeout ? 10000 : 0);
        // Contract numeric observations use the real metric API.
        if (outcome == ContractOutcome.Observation)
        {
            PluginTestContext.Stub(rig.Api, "/api/v1/query", """{"status":"ok","series":[{"pointlist":[[1699999990000,42]]}]}""");
            return new(key, SinkClientContractFixture.Json("""{"metric":"avg:x{*}"}"""));
        }
        return new(key, SinkClientContractFixture.Json($$"""{"monitorId":{{id}}}"""));
    }
    public override Task WaitForRequestAsync() => rig.RequestStarted.Task;
    public override async ValueTask DisposeAsync() { await client.DisposeAsync(); await rig.DisposeAsync(); }
}
