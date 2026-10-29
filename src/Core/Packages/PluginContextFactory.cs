using Microsoft.Extensions.Logging;
using SelfHostedEvaluators.Core.Auth;
using SelfHostedEvaluators.Core.Configuration;
using SelfHostedEvaluators.Core.Diagnostics;
using SelfHostedEvaluators.Plugins.Abstractions;

namespace SelfHostedEvaluators.Core.Packages;

/// <summary>Builds independently authenticated plugin contexts and tracks their owned-resource leases.</summary>
/// <param name="catalog">The loaded package catalog supplying capabilities and host restrictions.</param>
/// <param name="registry">The registry creating guarded authentication and HTTP pipelines.</param>
/// <param name="redactor">The shared redactor for instance logging.</param>
/// <param name="logger">The destination for redacted instance diagnostics.</param>
/// <param name="time">The clock for request limits and plugin observations.</param>
/// <param name="diagnostics">Optional runtime metrics for request-limit waits.</param>
public sealed class PluginContextFactory(PackageCatalog catalog, AuthRegistry registry, Redactor redactor,
    ILogger logger, TimeProvider time, RuntimeDiagnostics? diagnostics = null)
{
    private int activeContexts;
    /// <summary>Gets the number of successfully created context leases not yet disposed.</summary>
    public int ActiveContexts => Volatile.Read(ref activeContexts);

    /// <summary>Creates the instance's shared limiter, authentication context and named HTTP clients.</summary>
    /// <param name="configuration">The validated named plugin configuration.</param>
    /// <param name="ct">Cancels authentication and resource creation.</param>
    /// <returns>A caller-owned lease; partial resources are disposed before a failed creation returns.</returns>
    public async ValueTask<PluginContextLease> CreateAsync(PluginConfiguration configuration, CancellationToken ct)
    {
        catalog.ThrowIfDisposed();
        var package = catalog.Packages[configuration.Package];
        var limiter = new PluginRequestLimiter(configuration.RateLimit, time, diagnostics, configuration.Name);
        IAuthContext? auth = null;
        var clients = new Dictionary<string, HttpClient>(StringComparer.Ordinal);
        try
        {
            auth = await registry.CreateContextAsync(configuration.Auth, configuration.Endpoints.Values, limiter,
                configuration.Timeout, ct, allowedHosts: package.Capabilities.AllowedHosts).ConfigureAwait(false);
            foreach (var endpoint in configuration.Endpoints)
                clients.Add(endpoint.Key, registry.CreateEndpointClient(endpoint.Value, auth,
                    new([endpoint.Value], package.Capabilities.AllowedHosts), limiter, configuration.Timeout));
            var context = new PluginInstanceContext(configuration.Name, configuration.Settings, configuration.Batch.MaxSize,
                clients, auth, limiter, redactor.Wrap(logger), time);
            ct.ThrowIfCancellationRequested();
            Interlocked.Increment(ref activeContexts);
            return new(context, () => Interlocked.Decrement(ref activeContexts));
        }
        catch
        {
            await ResourceDisposal.DisposeAsync(clients.Values, auth, limiter).ConfigureAwait(false);
            throw;
        }
    }
}

/// <summary>Owns the host-supplied transports, auth context and limiter for one sink client.</summary>
public sealed class PluginContextLease : IAsyncDisposable
{
    private readonly Action released;
    private int disposed;
    private int owned;
    internal PluginContextLease(PluginInstanceContext context, Action released) { Context = context; this.released = released; }
    /// <summary>Gets the context whose services remain valid until the lease is disposed.</summary>
    public PluginInstanceContext Context { get; }
    /// <summary>Transfers this lease to a client wrapper that waits for active queries before disposal.</summary>
    /// <param name="client">The single sink client whose lifetime will own the lease.</param>
    /// <returns>A wrapper that disposes the sink before releasing the context services.</returns>
    public ISinkClient Own(ISinkClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if (Interlocked.Exchange(ref owned, 1) != 0) throw new InvalidOperationException("The context already owns a client.");
        return new OwnedSinkClient(client, this);
    }
    /// <summary>Disposes context-owned services once and updates the factory's active count.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        try
        {
            await ResourceDisposal.DisposeAsync(Context.HttpClients.Values, Context.Auth, Context.RateLimiter).ConfigureAwait(false);
        }
        finally { released(); }
    }
}

internal sealed class OwnedSinkClient(ISinkClient inner, PluginContextLease resources) : ISinkClient
{
    private readonly object sync = new();
    private readonly TaskCompletionSource drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int active;
    private Task? disposal;
    private bool stopping;

    public async Task<IReadOnlyList<SignalResult>> QueryBatchAsync(IReadOnlyList<SignalQuery> batch, QueryWindow window, CancellationToken ct)
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(stopping, this);
            active++;
        }
        try { return await inner.QueryBatchAsync(batch, window, ct).ConfigureAwait(false); }
        finally
        {
            lock (sync) { active--; if (stopping && active == 0) drained.TrySetResult(); }
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (sync)
        {
            stopping = true;
            if (active == 0) drained.TrySetResult();
            disposal ??= DisposeCoreAsync();
            return new(disposal);
        }
    }

    private async Task DisposeCoreAsync()
    {
        await drained.Task.ConfigureAwait(false);
        try { await inner.DisposeAsync().ConfigureAwait(false); }
        finally { await resources.DisposeAsync().ConfigureAwait(false); }
    }
}
