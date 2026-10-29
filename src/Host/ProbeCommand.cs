namespace SelfHostedEvaluators.Host;

/// <summary>Executes a bounded local readiness or liveness request without proxies or redirects.</summary>
/// <param name="port">The loopback health endpoint port.</param>
public sealed class ProbeCommand(int port = 8080)
{
    /// <summary>The connection and overall request timeout used by probe commands.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);

    /// <summary>Checks the selected loopback probe and emits a success message only for HTTP 200.</summary>
    /// <param name="kind">Exactly ready or live.</param>
    /// <param name="ct">Cancels the probe request.</param>
    /// <returns>Zero for a successful probe; one for invalid kinds, non-200 responses or transport cancellation/failure.</returns>
    public async Task<int> ExecuteAsync(string kind, CancellationToken ct)
    {
        if (kind is not ("ready" or "live")) return 1;
        using var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false, UseCookies = false, UseProxy = false, Credentials = null,
            ConnectTimeout = Timeout
        };
        using var http = new HttpClient(handler) { Timeout = Timeout, MaxResponseContentBufferSize = 1024 };
        try
        {
            using var response = await http.GetAsync($"http://127.0.0.1:{port}/healthz/" + kind,
                HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (response.StatusCode != System.Net.HttpStatusCode.OK) return 1;
            Console.WriteLine($"Evaluator probe {kind} succeeded.");
            return 0;
        }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException) { return 1; }
    }
}
