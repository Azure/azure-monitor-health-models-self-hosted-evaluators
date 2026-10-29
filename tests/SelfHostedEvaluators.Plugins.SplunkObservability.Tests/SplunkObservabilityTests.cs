using SelfHostedEvaluators.Plugins.Abstractions;
using SelfHostedEvaluators.Plugins.Testing;

namespace SelfHostedEvaluators.Plugins.SplunkObservability.Tests;

public class SplunkObservabilityTests : SinkClientContractFixture
{
    [Test]
    public void SplunkObservabilityPackageManifestAndDependencyBoundary() => AssertPackage(new SplunkObservabilityPackage(), "splunk-observability", "api", "stream");
    private static ISinkPlugin Sink => Plugin("SplunkObservability");
    protected override async ValueTask<SinkClientScenario> CreateScenarioAsync() => await SplunkScenario.Create();
    internal const string End = "event: control-message\ndata: {\"event\":\"END_OF_CHANNEL\",\"timestampMs\":1700000000000}\n\n";
    internal static string Data(string value = "42", long timestamp = 1699999990000) =>
        $"event: data\nid: data-{timestamp}\ndata: {{\"logicalTimestampMs\":{timestamp},\ndata: \"data\":[{{\"tsId\":\"abc\",\"value\":{value}}}]}}\n\n";

    [TestCase("""{"metric":"cpu.utilization","window":"5m"}""", true)]
    [TestCase("""{"metric":"cpu'quoted","aggregation":"mean","resolution":"10s"}""", true)]
    [TestCase("""{"detectorId":"AbC_123-","detectLabel":"loadWarning"}""", true)]
    [TestCase("""{"detectorId":"id"}""", true)]
    [TestCase("""{"detectorId":"../id"}""", false)]
    [TestCase("""{"detectorId":"id","window":"5m"}""", false)]
    [TestCase("""{"metric":"x","detectorId":"id"}""", false)]
    [TestCase("""{"metric":"x","programText":"arbitrary"}""", false)]
    [TestCase("""{"metric":"x","resolution":"0s"}""", false)]
    [TestCase("""{"metric":"x","window":"2d"}""", false)]
    [TestCase("""{"metric":"x","aggregation":"random"}""", false)]
    [TestCase("""{"metric":"x","metric":"y"}""", false)]
    [TestCase("""{"metric":"x","token":"secret"}""", false)]
    [TestCase("{}", false)]
    [TestCase("[]", false)]
    public void SplunkObservabilityQuerySchema(string json, bool valid) => Assert.That(Sink.ValidateQuery(Json(json)).IsValid, Is.EqualTo(valid));

    [TestCase("{}", true)]
    [TestCase("""{"token":"secret"}""", false)]
    [TestCase("[]", false)]
    public void SplunkObservabilitySettings(string json, bool valid) => Assert.That(Sink.ValidateSettings(Json(json)).IsValid, Is.EqualTo(valid));

    [Test]
    public async Task SplunkObservabilityUsesStreamEndpointProgramAndMilliseconds()
    {
        await using var rig = new PluginTestContext();
        PluginTestContext.Stub(rig.Stream, "/v2/signalflow/execute", ": heartbeat\n\n" + Data() + End, contentType: "text/event-stream");
        await using var client = await Create(rig);
        var result = (SignalResult.Observation)(await client.QueryBatchAsync(
            [new(Key(), Json("""{"metric":"cpu'load","window":"5m","resolution":"10s"}"""))], Window, default)).Single();
        Assert.That(result.Value, Is.EqualTo(42));
        Assert.That(result.ObservedAt, Is.EqualTo(DateTimeOffset.FromUnixTimeMilliseconds(1699999990000)));
        Assert.That(rig.Api.LogEntries, Is.Empty);
        var request = rig.Stream.LogEntries.Single().RequestMessage!;
        Assert.That(request.Method, Is.EqualTo("POST"));
        Assert.That(request.Query!["start"].Single(), Is.EqualTo("1699999700000"));
        Assert.That(request.Query["stop"].Single(), Is.EqualTo("1700000000000"));
        Assert.That(request.Query["resolution"].Single(), Is.EqualTo("10000"));
        Assert.That(request.Headers!["X-SF-Token"].Single(), Is.EqualTo("eu-token"));
        Assert.That(request.Body, Is.EqualTo("data('cpu\\'load').publish()"));
        Assert.That(request.Headers["Content-Type"].Single(), Does.StartWith("text/plain"));
    }

