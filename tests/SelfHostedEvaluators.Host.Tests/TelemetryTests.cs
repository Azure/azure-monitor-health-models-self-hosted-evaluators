using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.IO.Compression;
using Azure.Core.Pipeline;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Trace;
using SelfHostedEvaluators.Core.Auth;
using SelfHostedEvaluators.Core.Diagnostics;
using SelfHostedEvaluators.Core.Configuration;
using SelfHostedEvaluators.Core.Reporting;

namespace SelfHostedEvaluators.Host.Tests;

[NonParallelizable]
public class TelemetryTests
{
    [Test]
    public void SharedMeterContainsEveryCanonicalInstrumentWithBoundedTags()
    {
        using var diagnostics = new RuntimeDiagnostics();
        var names = new HashSet<string>();
        var tags = new HashSet<string>();
        long active = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (i, l) =>
        {
            if (!ReferenceEquals(i.Meter, diagnostics.Meter)) return;
            names.Add(i.Name);
            l.EnableMeasurementEvents(i);
        };
        listener.SetMeasurementEventCallback<long>((i, value, attrs, _) =>
        {
            foreach (var attr in attrs) tags.Add(attr.Key);
            if (i.Name == "evaluator.signals.active") active = value;
        });
        listener.Start();
        diagnostics.HostStarted();
        diagnostics.ConfigLoaded(true);
        diagnostics.PackageLoaded("static", true);
        diagnostics.SetActiveSignals(7);
        listener.RecordObservableInstruments();
        Assert.Multiple(() =>
        {
            Assert.That(names, Is.SupersetOf(new[]
            {
                "evaluator.polls", "evaluator.poll.duration", "evaluator.evaluations", "evaluator.publishes",
                "evaluator.reports.superseded", "evaluator.reports.dropped", "evaluator.ratelimit.wait",
                "evaluator.schedule.lag", "evaluator.ticks.skipped", "evaluator.config.loads",
                "evaluator.package.loads", "evaluator.checks.warnings", "evaluator.signals.active", "evaluator.host.starts"
            }));
            Assert.That(tags, Is.SubsetOf(new[] { "model", "plugin", "package", "outcome", "state", "status" }));
            Assert.That(active, Is.EqualTo(7));
        });
    }

    [Test]
    public async Task SinkPermitWaitUsesSharedCanonicalInstrumentWithoutSignalDimensions()
    {
        using var diagnostics = new RuntimeDiagnostics();
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        var waits = new List<double>();
        var dimensions = new HashSet<string>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (i, l) =>
        {
            if (ReferenceEquals(i.Meter, diagnostics.Meter) && i.Name == "evaluator.ratelimit.wait")
                l.EnableMeasurementEvents(i);
        };
        listener.SetMeasurementEventCallback<double>((_, value, tags, _) =>
        {
            waits.Add(value);
            foreach (var tag in tags) dimensions.Add(tag.Key);
        });
        listener.Start();
        using var limiter = new PluginRequestLimiter(new(1, TimeSpan.FromSeconds(1)), time, diagnostics, "static-one");
        using var first = await limiter.AcquireAsync(1);
        var pending = limiter.AcquireAsync(1).AsTask();
        time.Advance(TimeSpan.FromSeconds(1));
        using var second = await pending;
        Assert.Multiple(() =>
        {
            Assert.That(waits, Is.EqualTo(new[] { 0d, 1000d }));
            Assert.That(dimensions, Is.EqualTo(new[] { "plugin" }));
        });
    }

