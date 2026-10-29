using System.Net;
using Microsoft.Extensions.Logging;
using SelfHostedEvaluators.Core.Reporting;

namespace SelfHostedEvaluators.Core.Tests;

[NonParallelizable]
public class ModelChecksTests
{
    [TestCase(false)]
    [TestCase(true)]
    public async Task UnreadableOrOversized429BodyCannotLoseTheSharedModelPause(bool oversized)
    {
        await using var rig = new ReportingRig();
        var snapshot = ReportingRig.Snapshot(secondModel: true);
        await rig.Initialize(snapshot);
        var body = new ProbeResponseBody(oversized: oversized, throws: !oversized);
        var first = true;
        rig.Transport.Send = (r, _) =>
        {
            if (r.Uri.Host == "model.test" && first)
            {
                first = false;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests)
                    { Content = body, Headers = { RetryAfter = new(TimeSpan.FromSeconds(10)) } });
            }
            return Task.FromResult(new HttpResponseMessage(r.Method == HttpMethod.Post ? HttpStatusCode.Accepted : HttpStatusCode.OK)
                { Content = new StringContent("""{"signals":[]}""") });
        };
        using var stop = new CancellationTokenSource();
        var running = new ModelChecks(rig.Models, rig.Time, rig.Redactor, rig.Diagnostics, rig.Log).RunAsync(snapshot, stop.Token);
        try
        {
            await PollingTestSupport.Until(() => body.Disposed && rig.Transport.Requests.Count(r => r.Uri.Host == "other.test") == 2);
            Assert.That(rig.Transport.Requests.Count(r => r.Uri.Host == "model.test"), Is.EqualTo(1));
            Assert.That(body.Reads, Is.Zero, "429 headers must reach the check before any body processing");
            Assert.That(rig.Metrics.Count("evaluator.checks.warnings", "http"), Is.EqualTo(1));
            await rig.Publisher!.EnqueueAsync(rig.Report(), default);
            await rig.Publisher.EnqueueAsync(rig.Report(model: 1), default);
            var flush = rig.Publisher.FlushAsync(default);
            await PollingTestSupport.Until(() => rig.Transport.Requests.Any(r => r.Uri.Host == "other.test" && r.Method == HttpMethod.Post));
            Assert.That(rig.Transport.Requests.Count(r => r.Uri.Host == "model.test"), Is.EqualTo(1));
            await PollingTestSupport.Until(() => rig.Time.TimerDueTimes.Count(d => d == TimeSpan.FromSeconds(10)) >= 2);
            rig.Time.Advance(TimeSpan.FromSeconds(9));
            Assert.That(rig.Transport.Requests.Count(r => r.Uri.Host == "model.test"), Is.EqualTo(1));
            rig.Time.Advance(TimeSpan.FromSeconds(1));
            await flush.WaitAsync(TimeSpan.FromSeconds(5));
            await PollingTestSupport.Until(() => rig.Transport.Requests.Count(r => r.Uri.Host == "model.test") == 3);
            Assert.That(rig.Log.Entries.Any(e => e.Text.Contains("sensitive-response-body", StringComparison.Ordinal)), Is.False);
        }
        finally { stop.Cancel(); await running; }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task SuccessfulEntityBodyErrorsStayBoundedWarnAndDispose(bool oversized)
    {
        await using var rig = new ReportingRig();
        var snapshot = ReportingRig.Snapshot();
        await rig.Initialize(snapshot);
        var body = new ProbeResponseBody(oversized: oversized, throws: !oversized);
        rig.Transport.Send = (r, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = r.Uri.AbsolutePath == "/api" ? new StringContent("{}") : body });
        using var stop = new CancellationTokenSource();
        var running = new ModelChecks(rig.Models, rig.Time, rig.Redactor, rig.Diagnostics, rig.Log).RunAsync(snapshot, stop.Token);
        try
        {
            await PollingTestSupport.Until(() => body.Disposed);
            Assert.That(rig.Metrics.Count("evaluator.checks.warnings", "transport"), Is.EqualTo(1));
            Assert.That(body.Reads, Is.EqualTo(1));
            Assert.That(rig.Log.Entries.Any(e => e.Text.Contains("sensitive-response-body", StringComparison.Ordinal)), Is.False);
        }
        finally { stop.Cancel(); await running; }
    }

    [TestCase("2026-06-01-preview")]
    [TestCase("2026-10-01-preview")]
    public async Task ChecksUseScopedAuthenticatedVersionedGetRoutesAndRepeatAfterThirtyMinutes(string apiVersion)
    {
        await using var rig = new ReportingRig();
        var snapshot = ReportingRig.Snapshot(signals: 2, secondModel: true, apiVersion: apiVersion);
        await rig.Initialize(snapshot, authenticated: true);
        rig.Transport.Send = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("""{"signals":[]}""") });
        var checks = new ModelChecks(rig.Models, rig.Time, rig.Redactor, rig.Diagnostics, rig.Log);
        using var stop = new CancellationTokenSource();
        var running = checks.RunAsync(snapshot, stop.Token);
        await PollingTestSupport.Until(() => rig.Transport.Requests.Count == 4);
        Assert.That(rig.Transport.Requests.Select(r => r.Uri.AbsolutePath), Is.EquivalentTo(
            new[] { "/api", "/api/entities/entity-one", "/api", "/api/entities/entity-one" }));
        Assert.That(rig.Transport.Requests.All(r => r.Method == HttpMethod.Get &&
            r.Uri.Query == "?api-version=" + apiVersion && r.Authorization == "Bearer offline-token"), Is.True);
        Assert.That(rig.Azure.Scopes, Is.EquivalentTo(new[] { "https://scope-0.test/.default", "https://scope-1.test/.default" }));
        rig.Time.Advance(TimeSpan.FromMinutes(29));
        Assert.That(rig.Transport.Requests.Count, Is.EqualTo(4));
        rig.Time.Advance(TimeSpan.FromMinutes(1));
        await PollingTestSupport.Until(() => rig.Transport.Requests.Count == 8);
        stop.Cancel();
        await running;
        Assert.That(rig.Metrics.Count("evaluator.checks.warnings"), Is.Zero);
    }

    [TestCase(401)]
    [TestCase(403)]
    [TestCase(404)]
    [TestCase(500)]
    public async Task RemoteFailureWarnsWithoutPreventingPublishing(int status)
    {
        await using var rig = new ReportingRig();
        var snapshot = ReportingRig.Snapshot();
        await rig.Initialize(snapshot);
        rig.Transport.Send = (r, _) => Task.FromResult(new HttpResponseMessage(
            r.Method == HttpMethod.Post ? HttpStatusCode.Accepted : (HttpStatusCode)status));
        using var stop = new CancellationTokenSource();
        var running = new ModelChecks(rig.Models, rig.Time, rig.Redactor, rig.Diagnostics, rig.Log).RunAsync(snapshot, stop.Token);
        await PollingTestSupport.Until(() => rig.Metrics.Count("evaluator.checks.warnings") == 2);
        await rig.Publisher!.EnqueueAsync(rig.Report(), default);
        await rig.Publisher.FlushAsync(default);
        Assert.That(rig.Transport.Requests.Count(r => r.Method == HttpMethod.Post), Is.EqualTo(1));
        Assert.That(rig.Log.Entries.All(e => e.Level is LogLevel.Warning or LogLevel.Debug), Is.True);
        stop.Cancel();
        await running;
    }

    [TestCase("""{"signals":[]}""", 0)]
    [TestCase("""{"signals":{}}""", 1)]
    [TestCase("""{"properties":{"signals":[]}}""", 0)]
    [TestCase("""{"signalInputs":[]}""", 0)]
    [TestCase("""{"entityName":"checkout-api","healthState":"Healthy","signalInputs":[{"name":"availability","effectiveHealthState":"Healthy"}],"dependencyInputs":[],"effectiveDependencyAggregation":{"configuration":{"aggregationType":"WorstOf","ignoreUnknown":false},"resolution":"ImplicitDefault"},"signalAggregationGroups":[]}""", 0)]
    [TestCase("""{"signalInputs":null}""", 1)]
    [TestCase("""{"signalInputs":{}}""", 1)]
    [TestCase("""{"signalInputs":"sensitive-content"}""", 1)]
    [TestCase("""{"signalInputs":{},"signals":[]}""", 1)]
    [TestCase("""{"signalInputs":{},"properties":{"signals":[]}}""", 1)]
    [TestCase("""{"signalInputs":[],"properties":{"signals":{}}}""", 0)]
    [TestCase("[]", 1)]
    [TestCase("null", 1)]
    [TestCase("not-json-sensitive-content", 1)]
    public async Task EntityShapeIsSafelyParsedAndMalformedBodiesAreNotLogged(string body, int warnings)
    {
        foreach (var apiVersion in new[] { "2026-06-01-preview", "2026-10-01-preview" })
        {
            await using var rig = new ReportingRig();
            var snapshot = ReportingRig.Snapshot(apiVersion: apiVersion);
            await rig.Initialize(snapshot);
            rig.Transport.Send = (r, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent(r.Uri.AbsolutePath == "/api" ? "{}" : body) });
            using var stop = new CancellationTokenSource();
            var running = new ModelChecks(rig.Models, rig.Time, rig.Redactor, rig.Diagnostics, rig.Log).RunAsync(snapshot, stop.Token);
            try
            {
                await PollingTestSupport.Until(() => rig.Transport.Requests.Count == 2);
                await PollingTestSupport.Until(() => rig.Time.TimerDueTimes.Contains(TimeSpan.FromMinutes(30)));
                Assert.That(rig.Metrics.Count("evaluator.checks.warnings"), Is.EqualTo(warnings), apiVersion);
                Assert.That(rig.Log.Entries.Any(e => e.Text.Contains("sensitive-content", StringComparison.Ordinal)), Is.False);
            }
            finally { stop.Cancel(); await running; }
        }
    }

    [TestCase(49, 0)]
    [TestCase(50, 1)]
    [TestCase(51, 1)]
    public async Task ExistingSignalCountAtTheServiceCapWarnsOnly(int count, int warnings)
    {
        await using var rig = new ReportingRig();
        var snapshot = ReportingRig.Snapshot();
        await rig.Initialize(snapshot);
        var body = """{"signals":[""" + string.Join(",", Enumerable.Repeat("""{"signalName":"existing"}""", count)) + "]}";
        rig.Transport.Send = (r, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(r.Uri.AbsolutePath == "/api" ? "{}" : body) });
        using var stop = new CancellationTokenSource();
        var running = new ModelChecks(rig.Models, rig.Time, rig.Redactor, rig.Diagnostics, rig.Log).RunAsync(snapshot, stop.Token);
        await PollingTestSupport.Until(() => rig.Transport.Requests.Count == 2);
        await PollingTestSupport.Until(() => rig.Time.TimerDueTimes.Contains(TimeSpan.FromMinutes(30)));
        Assert.That(rig.Metrics.Count("evaluator.checks.warnings"), Is.EqualTo(warnings));
        stop.Cancel();
        await running;
    }

    [TestCase("2026-06-01-preview", 49, 0)]
    [TestCase("2026-06-01-preview", 50, 1)]
    [TestCase("2026-06-01-preview", 51, 1)]
    [TestCase("2026-10-01-preview", 49, 0)]
    [TestCase("2026-10-01-preview", 50, 1)]
    [TestCase("2026-10-01-preview", 51, 1)]
    public async Task CurrentDiagnosticInputCountWarnsOnlyAtFifty(string apiVersion, int count, int warnings)
    {
        await using var rig = new ReportingRig();
        var snapshot = ReportingRig.Snapshot(apiVersion: apiVersion);
        await rig.Initialize(snapshot);
        var inputs = string.Join(",", Enumerable.Range(0, count)
            .Select(i => $$"""{"name":"input-{{i}}","effectiveHealthState":"Healthy"}"""));
        var body = $$"""{"entityName":"entity-one","healthState":"Healthy","signalInputs":[{{inputs}}],"dependencyInputs":[],"effectiveDependencyAggregation":{"configuration":{"aggregationType":"WorstOf","ignoreUnknown":false},"resolution":"ImplicitDefault"},"signalAggregationGroups":[]}""";
        rig.Transport.Send = (r, _) => Task.FromResult(new HttpResponseMessage(
            r.Method == HttpMethod.Post ? HttpStatusCode.Accepted : HttpStatusCode.OK)
            { Content = new StringContent(r.Uri.AbsolutePath == "/api" ? "{}" : body) });
        using var stop = new CancellationTokenSource();
        var running = new ModelChecks(rig.Models, rig.Time, rig.Redactor, rig.Diagnostics, rig.Log).RunAsync(snapshot, stop.Token);
        try
        {
            await PollingTestSupport.Until(() => rig.Transport.Requests.Count == 2);
            await PollingTestSupport.Until(() => rig.Time.TimerDueTimes.Contains(TimeSpan.FromMinutes(30)));
            Assert.That(rig.Metrics.Count("evaluator.checks.warnings"), Is.EqualTo(warnings));
            Assert.That(rig.Metrics.Count("evaluator.checks.warnings", "signal_cap"), Is.EqualTo(warnings));
            Assert.That(rig.Metrics.Count("evaluator.checks.warnings", "shape"), Is.Zero);
            await rig.Publisher!.EnqueueAsync(rig.Report(), default);
            await rig.Publisher.FlushAsync(default);
            Assert.That(rig.Transport.Requests.Count(r => r.Method == HttpMethod.Post), Is.EqualTo(1));
        }
        finally { stop.Cancel(); await running; }
    }

    [Test]
    public async Task PausedModelCheckDoesNotDelayAnotherModelsThirtyMinuteChecks()
    {
        await using var rig = new ReportingRig();
        var snapshot = ReportingRig.Snapshot(secondModel: true);
        await rig.Initialize(snapshot);
        rig.Transport.Send = (request, _) =>
        {
            var response = new HttpResponseMessage(request.Uri.Host == "model.test" ? HttpStatusCode.TooManyRequests : HttpStatusCode.OK)
                { Content = new StringContent("""{"signals":[]}""") };
            response.Headers.TryAddWithoutValidation("Retry-After", "3600");
            return Task.FromResult(response);
        };
        using var stop = new CancellationTokenSource();
        var running = new ModelChecks(rig.Models, rig.Time, rig.Redactor, rig.Diagnostics, rig.Log).RunAsync(snapshot, stop.Token);
        await PollingTestSupport.Until(() => rig.Time.TimerDueTimes.Contains(TimeSpan.FromMinutes(30)) ||
            rig.Transport.Requests.Count >= 3);
        rig.Time.Advance(TimeSpan.FromMinutes(30));
        await PollingTestSupport.Until(() => rig.Transport.Requests.Count(r => r.Uri.Host == "other.test") >= 4);
        Assert.That(rig.Transport.Requests.Count(r => r.Uri.Host == "model.test"), Is.EqualTo(1));
        stop.Cancel();
        await running;
    }

    [Test]
    public async Task SlowChecksDoNotShiftTheThirtyMinuteCadence()
    {
        await using var rig = new ReportingRig();
        var snapshot = ReportingRig.Snapshot();
        await rig.Initialize(snapshot);
        var finish = PollingTestSupport.Gate();
        rig.Transport.Send = async (_, _) =>
        {
            if (rig.Transport.Requests.Count == 1) await finish.Task;
            return new(HttpStatusCode.OK) { Content = new StringContent("""{"signals":[]}""") };
        };
        using var stop = new CancellationTokenSource();
        var running = new ModelChecks(rig.Models, rig.Time, rig.Redactor, rig.Diagnostics, rig.Log).RunAsync(snapshot, stop.Token);
        rig.Time.Advance(TimeSpan.FromSeconds(5));
        finish.SetResult();
        await PollingTestSupport.Until(() => rig.Transport.Requests.Count == 2 &&
            rig.Time.TimerDueTimes.Contains(TimeSpan.FromMinutes(30)));
        rig.Time.Advance(TimeSpan.FromMinutes(30) - TimeSpan.FromSeconds(5));
        await PollingTestSupport.Until(() => rig.Transport.Requests.Count == 4);
        stop.Cancel();
        await running;
    }
}
