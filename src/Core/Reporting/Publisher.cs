using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SelfHostedEvaluators.Core.Auth;
using SelfHostedEvaluators.Core.Diagnostics;
using SelfHostedEvaluators.Plugins.Abstractions;

namespace SelfHostedEvaluators.Core.Reporting;

/// <summary>Publishes the latest in-memory report per full signal key with expiry-aware, bounded retries.</summary>
/// <remarks>No durable queue is provided. Newer reports supersede older work for the same signal.</remarks>
public sealed class Publisher
{
    private static readonly ActivitySource Activities = new("SelfHostedEvaluators.Core.Reporting");
    private readonly object sync = new();
    private readonly Dictionary<Uri, ModelClient> models;
    private readonly Dictionary<SignalKey, Mailbox> mailboxes = [];
    private readonly TimeProvider time;
    private readonly Redactor redactor;
    private readonly RuntimeDiagnostics diagnostics;
    private readonly ILogger logger;
    private bool running;
    private bool closing;

    /// <summary>Creates a publisher sharing externally owned model clients and their request budgets.</summary>
    /// <param name="models">The clients indexed by configured model data-plane URI.</param>
    /// <param name="time">The clock for report expiry and retry delays.</param>
    /// <param name="redactor">The shared redactor for diagnostics.</param>
    /// <param name="diagnostics">Optional report outcome metrics.</param>
    /// <param name="logger">An optional destination for redacted publish diagnostics.</param>
    public Publisher(IEnumerable<ModelClient> models, TimeProvider time, Redactor redactor,
        RuntimeDiagnostics? diagnostics = null, ILogger? logger = null)
    {
        this.models = models.ToDictionary(m => m.Configuration.DataPlaneUri);
        this.time = time;
        this.redactor = redactor;
        this.diagnostics = diagnostics ?? RuntimeDiagnostics.Shared;
        this.logger = redactor.Wrap(logger ?? NullLogger.Instance);
    }

    /// <summary>Gets the combined lifetime of the mailbox workers currently known to the publisher.</summary>
    public Task Completion
    {
        get { lock (sync) return Task.WhenAll(mailboxes.Values.Select(m => m.Worker ?? Task.CompletedTask)); }
    }

    /// <summary>Replaces the signal's pending report and starts delivery if the publisher is running.</summary>
    /// <param name="report">The report whose full key selects its model and mailbox.</param>
    /// <param name="ct">Cancels enqueueing before the report is accepted.</param>
    /// <returns>Completion of enqueueing and retired-work cleanup, not confirmation of remote delivery.</returns>
    /// <remarks>Expired reports are counted as dropped; flushing rejects new reports.</remarks>
    public ValueTask EnqueueAsync(HealthReport report, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(report);
        ct.ThrowIfCancellationRequested();
        Work? retired = null;
        lock (sync)
        {
            if (closing) throw new InvalidOperationException("Publisher is flushing; new reports are not accepted.");
            if (!models.TryGetValue(report.Key.ModelUri, out var model)) throw new ArgumentException("Unknown report model.", nameof(report));
            var remaining = report.ExpiresAt - time.GetUtcNow();
            if (remaining <= TimeSpan.Zero)
            {
                diagnostics.Dropped(model.Name, "expired");
                return ValueTask.CompletedTask;
            }
            if (!mailboxes.TryGetValue(report.Key, out var mailbox)) mailboxes.Add(report.Key, mailbox = new(model));
            if (mailbox.Latest is { } previous)
            {
                diagnostics.Superseded(model.Name);
                previous.Cancel();
                previous.StopExpiry();
                if (!ReferenceEquals(previous, mailbox.Active)) retired = previous;
            }
            var work = new Work(report);
            mailbox.Latest = work;
            work.Expiry = time.CreateTimer(_ => Expire(mailbox, work), null,
                remaining, Timeout.InfiniteTimeSpan);
            HasExpired(mailbox, work);
            if (running) Start(mailbox);
        }
        return retired?.DisposeAsync() ?? ValueTask.CompletedTask;
    }