    [Test]
    public void ExportBoundaryRedactsMutableDataAndRejectsUnsafeImmutableEventsAndLinks()
    {
        var redactor = new Redactor();
        redactor.RegisterSecret("private-value");
        using var exporter = new RecordingTraces();
        using var provider = Sdk.CreateTracerProviderBuilder().AddSource("SelfHostedEvaluators.Tests*")
            .AddProcessor(new TraceRedactionProcessor(redactor))
            .AddProcessor(new SimpleActivityExportProcessor(exporter)).Build();
        using var source = new ActivitySource("SelfHostedEvaluators.Tests");
        using (var span = source.StartActivity("safe-span")!)
        {
            span.DisplayName = "private-value https://sink.test?secret=private-query";
            span.SetTag("http.request.header.authorization", "Bearer unregistered-token");
            span.SetTag("url.query", "key=unregistered-query");
            span.SetTag("QueryString", "key=unregistered-camel-query");
            span.SetTag("url.full", "https://sink.test/path?key=private-query");
            span.SetTag("detail", "private-value");
            span.SetTag("request.detail", "GET /items?filter=unregistered-relative-query");
            span.AddBaggage("http.target", "/?key=private-query");
            span.AddEvent(new("safe-event", tags: new ActivityTagsCollection { ["answer"] = 42 }));
            span.SetStatus(ActivityStatusCode.Error, "unregistered-error-description");
        }
        using (var span = source.StartActivity("unsafe-event")!)
            span.AddEvent(new("private-value", tags: new ActivityTagsCollection { ["url.query"] = "private-query" }));
        using (var span = source.StartActivity("unsafe-link", ActivityKind.Internal, default(ActivityContext),
            links: [new ActivityLink(new(ActivityTraceId.CreateRandom(), ActivitySpanId.CreateRandom(), ActivityTraceFlags.Recorded),
                new ActivityTagsCollection { ["http.request.header.authorization"] = "private-link-token" })])!) { }
        using (var span = source.StartActivity("unsafe-exception")!)
            span.AddEvent(new("exception", tags: new ActivityTagsCollection { ["exception.message"] = "unregistered-private-message" }));
        using (var span = source.StartActivity("unsafe-link-tracestate", ActivityKind.Internal, default(ActivityContext),
            links: [new ActivityLink(new(ActivityTraceId.CreateRandom(), ActivitySpanId.CreateRandom(),
                ActivityTraceFlags.Recorded, "key=private-value"))])!) { }
        using (var sensitiveSource = new ActivitySource("SelfHostedEvaluators.Tests.private-value"))
        using (var span = sensitiveSource.StartActivity("unsafe-source")!) { }
        Assert.That(exporter.Activities.Select(a => a.OperationName), Is.EqualTo(new[] { "safe-span" }));
        var exported = exporter.Activities.Single();
        var all = exported.DisplayName + exported.StatusDescription + string.Join(" ", exported.TagObjects.Select(t => t.Key + "=" + t.Value)) +
            string.Join(" ", exported.Baggage);
        Assert.That(all, Does.Not.Contain("private-value").And.Not.Contain("private-query")
            .And.Not.Contain("unregistered-query").And.Not.Contain("unregistered-token")
            .And.Not.Contain("unregistered-relative-query")
            .And.Not.Contain("unregistered-camel-query").And.Not.Contain("unregistered-error-description"));
    }

