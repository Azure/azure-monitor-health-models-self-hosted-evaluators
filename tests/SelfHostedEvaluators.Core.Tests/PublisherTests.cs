using System.Diagnostics;
using System.Net;
using System.Text;
using SelfHostedEvaluators.Core.Reporting;
using SelfHostedEvaluators.Plugins.Abstractions;

namespace SelfHostedEvaluators.Core.Tests;

[NonParallelizable]
public class PublisherTests
{
    [TestCase(503)]
    [TestCase(429)]
    public async Task TtlClippedRetryCannotDispatchBeforeTheExpiryCallbackRuns(int status)
    {
        await using var rig = new ReportingRig();
        await rig.Initialize(ReportingRig.Snapshot());
        rig.Transport.Send = (_, _) => Task.FromResult(new HttpResponseMessage(
            rig.Transport.Requests.Count == 1 ? (HttpStatusCode)status : HttpStatusCode.Accepted)
            { Headers = { RetryAfter = new(TimeSpan.FromMinutes(10)) } });
        rig.Time.DeferNextTimer = true;
        await rig.Publisher!.EnqueueAsync(rig.Report(), default);
        var flush = rig.Publisher.FlushAsync(default);
        await PollingTestSupport.Until(() => rig.Time.TimerDueTimes.Count(d => d == TimeSpan.FromMinutes(1)) == 2);
        rig.Time.Advance(TimeSpan.FromMinutes(1));
        await flush.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(rig.Transport.Requests.Count, Is.EqualTo(1), "the clock deadline, not callback order, forbids a retry");
        Assert.That(rig.Metrics.Count("evaluator.reports.dropped", "expired"), Is.EqualTo(1));
        while (rig.Time.DeferredCallbacks.TryDequeue(out var callback)) callback();
        Assert.That(rig.Metrics.Count("evaluator.reports.dropped", "expired"), Is.EqualTo(1));
    }

    [Test]
    public async Task PermitResumingAtExpiryCannotDispatchBeforeTheExpiryCallbackRuns()
    {
        await using var rig = new ReportingRig();
        await rig.Initialize(ReportingRig.Snapshot(budget: 1));
        using (var request = new HttpRequestMessage(HttpMethod.Get, rig.Models[0].Route("/api")))
        using (await rig.Models[0].SendAsync(request, default)) { }
        rig.Time.DeferNextTimer = true;
        await rig.Publisher!.EnqueueAsync(rig.Report(), default);
        var flush = rig.Publisher.FlushAsync(default);
        await PollingTestSupport.Until(() => rig.Time.TimerDueTimes.Count(d => d == TimeSpan.FromMinutes(1)) == 2);
        rig.Time.Advance(TimeSpan.FromMinutes(1));
        await flush.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(rig.Transport.Requests.Count(r => r.Method == HttpMethod.Post), Is.Zero);
        Assert.That(rig.Metrics.Count("evaluator.reports.dropped", "expired"), Is.EqualTo(1));
    }

