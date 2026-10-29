using System.Collections.Concurrent;
using System.Security.Cryptography.X509Certificates;

namespace SelfHostedEvaluators.ContainerFixtures;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args is [var command] && command is "snapshot" or "reset" or "ready")
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            using var response = command == "reset"
                ? await client.PostAsync("http://127.0.0.1:8081/fixture/reset", null)
                : await client.GetAsync("http://127.0.0.1:8081/fixture/" + command);
            response.EnsureSuccessStatusCode();
            Console.WriteLine(await response.Content.ReadAsStringAsync());
            return 0;
        }
        if (args is not ["serve", var certificate]) return 2;
        using var cert = X509CertificateLoader.LoadPkcs12FromFile(certificate, null);
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.ListenAnyIP(8081);
            options.ListenAnyIP(8443, listener => listener.UseHttps(cert));
            options.ListenAnyIP(8444, listener => listener.UseHttps(cert));
        });
        await using var server = builder.Build();
        var reports = new ConcurrentQueue<Report>();
        var attempts = new ConcurrentDictionary<string, int>();
        var streamsStarted = 0;
        var streamsAborted = 0;
        var identities = 0;
        var remoteChecks = 0;
        var unexpected = 0;
        server.Run(async context =>
        {
            var path = context.Request.Path.Value ?? "";
            if (path == "/fixture/ready") { await context.Response.WriteAsync("ready"); return; }
            if (path == "/fixture/reset" && context.Request.Method == "POST")
            {
                reports.Clear();
                attempts.Clear();
                identities = 0;
                remoteChecks = 0;
                unexpected = 0;
                await context.Response.WriteAsync("reset");
                return;
            }
            if (path == "/fixture/snapshot")
            {
                await context.Response.WriteAsJsonAsync(new { reports = reports.ToArray(), streamsStarted, streamsAborted, identities, remoteChecks, unexpected });
                return;
            }
            if (path == "/identity" && context.Request.Method == "GET" &&
                context.Request.Headers["X-IDENTITY-HEADER"] == "container-fixture-only")
            {
                Interlocked.Increment(ref identities);
                await context.Response.WriteAsJsonAsync(new
                {
                    access_token = "container-fixture-token", token_type = "Bearer",
                    expires_on = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds().ToString(),
                    resource = context.Request.Query["resource"].ToString()
                });
                return;
            }
            if (path.StartsWith("/api/v1/monitor/", StringComparison.Ordinal))
            {
                await context.Response.WriteAsJsonAsync(new
                {
                    id = int.Parse(path[(path.LastIndexOf('/') + 1)..]),
                    overall_state = path.EndsWith("/1", StringComparison.Ordinal) ? "OK" : "Alert"
                });
                return;
            }
            if (path == "/v2/signalflow/execute" && context.Request.Method == "POST")
            {
                var body = await new StreamReader(context.Request.Body).ReadToEndAsync(context.RequestAborted);
                context.Response.ContentType = "text/event-stream";
                if (body.Contains("partial", StringComparison.Ordinal))
                {
                    Interlocked.Increment(ref streamsStarted);
                    await context.Response.WriteAsync("event: data\ndata: {\"logicalTimestampMs\":", context.RequestAborted);
                    await context.Response.Body.FlushAsync(context.RequestAborted);
                    try { await Task.Delay(Timeout.InfiniteTimeSpan, context.RequestAborted); }
                    catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
                    { Interlocked.Increment(ref streamsAborted); }
                }
                else
                {
                    var stop = long.Parse(context.Request.Query["stop"].ToString());
                    await context.Response.WriteAsync(
                        $"event: data\ndata: {{\"logicalTimestampMs\":{stop},\"data\":[{{\"tsId\":\"cpu\",\"value\":42}}]}}\n\n" +
                        $"event: control-message\ndata: {{\"event\":\"END_OF_CHANNEL\",\"timestampMs\":{stop}}}\n\n");
                }
                return;
            }
            if (path.StartsWith("/api/entities/", StringComparison.Ordinal) && path.EndsWith("/healthReports", StringComparison.Ordinal)
                && context.Request.Method == "POST" && context.Request.Headers.Authorization == "Bearer container-fixture-token")
            {
                var body = await new StreamReader(context.Request.Body).ReadToEndAsync(context.RequestAborted);
                var id = context.Request.Headers["x-ms-client-request-id"].ToString();
                var attempt = attempts.AddOrUpdate(id, 1, (_, count) => count + 1);
                var status = attempt switch { 1 => 429, 2 => 503, _ => 202 };
                reports.Enqueue(new(id, body, status, context.Request.QueryString.Value ?? "",
                    context.Request.ContentLength, context.Request.Headers.TransferEncoding.ToString()));
                context.Response.StatusCode = status;
                if (status is 429 or 503) context.Response.Headers.RetryAfter = "1";
                return;
            }
            if (context.Request.Method == "GET" && (path == "/api" || path.StartsWith("/api/entities/", StringComparison.Ordinal)))
            {
                Interlocked.Increment(ref remoteChecks);
                context.Response.StatusCode = 404;
                return;
            }
            Interlocked.Increment(ref unexpected);
            context.Response.StatusCode = 404;
        });
        await server.RunAsync();
        return 0;
    }

    private sealed record Report(string Id, string Body, int Status, string Query, long? Length, string TransferEncoding);
}