    [TestCase("last", 9)]
    [TestCase("mean", 5)]
    [TestCase("min", 1)]
    [TestCase("max", 9)]
    [TestCase("sum", 15)]
    public async Task SplunkObservabilityAggregationAndDefaultInterval(string aggregation, double expected)
    {
        await using var rig = new PluginTestContext();
        PluginTestContext.Stub(rig.Stream, "/v2/signalflow/execute",
            Data("1", 1699999900000) + Data("5", 1699999980000) + Data("9") + End, contentType: "text/event-stream");
        await using var client = await Create(rig);
        var result = (SignalResult.Observation)(await client.QueryBatchAsync(
            [new(Key(), Json($$"""{"metric":"cpu","aggregation":"{{aggregation}}"}"""))], Window, default)).Single();
        Assert.That(result.Value, Is.EqualTo(expected));
        Assert.That(rig.Stream.LogEntries.Single().RequestMessage!.Query!["start"].Single(), Is.EqualTo("1699999880000"));
    }

    [TestCase("ANOMALOUS")]
    [TestCase("OK")]
    [TestCase("MANUALLY_RESOLVED")]
    [TestCase("STOPPED")]
    public async Task SplunkObservabilityDetectorUsesDocumentedIncidentsAndNativeState(string state)
    {
        await using var rig = new PluginTestContext();
        PluginTestContext.Stub(rig.Api, "/v2/detector/abc/incidents",
            $$"""[{"active":true,"anomalyState":"{{state}}","detectLabel":"load","detectorId":"abc","events":[{"timestamp":1699999990000}]}]""");
        await using var client = await Create(rig);
        var result = (SignalResult.Observation)(await client.QueryBatchAsync(
            [new(Key(), Json("""{"detectorId":"abc","detectLabel":"load"}"""))], Window, default)).Single();
        Assert.That(result.NativeState, Is.EqualTo(state));
        Assert.That(result.Value, Is.Null);
        Assert.That(result.ObservedAt, Is.EqualTo(DateTimeOffset.FromUnixTimeMilliseconds(1699999990000)));
        Assert.That(rig.Stream.LogEntries, Is.Empty);
        Assert.That(rig.Api.LogEntries.Single().RequestMessage!.Headers!["X-SF-Token"].Single(), Is.EqualTo("eu-token"));
    }

    [TestCase("[]", true)]
    [TestCase("""[{"active":false,"anomalyState":"OK","detectorId":"abc","detectLabel":"load","events":[]}]""", true)]
    [TestCase("{}", false)]
    [TestCase("""[{"active":true,"anomalyState":"MADE_UP","detectorId":"abc","detectLabel":"load","events":[]}]""", false)]
    [TestCase("""[{"active":true,"anomalyState":"OK","detectorId":"wrong","detectLabel":"load","events":[]}]""", false)]
    public async Task SplunkObservabilityDetectorNoDataAndMalformed(string body, bool noData)
    {
        await using var rig = new PluginTestContext();
        PluginTestContext.Stub(rig.Api, "/v2/detector/abc/incidents", body);
        await using var client = await Create(rig);
        var result = (await client.QueryBatchAsync([new(Key(), Json("""{"detectorId":"abc"}"""))], Window, default)).Single();
        Assert.That(result.GetType(), Is.EqualTo(noData ? typeof(SignalResult.NoData) : typeof(SignalResult.Failure)));
    }

