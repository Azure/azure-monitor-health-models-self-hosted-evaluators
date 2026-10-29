using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SelfHostedEvaluators.Core.Auth;
using SelfHostedEvaluators.Core.Configuration;
using SelfHostedEvaluators.Core.Diagnostics;

namespace SelfHostedEvaluators.Core.Reporting;

/// <summary>Owns a model's authenticated single-attempt transport and shared data-plane request budget.</summary>
public sealed class ModelClient : IDisposable
{
    private readonly HttpClient http;
    private readonly ModelRequestBudget budget;
    private readonly TimeProvider time;
    private readonly RuntimeDiagnostics diagnostics;
    private readonly ILogger logger;
    private readonly object sync = new();
    private int active;
    private bool disposed;

    /// <summary>Creates a model client and takes ownership of its authenticated HTTP transport.</summary>
    /// <param name="name">The local model configuration name used in diagnostics.</param>
    /// <param name="configuration">The validated data-plane settings and per-minute budget.</param>
    /// <param name="singleAttemptClient">A client with no data-plane retries; retry policy belongs to the publisher.</param>
    /// <param name="time">The clock for rate limits and retry delays.</param>
    /// <param name="redactor">The shared redactor for diagnostics.</param>
    /// <param name="diagnostics">Optional metrics for request-budget waits.</param>
    /// <param name="logger">An optional destination for redacted retry warnings.</param>
    public ModelClient(string name, HealthModelConfiguration configuration, HttpClient singleAttemptClient,
        TimeProvider time, Redactor redactor, RuntimeDiagnostics? diagnostics = null, ILogger? logger = null)
    {
        if (configuration.ReportsPerMinute is < 1 or >= 6000) throw new ArgumentOutOfRangeException(nameof(configuration));
        Name = name;
        Configuration = configuration;
        http = singleAttemptClient;
        this.time = time;
        this.diagnostics = diagnostics ?? RuntimeDiagnostics.Shared;
        this.logger = redactor.Wrap(logger ?? NullLogger.Instance);
        budget = new(configuration.ReportsPerMinute, time);
    }

    /// <summary>Gets the local model name used in logs and metrics.</summary>
    public string Name { get; }
    /// <summary>Gets the reporting endpoint, API version, authentication and request budget.</summary>
    public HealthModelConfiguration Configuration { get; }

    /// <summary>Creates a model client through the registry's authenticated single-attempt pipeline.</summary>
    /// <param name="name">The local model configuration name.</param>
    /// <param name="configuration">The model's validated reporting settings.</param>
    /// <param name="registry">The registry supplying the authenticated transport.</param>
    /// <param name="time">The clock for request budgets and deadlines.</param>
    /// <param name="redactor">The shared diagnostic redactor.</param>
    /// <param name="diagnostics">Optional runtime metrics.</param>
    /// <param name="logger">An optional destination for redacted warnings.</param>
    /// <param name="ct">Cancels authentication initialization.</param>
    /// <returns>A caller-owned model client.</returns>
    public static async ValueTask<ModelClient> CreateAsync(string name, HealthModelConfiguration configuration,
        AuthRegistry registry, TimeProvider time, Redactor redactor, RuntimeDiagnostics? diagnostics = null,
        ILogger? logger = null, CancellationToken ct = default)
    {
        var client = await registry.CreateSingleAttemptClientAsync(configuration.Auth, configuration.DataPlaneUri,
            TimeSpan.FromSeconds(30), ct, configuration.TokenScope).ConfigureAwait(false);
        try { return new(name, configuration, client, time, redactor, diagnostics, logger); }
        catch { client.Dispose(); throw; }
    }

    /// <summary>Resolves a data-plane path against the model endpoint and appends its configured API version.</summary>
    /// <param name="path">The route path without query parameters.</param>
    /// <returns>The versioned request URI.</returns>
    public Uri Route(string path) => new(Configuration.DataPlaneUri,
        path + "?api-version=" + Uri.EscapeDataString(Configuration.ApiVersion));

    /// <summary>Acquires the shared model budget and sends one request without retrying it.</summary>
    /// <param name="request">The request with an optional publisher deadline.</param>
    /// <param name="ct">Cancels budget waiting or the outbound request.</param>
    /// <returns>A caller-owned response; disposing it releases the active transport lifetime.</returns>
    public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        lock (sync) { ObjectDisposedException.ThrowIf(disposed, this); active++; }
        var responseOwnsLifetime = false;
        try
        {
            AuthRegistry.ThrowIfDeadlineElapsed(request, time);
            var start = time.GetTimestamp();
            try
            {
                await budget.AcquireAsync(ct, request.Options.TryGetValue(AuthRegistry.RequestDeadline, out var deadline)
                    ? deadline : null).ConfigureAwait(false);
            }
            finally
            {
                var elapsed = time.GetElapsedTime(start);
                if (elapsed > TimeSpan.Zero) diagnostics.RateLimitWait(Name, elapsed);
            }
            ct.ThrowIfCancellationRequested();
            AuthRegistry.ThrowIfDeadlineElapsed(request, time);
            var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            try
            {
                response.Content = new ResponseLifetimeContent(response.Content, Release);
                responseOwnsLifetime = true;
                return response;
            }
            catch { response.Dispose(); throw; }
        }
        finally
        {
            if (!responseOwnsLifetime) Release();
        }
    }

    private void Release()
    {
        lock (sync) { active--; if (disposed && active == 0) http.Dispose(); }
    }

    internal TimeSpan RetryDelay(HttpResponseMessage response, int attempt)
    {
        TimeSpan? parsed = null;
        if (response.Headers.TryGetValues("Retry-After", out var values))
        {
            var all = values.ToArray();
            if (all.Length == 1) parsed = RetryAfterParser.Parse(all[0], time.GetUtcNow());
            if (parsed is null) logger.LogWarning("Model {Model} returned malformed Retry-After; using jittered backoff.", Name);
        }
        var delay = parsed ?? NetworkDelay(attempt);
        if (response.StatusCode == HttpStatusCode.TooManyRequests) budget.Pause(delay);
        return delay;
    }

    internal static TimeSpan NetworkDelay(int attempt) => TimeSpan.FromMilliseconds(
        Math.Min(30000, 1000 * Math.Pow(2, Math.Min(attempt, 5))) * (0.5 + Random.Shared.NextDouble() * 0.5));

    /// <summary>Rejects new requests and disposes the HTTP client after active responses release their lifetimes.</summary>
    public void Dispose()
    {
        lock (sync)
        {
            if (disposed) return;
            disposed = true;
            if (active == 0) http.Dispose();
        }
    }
}
