namespace SelfHostedEvaluators.Plugins.Abstractions;

/// <summary>Adapts single-query implementations to ordered batches with bounded fan-out and redacted failures.</summary>
/// <remarks>Provides no networking, retries or timeouts. Started queries remain observable until they finish.</remarks>
public abstract class SingleQueryClientBase : ISinkClient
{
    private readonly int _maxConcurrency;

    /// <summary>Sets the positive concurrency bound within each batch.</summary>
    /// <param name="maxConcurrency">The maximum simultaneous single-query calls per batch.</param>
    protected SingleQueryClientBase(int maxConcurrency)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxConcurrency);
        _maxConcurrency = maxConcurrency;
    }

    /// <inheritdoc />
    /// <remarks>Preserves input ordering and propagates caller cancellation after started workers finish.</remarks>
    public async Task<IReadOnlyList<SignalResult>> QueryBatchAsync(
        IReadOnlyList<SignalQuery> batch, QueryWindow window, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(window);
        ct.ThrowIfCancellationRequested();
        var results = new SignalResult[batch.Count];
        var processing = Parallel.ForEachAsync(
            Enumerable.Range(0, batch.Count),
            new ParallelOptions { MaxDegreeOfParallelism = _maxConcurrency, CancellationToken = ct },
            async (index, token) =>
            {
                var query = batch[index];
                try
                {
                    var result = await QueryAsync(query, window, token).ConfigureAwait(false);
                    results[index] = result is not null && result.Key == query.Key
                        ? result
                        : new SignalResult.Failure(query.Key, "invalid_result",
                            "The plugin returned no result or a result for a different signal.");
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception) when (exception is TimeoutException or OperationCanceledException)
                {
                    results[index] = new SignalResult.Failure(query.Key, "timeout", "The query timed out.");
                }
                catch (Exception)
                {
                    results[index] = new SignalResult.Failure(query.Key, "query_failed", "The query failed.");
                }
            });

        // Keep worker lifetimes observable even when a plugin ignores cancellation.
        // The engine bounds its wait and retains resources until this task finishes.
        await processing.ConfigureAwait(false);
        return results;
    }

    /// <summary>Executes one query, preserving its full key and observing cancellation throughout response reads.</summary>
    /// <param name="query">The validated sink query and full signal identity.</param>
    /// <param name="window">The scheduled query-window anchor and interval.</param>
    /// <param name="ct">Cancels this query's outbound work.</param>
    /// <returns>A keyed observation, no-data outcome or redacted failure.</returns>
    protected abstract Task<SignalResult> QueryAsync(SignalQuery query, QueryWindow window, CancellationToken ct);

    /// <summary>Releases no resources by default; override to dispose only resources owned by the sink.</summary>
    public virtual ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
