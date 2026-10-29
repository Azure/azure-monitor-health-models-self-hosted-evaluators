using System.Globalization;
using System.Net;
using SelfHostedEvaluators.Plugins.Abstractions;
using SelfHostedEvaluators.Plugins.Testing;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;

namespace SelfHostedEvaluators.Plugins.AzureMonitor.Tests;

public class AzureMonitorTests : SinkClientContractFixture
{
    private const string Query = """{"promql":"sum(rate(http_requests_total[5m]))"}""";
    private const string Vector = """{"status":"success","data":{"resultType":"vector","result":[{"metric":{},"value":[1699999990.125,"42"]}]}}""";
    internal static ISinkPlugin Sink => Plugin("AzureMonitor");

    [Test]
    public void PackageManifestAndDependencyBoundary() => AssertPackage(new AzureMonitorPackage(), "azure-monitor", "default");

    protected override async ValueTask<SinkClientScenario> CreateScenarioAsync() => await AzureMonitorScenario.Create();

    [TestCase(Query, true)]
    [TestCase("""{"promql":"up{job=\"checkout\"}"}""", true)]
    [TestCase("{}", false)]
    [TestCase("[]", false)]
    [TestCase("""{"promql":null}""", false)]
    [TestCase("""{"promql":1}""", false)]
    [TestCase("""{"promql":""}""", false)]
    [TestCase("""{"promql":"   "}""", false)]
    [TestCase("""{"promql":"up\n"}""", false)]
    [TestCase("""{"promql":"up","promql":"down"}""", false)]
    [TestCase("""{"PromQL":"up"}""", false)]
    [TestCase("""{"promql":"up","window":"5m"}""", false)]
    [TestCase("""{"promql":"up","aggregation":"sum"}""", false)]
    [TestCase("""{"promql":"up","endpoint":"https://other.test"}""", false)]
    [TestCase("""{"promql":"up","token":"secret"}""", false)]
    public void QuerySchema(string json, bool valid) => Assert.That(Sink.ValidateQuery(Json(json)).IsValid, Is.EqualTo(valid));

    [TestCase(4096, true)]
    [TestCase(4097, false)]
    public void QueryLengthIsBounded(int length, bool valid) =>
        Assert.That(Sink.ValidateQuery(Json($$"""{"promql":"{{new string('x', length)}}"}""")).IsValid, Is.EqualTo(valid));

    [TestCase("{}", true)]
    [TestCase("null", false)]
    [TestCase("[]", false)]
    [TestCase("""{"workspace":"secret"}""", false)]
    public void SettingsSchema(string json, bool valid) => Assert.That(Sink.ValidateSettings(Json(json)).IsValid, Is.EqualTo(valid));

    [Test]
    public async Task InstantQueryPostsEncodedExpressionAndScheduledTimeWithHostCredentials()
    {
        await using var rig = new PluginTestContext();
        PluginTestContext.Stub(rig.Api, "/api/v1/query", Vector);
        await using var client = await Sink.CreateClientAsync(rig.Context(new() { ["default"] = rig.Http(rig.Api, "AzureMonitor") }), default);
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            var result = (SignalResult.Observation)(await client.QueryBatchAsync(
                [new(Key(), Json("""{"promql":"sum(rate(http_requests_total{job=\"a+b&c\"}[5m]))"}"""))],
                new(Window.ScheduledAt.AddMilliseconds(125), Window.Interval), default)).Single();
            Assert.That(result.Value, Is.EqualTo(42));
        }
        finally { CultureInfo.CurrentCulture = previous; }