    [TestCase("", true, false)]
    [TestCase("event: data\ndata: {\"logicalTimestampMs\":1699999990000,\"data\":[]}\n\n", true, false)]
    [TestCase("event: data\ndata: {\"logicalTimestampMs\":1699999990000,\"data\":[{\"tsId\":\"a\",\"value\":null}]}\n\n", true, false)]
    [TestCase("event: error\ndata: {\"errors\":[{\"code\":\"secret\"}]}\n\n", false, true)]
    [TestCase("event: control-message\ndata: {\"event\":\"CHANNEL_ABORT\",\"abortInfo\":{\"secret\":\"secret\"}}\n\n", false, true)]
    [TestCase("event: data\ndata: {\"logicalTimestampMs\":1699999990000,\"data\":[{\"tsId\":\"a\",\"value\":1e999}]}\n\n", false, true)]
    [TestCase("event: data\ndata: {\"logicalTimestampMs\":1699999990000,\"data\":[{\"tsId\":\"a\",\"value\":\"NaN\"}]}\n\n", false, true)]
    [TestCase("event: data\ndata: broken\n\n", false, true)]
    [TestCase("event: data\ndata: {}\n\n", false, true)]
    public async Task SplunkObservabilityStreamNoDataMalformedAndAbort(string body, bool appendEnd, bool failure)
    {
        await using var rig = new PluginTestContext();
        PluginTestContext.Stub(rig.Stream, "/v2/signalflow/execute", body + (appendEnd ? End : ""), contentType: "text/event-stream");
        await using var client = await Create(rig);
        var result = (await client.QueryBatchAsync([new(Key(), Json("""{"metric":"cpu"}"""))], Window, default)).Single();
        Assert.That(result.GetType(), Is.EqualTo(failure ? typeof(SignalResult.Failure) : typeof(SignalResult.NoData)));
        if (result is SignalResult.Failure failed) Assert.That(failed.Message, Does.Not.Contain("secret"));
    }

    [Test]
    public async Task SplunkObservabilityTruncatedDataMustNotBecomeSuccess()
    {
        await using var rig = new PluginTestContext();
        PluginTestContext.Stub(rig.Stream, "/v2/signalflow/execute", Data(), contentType: "text/event-stream");
        await using var client = await Create(rig);
        Assert.That((await client.QueryBatchAsync([new(Key(), Json("""{"metric":"cpu"}"""))], Window, default)).Single(),
            Is.TypeOf<SignalResult.Failure>());
    }

    [TestCase(401, false)]
    [TestCase(429, false)]
    [TestCase(503, false)]
    [TestCase(403, true)]
    [TestCase(429, true)]
    [TestCase(500, true)]
    public async Task SplunkObservabilityHttpFailuresOnBothEndpoints(int status, bool detector)
    {
        await using var rig = new PluginTestContext();
        PluginTestContext.Stub(detector ? rig.Api : rig.Stream,
            detector ? "/v2/detector/abc/incidents" : "/v2/signalflow/execute", "secret", status);
        await using var client = await Create(rig);
        var result = (SignalResult.Failure)(await client.QueryBatchAsync(
            [new(Key(), Json(detector ? """{"detectorId":"abc"}""" : """{"metric":"cpu"}"""))], Window, default)).Single();
        Assert.That(result.Code, Is.EqualTo($"http_{status}"));
        Assert.That(result.Message, Does.Not.Contain("secret"));
    }

    [TestCase("event: data\ndata: {\"logicalTimestampMs\":\"bad\",\"data\":[]}\n\n")]
    [TestCase("event: data\ndata: {\"data\":[]}\n\n")]
    [TestCase("event: invented\ndata: {}\n\n")]
    [TestCase("event: control-message\ndata: {\"event\":\"END_OF_CHANNEL\"}\n\n")]
    public async Task SplunkObservabilityRejectsInvalidStreamEnvelopes(string body)
    {
        await using var rig = new PluginTestContext();
        PluginTestContext.Stub(rig.Stream, "/v2/signalflow/execute", body + End, contentType: "text/event-stream");
        await using var client = await Create(rig);
        Assert.That((await client.QueryBatchAsync([new(Key(), Json("""{"metric":"cpu"}"""))], Window, default)).Single(),
            Is.TypeOf<SignalResult.Failure>());
    }

