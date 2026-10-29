using System.Diagnostics;
using Azure.Core;
using Azure.Monitor.OpenTelemetry.AspNetCore;
using Azure.Monitor.OpenTelemetry.Exporter;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Configuration;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using OpenTelemetry.Instrumentation.AspNetCore;
using OpenTelemetry.Instrumentation.Http;
using SelfHostedEvaluators.Core.Auth;
using SelfHostedEvaluators.Core.Configuration;
using SelfHostedEvaluators.Core.Diagnostics;

namespace SelfHostedEvaluators.Host;

internal sealed class SafeLoggerFactory(ILoggerFactory inner, Redactor redactor) : ILoggerFactory
{
    public ILogger CreateLogger(string categoryName) => redactor.Wrap(inner.CreateLogger(redactor.Redact(categoryName)));
    public void AddProvider(ILoggerProvider provider) => inner.AddProvider(provider);
    public void Dispose() => inner.Dispose();
}

/// <summary>Redacts mutable span fields and suppresses export when immutable fields still contain sensitive data.</summary>
/// <param name="redactor">The shared redactor used to detect and sanitize sensitive values.</param>
/// <remarks>Unsafe event/link text cannot be rewritten; suppression preserves the original trace identity.</remarks>
public sealed class TraceRedactionProcessor(Redactor redactor) : BaseProcessor<Activity>
{
    /// <summary>Sanitizes the completed activity and clears its recorded flag if unsafe immutable fields remain.</summary>
    /// <param name="activity">The activity about to reach subsequent export processors.</param>
    public override void OnEnd(Activity activity)
    {
        redactor.RedactActivity(activity);
        bool UnsafeText(string text) => redactor.Redact(text) != text;
        bool UnsafeTags(IEnumerable<KeyValuePair<string, object?>> tags) => tags.Any(tag =>
            UnsafeText(tag.Key) || !Equivalent(tag.Value, redactor.RedactAttribute(tag.Key, tag.Value)));
        if (UnsafeText(activity.OperationName) || UnsafeText(activity.Source.Name) ||
            activity.Source.Version is { } version && UnsafeText(version) ||
            UnsafeTags(activity.Source.Tags ?? []) ||
            activity.TagObjects.Any(t => UnsafeText(t.Key)) ||
            activity.Baggage.Any(b => UnsafeText(b.Key)) ||
            activity.Events.Any(e => e.Name == "exception" || UnsafeText(e.Name) || UnsafeTags(e.Tags)) ||
            activity.Links.Any(l => UnsafeTags(l.Tags ?? []) ||
                l.Context.TraceState is { } traceState && UnsafeText(traceState)))
            activity.ActivityTraceFlags &= ~ActivityTraceFlags.Recorded;
    }

    private static bool Equivalent(object? left, object? right) =>
        left is string[] a && right is string[] b ? a.SequenceEqual(b) : Equals(left, right);
}

internal sealed class TelemetrySession : IDisposable
{
    private readonly ServiceProvider services;
    private readonly TracerProvider traces;
    private readonly MeterProvider metrics;
    public ILogger Logger { get; }