        var request = rig.Api.LogEntries.Single().RequestMessage!;
        Assert.Multiple(() =>
        {
            Assert.That(request.Method, Is.EqualTo("POST"));
            Assert.That(request.Query is null || request.Query.Count == 0, Is.True, "PromQL must not enter the request URL.");
            Assert.That(request.Headers!["Authorization"].Single(), Is.EqualTo("Bearer eu-token"));
            Assert.That(request.Headers["Content-Type"].Single(), Does.StartWith("application/x-www-form-urlencoded"));
        });
        var fields = request.Body!.Split('&').Select(field => field.Split('=', 2))
            .ToDictionary(field => WebUtility.UrlDecode(field[0]), field => WebUtility.UrlDecode(field[1]));
        Assert.That(fields, Has.Count.EqualTo(2));
        Assert.That(fields["query"], Is.EqualTo("sum(rate(http_requests_total{job=\"a+b&c\"}[5m]))"));
        Assert.That(fields["time"], Is.EqualTo("1700000000.125"));
    }

    [TestCase(Vector, 42)]
    [TestCase("""{"status":"success","data":{"resultType":"scalar","result":[1699999990.125,"-1.25e+2"]}}""", -125)]
    [TestCase("""{"status":"success","data":{"resultType":"vector","result":[{"metric":{"job":"checkout"},"value":[1699999990.125,"0"]}]}}""", 0)]
    public async Task NumericResultsPreserveFractionalSampleTime(string body, double expected)
    {
        await using var rig = new PluginTestContext();
        PluginTestContext.Stub(rig.Api, "/api/v1/query", body);
        await using var client = await Sink.CreateClientAsync(rig.Context(new() { ["default"] = rig.Http(rig.Api, "AzureMonitor") }), default);
        var result = (SignalResult.Observation)(await client.QueryBatchAsync([new(Key(), Json(Query))], Window, default)).Single();
        Assert.Multiple(() =>
        {
            Assert.That(result.Key, Is.EqualTo(Key()));
            Assert.That(result.Value, Is.EqualTo(expected));
            Assert.That(result.NativeState, Is.Null);
            Assert.That(result.ObservedAt, Is.EqualTo(DateTimeOffset.FromUnixTimeMilliseconds(1699999990125)));
        });
    }

    [Test]
    public async Task EmptyVectorIsNoData()
    {
        await using var rig = new PluginTestContext();
        PluginTestContext.Stub(rig.Api, "/api/v1/query", """{"status":"success","data":{"resultType":"vector","result":[]}}""");
        await using var client = await Sink.CreateClientAsync(rig.Context(new() { ["default"] = rig.Http(rig.Api, "AzureMonitor") }), default);
        Assert.That((await client.QueryBatchAsync([new(Key(), Json(Query))], Window, default)).Single(), Is.TypeOf<SignalResult.NoData>());
    }

    [Test]
    public async Task MultipleSeriesRequireExplicitPromqlAggregation()
    {
        await using var rig = new PluginTestContext();
        PluginTestContext.Stub(rig.Api, "/api/v1/query",
            """{"status":"success","data":{"resultType":"vector","result":[{"metric":{"instance":"a"},"value":[1700000000,"1"]},{"metric":{"instance":"b"},"value":[1700000000,"99"]}]}}""");
        await using var client = await Sink.CreateClientAsync(rig.Context(new() { ["default"] = rig.Http(rig.Api, "AzureMonitor") }), default);
        var failure = (SignalResult.Failure)(await client.QueryBatchAsync([new(Key(), Json(Query))], Window, default)).Single();
        Assert.That(failure.Code, Is.EqualTo("multiple_series"));
        Assert.That(failure.Message, Does.Contain("PromQL"));
    }

    [TestCase("not-json")]
    [TestCase("{}")]
    [TestCase("null")]
    [TestCase("""{"status":"error","errorType":"execution","error":"secret"}""")]
    [TestCase("""{"status":"success","data":{}}""")]
    [TestCase("""{"status":"success","data":{"resultType":"matrix","result":[]}}""")]
    [TestCase("""{"status":"success","data":{"resultType":"string","result":[1700000000,"secret"]}}""")]
    [TestCase("""{"status":"success","data":{"resultType":"vector","result":null}}""")]
    [TestCase("""{"status":"success","data":{"resultType":"vector","result":[{}]}}""")]
    [TestCase("""{"status":"success","data":{"resultType":"vector","result":[{"metric":null,"value":[1700000000,"42"]}]}}""")]
    [TestCase("""{"status":"success","data":{"resultType":"vector","result":[{"metric":{"job":42},"value":[1700000000,"42"]}]}}""")]
    [TestCase("""{"status":"success","data":{"resultType":"vector","result":[{"metric":{},"histogram":[1700000000,{}]}]}}""")]
    [TestCase("""{"status":"success","data":{"resultType":"vector","result":[{"metric":{},"value":[1700000000,"42"],"histogram":[1700000000,{}]}]}}""")]
    [TestCase("""{"status":"success","data":{"resultType":"scalar","result":[]}}""")]
    [TestCase("""{"status":"success","data":{"resultType":"scalar","result":[1700000000]}}""")]
    [TestCase("""{"status":"success","data":{"resultType":"scalar","result":[1700000000,"42","extra"]}}""")]
    [TestCase("""{"status":"success","data":{"resultType":"scalar","result":["1700000000","42"]}}""")]
    [TestCase("""{"status":"success","data":{"resultType":"scalar","result":[1700000000,42]}}""")]
    [TestCase("""{"status":"success","data":{"resultType":"scalar","result":[1700000000,null]}}""")]
    [TestCase("""{"status":"success","data":{"resultType":"scalar","result":[1700000000,"NaN"]}}""")]
    [TestCase("""{"status":"success","data":{"resultType":"scalar","result":[1700000000,"+Inf"]}}""")]
    [TestCase("""{"status":"success","data":{"resultType":"scalar","result":[1700000000,"-Inf"]}}""")]
    [TestCase("""{"status":"success","data":{"resultType":"scalar","result":[1700000000,"Infinity"]}}""")]
    [TestCase("""{"status":"success","data":{"resultType":"scalar","result":[1700000000,"1e999"]}}""")]
    [TestCase("""{"status":"success","data":{"resultType":"scalar","result":[1e999,"42"]}}""")]
    [TestCase("""{"status":"success","data":{"resultType":"scalar","result":[9999999999999999999999999999,"42"]}}""")]
    [TestCase("""{"status":"success","data":{"resultType":"scalar","result":[-62135596801,"42"]}}""")]
    [TestCase("""{"status":"success","data":{"resultType":"scalar","result":[253402300800,"42"]}}""")]
    [TestCase("""{"status":"success","data":{"resultType":"scalar","result":[1700000001,"42"]}}""")]
    public async Task InvalidOrUnsupportedResponsesFailWithoutLeakingBodies(string body)
    {
        await using var rig = new PluginTestContext();
        PluginTestContext.Stub(rig.Api, "/api/v1/query", body);
        await using var client = await Sink.CreateClientAsync(rig.Context(new() { ["default"] = rig.Http(rig.Api, "AzureMonitor") }), default);
        var failure = (SignalResult.Failure)(await client.QueryBatchAsync([new(Key(), Json(Query))], Window, default)).Single();
        Assert.That(failure.Code, Is.EqualTo("invalid_response"));
        Assert.That(failure.Message, Does.Not.Contain("secret"));
    }

    [TestCase("warnings")]
    [TestCase("infos")]
    public async Task QueryAnnotationsCannotSilentlyPublishPartialData(string field)
    {
        await using var rig = new PluginTestContext();
        PluginTestContext.Stub(rig.Api, "/api/v1/query",
            $$$"""{"status":"success","{{{field}}}":["secret"],"data":{"resultType":"scalar","result":[1700000000,"42"]}}""");
        await using var client = await Sink.CreateClientAsync(rig.Context(new() { ["default"] = rig.Http(rig.Api, "AzureMonitor") }), default);
        var failure = (SignalResult.Failure)(await client.QueryBatchAsync([new(Key(), Json(Query))], Window, default)).Single();
        Assert.That(failure.Code, Is.EqualTo("query_annotation"));
        Assert.That(failure.Message, Does.Not.Contain("secret"));
    }

    [TestCase(302)]
    [TestCase(400)]
    [TestCase(401)]
    [TestCase(403)]
    [TestCase(422)]
    [TestCase(429)]
    [TestCase(500)]
    [TestCase(503)]
    public async Task HttpErrorsPreserveStatusAndRetryDelayWithoutLeakingBody(int status)
    {
        await using var rig = new PluginTestContext();
        PluginTestContext.Stub(rig.Api, "/api/v1/query", "secret", status);
        await using var client = await Sink.CreateClientAsync(rig.Context(new() { ["default"] = rig.Http(rig.Api, "AzureMonitor") }), default);
        var failure = (SignalResult.Failure)(await client.QueryBatchAsync([new(Key(), Json(Query))], Window, default)).Single();
        Assert.That(failure.Code, Is.EqualTo($"http_{status}"));
        Assert.That(failure.RetryAfter, Is.EqualTo(TimeSpan.FromSeconds(7)));
        Assert.That(failure.Message, Does.Not.Contain("secret"));
    }

    [Test]
    public async Task InvalidQueryDoesNotMakeRequest()
    {
        await using var rig = new PluginTestContext();
        await using var client = await Sink.CreateClientAsync(rig.Context(new() { ["default"] = rig.Http(rig.Api, "AzureMonitor") }), default);
        var failure = (SignalResult.Failure)(await client.QueryBatchAsync([new(Key(), Json("""{"promql":"up","token":"secret"}"""))], Window, default)).Single();
        Assert.That(failure.Code, Is.EqualTo("invalid_query"));
        Assert.That(rig.Api.LogEntries, Is.Empty);
    }

    [Test]
    public async Task DisposalLeavesHostHttpClientUsable()
    {
        await using var rig = new PluginTestContext();
        var http = rig.Http(rig.Api, "AzureMonitor");
        var client = await Sink.CreateClientAsync(rig.Context(new() { ["default"] = http }), default);
        await client.DisposeAsync();
        PluginTestContext.Stub(rig.Api, "/still-owned", "{}");
        using var response = await http.GetAsync("still-owned");
        Assert.That(response.IsSuccessStatusCode, Is.True);
    }

    [Test]
    public async Task InstancesKeepWorkspaceClientsAndCredentialsIndependent()
    {
        await using var rig = new PluginTestContext();
        PluginTestContext.Stub(rig.Api, "/api/v1/query", Vector);
        PluginTestContext.Stub(rig.Stream, "/api/v1/query", Vector);
        await using var first = await Sink.CreateClientAsync(rig.Context(new() { ["default"] = rig.Http(rig.Api, "AzureMonitor", "first") }), default);
        await using var second = await Sink.CreateClientAsync(rig.Context(new() { ["default"] = rig.Http(rig.Stream, "AzureMonitor", "second") }), default);
        await first.QueryBatchAsync([new(Key(), Json(Query))], Window, default);
        await second.QueryBatchAsync([new(Key(), Json(Query))], Window, default);
        Assert.That(rig.Api.LogEntries.Single().RequestMessage!.Headers!["Authorization"].Single(), Is.EqualTo("Bearer first-token"));
        Assert.That(rig.Stream.LogEntries.Single().RequestMessage!.Headers!["Authorization"].Single(), Is.EqualTo("Bearer second-token"));
    }

    [Test]
    public async Task ActivationRejectsInvalidSettingsAndObservesCancellation()
    {
        await using var rig = new PluginTestContext();
        var endpoints = new Dictionary<string, HttpClient> { ["default"] = rig.Http(rig.Api, "AzureMonitor") };
        var sink = Sink;
        Assert.ThrowsAsync<ArgumentException>(async () => await sink.CreateClientAsync(rig.Context(endpoints, """{"token":"secret"}"""), default));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.CatchAsync<OperationCanceledException>(async () => await sink.CreateClientAsync(rig.Context(endpoints), cancellation.Token));
    }

    [TestCase("http://sink.test")]
    [TestCase("https://sink.test/path")]
    [TestCase("https://sink.test?query=up")]
    [TestCase("https://sink.test#fragment")]
    public async Task ActivationRequiresRootHttpsEndpoint(string endpoint)
    {
        await using var rig = new PluginTestContext();
        var http = rig.Http(rig.Api, "AzureMonitor");
        http.BaseAddress = new Uri(endpoint);
        var sink = Sink;
        Assert.ThrowsAsync<ArgumentException>(async () => await sink.CreateClientAsync(rig.Context(new() { ["default"] = http }), default));
    }

    [Test]
    public async Task ActivationRequiresDefaultEndpoint()
    {
        await using var rig = new PluginTestContext();
        var sink = Sink;
        Assert.ThrowsAsync<ArgumentException>(async () => await sink.CreateClientAsync(rig.Context(new()), default));
    }
}