    [Test]
    public async Task ResponseCompletingAfterExpiryCountsItsDropWithoutTheExpiryCallback()
    {
        await using var rig = new ReportingRig();
        await rig.Initialize(ReportingRig.Snapshot());
        var finish = PollingTestSupport.Gate();
        rig.Transport.Send = async (_, _) => { await finish.Task; return new(HttpStatusCode.Accepted); };
        rig.Time.DeferNextTimer = true;
        await rig.Publisher!.EnqueueAsync(rig.Report(), default);
        rig.Time.DeferNextTimer = true;
        var flush = rig.Publisher.FlushAsync(default);
        await PollingTestSupport.Until(() => rig.Transport.Requests.Count == 1);
        rig.Time.Advance(TimeSpan.FromMinutes(1));
        finish.SetResult();
        await flush.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(rig.Metrics.Count("evaluator.reports.dropped", "expired"), Is.EqualTo(1));
        Assert.That(rig.Transport.Requests.Count, Is.EqualTo(1));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task AuthenticationResumingAtExpiryCannotDispatchWithoutAnyClockCallbacks(bool configure)
    {
        await using var rig = new ReportingRig();
        var auth = new DelayedAuth(configure);
        await rig.Initialize(ReportingRig.Snapshot(), authScheme: auth);
        rig.Time.DeferNextTimer = true;
        await rig.Publisher!.EnqueueAsync(rig.Report(), default);
        rig.Time.DeferNextTimer = true; // Also defer the request timeout to prove the absolute deadline gate.
        var flush = rig.Publisher.FlushAsync(default);
        await auth.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        rig.Time.Advance(TimeSpan.FromMinutes(1));
        auth.Finish.SetResult();
        await flush.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(rig.Transport.Requests, Is.Empty);
        Assert.That(rig.Metrics.Count("evaluator.reports.dropped", "expired"), Is.EqualTo(1));
    }

    [Test]
    public async Task EnqueueCrossingExpiryDropsCleanlyAndKeepsTheMailboxUsable()
    {
        await using var rig = new ReportingRig();
        await rig.Initialize(ReportingRig.Snapshot());
        var report = rig.Report();
        rig.Time.AdvanceAfterNextUtcRead = TimeSpan.FromMinutes(2);
        await rig.Publisher!.EnqueueAsync(report, default);
        Assert.That(rig.Metrics.Count("evaluator.reports.dropped", "expired"), Is.EqualTo(1));
        var next = rig.Report(value: 2);
        await rig.Publisher.EnqueueAsync(next, default);
        await rig.Publisher.FlushAsync(default).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(rig.Transport.Requests.Select(r => r.ClientId), Is.EqualTo(new[] { next.ClientRequestId }));
        Assert.That(rig.Metrics.Count("evaluator.reports.superseded"), Is.Zero);
        Assert.That(rig.Metrics.Count("evaluator.reports.dropped", "expired"), Is.EqualTo(1));
    }

    [Test]
    public async Task ReportingContinuesPastTheSinkPipelinesThreeAttemptLimit()
    {
        await using var rig = new ReportingRig();
        await rig.Initialize(ReportingRig.Snapshot());
        rig.Transport.Send = (_, _) => Task.FromResult(new HttpResponseMessage(
            rig.Transport.Requests.Count <= 5 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.Accepted)
            { Headers = { RetryAfter = new(TimeSpan.Zero) } });
        await rig.Publisher!.EnqueueAsync(rig.Report(), default);
        await rig.Publisher.FlushAsync(default);
        Assert.That(rig.Transport.Requests.Count, Is.EqualTo(6));
        Assert.That(rig.Transport.Requests.Select(r => r.ClientId).Distinct().Count(), Is.EqualTo(1));
        Assert.That(rig.Metrics.Count("evaluator.publishes"), Is.EqualTo(6));
    }

    [Test]
    public async Task RunCancellationLeavesPendingWorkForTheHostsSharedFlushBudget()
    {
        await using var rig = new ReportingRig();
        await rig.Initialize(ReportingRig.Snapshot());
        using var run = new CancellationTokenSource();
        var running = rig.Publisher!.RunAsync(run.Token);
        run.Cancel();
        await running;
        await rig.Publisher.EnqueueAsync(rig.Report(), default);
        await rig.Publisher.FlushAsync(default);
        Assert.That(rig.Transport.Requests.Count, Is.EqualTo(1));
        Assert.That(rig.Metrics.Count("evaluator.reports.dropped"), Is.Zero);
    }

    [Test]
    public async Task AlreadyExhaustedShutdownBudgetDropsWithoutStartingPendingSends()
    {
        await using var rig = new ReportingRig();
        await rig.Initialize(ReportingRig.Snapshot());
        await rig.Publisher!.EnqueueAsync(rig.Report(), default);
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        await rig.Publisher.FlushAsync(stop.Token);
        Assert.That(rig.Transport.Requests, Is.Empty);
        Assert.That(rig.Metrics.Count("evaluator.reports.dropped", "shutdown"), Is.EqualTo(1));
    }

    [TestCase(202, 0)]
    [TestCase(400, 1)]
    [TestCase(413, 1)]
    public async Task UnneededResponseBodyCannotTurnAcceptanceOrPermanentStatusIntoARetry(int status, int drops)
    {
        await using var rig = new ReportingRig();
        await rig.Initialize(ReportingRig.Snapshot());
        rig.Transport.Send = (_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)
            { Content = new UnreadableResponseBody() });
        await rig.Publisher!.EnqueueAsync(rig.Report(), default);
        await rig.Publisher.FlushAsync(default).WaitAsync(TimeSpan.FromSeconds(1));
        Assert.That(rig.Transport.Requests.Count, Is.EqualTo(1));
        Assert.That(rig.Metrics.Count("evaluator.reports.dropped", "permanent"), Is.EqualTo(drops));
        Assert.That(rig.Metrics.Measurements.Any(m => m.Name == "evaluator.publishes" &&
            Equals(m.Tags.GetValueOrDefault("status"), status.ToString())), Is.True);
    }