    [Test]
    public async Task InvalidSnapshotDoesNotAuthenticateOrExportEvenWithAzureTelemetryConfigured()
    {
        using var fixture = new HostFixture();
        using var offline = new OfflineRuntime();
        fixture.Environment["AZURE_CLIENT_ID"] = "11111111-1111-1111-1111-111111111111";
        fixture.Environment["APPLICATIONINSIGHTS_CONNECTION_STRING"] =
            "InstrumentationKey=22222222-2222-2222-2222-222222222222;IngestionEndpoint=https://monitor.test/";
        fixture.Environment["SHE_PLUGIN_STATIC"] = "{invalid-private-json";
        using var handler = new MonitorTransport();
        using var http = new HttpClient(handler);
        var code = await new HostRunner(new HostDependencies
        {
            ReadEnvironment = () => fixture.Environment,
            Diagnostics = offline.Diagnostics,
            TelemetryCredential = offline.Credentials.CreateManagedIdentity(fixture.Environment["AZURE_CLIENT_ID"]),
            AzureMonitorTransport = new HttpClientTransport(http)
        }).RunAsync([], CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(code, Is.Not.Zero);
            Assert.That(offline.Credentials.Tokens, Is.Zero);
            Assert.That(handler.Bodies, Is.Empty);
        });
    }

    [Test]
    public async Task FailedStartupStillEmitsStructuredStartAndRejectedLoadMetrics()
    {
        using var fixture = new HostFixture();
        using var offline = new OfflineRuntime();
        fixture.Environment["SHE_PLUGIN_STATIC"] = "{malformed-private";
        long starts = 0, rejects = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (i, l) => { if (ReferenceEquals(i.Meter, offline.Diagnostics.Meter)) l.EnableMeasurementEvents(i); };
        listener.SetMeasurementEventCallback<long>((i, value, tags, _) =>
        {
            if (i.Name == "evaluator.host.starts") Interlocked.Add(ref starts, value);
            if (i.Name == "evaluator.config.loads" && tags.ToArray().Any(t => Equals(t.Value, "failure")))
                Interlocked.Add(ref rejects, value);
        });
        listener.Start();
        var code = await new HostRunner(offline.Dependencies(fixture)).RunAsync([], CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(code, Is.Not.Zero);
            Assert.That(starts, Is.EqualTo(1));
            Assert.That(rejects, Is.EqualTo(1));
            Assert.That(offline.Logs.Entries.Count(e => e.Level == LogLevel.Information &&
                e.Attributes.Any(a => a.Key == "Event" && Equals(a.Value, "evaluator.host.starts"))), Is.EqualTo(1));
        });
    }

    [Test]
    public async Task LoadedPackagesHaveDefaultLevelAuditEvenWhenLaterConfigurationIsRejected()
    {
        using var fixture = new HostFixture();
        using var offline = new OfflineRuntime();
        fixture.Environment["SHE_MODEL_ONE"] = "{}";
        var code = await new HostRunner(offline.Dependencies(fixture)).RunAsync([], CancellationToken.None);
        Assert.That(code, Is.Not.Zero);
        var audit = offline.Logs.Entries.Single(e => e.Level == LogLevel.Information &&
            e.Attributes.Any(a => a.Key == "Package" && Equals(a.Value, "static")));
        Assert.That(audit.Attributes.Single(a => a.Key == "Version").Value, Is.EqualTo("1.0.0"));
        Assert.That(audit.Attributes.Single(a => a.Key == "Sha256").Value, Does.Match("^[A-F0-9]{64}$"));
        Assert.That(audit.Message, Does.Not.Contain(fixture.Root).And.Not.Contain("SHE_MODEL_ONE"));
        Assert.That(offline.Transport.Requests, Is.Empty);
    }

    [Test]
    public async Task TickBatchPublishSpansAndStateChangeLogsAreComposed()
    {
        using var fixture = new HostFixture();
        using var offline = new OfflineRuntime();
        using var stop = new CancellationTokenSource();
        var run = new HostRunner(offline.Dependencies(fixture)).RunAsync([], stop.Token);
        try
        {
            await OfflineRuntime.Until(() => offline.Traces.Activities.Any(a => a.OperationName == "report.publish"));
        }
        finally { stop.Cancel(); await run.WaitAsync(TimeSpan.FromSeconds(5)); }
        var tick = offline.Traces.Activities.First(a => a.OperationName == "group.tick");
        var batch = offline.Traces.Activities.First(a => a.OperationName == "poll.batch");
        Assert.Multiple(() =>
        {
            var packageAudit = offline.Logs.Entries.Where(e => e.Level == LogLevel.Information &&
                e.Attributes.Any(a => a.Key == "Package")).ToArray();
            Assert.That(packageAudit, Has.Length.EqualTo(1), "Every loaded package is auditable at the default log level.");
            Assert.That(packageAudit[0].Attributes, Does.Contain(new KeyValuePair<string, object?>("Package", "static")));
            Assert.That(packageAudit[0].Attributes, Does.Contain(new KeyValuePair<string, object?>("Version", "1.0.0")));
            Assert.That(packageAudit[0].Attributes.Single(a => a.Key == "Sha256").Value,
                Does.Match("^[A-F0-9]{64}$"));
            Assert.That(packageAudit[0].Message, Does.Not.Contain(fixture.Root).And.Not.Contain("test-private-token"));
            Assert.That(string.Join(" ", offline.ExportedLogs.Text), Does.Contain("Package load static 1.0.0"));
            Assert.That(batch.ParentSpanId, Is.EqualTo(tick.SpanId));
            Assert.That(offline.Traces.Activities.First(a => a.OperationName == "report.publish").ParentSpanId, Is.EqualTo(batch.SpanId));
            Assert.That(tick.Duration, Is.GreaterThanOrEqualTo(batch.Duration));
            Assert.That(offline.Logs.Entries.Count(e => e.Level == LogLevel.Information &&
                e.Message.Contains("Signal state changed", StringComparison.Ordinal)), Is.EqualTo(1));
            Assert.That(string.Join(" ", offline.ExportedLogs.Text), Does.Not.Contain("test-private-token"));
        });
    }

    [Test]
    public async Task ReplacedReportPublishRetainsItsOwnBatchTraceNotTheMailboxOriginalTrace()
    {
        using var fixture = new HostFixture();
        using var diagnostics = new RuntimeDiagnostics();
        var config = new EnvironmentConfigSource().Load(fixture.Environment).Models[0];
        var redactor = new Redactor();
        using var exporter = new RecordingTraces();
        using var provider = Sdk.CreateTracerProviderBuilder().AddSource("SelfHostedEvaluators.*")
            .AddProcessor(new TraceRedactionProcessor(redactor))
            .AddProcessor(new SimpleActivityExportProcessor(exporter)).Build();
        using var source = new ActivitySource("SelfHostedEvaluators.Tests");
        using var handler = new ReplacingTransport();
        using var model = new ModelClient(config.Name, config.HealthModel, new HttpClient(handler),
            TimeProvider.System, redactor, diagnostics);
        var publisher = new Publisher([model], TimeProvider.System, redactor, diagnostics);
        var builder = new ReportBuilder(TimeProvider.System, redactor);
        using var stop = new CancellationTokenSource();
        var run = publisher.RunAsync(stop.Token);
        var signal = new Core.Evaluation.EvaluatedSignal(new(config.HealthModel.DataPlaneUri, "entity", "signal"),
            HealthState.Healthy, 1, null, 3, null);
        using (source.StartActivity("first-batch"))
            await publisher.EnqueueAsync(builder.Build(signal), CancellationToken.None);
        await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        ActivityTraceId expected;
        using (var span = source.StartActivity("second-batch")!)
        {
            expected = span.TraceId;
            await publisher.EnqueueAsync(builder.Build(signal with { Value = 2 }), CancellationToken.None);
        }
        try
        {
            await OfflineRuntime.Until(() => exporter.Activities.Count(a => a.OperationName == "report.publish") == 2);
            Assert.That(exporter.Activities.Last(a => a.OperationName == "report.publish").TraceId, Is.EqualTo(expected));
        }
        finally { stop.Cancel(); await run; await publisher.FlushAsync(CancellationToken.None); }
    }

    private sealed class ReplacingTransport : HttpMessageHandler
    {
        private int calls;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                Entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
            return new(HttpStatusCode.Accepted);
        }
    }

    [Test]
    public async Task RealDistroExportsWithExplicitCredentialThroughInjectedTransportAfterRedaction()
    {
        using var fixture = new HostFixture();
        using var offline = new OfflineRuntime();
        var redactor = new Redactor();
        redactor.RegisterSecret("private-value");
        var options = new EnvironmentConfigSource().Load(fixture.Environment).Host with
        {
            AzureClientId = "11111111-1111-1111-1111-111111111111",
            ApplicationInsightsConnectionString =
                "InstrumentationKey=22222222-2222-2222-2222-222222222222;IngestionEndpoint=https://monitor.test/"
        };
        using var handler = new MonitorTransport();
        using var http = new HttpClient(handler);
        using var session = new TelemetrySession(options, new HostDependencies
        {
            TelemetryCredential = offline.Credentials.CreateManagedIdentity(options.AzureClientId),
            AzureMonitorTransport = new HttpClientTransport(http),
            LoggerProvider = offline.Logs
        }, redactor);
        await session.StartAsync(CancellationToken.None);
        using var source = new ActivitySource("SelfHostedEvaluators.DistroTest");
        using (var span = source.StartActivity("distro-export")!)
        {
            Assert.That(span.Recorded, Is.True);
            span.DisplayName = "distro-export private-value https://sink.test/?key=unknown-query";
            span.SetTag("url.query", "key=unknown-separated-query");
            span.SetTag("http.request.header.authorization", "Bearer unknown-header");
        }
        using (var span = source.StartActivity("forbidden-event-span")!)
            span.AddEvent(new("private-value"));
        using (var span = source.StartActivity("forbidden-link-span", ActivityKind.Internal, default(ActivityContext),
            links: [new ActivityLink(new(ActivityTraceId.CreateRandom(), ActivitySpanId.CreateRandom(), ActivityTraceFlags.Recorded),
                new ActivityTagsCollection { ["url.query"] = "private-value" })])!) { }
        session.Logger.LogInformation("Telemetry message {Value} {url.query}", "private-value", "unknown-log-query");
        session.Logger.LogError(new InvalidOperationException("unknown-private-exception"), "Safe failure {Failure}", "fixed-code");
        session.Logger.LogInformation("Ignored query {QueryString}", "unregistered-log-query");
        session.Logger.LogInformation("Ignored error {Detail}", new InvalidOperationException("unregistered-exception-object"));
        session.Flush(5000);
        session.Logger.LogInformation("Credential result {Value}", "test-private-token");
        session.Flush(5000);
        Assert.Multiple(() =>
        {
            Assert.That(offline.Credentials.Tokens, Is.GreaterThan(0), "Real Azure exporter must use the injected TokenCredential.");
            Assert.That(handler.Bodies, Is.Not.Empty);
            Assert.That(string.Join(" ", handler.Bodies), Does.Contain("distro-export").And.Contain("Telemetry message")
                .And.Contain("Safe failure").And.Contain("evaluator.signals.active"));
            Assert.That(string.Join(" ", handler.Bodies), Does.Not.Contain("private-value").And.Not.Contain("unknown-query")
                .And.Not.Contain("unknown-separated-query").And.Not.Contain("unknown-header")
                .And.Not.Contain("unknown-log-query").And.Not.Contain("unknown-private-exception"));
            Assert.That(string.Join(" ", handler.Bodies), Does.Not.Contain("unregistered-log-query").And.Not.Contain("unregistered-exception-object"));
            Assert.That(string.Join(" ", handler.Bodies), Does.Not.Contain("test-private-token"));
            Assert.That(string.Join(" ", handler.Bodies), Does.Not.Contain("forbidden-event-span").And.Not.Contain("forbidden-link-span"));
        });
    }

    private sealed class MonitorTransport : HttpMessageHandler
    {
        public System.Collections.Concurrent.ConcurrentQueue<string> Bodies { get; } = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.That(request.RequestUri!.Host, Is.EqualTo("monitor.test"));
            Assert.That(request.Headers.Authorization?.Scheme, Is.EqualTo("Bearer"));
            if (request.Content is { } content)
            {
                var bytes = await content.ReadAsByteArrayAsync(ct);
                using var stream = new MemoryStream(bytes);
                using Stream decoded = content.Headers.ContentEncoding.Contains("gzip") ? new GZipStream(stream, CompressionMode.Decompress) : stream;
                using var reader = new StreamReader(decoded);
                Bodies.Enqueue(await reader.ReadToEndAsync(ct));
            }
            return new(HttpStatusCode.OK) { Content = new StringContent("") };
        }
    }
}