internal sealed class AzureMonitorScenario : SinkClientScenario
{
    private readonly PluginTestContext rig = new();
    private ISinkClient client = null!;
    public override ISinkClient Client => client;

    internal static async Task<AzureMonitorScenario> Create()
    {
        var sink = AzureMonitorTests.Sink;
        var scenario = new AzureMonitorScenario();
        scenario.client = await sink.CreateClientAsync(
            scenario.rig.Context(new() { ["default"] = scenario.rig.Http(scenario.rig.Api, "AzureMonitor") }), default);
        return scenario;
    }

    public override SignalQuery Query(SignalKey key, ContractOutcome outcome)
    {
        if (outcome == ContractOutcome.Timeout) rig.SetTimeout(TimeSpan.FromMilliseconds(200));
        var expression = "contract_" + outcome.ToString().ToLowerInvariant();
        var response = Response.Create().WithStatusCode(outcome == ContractOutcome.Failure ? 429 : 200)
            .WithHeader("Content-Type", "application/json").WithHeader("Retry-After", "7")
            .WithBody(outcome == ContractOutcome.NoData
                ? """{"status":"success","data":{"resultType":"vector","result":[]}}"""
                : """{"status":"success","data":{"resultType":"vector","result":[{"metric":{},"value":[1700000000,"42"]}]}}""");
        if (outcome is ContractOutcome.Delayed or ContractOutcome.Timeout) response.WithDelay(TimeSpan.FromSeconds(10));
        rig.Api.Given(Request.Create().WithPath("/api/v1/query").UsingPost()
            .WithBody($"query={expression}&time=1700000000")).RespondWith(response);
        return new(key, SinkClientContractFixture.Json($$"""{"promql":"{{expression}}"}"""));
    }

    public override Task WaitForRequestAsync() => rig.RequestStarted.Task;
    public override async ValueTask DisposeAsync() { await client.DisposeAsync(); await rig.DisposeAsync(); }
}
