namespace SelfHostedEvaluators.Plugins.Abstractions;

/// <summary>Queries one configured sink instance using host-owned authenticated transports.</summary>
/// <remarks>Dispose only client-created resources, not the supplied HTTP clients, auth context or limiter.</remarks>
public interface ISinkClient : IAsyncDisposable
{
    /// <summary>Queries a scheduled batch and returns one fully keyed outcome for each input.</summary>
    /// <param name="batch">Queries whose full model/entity/name identities must be preserved, including duplicates.</param>
    /// <param name="window">The scheduled tick and interval used to determine query bounds.</param>
    /// <param name="ct">Cancels active and queued queries; caller cancellation must propagate.</param>
    /// <returns>Observations, no-data outcomes or failures; an empty batch produces an empty list.</returns>
    Task<IReadOnlyList<SignalResult>> QueryBatchAsync(
        IReadOnlyList<SignalQuery> batch, QueryWindow window, CancellationToken ct);
}
