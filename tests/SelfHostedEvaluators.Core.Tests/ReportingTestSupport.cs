using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Net;
using System.Text.Json.Nodes;
using Azure.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using SelfHostedEvaluators.Core.Auth;
using SelfHostedEvaluators.Core.Configuration;
using SelfHostedEvaluators.Core.Diagnostics;
using SelfHostedEvaluators.Core.Evaluation;
using SelfHostedEvaluators.Core.Reporting;
using SelfHostedEvaluators.Plugins.Abstractions;

namespace SelfHostedEvaluators.Core.Tests;

internal sealed class ReportingRig : IAsyncDisposable
{
    public ReportingTimeProvider Time { get; } = new();
    public Redactor Redactor { get; } = new();
    public RecordingTransport Transport { get; } = new();
    public RecordingLog Log { get; } = new();
    public RuntimeDiagnostics Diagnostics { get; } = new();
    public RecordingMetrics Metrics { get; } = new();
    public RecordingAzure Azure { get; } = new();
    public List<ModelClient> Models { get; } = [];
    public ReportBuilder Builder => new(Time, Redactor);
    public Publisher? Publisher { get; private set; }

    public static ConfigSnapshot Snapshot(int signals = 1, bool secondModel = false, int budget = 5000,
        string apiVersion = "2026-06-01-preview")
    {
        var snapshot = PollingTestSupport.Snapshot(signals, secondModel: secondModel, interval: "1d");
        var env = EnvironmentConfigTests.Environment();
        env.Remove("SHE_MODEL_ONE");
        for (var i = 0; i < snapshot.Models.Length; i++)
        {
            var node = JsonNode.Parse(EnvironmentConfigTests.Model)!;
            node["name"] = snapshot.Models[i].Name;
            node["healthModel"]!["dataPlaneUri"] = snapshot.Models[i].HealthModel.DataPlaneUri.ToString();
            node["healthModel"]!["reportsPerMinute"] = budget;
            node["healthModel"]!["apiVersion"] = apiVersion;
            node["healthModel"]!["tokenScope"] = $"https://scope-{i}.test/.default";
            node["signals"] = new JsonArray(snapshot.Models[i].Signals.Select(s =>
            {
                var signal = JsonNode.Parse(EnvironmentConfigTests.Model)!["signals"]![0]!.DeepClone();
                signal["entity"] = s.Entity;
                signal["name"] = s.Name;
                signal["interval"] = "1d";
                return signal;
            }).ToArray());
            env[$"SHE_MODEL_M{i}"] = node.ToJsonString();
        }
        return new EnvironmentConfigSource().Load(env);
    }

    internal sealed class ReportingTimeProvider() : FakeTimeProvider(PollingTestSupport.Start)
    {
        public ConcurrentQueue<TimeSpan> TimerDueTimes { get; } = new();
        public ConcurrentQueue<Action> DeferredCallbacks { get; } = new();
        public bool DeferNextTimer { get; set; }
        public TimeSpan? AdvanceAfterNextUtcRead { get; set; }
        public override DateTimeOffset GetUtcNow()
        {
            var now = base.GetUtcNow();
            if (AdvanceAfterNextUtcRead is { } advance)
            {
                AdvanceAfterNextUtcRead = null;
                Advance(advance);
            }
            return now;
        }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var defer = DeferNextTimer;
            DeferNextTimer = false;
            var timer = base.CreateTimer(s =>
            {
                if (defer) DeferredCallbacks.Enqueue(() => callback(s));
                else callback(s);
            }, state, dueTime, period);
            TimerDueTimes.Enqueue(dueTime);
            return timer;
        }
    }

    public async Task Initialize(ConfigSnapshot snapshot, bool authenticated = false, IAuthScheme? authScheme = null)
    {
        var secrets = new SecretResolver(new FakeVault(), TimeSpan.FromMinutes(5), Time, Redactor);
        var credentials = new AzureCredentialFactory(secrets, Azure, Time, Redactor);
        var registry = new AuthRegistry(secrets, credentials, Redactor, Time, Transport, Log);
        if (authScheme is not null) registry.RegisterScheme("delayed", () => authScheme);
        foreach (var model in snapshot.Models)
        {
            var config = authenticated ? model.HealthModel with { Auth = new ManagedIdentityAuthProfile("test-identity") } : model.HealthModel;
            if (authScheme is not null) config = config with { Auth = AuthRegistryTests.Profile("""{"type":"delayed"}""") };
            Models.Add(await ModelClient.CreateAsync(model.Name, config, registry, Time, Redactor, Diagnostics, Log));
        }

        Publisher = new(Models, Time, Redactor, Diagnostics, Log);
    }

    public HealthReport Report(int model = 0, string signal = "signal-000", double value = 5, int ttl = 1) =>
        Builder.Build(new(new(Models[model].Configuration.DataPlaneUri, "entity-one", signal),
            HealthState.Healthy, value, null, ttl, null));

    public async ValueTask DisposeAsync()
    {
        if (Publisher is not null)
        {
            using var stop = new CancellationTokenSource();
            stop.Cancel();
            await Publisher.FlushAsync(stop.Token);
        }
        foreach (var model in Models) model.Dispose();
        Diagnostics.Dispose();
        Metrics.Dispose();
    }
}

