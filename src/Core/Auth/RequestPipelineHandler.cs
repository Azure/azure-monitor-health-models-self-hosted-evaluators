using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using SelfHostedEvaluators.Plugins.Abstractions;

namespace SelfHostedEvaluators.Core.Auth;

/// <summary>Creates owning HTTP transports from the host's security-hardened handler settings.</summary>
public interface IHttpTransportFactory
{
    /// <summary>Returns a transport that preserves the supplied settings, especially disabled redirects.</summary>
    /// <param name="settings">The host-configured handler whose lifetime the returned transport must own.</param>
    /// <returns>An owning transport; test implementations must retain equivalent destination safeguards.</returns>
    HttpMessageHandler Create(HttpClientHandler settings);
}

/// <summary>Uses the host-configured HTTP handler directly as the production transport.</summary>
public sealed class HttpTransportFactory : IHttpTransportFactory
{
    /// <inheritdoc />
    public HttpMessageHandler Create(HttpClientHandler settings) => settings;
}

internal sealed class RequestPipelineHandler(Uri endpoint, IAuthContext? auth, DestinationPolicy policy,
    PluginRequestLimiter limiter, TimeSpan timeout, IHttpTransportFactory transports, TimeProvider time, ILogger logger,
    bool singleAttempt = false) : HttpMessageHandler
{
    internal static readonly ActivitySource Activities = new("SelfHostedEvaluators.Core.Auth");
    private readonly object sync = new();
    private readonly List<Generation> generations = [];
    private Generation? current;
    private bool disposed;
    private bool ownerDisposed;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        policy.Check(request);
        var deadline = time.GetUtcNow() + timeout;
        if (AuthRegistry.PollDeadline.Value is { } pollDeadline && pollDeadline < deadline) deadline = pollDeadline;
        if (request.Options.TryGetValue(AuthRegistry.RequestDeadline, out var requestDeadline) && requestDeadline < deadline)
            deadline = requestDeadline;
        var remaining = deadline - time.GetUtcNow();
        if (remaining <= TimeSpan.Zero) throw new OperationCanceledException("Request deadline elapsed.");
        var budget = new CancellationTokenSource(remaining, time);
        var linked = CancellationTokenSource.CreateLinkedTokenSource(budget.Token, cancellationToken);
        var responseOwnsLifetime = false;
        var ct = linked.Token;
        using var activity = Activities.StartActivity("outbound.request", ActivityKind.Client);
        activity?.SetTag("server.address", endpoint.IdnHost);
        activity?.SetTag("server.port", endpoint.Port);
        try
        {
            var body = request.Content is null ? null : await request.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            for (var attempt = 0; ; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                using var lease = singleAttempt ? null : await limiter.AcquireWithinAsync(deadline - time.GetUtcNow(), ct).ConfigureAwait(false);
                if (lease is { IsAcquired: false }) throw new AuthenticationFailureException("request_rate_limited");
                AuthRegistry.ThrowIfDeadlineElapsed(request, time);
                using var outbound = Clone(request, body);
                if (auth is not null) await auth.AuthenticateAsync(outbound, ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                AuthRegistry.ThrowIfDeadlineElapsed(request, time);
                policy.Check(outbound);
                var generation = await AcquireTransportAsync(ct).ConfigureAwait(false);
                var responseOwnsGeneration = false;
                HttpResponseMessage response;
                try
                {
                    ct.ThrowIfCancellationRequested();
                    AuthRegistry.ThrowIfDeadlineElapsed(request, time);
                    response = await generation.Invoker.SendAsync(outbound, ct).ConfigureAwait(false);
                    try
                    {
                        if (singleAttempt && request.Options.TryGetValue(AuthRegistry.DiscardResponseBody, out var discard) && discard)
                        {
                            response.Content.Dispose();
                            response.Content = new ByteArrayContent([]);
                        }
                        else if (singleAttempt)
                        {
                            response.Content = new ResponseLifetimeContent(response.Content, () =>
                            {
                                try { Release(generation); }
                                finally { linked.Dispose(); budget.Dispose(); }
                            }, ct);
                            responseOwnsGeneration = true;
                            responseOwnsLifetime = true;
                        }
                        else await response.Content.LoadIntoBufferAsync(ct).ConfigureAwait(false);
                    }
                    catch { response.Dispose(); throw; }
                }
                finally { if (!responseOwnsGeneration) Release(generation); }
                activity?.SetTag("http.response.status_code", (int)response.StatusCode);
                if (singleAttempt) return response;
                if ((int)response.StatusCode is >= 300 and <= 399)
                {
                    response.Dispose();
                    throw new AuthenticationFailureException("redirect_blocked");
                }
                var retryable = response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500;
                var delay = retryable ? RetryDelay(response, attempt, time) : TimeSpan.Zero;
                if (response.StatusCode == HttpStatusCode.TooManyRequests) limiter.Pause(delay);
                if (retryable && attempt < 2 && delay < deadline - time.GetUtcNow())
                {
                    response.Dispose();
                    await Task.Delay(delay, time, ct).ConfigureAwait(false);
                    continue;
                }
                return response;
            }
        }
        catch (OperationCanceledException) when (singleAttempt || ct.IsCancellationRequested) { throw; }
        catch (HttpRequestException) when (singleAttempt) { throw; }
        catch (Exception error)
        {
            activity?.SetStatus(ActivityStatusCode.Error, "outbound_failure");
            logger.LogWarning("Outbound request failed ({FailureType}).", error.GetType().Name);
            throw new AuthenticationFailureException(error is AuthenticationFailureException failure ? failure.Code : "outbound_request");
        }
        finally
        {
            if (!responseOwnsLifetime) { linked.Dispose(); budget.Dispose(); }
        }
    }

    internal static TimeSpan RetryDelay(HttpResponseMessage response, int attempt, TimeProvider time)
    {
        var retry = response.Headers.RetryAfter;
        var delay = retry?.Delta ?? (retry?.Date is { } date ? date - time.GetUtcNow() :
            TimeSpan.FromMilliseconds(Math.Min(30000, 100 * Math.Pow(2, attempt) + Random.Shared.Next(100))));
        return delay > TimeSpan.Zero ? delay : TimeSpan.Zero;
    }

    internal static HttpRequestMessage Clone(HttpRequestMessage request, byte[]? body)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri)
        { Version = request.Version, VersionPolicy = request.VersionPolicy };
        foreach (var header in request.Headers) clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        foreach (var option in request.Options) clone.Options.Set(new HttpRequestOptionsKey<object?>(option.Key), option.Value);
        if (body is not null)
        {
            clone.Content = new ByteArrayContent(body);
            foreach (var header in request.Content!.Headers) clone.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
        return clone;
    }

    private async ValueTask<Generation> AcquireTransportAsync(CancellationToken ct)
    {
        var settings = new HttpClientHandler();
        try
        {
            if (auth is not null) await auth.ConfigureHttpHandlerAsync(endpoint, settings, ct).ConfigureAwait(false);
            settings.AllowAutoRedirect = false;
            settings.UseDefaultCredentials = false;
            settings.Credentials = null;
            settings.UseCookies = false;
            settings.PreAuthenticate = false;
            var fingerprint = string.Join(";", settings.ClientCertificates.Cast<System.Security.Cryptography.X509Certificates.X509Certificate2>()
                .Select(cert => Convert.ToHexString(SHA256.HashData(cert.RawData))));
            lock (sync)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (current is null || current.Fingerprint != fingerprint)
                {
                    var next = new Generation(fingerprint, settings, transports.Create(settings));
                    if (current is { Active: 0 }) { current.Dispose(); generations.Remove(current); }
                    current = next;
                    generations.Add(next);
                    settings = null!;
                }
                current.Active++;
                return current;
            }
        }
        finally { settings?.Dispose(); }
    }

    private void Release(Generation generation)
    {
        lock (sync)
        {
            generation.Active--;
            try
            {
                if (generation.Active == 0 && (disposed || generation != current))
                { generations.Remove(generation); generation.Dispose(); }
            }
            finally { DisposeOwnerIfDrained(); }
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            lock (sync)
            {
                disposed = true;
                try
                {
                    foreach (var generation in generations.Where(g => g.Active == 0).ToArray())
                    { generations.Remove(generation); generation.Dispose(); }
                }
                finally { DisposeOwnerIfDrained(); }
            }
        }
        base.Dispose(disposing);
    }

    private void DisposeOwnerIfDrained()
    {
        // Single-attempt clients own their auth context through streamed response disposal.
        if (!singleAttempt || !disposed || generations.Count != 0 || ownerDisposed) return;
        ownerDisposed = true;
        try { auth?.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        finally { limiter.Dispose(); }
    }

    private sealed class Generation(string fingerprint, HttpClientHandler settings, HttpMessageHandler handler) : IDisposable
    {
        public string Fingerprint { get; } = fingerprint;
        public HttpMessageInvoker Invoker { get; } = new(handler);
        public int Active { get; set; }
        public void Dispose()
        {
            try { Invoker.Dispose(); }
            finally { if (!ReferenceEquals(settings, handler)) settings.Dispose(); }
        }
    }
}
