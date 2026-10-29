using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SelfHostedEvaluators.Core.Auth;
using SelfHostedEvaluators.Core.Configuration;
using SelfHostedEvaluators.Core.Diagnostics;
using SelfHostedEvaluators.Core.Packages;
using SelfHostedEvaluators.Plugins.Abstractions;

namespace SelfHostedEvaluators.Core.Polling;

/// <summary>Owns sink clients and executes bounded batches while retaining resources used by unfinished calls.</summary>
public sealed class PollExecutor : IAsyncDisposable
{
    private readonly object sync = new();
    private readonly Dictionary<string, PluginState> plugins;
    private readonly HashSet<Task> active = [];
    private readonly CancellationTokenSource shutdown = new();
    private readonly TimeProvider time;
    private readonly RuntimeDiagnostics diagnostics;
    private readonly ILogger logger;
    private Task? disposal;

    /// <summary>Creates an executor using the package manager's client activation and ownership policy.</summary>
    /// <param name="snapshot">The startup snapshot defining plugin limits.</param>
    /// <param name="manager">The manager used to activate clients lazily.</param>
    /// <param name="time">An optional clock for timeouts and cancellation grace periods.</param>
    /// <param name="diagnostics">Optional runtime metrics.</param>
    /// <param name="logger">An optional logger for plugin quarantine and recovery.</param>
    public PollExecutor(ConfigSnapshot snapshot, PluginManager manager, TimeProvider? time = null, RuntimeDiagnostics? diagnostics = null,
        ILogger? logger = null)
        : this(snapshot, manager.CreateClientAsync, time, diagnostics, logger) { }

    /// <summary>Creates an executor with an injectable client factory; returned clients become executor-owned.</summary>
    /// <param name="snapshot">The startup snapshot defining plugin limits.</param>
    /// <param name="createClient">The per-plugin activation factory, cached by the executor.</param>
    /// <param name="time">An optional clock for timeouts and cancellation grace periods.</param>
    /// <param name="diagnostics">Optional runtime metrics.</param>
    /// <param name="logger">An optional logger for plugin quarantine and recovery.</param>
    public PollExecutor(ConfigSnapshot snapshot,
        Func<PluginConfiguration, CancellationToken, ValueTask<ISinkClient>> createClient,
        TimeProvider? time = null, RuntimeDiagnostics? diagnostics = null, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(createClient);
        this.time = time ?? TimeProvider.System;
        this.diagnostics = diagnostics ?? RuntimeDiagnostics.Shared;
        this.logger = logger ?? NullLogger.Instance;
        plugins = snapshot.Plugins.ToDictionary(p => p.Name, p => new PluginState(p, createClient), StringComparer.Ordinal);
    }