internal sealed class ProbeResponseBody(bool oversized = false, bool throws = false) : HttpContent
{
    public int Reads { get; private set; }
    public bool Disposed { get; private set; }
    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
    {
        Reads++;
        if (throws) throw new HttpRequestException("sensitive-response-body");
        await stream.WriteAsync(new byte[oversized ? 65537 : 1]);
    }
    protected override bool TryComputeLength(out long length) { length = 0; return false; }
    protected override void Dispose(bool disposing)
    {
        if (disposing) Disposed = true;
        base.Dispose(disposing);
    }
}

internal sealed record RecordedRequest(HttpMethod Method, Uri Uri, byte[] Body, long? ContentLength,
    bool? Chunked, string? ClientId, string? ReturnClientId, string? Authorization, string? ContentType);

internal sealed class RecordingTransport : IHttpTransportFactory
{
    private int disposals;
    public int Disposals => Volatile.Read(ref disposals);
    public ConcurrentQueue<RecordedRequest> Requests { get; } = new();
    public Func<RecordedRequest, CancellationToken, Task<HttpResponseMessage>> Send { get; set; } =
        (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Accepted));
    public HttpMessageHandler Create(HttpClientHandler settings) => new Handler(this);
    private sealed class Handler(RecordingTransport owner) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var captured = new RecordedRequest(request.Method, request.RequestUri!,
                request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(ct),
                request.Content?.Headers.ContentLength, request.Headers.TransferEncodingChunked,
                request.Headers.TryGetValues("x-ms-client-request-id", out var ids) ? ids.Single() : null,
                request.Headers.TryGetValues("x-ms-return-client-request-id", out var returned) ? returned.Single() : null,
                request.Headers.Authorization?.ToString(), request.Content?.Headers.ContentType?.ToString());
            owner.Requests.Enqueue(captured);
            return await owner.Send(captured, ct);
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) Interlocked.Increment(ref owner.disposals);
            base.Dispose(disposing);
        }
    }
}

internal sealed class RecordingAzure : IAzureCredentialSource
{
    public ConcurrentQueue<string> Scopes { get; } = new();
    public TokenCredential CreateManagedIdentity(string? clientId) => new Credential(this);
    public TokenCredential CreateWorkloadIdentity(string tenantId, string clientId, string tokenFile) => throw new NotSupportedException();
    public TokenCredential CreateClientSecret(string tenantId, string clientId, string secret) => throw new NotSupportedException();
    public TokenCredential CreateClientCertificate(string tenantId, string clientId,
        System.Security.Cryptography.X509Certificates.X509Certificate2 certificate) => throw new NotSupportedException();
    private sealed class Credential(RecordingAzure owner) : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext context, CancellationToken ct)
        {
            owner.Scopes.Enqueue(string.Join(" ", context.Scopes));
            return new("offline-token", DateTimeOffset.MaxValue);
        }
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext context, CancellationToken ct) =>
            ValueTask.FromResult(GetToken(context, ct));
    }
}

internal sealed class RecordingLog : ILogger
{
    public ConcurrentQueue<(LogLevel Level, string Text)> Entries { get; } = new();
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter) => Entries.Enqueue((logLevel, formatter(state, exception)));
}

internal sealed class RecordingMetrics : IDisposable
{
    private readonly MeterListener listener = new();
    public ConcurrentQueue<(string Name, double Value, Dictionary<string, object?> Tags)> Measurements { get; } = new();
    public RecordingMetrics()
    {
        listener.InstrumentPublished = (instrument, target) =>
        {
            if (instrument.Meter.Name == RuntimeDiagnostics.MeterName) target.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((i, v, t, _) => Measurements.Enqueue((i.Name, v, t.ToArray().ToDictionary())));
        listener.SetMeasurementEventCallback<double>((i, v, t, _) => Measurements.Enqueue((i.Name, v, t.ToArray().ToDictionary())));
        listener.Start();
    }
    public double Count(string name, string? outcome = null) => Measurements
        .Where(m => m.Name == name && (outcome is null || Equals(m.Tags.GetValueOrDefault("outcome"), outcome)))
        .Sum(m => m.Value);
    public void Dispose() => listener.Dispose();
}