    /// <summary>Starts queued mailbox workers and keeps the publishing loop alive until cancellation.</summary>
    /// <param name="ct">Stops the run wait; use FlushAsync to drain or cancel mailbox work.</param>
    public async Task RunAsync(CancellationToken ct)
    {
        lock (sync)
        {
            if (running || closing) throw new InvalidOperationException("Publisher has already started or is flushing.");
            running = true;
            foreach (var mailbox in mailboxes.Values) Start(mailbox);
        }
        try { await Task.Delay(Timeout.InfiniteTimeSpan, time, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    /// <summary>Stops accepting reports and drains pending work within the caller's shutdown budget.</summary>
    /// <param name="ct">Bounds draining; cancellation drops queued reports and requests active-work cancellation.</param>
    public async Task FlushAsync(CancellationToken ct)
    {
        Task completion;
        lock (sync)
        {
            closing = true;
            if (!ct.IsCancellationRequested)
                foreach (var mailbox in mailboxes.Values) Start(mailbox);
            completion = Completion;
        }
        try
        {
            ct.ThrowIfCancellationRequested();
            await completion.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            List<Work> retired = [];
            lock (sync)
            {
                foreach (var mailbox in mailboxes.Values)
                {
                    if (mailbox.Latest is { } pending)
                    {
                        diagnostics.Dropped(mailbox.Model.Name, "shutdown");
                        pending.Cancel();
                        pending.StopExpiry();
                        if (!ReferenceEquals(pending, mailbox.Active)) retired.Add(pending);
                        mailbox.Latest = null;
                    }
                    mailbox.Active?.Cancel();
                }
            }
            foreach (var work in retired) await work.DisposeAsync().ConfigureAwait(false);
        }
    }

    private void Expire(Mailbox mailbox, Work work)
    {
        lock (sync)
        {
            if (!ReferenceEquals(mailbox.Latest, work)) return;
            diagnostics.Dropped(mailbox.Model.Name, "expired");
            mailbox.Latest = null;
            work.Cancel();
            if (!ReferenceEquals(mailbox.Active, work))
            {
                // No request ever acquired this work, so it can release its resources immediately.
                _ = work.DisposeAsync();
            }
        }
    }

    private void Settle(Mailbox mailbox, Work work, string? dropped)
    {
        lock (sync)
        {
            if (!ReferenceEquals(mailbox.Latest, work)) return;
            if (dropped is not null) diagnostics.Dropped(mailbox.Model.Name, dropped);
            mailbox.Latest = null;
            work.StopExpiry();
        }
    }

    private bool HasExpired(Mailbox mailbox, Work work)
    {
        if (work.Report.ExpiresAt > time.GetUtcNow()) return false;
        Expire(mailbox, work);
        return true;
    }

    private void Start(Mailbox mailbox)
    {
        if (mailbox.Worker is null && mailbox.Latest is not null)
            mailbox.Worker = Task.Run(() => ProcessAsync(mailbox));
    }

    private async Task ProcessAsync(Mailbox mailbox)
    {
        while (true)
        {
            Work work;
            lock (sync)
            {
                if (mailbox.Latest is null) { mailbox.Worker = null; return; }
                work = mailbox.Latest;
                mailbox.Active = work;
            }
            string? dropped = null;
            try { dropped = await PublishAsync(mailbox, work).ConfigureAwait(false); }
            catch (Exception error)
            {
                logger.LogWarning("Publish operation failed for model {Model} ({FailureType}).", mailbox.Model.Name, error.GetType().Name);
                dropped = "failure";
            }
            Settle(mailbox, work, dropped);
            lock (sync) mailbox.Active = null;
            await work.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task<string?> PublishAsync(Mailbox mailbox, Work work)
    {
        var model = mailbox.Model;
        var report = work.Report;
        var remaining = report.ExpiresAt - time.GetUtcNow();
        if (remaining <= TimeSpan.Zero) { Expire(mailbox, work); return null; }
        var ct = work.Cancellation.Token;
        for (var attempt = 0; ; attempt = Math.Min(attempt + 1, 30))
        {
            if (HasExpired(mailbox, work)) return null;
            if (ct.IsCancellationRequested) return null;
            using var activity = Activities.StartActivity("report.publish", ActivityKind.Client, work.ParentContext);
            activity?.SetTag("model", model.Name);
            activity?.SetTag("client.request.id", report.ClientRequestId);
            TimeSpan delay;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post,
                    model.Route("/api/entities/" + Uri.EscapeDataString(report.Key.Entity) + "/healthReports"))
                {
                    Content = new ByteArrayContent(report.Body.ToArray())
                };
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
                request.Content.Headers.ContentLength = report.Body.Length;
                request.Headers.TransferEncodingChunked = false;
                request.Headers.Add("x-ms-client-request-id", report.ClientRequestId);
                request.Headers.Add("x-ms-return-client-request-id", "true");
                request.Options.Set(AuthRegistry.DiscardResponseBody, true);
                request.Options.Set(AuthRegistry.RequestDeadline, report.ExpiresAt);
                using var response = await model.SendAsync(request, ct).ConfigureAwait(false);
                var status = (int)response.StatusCode;
                if (response.Headers.TryGetValues("x-ms-request-id", out var ids))
                {
                    var id = ids.FirstOrDefault();
                    activity?.SetTag("service.request.id", id is { Length: <= 128 } && !id.Any(char.IsControl)
                        ? redactor.Redact(id) : "[REDACTED]");
                }
                activity?.SetTag("http.response.status_code", status);
                // Apply a 429 even when this response belongs to a report just superseded.
                delay = status is 408 or 429 or >= 500 and <= 599 ? model.RetryDelay(response, attempt) : TimeSpan.Zero;
                // The shared budget owns and measures the 429 wait on the next attempt.
                if (status == 429) delay = TimeSpan.Zero;
                var expired = HasExpired(mailbox, work);
                if (!expired)
                {
                    if (response.StatusCode == HttpStatusCode.Accepted) Settle(mailbox, work, null);
                    else if (status is not (408 or 429 or >= 500 and <= 599)) Settle(mailbox, work, "permanent");
                }
                diagnostics.Published(model.Name, status.ToString(CultureInfo.InvariantCulture));
                if (expired || response.StatusCode == HttpStatusCode.Accepted) return null;
                if (status is not (408 or 429 or >= 500 and <= 599))
                {
                    activity?.SetStatus(ActivityStatusCode.Error, "permanent");
                    logger.LogWarning("Report rejected permanently for model {Model} (HTTP {Status}).", model.Name, status);
                    return "permanent";
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            { return null; }
            catch (Exception error) when (error is HttpRequestException or AuthenticationFailureException or OperationCanceledException)
            {
                if (HasExpired(mailbox, work)) return null;
                var status = error is OperationCanceledException ? "timeout" :
                    error is AuthenticationFailureException ? "auth_failure" : "network";
                diagnostics.Published(model.Name, status);
                activity?.SetStatus(ActivityStatusCode.Error, status);
                logger.LogDebug("Transient publish failure for model {Model} ({Status}).", model.Name, status);
                delay = ModelClient.NetworkDelay(attempt);
            }
            try
            {
                // TTL is at most seven days; no individual delay needs to exceed the remaining lifetime.
                remaining = report.ExpiresAt - time.GetUtcNow();
                if (remaining <= TimeSpan.Zero) { Expire(mailbox, work); return null; }
                await Task.Delay(delay < remaining ? delay : remaining, time, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            { return null; }
        }
    }

    private sealed class Mailbox(ModelClient model)
    {
        public ModelClient Model { get; } = model;
        public Work? Latest { get; set; }
        public Work? Active { get; set; }
        public Task? Worker { get; set; }
    }

    private sealed class Work(HealthReport report) : IAsyncDisposable
    {
        public HealthReport Report { get; } = report;
        public ActivityContext ParentContext { get; } = Activity.Current?.Context ?? default;
        public CancellationTokenSource Cancellation { get; } = new();
        public ITimer? Expiry { get; set; }
        private Task? cancellation;
        public void StopExpiry() => Expiry?.Dispose();
        public void Cancel() => cancellation ??= Cancellation.CancelAsync();
        public async ValueTask DisposeAsync()
        {
            try { if (cancellation is not null) await cancellation.ConfigureAwait(false); }
            catch (Exception) { /* Cancellation callbacks must not fault the mailbox worker. */ }
            finally { StopExpiry(); Cancellation.Dispose(); }
        }
    }
}