    [TestCase("metadata", "{}")]
    [TestCase("metadata", "[]")]
    [TestCase("metadata", """{"tsId":"abc","properties":[]}""")]
    [TestCase("metadata", """{"tsId":12,"properties":{}}""")]
    [TestCase("expired-tsid", "null")]
    [TestCase("expired-tsid", """{"tsId":""}""")]
    [TestCase("message", "{}")]
    [TestCase("message", """{"logicalTimestampMs":"bad","message":{}}""")]
    [TestCase("message", """{"logicalTimestampMs":1699999990000,"message":[]}""")]
    [TestCase("event", "{}")]
    [TestCase("event", """{"tsId":"abc","timestampMs":1699999990000,"metadata":{},"properties":[]}""")]
    public async Task IgnoredEnvelopesStillRequireDocumentedShape(string kind, string payload)
    {
        await using var rig = new PluginTestContext();
        PluginTestContext.Stub(rig.Stream, "/v2/signalflow/execute",
            $"event: {kind}\ndata: {payload}\n\n" + Data() + End, contentType: "text/event-stream");
        await using var client = await Create(rig);
        var result = (await client.QueryBatchAsync([new(Key(), Json("""{"metric":"cpu"}"""))], Window, default)).Single();
        Assert.That(result, Is.TypeOf<SignalResult.Failure>());
        Assert.That(((SignalResult.Failure)result).Code, Is.EqualTo("invalid_response"));
    }

    [TestCase("metadata", """{"tsId":"abc","properties":{"sf_metric":"cpu"}}""")]
    [TestCase("expired-tsid", """{"tsId":"abc"}""")]
    [TestCase("message", """{"logicalTimestampMs":1699999990000,"message":{"messageCode":"TEST","contents":{}}}""")]
    [TestCase("event", """{"tsId":"abc","timestampMs":1699999990000,"metadata":{},"properties":{}}""")]
    public async Task ValidIgnoredEnvelopesDoNotDiscardData(string kind, string payload)
    {
        await using var rig = new PluginTestContext();
        PluginTestContext.Stub(rig.Stream, "/v2/signalflow/execute",
            $"event: {kind}\ndata: {payload}\n\n" + Data() + End, contentType: "text/event-stream");
        await using var client = await Create(rig);
        var result = (await client.QueryBatchAsync([new(Key(), Json("""{"metric":"cpu"}"""))], Window, default)).Single();
        Assert.That(result, Is.TypeOf<SignalResult.Observation>());
        Assert.That(((SignalResult.Observation)result).Value, Is.EqualTo(42));
    }

    [Test]
    public async Task SplunkObservabilityDetectorDoesNotHideConflictingStates()
    {
        await using var rig = new PluginTestContext();
        PluginTestContext.Stub(rig.Api, "/v2/detector/abc/incidents",
            """[{"active":true,"anomalyState":"ANOMALOUS","detectorId":"abc","detectLabel":"one","events":[]},{"active":true,"anomalyState":"STOPPED","detectorId":"abc","detectLabel":"two","events":[]}]""");
        await using var client = await Create(rig);
        var failure = (SignalResult.Failure)(await client.QueryBatchAsync([new(Key(), Json("""{"detectorId":"abc"}"""))], Window, default)).Single();
        Assert.That(failure.Code, Is.EqualTo("ambiguous_state"));
        var selected = (SignalResult.Observation)(await client.QueryBatchAsync(
            [new(Key(), Json("""{"detectorId":"abc","detectLabel":"one"}"""))], Window, default)).Single();
        Assert.That(selected.NativeState, Is.EqualTo("ANOMALOUS"));
    }

