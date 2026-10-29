using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using Azure.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Trace;
using SelfHostedEvaluators.Core.Auth;
using SelfHostedEvaluators.Core.Diagnostics;

namespace SelfHostedEvaluators.Host.Tests;

internal sealed class OfflineRuntime : IDisposable
{
    public RuntimeDiagnostics Diagnostics { get; } = new();
    public RecordingTransport Transport { get; } = new();
    public FakeCredentials Credentials { get; } = new();
    public RecordingLogs Logs { get; } = new();
    public RecordingTraces Traces { get; } = new();
    public RecordingLogExporter ExportedLogs { get; } = new();
    public int SecretSources;
    public HostDependencies Dependencies(HostFixture fixture, IKeyVaultSource? source = null) => new()
    {
        ReadEnvironment = () => fixture.Environment,
        Diagnostics = Diagnostics, Transports = Transport, AzureCredentials = Credentials, LoggerProvider = Logs,
        SecretSource = _ => { Interlocked.Increment(ref SecretSources); return source ?? new RejectSecrets(); },
        Exporters = telemetry =>
        {
            telemetry.WithTracing(t => t.AddProcessor(new SimpleActivityExportProcessor(Traces)));
            telemetry.Services.AddLogging(l => l.AddOpenTelemetry(o =>
            {
                o.IncludeFormattedMessage = true;
                o.AddProcessor(new SimpleLogRecordExportProcessor(ExportedLogs));
            }));
        }
    };
    public void Dispose() => Diagnostics.Dispose();

    public static async Task Until(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!predicate()) await Task.Delay(5, timeout.Token);
    }

    public static async Task Ready()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (await Probe("ready", timeout.Token) != 0)
            await Task.Delay(5, timeout.Token);
    }

    /// <summary>Requests a health endpoint over loopback HTTP; returns zero only for HTTP 200.</summary>
    public static async Task<int> Probe(string kind, CancellationToken ct = default)
    {
        using var handler = new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false, UseProxy = false };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(2) };
        try
        {
            using var response = await http.GetAsync($"http://127.0.0.1:8080/healthz/{kind}",
                HttpCompletionOption.ResponseHeadersRead, ct);
            return response.StatusCode == HttpStatusCode.OK ? 0 : 1;
        }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException) { return 1; }
    }

    private sealed class RejectSecrets : IKeyVaultSource
    {
        public ValueTask<string> GetSecretAsync(KeyVaultReference reference, CancellationToken ct) =>
            throw new InvalidOperationException("No test should contact Key Vault.");
    }
}

internal sealed class RecordingTransport : IHttpTransportFactory
{
    public ConcurrentQueue<(string Method, string Host, string Path, string Body, string? Authorization)> Requests { get; } = new();
    public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? Send { get; set; }
    public int Disposals;
    public HttpMessageHandler Create(HttpClientHandler settings)
    {
        Assert.That(settings.AllowAutoRedirect, Is.False);
        return new Handler(this);
    }
    private sealed class Handler(RecordingTransport owner) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            owner.Requests.Enqueue((request.Method.Method, request.RequestUri!.Host, request.RequestUri.AbsolutePath,
                body, request.Headers.Authorization?.ToString()));
            if (owner.Send is { } send) return await send(request, ct);
            return new(request.Method == HttpMethod.Post ? HttpStatusCode.Accepted : HttpStatusCode.OK)
            {
                Content = new StringContent("""{"signals":[]}""")
            };
        }
        protected override void Dispose(bool disposing) { if (disposing) Interlocked.Increment(ref owner.Disposals); base.Dispose(disposing); }
    }
}

internal sealed class FakeCredentials : IAzureCredentialSource
{
    public int Constructions;
    public int Tokens;
    public Func<CancellationToken, ValueTask<AccessToken>>? Acquire { get; set; }
    public TokenCredential CreateManagedIdentity(string? clientId) { Interlocked.Increment(ref Constructions); return new Credential(this); }
    public TokenCredential CreateWorkloadIdentity(string tenantId, string clientId, string tokenFile) => CreateManagedIdentity(clientId);
    public TokenCredential CreateClientSecret(string tenantId, string clientId, string secret) => CreateManagedIdentity(clientId);
    public TokenCredential CreateClientCertificate(string tenantId, string clientId, X509Certificate2 certificate) => CreateManagedIdentity(clientId);
    private sealed class Credential(FakeCredentials owner) : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext context, CancellationToken ct) => GetTokenAsync(context, ct).AsTask().GetAwaiter().GetResult();
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext context, CancellationToken ct)
        {
            Interlocked.Increment(ref owner.Tokens);
            return owner.Acquire?.Invoke(ct) ?? ValueTask.FromResult(new AccessToken("test-private-token", DateTimeOffset.UtcNow.AddHours(1)));
        }
    }
}

internal sealed class RecordingLogs : ILoggerProvider
{
    public ConcurrentQueue<(LogLevel Level, string Message, KeyValuePair<string, object?>[] Attributes)> Entries { get; } = new();
    public ILogger CreateLogger(string categoryName) => new Logger(this);
    public void Dispose() { }
    private sealed class Logger(RecordingLogs owner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> formatter)
        {
            Assert.That(error, Is.Null, "Exception objects must not cross the logging boundary.");
            owner.Entries.Enqueue((level, formatter(state, error), state is IEnumerable<KeyValuePair<string, object?>> attrs ? attrs.ToArray() : []));
        }
    }
}

internal sealed class RecordingTraces : BaseExporter<Activity>
{
    public ConcurrentQueue<Activity> Activities { get; } = new();
    public override ExportResult Export(in Batch<Activity> batch)
    {
        foreach (var activity in batch) Activities.Enqueue(activity);
        return ExportResult.Success;
    }
}

internal sealed class RecordingLogExporter : BaseExporter<LogRecord>
{
    public ConcurrentQueue<string> Text { get; } = new();
    public override ExportResult Export(in Batch<LogRecord> batch)
    {
        foreach (var log in batch)
            Text.Enqueue(log.FormattedMessage + " " + log.Body + " " + log.Exception + " " +
                string.Join(" ", log.Attributes?.Select(p => p.Key + "=" + p.Value) ?? []));
        return ExportResult.Success;
    }
}