    /// <summary>Prewarms the same cached client activations used by batch execution.</summary>
    /// <param name="pluginNames">Configured plugin names to initialize.</param>
    /// <param name="ct">Cancels activation and waiting; the executor retains client ownership.</param>
    public async Task InitializeAsync(IEnumerable<string> pluginNames, CancellationToken ct)
    {
        foreach (var name in pluginNames)
        {
            Task<ISinkClient> activation;
            lock (sync)
            {
                ObjectDisposedException.ThrowIf(disposal is not null, this);
                activation = plugins[name].GetClientAsync(ct);
            }
            await activation.WaitAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>Executes a batch within concurrency and timeout bounds and matches results to full signal keys.</summary>
    /// <param name="batch">The scheduled, uniquely keyed queries for one plugin.</param>
    /// <param name="ct">Requests cancellation; cancellation is returned as keyed failures at this engine boundary.</param>
    /// <returns>One outcome per query, including failures for unavailable plugins, invalid results or timeouts.</returns>
    /// <remarks>Unfinished canceled work keeps its permit and resources until completion; the plugin is temporarily blocked.</remarks>
    public async Task<IReadOnlyList<SignalResult>> ExecuteAsync(ScheduledBatch batch, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(batch);
        var started = time.GetTimestamp();
        var outcome = "failure";
        try
        {
            PluginState state;
            Task<IReadOnlyList<SignalResult>> underlying;
            CancellationTokenSource execution;
            TaskCompletionSource<Task> returned;
            ExecutionStatus status;
            lock (sync)
            {
                if (disposal is not null || shutdown.IsCancellationRequested)
                    return Fail(batch, "stopping", "Polling is stopping.");
                if (ct.IsCancellationRequested) return Fail(batch, "cancelled", "The poll was cancelled.");
                if (!plugins.TryGetValue(batch.PluginName, out state!))
                    return Fail(batch, "plugin_unavailable", "The signal plugin is unavailable.");
                if (state.Quarantined) return Fail(batch, "plugin_quarantined", "The plugin has an unfinished cancelled call.");
                if (batch.Queries.Count > state.Configuration.Batch.MaxSize)
                    return Fail(batch, "invalid_batch", "The batch exceeds the configured size.");
                execution = new();
                status = new();
                returned = new(TaskCreationOptions.RunContinuationsAsynchronously);
                var deadline = time.GetUtcNow() + state.Configuration.GetEffectiveTimeout(batch.Window.Interval);
                underlying = Task.Run(() => InvokeAsync(state, batch, execution.Token, status, deadline));
                var cleanup = CleanupAsync(underlying, returned.Task, execution, state, status);
                active.Add(cleanup);
                _ = cleanup.ContinueWith(completed => { lock (sync) active.Remove(completed); },
                    CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }

            Task cancel = Task.CompletedTask;
            using var waiting = CancellationTokenSource.CreateLinkedTokenSource(ct, shutdown.Token);
            try
            {
                var results = await underlying.WaitAsync(state.Configuration.GetEffectiveTimeout(batch.Window.Interval), time, waiting.Token)
                    .ConfigureAwait(false);
                outcome = results.Any(r => r is SignalResult.Failure) ? "failure" : "success";
                return results;
            }
            catch (Exception error) when (error is TimeoutException or OperationCanceledException)
            {
                // CancelAsync keeps hostile synchronous cancellation callbacks off the scheduler thread.
                lock (sync)
                {
                    cancel = execution.CancelAsync();
                    if ((!underlying.IsCompleted || !cancel.IsCompleted) && status.Started != 0)
                    {
                        state.MarkUnsafe();
                        status.GraceCancellation = new();
                        status.Grace = Task.Delay(TimeSpan.FromSeconds(5), time, status.GraceCancellation.Token);
                    }
                }
                outcome = waiting.IsCancellationRequested ? "cancelled" : "timeout";
                return Fail(batch, outcome, outcome == "timeout" ? "The batch timed out." : "The poll was cancelled.");
            }
            finally { returned.TrySetResult(cancel); }
        }
        finally { diagnostics.PollFinished(batch.PluginName, outcome, time.GetElapsedTime(started)); }
    }

    private async Task<IReadOnlyList<SignalResult>> InvokeAsync(PluginState state, ScheduledBatch batch,
        CancellationToken ct, ExecutionStatus status, DateTimeOffset deadline)
    {
        var previousDeadline = AuthRegistry.PollDeadline.Value;
        AuthRegistry.PollDeadline.Value = deadline;
        status.StartedAt = time.GetTimestamp();
        try
        {
            await state.Concurrency.WaitAsync(ct).ConfigureAwait(false);
            status.Acquired = true;
            lock (sync)
            {
                ct.ThrowIfCancellationRequested();
                if (state.Quarantined) return Fail(batch, "plugin_quarantined", "The plugin has an unfinished cancelled call.");
                status.Started = 1;
            }
            diagnostics.Inflight(batch.PluginName, 1);
            var client = await state.GetClientAsync(ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (state.Quarantined) return Fail(batch, "plugin_quarantined", "The plugin has an unfinished cancelled call.");
            var results = await client.QueryBatchAsync(batch.Queries, batch.Window, ct).ConfigureAwait(false);
            return Match(batch, results);
        }
        catch (OperationCanceledException) { return Fail(batch, "cancelled", "The poll was cancelled."); }
        catch (AuthenticationFailureException error) { return Fail(batch, error.Code, "The outbound request failed."); }
        catch (Exception) { return Fail(batch, "poll_failed", "The plugin call failed."); }
        finally { AuthRegistry.PollDeadline.Value = previousDeadline; }
    }

    private static IReadOnlyList<SignalResult> Match(ScheduledBatch batch, IReadOnlyList<SignalResult>? results)
    {
        if (results is null || results.Any(r => r is null || r.Key is null))
            return Fail(batch, "invalid_result", "The plugin returned malformed results.");
        var indexed = results.GroupBy(r => r.Key).ToDictionary(g => g.Key, g => g.ToArray());
        return batch.Queries.Select(query =>
        {
            if (!indexed.TryGetValue(query.Key, out var matches))
                return (SignalResult)new SignalResult.Failure(query.Key, "missing_result", "The plugin returned no result for the signal.");
            if (matches.Length != 1 || !Valid(matches[0]))
                return new SignalResult.Failure(query.Key, "invalid_result", "The plugin returned duplicate or malformed results.");
            return matches[0];
        }).ToArray();
    }

    private static bool Valid(SignalResult result) => result switch
    {
        SignalResult.Observation observation => observation.Value is null || double.IsFinite(observation.Value.Value),
        SignalResult.NoData noData => noData.Reason is not null,
        SignalResult.Failure failure => !string.IsNullOrWhiteSpace(failure.Code) && failure.Message is not null,
        _ => false
    };

    private static IReadOnlyList<SignalResult> Fail(ScheduledBatch batch, string code, string message) =>
        batch.Queries.Select(q => (SignalResult)new SignalResult.Failure(q.Key, code, message)).ToArray();

    private async Task CleanupAsync(Task underlying, Task<Task> returned, CancellationTokenSource execution,
        PluginState state, ExecutionStatus status)
    {
        try
        {
            var cancellation = await returned.ConfigureAwait(false);
            var finished = Task.WhenAll(underlying, cancellation);
            if (status.Grace is { } grace)
            {
                await Task.WhenAny(finished, grace).ConfigureAwait(false);
                lock (sync)
                {
                    if (!finished.IsCompleted)
                    {
                        diagnostics.Hang(state.Configuration.Name);
                        if (!state.HangReported)
                        {
                            state.HangReported = true;
                            diagnostics.Quarantine(state.Configuration.Name);
                            logger.LogWarning("Plugin {Plugin} quarantined: cancelled work remains unfinished after the cancellation grace period.",
                                state.Configuration.Name);
                        }
                    }
                }
            }
            try { await finished.ConfigureAwait(false); }
            catch (Exception) { /* A plugin cancellation callback must not strand cleanup. */ }
        }
        finally
        {
            status.GraceCancellation?.Cancel();
            status.GraceCancellation?.Dispose();
            execution.Dispose();
            lock (sync)
            {
                if (status.Grace is not null) state.FinishUnsafe();
                if (!state.Quarantined && state.HangReported)
                {
                    state.HangReported = false;
                    logger.LogWarning("Plugin {Plugin} recovered: all unfinished cancelled work has completed.", state.Configuration.Name);
                }
                if (status.Started != 0)
                {
                    diagnostics.Inflight(state.Configuration.Name, -1);
                    diagnostics.UnderlyingFinished(state.Configuration.Name, time.GetElapsedTime(status.StartedAt));
                }
                if (status.Acquired) state.Concurrency.Release();
            }
        }
    }

    /// <summary>Waits for tracked calls and their cancellation callbacks to release owned resources.</summary>
    /// <param name="ct">Cancels only the drain wait, not ownership of unfinished work.</param>
    public Task DrainAsync(CancellationToken ct)
    {
        lock (sync) return Task.WhenAll(active.ToArray()).WaitAsync(ct);
    }

    /// <summary>Stops polling, drains unfinished work and disposes cached clients without reusing active resources.</summary>
    public ValueTask DisposeAsync()
    {
        lock (sync)
        {
            disposal ??= Task.Run(DisposeCoreAsync);
            return new(disposal);
        }
    }

    private async Task DisposeCoreAsync()
    {
        await shutdown.CancelAsync().ConfigureAwait(false);
        await DrainAsync(CancellationToken.None).ConfigureAwait(false);
        var disposals = plugins.Values.Select(async state =>
        {
            try
            {
                if (state.Client is { } client)
                {
                    ISinkClient? instance = null;
                    try { instance = await client.ConfigureAwait(false); }
                    catch (Exception) { }
                    if (instance is not null) await instance.DisposeAsync().ConfigureAwait(false);
                }
            }
            finally { state.Concurrency.Dispose(); }
        });
        try { await Task.WhenAll(disposals).ConfigureAwait(false); }
        finally { shutdown.Dispose(); }
    }

    private sealed class ExecutionStatus
    {
        public int Started;
        public bool Acquired;
        public long StartedAt;
        public Task? Grace;
        public CancellationTokenSource? GraceCancellation;
    }

    private sealed class PluginState(PluginConfiguration configuration,
        Func<PluginConfiguration, CancellationToken, ValueTask<ISinkClient>> create)
    {
        private readonly object sync = new();
        private int unsafeWork;
        public void MarkUnsafe() => Interlocked.Increment(ref unsafeWork);
        public void FinishUnsafe() => Interlocked.Decrement(ref unsafeWork);
        public bool HangReported { get; set; }
        public PluginConfiguration Configuration { get; } = configuration;
        public SemaphoreSlim Concurrency { get; } = new(configuration.Concurrency);
        public Task<ISinkClient>? Client { get; private set; }
        public bool Quarantined => Volatile.Read(ref unsafeWork) != 0;
        public Task<ISinkClient> GetClientAsync(CancellationToken ct)
        {
            lock (sync)
            {
                if (Client is null || (!Quarantined && Client is { IsCompleted: true, IsCompletedSuccessfully: false }))
                    Client = Task.Run(async () => await create(Configuration, ct).ConfigureAwait(false)
                        ?? throw new InvalidOperationException("The plugin returned no client."));
                return Client;
            }
        }
    }
}