    [Test]
    public async Task ReplacingAReportBeforeRunKeepsOnlyTheLatestBody()
    {
        await using var rig = new ReportingRig();
        await rig.Initialize(ReportingRig.Snapshot());
        await rig.Publisher!.EnqueueAsync(rig.Report(value: 1), default);
        var latest = rig.Report(value: 2);
        await rig.Publisher.EnqueueAsync(latest, default);
        await rig.Publisher.FlushAsync(default);
        Assert.That(rig.Transport.Requests.Single().ClientId, Is.EqualTo(latest.ClientRequestId));
        Assert.That(rig.Metrics.Count("evaluator.reports.superseded"), Is.EqualTo(1));
    }

    [Test]
    public async Task AcceptedReportIsBufferedAuthenticatedAndNeverRetried()
    {
        await using var rig = new ReportingRig();
        await rig.Initialize(ReportingRig.Snapshot(), authenticated: true);
        var report = rig.Report();
        using var run = new CancellationTokenSource();
        var running = rig.Publisher!.RunAsync(run.Token);
        await rig.Publisher.EnqueueAsync(report, default);
        await rig.Publisher.FlushAsync(default);
        run.Cancel();
        await running;
        var request = rig.Transport.Requests.Single();
        Assert.That(request.Method, Is.EqualTo(HttpMethod.Post));
        Assert.That(request.Uri.AbsoluteUri, Is.EqualTo("https://model.test/api/entities/entity-one/healthReports?api-version=2026-06-01-preview"));
        Assert.That(request.ContentLength, Is.EqualTo(request.Body.Length));
        Assert.That(request.Chunked, Is.Not.True);
        Assert.That(request.ContentType, Is.EqualTo("application/json; charset=utf-8"));
        Assert.That(request.ClientId, Is.EqualTo(report.ClientRequestId));
        Assert.That(request.ReturnClientId, Is.EqualTo("true"));
        Assert.That(request.Authorization, Is.EqualTo("Bearer offline-token"));
        Assert.That(rig.Azure.Scopes, Is.EqualTo(new[] { "https://scope-0.test/.default" }));
        Assert.That(Encoding.UTF8.GetString(request.Body), Is.EqualTo(
            """{"signalName":"signal-000","healthState":"Healthy","value":5,"expiresInMinutes":1}"""));
        Assert.That(rig.Metrics.Count("evaluator.publishes"), Is.EqualTo(1));
        Assert.That(rig.Metrics.Measurements.SelectMany(m => m.Tags.Keys),
            Is.SubsetOf(new[] { "model", "plugin", "package", "outcome", "state", "status" }));
    }