    public TelemetrySession(Core.Configuration.HostOptions options, HostDependencies dependencies, Redactor redactor)
    {
        var collection = new ServiceCollection();
        collection.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["OTEL_DOTNET_EXPERIMENTAL_ASPNETCORE_DISABLE_URL_QUERY_REDACTION"] = "false",
            ["OTEL_DOTNET_EXPERIMENTAL_HTTPCLIENT_DISABLE_URL_QUERY_REDACTION"] = "false"
        }).Build());
        collection.Configure<AspNetCoreTraceInstrumentationOptions>(instrumentation => instrumentation.RecordException = false);
        collection.Configure<HttpClientTraceInstrumentationOptions>(instrumentation => instrumentation.RecordException = false);
        collection.AddLogging(logging =>
        {
            logging.SetMinimumLevel(Enum.Parse<LogLevel>(options.LogLevel));
            logging.AddJsonConsole();
            logging.AddOpenTelemetry(logs => logs.IncludeFormattedMessage = true);
            if (dependencies.LoggerProvider is { } provider) logging.AddProvider(new BorrowedLoggerProvider(provider));
        });
        collection.AddSingleton<ILoggerFactory>(sp =>
            new SafeLoggerFactory(ActivatorUtilities.CreateInstance<LoggerFactory>(sp), redactor));
        var telemetry = collection.AddOpenTelemetry()
            .WithTracing(tracing => tracing
                .AddSource("SelfHostedEvaluators.*")
                .AddProcessor(new TraceRedactionProcessor(redactor)))
            .WithMetrics(meter => meter.AddMeter(RuntimeDiagnostics.MeterName)
                .AddView(instrument => instrument.Meter.Name == RuntimeDiagnostics.MeterName ? null : MetricStreamConfiguration.Drop));

        if (dependencies.Exporters is { } exporters)
        {
            exporters(telemetry);
        }
        else if (options.ApplicationInsightsConnectionString is { } connection)
        {
            if (options.AzureClientId is null) throw new ConfigurationException([new("AZURE_CLIENT_ID", "$")]);
            // Statsbeat has a separate, unauthenticated exporter outside our redaction boundary.
            Environment.SetEnvironmentVariable("APPLICATIONINSIGHTS_STATSBEAT_DISABLED", "true");
            redactor.RegisterSecret(connection);
            telemetry.UseAzureMonitor(azure =>
            {
                azure.ConnectionString = connection;
                azure.Credential = new RedactingTelemetryCredential(dependencies.TelemetryCredential ??
                    new AzureSdkCredentialSource().CreateManagedIdentity(options.AzureClientId), redactor);
                azure.EnableLiveMetrics = false;
                azure.DisableOfflineStorage = true;
                azure.EnableStandardMetrics = false;
                azure.EnablePerformanceCounters = false;
                azure.TracesPerSecond = null;
                azure.SamplingRatio = 1.0f;
            });
            collection.PostConfigureAll<AzureMonitorExporterOptions>(azure =>
            {
                azure.DisableOfflineStorage = true;
                if (dependencies.AzureMonitorTransport is { } transport) azure.Transport = transport;
            });
        }
        else
        {
            telemetry.WithTracing(tracing => tracing.AddConsoleExporter())
                .WithMetrics(meter => meter.AddConsoleExporter());
            collection.AddLogging(logging => logging.AddOpenTelemetry(logs => logs.AddConsoleExporter()));
        }
        // Do not run automatic cloud/VM metadata detectors or ingest OTEL_RESOURCE_ATTRIBUTES.
        telemetry.ConfigureResource(resource => resource.Clear().AddService("SelfHostedEvaluators", serviceInstanceId: "worker"));
        services = collection.BuildServiceProvider();
        try
        {
            traces = services.GetRequiredService<TracerProvider>();
            metrics = services.GetRequiredService<MeterProvider>();
            Logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("SelfHostedEvaluators.Host");
        }
        catch
        {
            services.Dispose();
            throw;
        }
    }

    public async Task StartAsync(CancellationToken ct)
    {
        // The distro registers its trace/log exporters in an IHostedService, not
        // just in provider constructors. Activate those services before any work.
        foreach (var service in services.GetServices<IHostedService>())
            await service.StartAsync(ct).ConfigureAwait(false);
    }

    public async Task StopAsync(CancellationToken ct)
    {
        foreach (var service in services.GetServices<IHostedService>().Reverse())
        {
            try { await service.StopAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        }
    }

    public void Flush(int milliseconds)
    {
        if (milliseconds <= 0) return;
        var started = Stopwatch.GetTimestamp();
        traces.ForceFlush(milliseconds);
        var remaining = milliseconds - (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        if (remaining > 0) metrics.ForceFlush(remaining);
        if (services.GetService<LoggerProvider>() is { } logs)
        {
            remaining = milliseconds - (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            if (remaining > 0) logs.ForceFlush(remaining);
        }
    }

    public void Dispose() => services.Dispose();

    private sealed class RedactingTelemetryCredential(TokenCredential inner, Redactor redactor) : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext context, CancellationToken ct)
        {
            var token = inner.GetToken(context, ct);
            redactor.RegisterSecret(token.Token);
            return token;
        }

        public override async ValueTask<AccessToken> GetTokenAsync(TokenRequestContext context, CancellationToken ct)
        {
            var token = await inner.GetTokenAsync(context, ct).ConfigureAwait(false);
            redactor.RegisterSecret(token.Token);
            return token;
        }
    }

    private sealed class BorrowedLoggerProvider(ILoggerProvider inner) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => inner.CreateLogger(categoryName);
        public void Dispose() { }
    }
}
