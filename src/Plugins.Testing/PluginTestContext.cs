using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading.RateLimiting;
using Microsoft.Extensions.Logging.Abstractions;
using SelfHostedEvaluators.Plugins.Abstractions;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using WireMock.Settings;

namespace SelfHostedEvaluators.Plugins.Testing;

/// <summary>Owns loopback HTTPS fake services and preauthenticated clients for real sink-client tests.</summary>
/// <remarks>Certificate trust is pinned to this fixture only; the helper is not a production transport.</remarks>
public sealed class PluginTestContext : IAsyncDisposable
{
    private readonly List<HttpClient> clients = [];
    private readonly RateLimiter limiter = new ConcurrencyLimiter(new() { PermitLimit = 8, QueueLimit = 8 });
    private readonly X509Certificate2 certificate;
    private readonly byte[] certificateHash;
    /// <summary>Gets a completion source signaled when any owned client begins an outbound request.</summary>
    public TaskCompletionSource RequestStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    /// <summary>Gets the owned loopback API fake service.</summary>
    public WireMockServer Api { get; }
    /// <summary>Gets the separately hosted loopback stream fake service.</summary>
    public WireMockServer Stream { get; }

    /// <summary>Creates a fixture certificate, shared limiter and two HTTPS fake services.</summary>
    public PluginTestContext()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("localhost");
        names.AddIpAddress(System.Net.IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1));
        // Importing the private key also works with Windows Schannel.
        certificate = X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pfx), null);
        certificateHash = certificate.GetCertHash(HashAlgorithmName.SHA256);
        try
        {
            var settings = new WireMockServerSettings { UseSSL = true, CertificateSettings = new() { X509Certificate = certificate } };
            Api = WireMockServer.Start(settings);
            try { Stream = WireMockServer.Start(settings); }
            catch { Api.Dispose(); throw; }
        }
        catch { certificate.Dispose(); limiter.Dispose(); throw; }
    }

    /// <summary>Creates and tracks a fixture-pinned HTTP client with test-only vendor credentials.</summary>
    /// <param name="server">The fixture server used as the base address.</param>
    /// <param name="vendor">Datadog for its two key headers, AzureMonitor for a bearer token; otherwise the Splunk token header is used.</param>
    /// <param name="site">The label embedded in deterministic fake credentials.</param>
    /// <returns>A client owned and disposed by this fixture.</returns>
    public HttpClient Http(WireMockServer server, string vendor, string site = "eu")
    {
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (request, cert, _, _) => request.RequestUri!.IsLoopback &&
                cert is not null && CryptographicOperations.FixedTimeEquals(certificateHash, cert.GetCertHash(HashAlgorithmName.SHA256))
        };
        var http = new HttpClient(new StartHandler(RequestStarted) { InnerHandler = handler })
            { BaseAddress = new Uri(server.Url!), Timeout = TimeSpan.FromSeconds(10) };
        if (vendor == "Datadog")
        {
            http.DefaultRequestHeaders.Add("DD-API-KEY", $"{site}-api");
            http.DefaultRequestHeaders.Add("DD-APPLICATION-KEY", $"{site}-app");
        }
        else if (vendor == "AzureMonitor") http.DefaultRequestHeaders.Authorization = new("Bearer", $"{site}-token");
        else http.DefaultRequestHeaders.Add("X-SF-Token", $"{site}-token");
        clients.Add(http);
        return http;
    }

    /// <summary>Builds a plugin context whose auth operations fail if a sink attempts to authenticate HTTP again.</summary>
    /// <param name="endpoints">The named, preauthenticated fixture clients.</param>
    /// <param name="settings">The settings JSON to clone.</param>
    /// <param name="time">An optional test clock.</param>
    /// <returns>A context sharing the fixture limiter and supplied transports.</returns>
    public PluginInstanceContext Context(Dictionary<string, HttpClient> endpoints, string settings = "{}", TimeProvider? time = null) =>
        new("test", SinkClientContractFixture.Json(settings), 8, endpoints, new ForbiddenAuth(), limiter,
            NullLogger.Instance, time ?? TimeProvider.System);

    /// <summary>Changes the timeout of every client created so far.</summary>
    /// <param name="timeout">The timeout to apply before the clients send requests.</param>
    public void SetTimeout(TimeSpan timeout) { foreach (var client in clients) client.Timeout = timeout; }

    /// <summary>Stubs any HTTP method at a path with a controlled body, status and optional delay.</summary>
    /// <param name="server">The server on which to register the response.</param>
    /// <param name="path">The request path to match.</param>
    /// <param name="body">The response payload.</param>
    /// <param name="status">The HTTP status, defaulting to success.</param>
    /// <param name="contentType">The response media type.</param>
    /// <param name="delayMs">An optional response delay in milliseconds.</param>
    /// <remarks>Responses include a seven-second Retry-After header for contract assertions.</remarks>
    public static void Stub(WireMockServer server, string path, string body, int status = 200,
        string contentType = "application/json", int delayMs = 0)
    {
        var response = Response.Create().WithStatusCode(status).WithHeader("Content-Type", contentType)
            .WithHeader("Retry-After", "7").WithBody(body);
        if (delayMs > 0) response.WithDelay(TimeSpan.FromMilliseconds(delayMs));
        server.Given(Request.Create().WithPath(path).UsingAnyMethod()).RespondWith(response);
    }

    /// <summary>Disposes owned clients, fake services, the fixture certificate and the shared limiter.</summary>
    public async ValueTask DisposeAsync()
    {
        foreach (var client in clients) client.Dispose();
        Api.Dispose();
        Stream.Dispose();
        certificate.Dispose();
        await limiter.DisposeAsync();
    }

    private sealed class ForbiddenAuth : IAuthContext
    {
        public string Scheme => "preauthenticated";
        public ValueTask ConfigureHttpHandlerAsync(Uri destination, HttpClientHandler handler, CancellationToken ct) => throw new InvalidOperationException("Sink must not configure auth.");
        public ValueTask AuthenticateAsync(HttpRequestMessage request, CancellationToken ct) => throw new InvalidOperationException("Sink must not authenticate HTTP twice.");
        public ValueTask AuthenticateWebSocketAsync(Uri destination, ClientWebSocketOptions options, CancellationToken ct) => throw new InvalidOperationException("HTTP sinks do not need socket auth.");
        public ValueTask DisposeAsync() => throw new InvalidOperationException("Auth is owned by Core.");
    }

    private sealed class StartHandler(TaskCompletionSource started) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            started.TrySetResult();
            return base.SendAsync(request, ct);
        }
    }
}
