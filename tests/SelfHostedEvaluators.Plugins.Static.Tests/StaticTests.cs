using System.Threading.RateLimiting;
using Microsoft.Extensions.Logging.Abstractions;
using SelfHostedEvaluators.Plugins.Abstractions;
using SelfHostedEvaluators.Plugins.Testing;

namespace SelfHostedEvaluators.Plugins.Static.Tests;

public class StaticTests : SinkClientContractFixture
{
    [Test]
    public void StaticPackageManifestAndDependencyBoundary() => AssertPackage(new StaticPackage(), "static");
    protected override ValueTask<SinkClientScenario> CreateScenarioAsync() => ValueTask.FromResult<SinkClientScenario>(new StaticScenario());
    private static ISinkPlugin Sink => Plugin("Static");

    [TestCase("""{"kind":"observation","value":3}""", true)]
    [TestCase("""{"kind":"observation","nativeState":"OK"}""", true)]
    [TestCase("""{"kind":"observation","value":3,"nativeState":"Warn"}""", true)]
    [TestCase("""{"kind":"noData","reason":"empty"}""", true)]
    [TestCase("""{"kind":"failure","code":"failed","message":"fake failure","retryAfter":"7s"}""", true)]
    [TestCase("""{"kind":"observation"}""", false)]
    [TestCase("""{"kind":"observation","value":1e999}""", false)]
    [TestCase("""{"kind":"observation","value":"NaN"}""", false)]
    [TestCase("""{"kind":"observation","value":null}""", false)]
    [TestCase("""{"kind":"observation","nativeState":""}""", false)]
    [TestCase("""{"kind":"noData","reason":"x","value":1}""", false)]
    [TestCase("""{"kind":"failure","message":"x"}""", false)]
    [TestCase("""{"kind":"failure","code":"x","message":"x","retryAfter":"0s"}""", false)]
    [TestCase("""{"kind":"observation","value":1,"value":2}""", false)]
    [TestCase("""{"kind":"observation","value":1,"token":"secret"}""", false)]
    [TestCase("null", false)]
    public void StaticQuerySchema(string query, bool valid) => Assert.That(Sink.ValidateQuery(Json(query)).IsValid, Is.EqualTo(valid));

    [TestCase("{}", true)]
    [TestCase("""{"delay":"1s"}""", true)]
    [TestCase("""{"delay":"0s"}""", false)]
    [TestCase("""{"delay":"2d"}""", false)]
    [TestCase("""{"delay":"1s","delay":"2s"}""", false)]
    [TestCase("""{"secret":"value"}""", false)]
    public void StaticSettingsSchema(string settings, bool valid) => Assert.That(Sink.ValidateSettings(Json(settings)).IsValid, Is.EqualTo(valid));

    [Test]
    public async Task StaticObservationUsesScheduledTimestampAndIsRepeatable()
    {
        await using var rig = new StaticScenario();
        var query = new SignalQuery(Key(), Json("""{"kind":"observation","value":3,"nativeState":"OK","detail":"fixture"}"""));
        var first = await rig.Client.QueryBatchAsync([query], Window, default);
        var second = await rig.Client.QueryBatchAsync([query], Window, default);
        Assert.That(first, Is.EqualTo(second));
        Assert.That((SignalResult.Observation)first.Single(), Is.EqualTo(new SignalResult.Observation(Key(), 3, "OK", Window.ScheduledAt, "fixture")));
        Assert.That(Sink.Capabilities.RequiredEndpoints, Is.Empty);
    }
}

internal sealed class StaticScenario : SinkClientScenario
{
    private readonly TrackingTime time = new();
    private readonly RateLimiter limiter = new ConcurrencyLimiter(new() { PermitLimit = 8, QueueLimit = 8 });
    private ISinkClient? client;
    public override ISinkClient Client => client ??= SinkClientContractFixture.Plugin("Static")
        .CreateClientAsync(new("static", SinkClientContractFixture.Json("""{"delay":"1s"}"""), 8, new Dictionary<string, HttpClient>(),
            nullAuth, limiter, NullLogger.Instance, time), default).AsTask().GetAwaiter().GetResult();
    private static readonly IAuthContext nullAuth = new UnusedAuth();
    public override SignalQuery Query(SignalKey key, ContractOutcome outcome)
    {
        time.Timeout = outcome == ContractOutcome.Timeout;
        time.Delay = outcome == ContractOutcome.Delayed;
        return new(key, SinkClientContractFixture.Json(outcome switch
        {
            ContractOutcome.NoData => """{"kind":"noData","reason":"empty"}""",
            ContractOutcome.Failure => """{"kind":"failure","code":"fixture","message":"fake failure","retryAfter":"7s"}""",
            _ => """{"kind":"observation","value":42}"""
        }));
    }
    public override Task WaitForRequestAsync() => time.Started.Task;
    public override async ValueTask DisposeAsync() { if (client is not null) await client.DisposeAsync(); await limiter.DisposeAsync(); }
    private sealed class TrackingTime : TimeProvider
    {
        public bool Timeout { get; set; }
        public bool Delay { get; set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Started.TrySetResult();
            if (Timeout) throw new TimeoutException("secret");
            return base.CreateTimer(callback, state, Delay ? TimeSpan.FromSeconds(30) : TimeSpan.Zero, period);
        }
    }
    private sealed class UnusedAuth : IAuthContext
    {
        public string Scheme => "none";
        public ValueTask ConfigureHttpHandlerAsync(Uri uri, HttpClientHandler handler, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask AuthenticateAsync(HttpRequestMessage request, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask AuthenticateWebSocketAsync(Uri uri, System.Net.WebSockets.ClientWebSocketOptions options, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => throw new InvalidOperationException("Core-owned");
    }
}