    [Test]
    public async Task SplunkObservabilityDetectorPaginatesBeforeReturningState()
    {
        await using var rig = new PluginTestContext();
        var page = "[" + string.Join(",", Enumerable.Repeat(
            """{"active":true,"anomalyState":"ANOMALOUS","detectorId":"abc","detectLabel":"one","events":[]}""", 100)) + "]";
        rig.Api.Given(WireMock.RequestBuilders.Request.Create().WithPath("/v2/detector/abc/incidents").WithParam("offset", "0").UsingGet())
            .RespondWith(WireMock.ResponseBuilders.Response.Create().WithStatusCode(200).WithBody(page));
        rig.Api.Given(WireMock.RequestBuilders.Request.Create().WithPath("/v2/detector/abc/incidents").WithParam("offset", "100").UsingGet())
            .RespondWith(WireMock.ResponseBuilders.Response.Create().WithStatusCode(200).WithBody("[]"));
        await using var client = await Create(rig);
        var result = (SignalResult.Observation)(await client.QueryBatchAsync([new(Key(), Json("""{"detectorId":"abc"}"""))], Window, default)).Single();
        Assert.That(result.NativeState, Is.EqualTo("ANOMALOUS"));
        Assert.That(rig.Api.LogEntries.Count(), Is.EqualTo(2));
    }

    [Test]
    public async Task SplunkObservabilityNativeStateObservationsWithoutTimestampsUseInjectedClock()
    {
        await using var rig = new PluginTestContext();
        PluginTestContext.Stub(rig.Api, "/v2/detector/abc/incidents",
            """[{"active":true,"anomalyState":"ANOMALOUS","detectorId":"abc","detectLabel":"one","events":[]}]""");
        var fixedTime = new FixedTime();
        await using var client = await Sink.CreateClientAsync(rig.Context(
            new() { ["api"] = rig.Http(rig.Api, "Splunk"), ["stream"] = rig.Http(rig.Stream, "Splunk") }, time: fixedTime), default);
        var result = (SignalResult.Observation)(await client.QueryBatchAsync([new(Key(), Json("""{"detectorId":"abc"}"""))], Window, default)).Single();
        Assert.That(result.ObservedAt, Is.EqualTo(Window.ScheduledAt));
    }

    private sealed class FixedTime : TimeProvider { public override DateTimeOffset GetUtcNow() => Window.ScheduledAt; }

    internal static ValueTask<ISinkClient> Create(PluginTestContext rig) => Plugin("SplunkObservability").CreateClientAsync(
        rig.Context(new() { ["api"] = rig.Http(rig.Api, "Splunk"), ["stream"] = rig.Http(rig.Stream, "Splunk") }), default);
}

internal sealed class SplunkScenario : SinkClientScenario
{
    private readonly PluginTestContext rig = new();
    private ISinkClient client = null!;
    public override ISinkClient Client => client;
    internal static async Task<SplunkScenario> Create()
    {
        var scenario = new SplunkScenario();
        scenario.client = await SplunkObservabilityTests.Create(scenario.rig);
        return scenario;
    }
    public override SignalQuery Query(SignalKey key, ContractOutcome outcome)
    {
        if (outcome == ContractOutcome.Timeout) rig.SetTimeout(TimeSpan.FromMilliseconds(200));
        var name = outcome.ToString();
        // Separate metric names allow mixed outcomes in a single real batch.
        var request = WireMock.RequestBuilders.Request.Create().WithPath("/v2/signalflow/execute")
            .WithBody($"data('{name}').publish()").UsingPost();
        var response = WireMock.ResponseBuilders.Response.Create().WithStatusCode(outcome == ContractOutcome.Failure ? 429 : 200)
            .WithHeader("Retry-After", "7").WithHeader("Content-Type", "text/event-stream")
            .WithBody((outcome == ContractOutcome.NoData ? "" : SplunkObservabilityTests.Data()) + SplunkObservabilityTests.End);
        if (outcome is ContractOutcome.Delayed or ContractOutcome.Timeout) response.WithDelay(TimeSpan.FromSeconds(10));
        rig.Stream.Given(request).RespondWith(response);
        return new(key, SinkClientContractFixture.Json($$"""{"metric":"{{name}}"}"""));
    }
    public override Task WaitForRequestAsync() => rig.RequestStarted.Task;
    public override async ValueTask DisposeAsync() { await client.DisposeAsync(); await rig.DisposeAsync(); }
}