    [TestCase(408)]
    [TestCase(429)]
    [TestCase(500)]
    [TestCase(503)]
    [TestCase(599)]
    [TestCase(0)]
    public async Task TransientFailuresRetryUntil202WithOneStableClientId(int status)
    {
        await using var rig = new ReportingRig();
        await rig.Initialize(ReportingRig.Snapshot());
        rig.Transport.Send = (_, _) =>
        {
            if (rig.Transport.Requests.Count > 1) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Accepted));
            if (status == 0) throw new HttpRequestException("sensitive-network-error");
            var response = new HttpResponseMessage((HttpStatusCode)status);
            response.Headers.TryAddWithoutValidation("Retry-After", "10");
            return Task.FromResult(response);
        };
        var report = rig.Report();
        await rig.Publisher!.EnqueueAsync(report, default);
        var flushing = rig.Publisher.FlushAsync(default);
        await PollingTestSupport.Until(() => rig.Metrics.Count("evaluator.publishes") == 1);
        Assert.That(rig.Transport.Requests.Count, Is.EqualTo(1), "auth pipeline must not retry invisibly");
        await PollingTestSupport.Until(() => status == 0
            ? rig.Time.TimerDueTimes.Any(d => d > TimeSpan.Zero && d <= TimeSpan.FromSeconds(1))
            : rig.Time.TimerDueTimes.Contains(TimeSpan.FromSeconds(10)));
        rig.Time.Advance(TimeSpan.FromSeconds(status == 0 ? 30 : 9));
        if (status != 0) Assert.That(rig.Transport.Requests.Count, Is.EqualTo(1));
        rig.Time.Advance(TimeSpan.FromSeconds(1));
        await flushing.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(rig.Transport.Requests.Select(r => r.ClientId), Is.EqualTo(new[] { report.ClientRequestId, report.ClientRequestId }));
        Assert.That(rig.Metrics.Count("evaluator.publishes"), Is.EqualTo(2));
        if (status == 429) Assert.That(rig.Metrics.Count("evaluator.ratelimit.wait"), Is.EqualTo(10000));
        Assert.That(rig.Log.Entries.Any(e => e.Text.Contains("sensitive-network-error", StringComparison.Ordinal)), Is.False);
    }

    [TestCase(200)]
    [TestCase(204)]
    [TestCase(301)]
    [TestCase(400)]
    [TestCase(401)]
    [TestCase(403)]
    [TestCase(404)]
    [TestCase(413)]
    [TestCase(415)]
    [TestCase(422)]
    public async Task NonRetryableResponsesAreExplicitlyLoggedAndDroppedOnce(int status)
    {
        await using var rig = new ReportingRig();
        await rig.Initialize(ReportingRig.Snapshot());
        rig.Transport.Send = (_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)
            { Content = new StringContent("do-not-log-response") });
        await rig.Publisher!.EnqueueAsync(rig.Report(), default);
        await rig.Publisher.FlushAsync(default);
        Assert.That(rig.Transport.Requests.Count, Is.EqualTo(1));
        Assert.That(rig.Metrics.Count("evaluator.reports.dropped", "permanent"), Is.EqualTo(1));
        Assert.That(rig.Log.Entries.Any(e => e.Text.Contains(status.ToString(), StringComparison.Ordinal)), Is.True);
        Assert.That(rig.Log.Entries.Any(e => e.Text.Contains("do-not-log-response", StringComparison.Ordinal)), Is.False);
    }

    [Test]
    public async Task RetryAfterHttpDatePausesOnlyItsOwnModelIncludingOtherSignals()
    {
        await using var rig = new ReportingRig();
        await rig.Initialize(ReportingRig.Snapshot(2, secondModel: true));
        var first = 0;
        rig.Transport.Send = (request, _) =>
        {
            var response = new HttpResponseMessage(request.Uri.Host == "model.test" && Interlocked.Increment(ref first) == 1
                ? HttpStatusCode.TooManyRequests : HttpStatusCode.Accepted);
            response.Headers.TryAddWithoutValidation("Retry-After", rig.Time.GetUtcNow().AddSeconds(10).ToString("R"));
            return Task.FromResult(response);
        };
        using var run = new CancellationTokenSource();
        var running = rig.Publisher!.RunAsync(run.Token);
        await rig.Publisher.EnqueueAsync(rig.Report(), default);
        await PollingTestSupport.Until(() => rig.Metrics.Count("evaluator.publishes") == 1);
        await rig.Publisher.EnqueueAsync(rig.Report(signal: "signal-001"), default);
        await rig.Publisher.EnqueueAsync(rig.Report(model: 1), default);
        await PollingTestSupport.Until(() => rig.Transport.Requests.Any(r => r.Uri.Host == "other.test"));
        Assert.That(rig.Transport.Requests.Count(r => r.Uri.Host == "model.test"), Is.EqualTo(1));
        var flush = rig.Publisher.FlushAsync(default);
        rig.Time.Advance(TimeSpan.FromSeconds(10));
        await flush.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(rig.Transport.Requests.Count(r => r.Uri.Host == "model.test"), Is.EqualTo(3));
        run.Cancel();
        await running;
    }

    [TestCase("garbage")]
    [TestCase("10, 20")]
    public async Task MalformedRetryAfterWarnsAndFallsBackWithinThirtySeconds(string header)
    {
        await using var rig = new ReportingRig();
        await rig.Initialize(ReportingRig.Snapshot());
        rig.Transport.Send = (_, _) =>
        {
            var response = new HttpResponseMessage(rig.Transport.Requests.Count == 1 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.Accepted);
            response.Headers.TryAddWithoutValidation("Retry-After", header);
            return Task.FromResult(response);
        };
        await rig.Publisher!.EnqueueAsync(rig.Report(), default);
        var flush = rig.Publisher.FlushAsync(default);
        await PollingTestSupport.Until(() => rig.Log.Entries.Any(e => e.Text.Contains("malformed", StringComparison.OrdinalIgnoreCase)));
        await PollingTestSupport.Until(() => rig.Time.TimerDueTimes.Any(d => d > TimeSpan.Zero && d <= TimeSpan.FromSeconds(1)));
        rig.Time.Advance(TimeSpan.FromSeconds(30));
        await flush.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(rig.Transport.Requests.Count, Is.EqualTo(2));
    }

    [Test]
    public async Task ReplacingRetryWaitSendsLatestImmediatelyWithoutWaitingForOldDelay()
    {
        await using var rig = new ReportingRig();
        await rig.Initialize(ReportingRig.Snapshot());
        rig.Transport.Send = (_, _) =>
        {
            var response = new HttpResponseMessage(rig.Transport.Requests.Count == 1 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.Accepted);
            response.Headers.TryAddWithoutValidation("Retry-After", "300");
            return Task.FromResult(response);
        };
        using var run = new CancellationTokenSource();
        var running = rig.Publisher!.RunAsync(run.Token);
        await rig.Publisher.EnqueueAsync(rig.Report(value: 1), default);
        await PollingTestSupport.Until(() => rig.Metrics.Count("evaluator.publishes") == 1);
        var latest = rig.Report(value: 2);
        await rig.Publisher.EnqueueAsync(latest, default);
        await rig.Publisher.FlushAsync(default).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(rig.Transport.Requests.Last().ClientId, Is.EqualTo(latest.ClientRequestId));
        Assert.That(rig.Metrics.Count("evaluator.reports.superseded"), Is.EqualTo(1));
        Assert.That(rig.Metrics.Count("evaluator.reports.dropped"), Is.Zero);
        run.Cancel();
        await running;
    }

    [Test]
    public async Task SupersedingNeverOverlapsAnUnderlyingSendThatIgnoresCancellation()
    {
        await using var rig = new ReportingRig();
        await rig.Initialize(ReportingRig.Snapshot());
        var finish = PollingTestSupport.Gate();
        CancellationToken originalToken = default;
        rig.Transport.Send = async (_, ct) =>
        {
            if (rig.Transport.Requests.Count == 1) { originalToken = ct; await finish.Task; }
            return new(HttpStatusCode.Accepted);
        };
        using var run = new CancellationTokenSource();
        var running = rig.Publisher!.RunAsync(run.Token);
        await rig.Publisher.EnqueueAsync(rig.Report(value: 1), default);
        await PollingTestSupport.Until(() => rig.Transport.Requests.Count == 1);
        await rig.Publisher.EnqueueAsync(rig.Report(value: 2), default);
        var latest = rig.Report(value: 3);
        await rig.Publisher.EnqueueAsync(latest, default);
        await PollingTestSupport.Until(() => originalToken.IsCancellationRequested);
        Assert.That(rig.Transport.Requests.Count, Is.EqualTo(1));
        finish.SetResult();
        await rig.Publisher.FlushAsync(default).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(rig.Transport.Requests.Select(r => r.ClientId).Last(), Is.EqualTo(latest.ClientRequestId));
        Assert.That(rig.Transport.Requests.Count, Is.EqualTo(2));
        Assert.That(rig.Metrics.Count("evaluator.reports.superseded"), Is.EqualTo(2));
        run.Cancel();
        await running;
    }

    [Test]
    public async Task ExpiryCancelsRetryAndCountsOneDrop()
    {
        await using var rig = new ReportingRig();
        await rig.Initialize(ReportingRig.Snapshot());
        rig.Transport.Send = (_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            response.Headers.TryAddWithoutValidation("Retry-After", "600");
            return Task.FromResult(response);
        };
        await rig.Publisher!.EnqueueAsync(rig.Report(), default);
        var flush = rig.Publisher.FlushAsync(default);
        await PollingTestSupport.Until(() => rig.Metrics.Count("evaluator.publishes") == 1);
        rig.Time.Advance(TimeSpan.FromMinutes(1));
        await flush.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(rig.Transport.Requests.Count, Is.EqualTo(1));
        Assert.That(rig.Metrics.Count("evaluator.reports.dropped", "expired"), Is.EqualTo(1));
    }

    [Test]
    public async Task AlreadyExpiredMailboxNeverMakesARequest()
    {
        await using var rig = new ReportingRig();
        await rig.Initialize(ReportingRig.Snapshot());
        var report = rig.Report();
        rig.Time.Advance(TimeSpan.FromMinutes(1));
        await rig.Publisher!.EnqueueAsync(report, default);
        await rig.Publisher.FlushAsync(default);
        Assert.That(rig.Transport.Requests, Is.Empty);
        Assert.That(rig.Metrics.Count("evaluator.reports.dropped", "expired"), Is.EqualTo(1));
    }

    [Test]
    public async Task EveryRetryAndRemoteGetConsumesTheSameModelBudget()
    {
        await using var rig = new ReportingRig();
        await rig.Initialize(ReportingRig.Snapshot(budget: 1));
        rig.Transport.Send = (_, _) => Task.FromResult(new HttpResponseMessage(
            rig.Transport.Requests.Count == 2 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.Accepted)
            { Headers = { RetryAfter = new(TimeSpan.Zero) } });
        using (var request = new HttpRequestMessage(HttpMethod.Get, rig.Models[0].Route("/api")))
        using (await rig.Models[0].SendAsync(request, default)) { }
        await rig.Publisher!.EnqueueAsync(rig.Report(ttl: 10), default);
        var flush = rig.Publisher.FlushAsync(default);
        Assert.That(rig.Transport.Requests.Count, Is.EqualTo(1));
        await PollingTestSupport.Until(() => rig.Time.TimerDueTimes.Contains(TimeSpan.FromMinutes(1)));
        rig.Time.Advance(TimeSpan.FromMinutes(1));
        await PollingTestSupport.Until(() => rig.Metrics.Count("evaluator.publishes") == 1);
        Assert.That(rig.Transport.Requests.Count, Is.EqualTo(2));
        await PollingTestSupport.Until(() => rig.Time.TimerDueTimes.Count(d => d == TimeSpan.FromMinutes(1)) >= 2);
        rig.Time.Advance(TimeSpan.FromMinutes(1));
        await flush.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(rig.Transport.Requests.Count, Is.EqualTo(3));
        Assert.That(rig.Metrics.Count("evaluator.ratelimit.wait"), Is.GreaterThanOrEqualTo(120000));
    }

    [Test]
    public async Task SharedShutdownBudgetReturnsWithoutWaitingForUncooperativeTransportAndCountsLatestOnly()
    {
        await using var rig = new ReportingRig();
        await rig.Initialize(ReportingRig.Snapshot());
        var finish = PollingTestSupport.Gate();
        rig.Transport.Send = async (_, _) => { await finish.Task; return new(HttpStatusCode.Accepted); };
        using var run = new CancellationTokenSource();
        var running = rig.Publisher!.RunAsync(run.Token);
        await rig.Publisher.EnqueueAsync(rig.Report(), default);
        await PollingTestSupport.Until(() => rig.Transport.Requests.Count == 1);
        await rig.Publisher.EnqueueAsync(rig.Report(value: 2), default);
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(5), rig.Time);
        var flush = rig.Publisher.FlushAsync(budget.Token);
        rig.Time.Advance(TimeSpan.FromSeconds(5));
        await flush.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(rig.Metrics.Count("evaluator.reports.dropped", "shutdown"), Is.EqualTo(1));
        Assert.That(rig.Metrics.Count("evaluator.reports.superseded"), Is.EqualTo(1));
        rig.Models[0].Dispose();
        Assert.That(rig.Transport.Disposals, Is.Zero, "a shutdown deadline cannot dispose a still-active transport");
        finish.SetResult();
        await rig.Publisher.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(rig.Transport.Disposals, Is.EqualTo(1));
        Assert.That(rig.Transport.Requests.Count, Is.EqualTo(1));
        await rig.Publisher.FlushAsync(default);
        Assert.That(rig.Metrics.Count("evaluator.reports.dropped", "shutdown"), Is.EqualTo(1));
        Assert.ThrowsAsync<InvalidOperationException>(async () => await rig.Publisher.EnqueueAsync(rig.Report(), default));
        run.Cancel();
        await running;
    }

    [Test]
    public async Task Replacing429RetryDoesNotCancelTheSharedModelPause()
    {
        await using var rig = new ReportingRig();
        await rig.Initialize(ReportingRig.Snapshot());
        rig.Transport.Send = (_, _) => Task.FromResult(new HttpResponseMessage(
            rig.Transport.Requests.Count == 1 ? HttpStatusCode.TooManyRequests : HttpStatusCode.Accepted)
            { Headers = { RetryAfter = new(TimeSpan.FromSeconds(10)) } });
        using var run = new CancellationTokenSource();
        var running = rig.Publisher!.RunAsync(run.Token);
        await rig.Publisher.EnqueueAsync(rig.Report(), default);
        await PollingTestSupport.Until(() => rig.Time.TimerDueTimes.Contains(TimeSpan.FromSeconds(10)));
        var latest = rig.Report(value: 2);
        await rig.Publisher.EnqueueAsync(latest, default);
        var flush = rig.Publisher.FlushAsync(default);
        await PollingTestSupport.Until(() => rig.Time.TimerDueTimes.Count(d => d == TimeSpan.FromSeconds(10)) >= 2);
        Assert.That(rig.Transport.Requests.Count, Is.EqualTo(1));
        rig.Time.Advance(TimeSpan.FromSeconds(10));
        await flush.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(rig.Transport.Requests.Last().ClientId, Is.EqualTo(latest.ClientRequestId));
        run.Cancel();
        await running;
    }

    [Test]
    public async Task SameNameOnDifferentEntitiesHasIndependentMailboxes()
    {
        await using var rig = new ReportingRig();
        await rig.Initialize(ReportingRig.Snapshot());
        await rig.Publisher!.EnqueueAsync(rig.Report(), default);
        await rig.Publisher.EnqueueAsync(rig.Builder.Build(new(
            new(rig.Models[0].Configuration.DataPlaneUri, "entity-two", "signal-000"),
            Core.Configuration.HealthState.Healthy, 7, null, 1, null)), default);
        await rig.Publisher.FlushAsync(default);
        Assert.That(rig.Transport.Requests.Select(r => r.Uri.AbsolutePath), Is.EquivalentTo(
            new[] { "/api/entities/entity-one/healthReports", "/api/entities/entity-two/healthReports" }));
        Assert.That(rig.Metrics.Count("evaluator.reports.superseded"), Is.Zero);
    }

    [Test]
    public async Task TransportTimeoutIsRetriedWithoutBeingTreatedAsReportCancellation()
    {
        await using var rig = new ReportingRig();
        await rig.Initialize(ReportingRig.Snapshot());
        rig.Transport.Send = async (_, ct) =>
        {
            if (rig.Transport.Requests.Count == 1) await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return new(HttpStatusCode.Accepted);
        };
        await rig.Publisher!.EnqueueAsync(rig.Report(ttl: 2), default);
        var flush = rig.Publisher.FlushAsync(default);
        await PollingTestSupport.Until(() => rig.Transport.Requests.Count == 1);
        rig.Time.Advance(TimeSpan.FromSeconds(30));
        await PollingTestSupport.Until(() => rig.Time.TimerDueTimes.Any(d => d > TimeSpan.Zero && d <= TimeSpan.FromSeconds(1)));
        rig.Time.Advance(TimeSpan.FromSeconds(1));
        await flush.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(rig.Transport.Requests.Count, Is.EqualTo(2));
        Assert.That(rig.Metrics.Measurements.Any(m => m.Name == "evaluator.publishes" &&
            Equals(m.Tags.GetValueOrDefault("status"), "timeout")), Is.True);
        Assert.That(rig.Metrics.Count("evaluator.reports.dropped"), Is.Zero);
    }

    [Test]
    public async Task RepeatedMissingHeaderBackoffRemainsJitteredAndCappedAtThirtySeconds()
    {
        await using var rig = new ReportingRig();
        await rig.Initialize(ReportingRig.Snapshot());
        rig.Transport.Send = (_, _) => Task.FromResult(new HttpResponseMessage(
            rig.Transport.Requests.Count <= 7 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.Accepted));
        await rig.Publisher!.EnqueueAsync(rig.Report(ttl: 10), default);
        var flush = rig.Publisher.FlushAsync(default);
        for (var attempt = 1; attempt <= 7; attempt++)
        {
            await PollingTestSupport.Until(() => rig.Time.TimerDueTimes.Count(d => d > TimeSpan.Zero && d < TimeSpan.FromSeconds(30)) >= attempt);
            rig.Time.Advance(TimeSpan.FromSeconds(30));
        }
        await flush.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(rig.Transport.Requests.Count, Is.EqualTo(8));
        Assert.That(rig.Transport.Requests.Select(r => Convert.ToHexString(r.Body)).Distinct().Count(), Is.EqualTo(1));
        Assert.That(rig.Log.Entries.Any(e => e.Text.Contains("malformed", StringComparison.OrdinalIgnoreCase)), Is.False);
    }

    [Test]
    public async Task ExpiryCountsPendingAndUncooperativeActiveReportsWithoutReleasingTheirSend()
    {
        await using var rig = new ReportingRig();
        await rig.Initialize(ReportingRig.Snapshot());
        var finish = PollingTestSupport.Gate();
        rig.Transport.Send = async (_, _) => { await finish.Task; return new(HttpStatusCode.Accepted); };
        using var run = new CancellationTokenSource();
        var running = rig.Publisher!.RunAsync(run.Token);
        await rig.Publisher.EnqueueAsync(rig.Report(value: 1), default);
        await PollingTestSupport.Until(() => rig.Transport.Requests.Count == 1);
        rig.Time.Advance(TimeSpan.FromMinutes(1));
        Assert.That(rig.Metrics.Count("evaluator.reports.dropped", "expired"), Is.EqualTo(1));
        await rig.Publisher.EnqueueAsync(rig.Report(value: 2), default);
        rig.Time.Advance(TimeSpan.FromMinutes(1));
        Assert.That(rig.Metrics.Count("evaluator.reports.dropped", "expired"), Is.EqualTo(2));
        Assert.That(rig.Transport.Requests.Count, Is.EqualTo(1));
        finish.SetResult();
        await rig.Publisher.FlushAsync(default).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(rig.Transport.Requests.Count, Is.EqualTo(1));
        Assert.That(rig.Metrics.Count("evaluator.reports.superseded"), Is.Zero);
        run.Cancel();
        await running;
    }

    [Test]
    public async Task PublishSpansIncludeSafeCorrelationIdsWithoutBodiesOrSignalTags()
    {
        await using var rig = new ReportingRig();
        await rig.Initialize(ReportingRig.Snapshot());
        rig.Redactor.RegisterSecret("secret-request-id");
        rig.Transport.Send = (_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Accepted);
            response.Headers.TryAddWithoutValidation("x-ms-request-id", "secret-request-id");
            return Task.FromResult(response);
        };
        var activities = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "SelfHostedEvaluators.Core.Reporting",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity => activities.Add(activity)
        };
        ActivitySource.AddActivityListener(listener);
        var report = rig.Report();
        await rig.Publisher!.EnqueueAsync(report, default);
        await rig.Publisher.FlushAsync(default);
        var span = activities.Single(a => a.OperationName == "report.publish");
        Assert.That(span.GetTagItem("client.request.id"), Is.EqualTo(report.ClientRequestId));
        Assert.That(span.GetTagItem("service.request.id"), Is.EqualTo("[REDACTED]"));
        Assert.That(span.TagObjects.Any(t => t.Key.Contains("signal", StringComparison.Ordinal)), Is.False);
    }

    private sealed class UnreadableResponseBody : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            throw new HttpRequestException("sensitive-unneeded-response-body");
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }

    private sealed class DelayedAuth(bool configure) : IAuthScheme, IAuthContext
    {
        public TaskCompletionSource Entered { get; } = PollingTestSupport.Gate();
        public TaskCompletionSource Finish { get; } = PollingTestSupport.Gate();
        public string Scheme => "delayed";
        public ValueTask<IAuthContext> CreateContextAsync(AuthSchemeContext input, CancellationToken ct) => ValueTask.FromResult<IAuthContext>(this);
        public ValueTask AuthenticateAsync(HttpRequestMessage request, CancellationToken ct) => configure ? ValueTask.CompletedTask : Wait();
        public ValueTask ConfigureHttpHandlerAsync(Uri destination, HttpClientHandler handler, CancellationToken ct) => configure ? Wait() : ValueTask.CompletedTask;
        private async ValueTask Wait() { Entered.TrySetResult(); await Finish.Task; }
        public ValueTask AuthenticateWebSocketAsync(Uri destination, System.Net.WebSockets.ClientWebSocketOptions options, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
