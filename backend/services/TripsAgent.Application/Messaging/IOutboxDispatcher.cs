namespace TripsAgent.Application.Messaging;

/// <summary>
/// Publishes whatever is waiting in the outbox. Run on a clock by
/// <c>OutboxDispatcherHostedService</c> in the Worker — see issue #30.
/// </summary>
public interface IOutboxDispatcher
{
    /// <summary>
    /// Publishes up to one batch of pending messages, marking each dispatched as it succeeds.
    /// A message that fails to publish is left pending with its attempt counted and a backoff
    /// applied, so the next tick's query skips straight past it rather than failing on it again
    /// immediately.
    /// </summary>
    public Task<OutboxDispatchResult> DispatchPendingAsync(CancellationToken cancellationToken = default);
}

/// <summary>What one dispatch pass did.</summary>
/// <param name="Dispatched">Messages successfully published this pass.</param>
/// <param name="Failed">Messages that failed to publish this pass and were left pending.</param>
/// <param name="PendingBacklog">
/// Total messages still waiting, dispatched or not, measured after this pass. What
/// <c>OutboxOptions.BacklogAlertThreshold</c> is compared against.
/// </param>
public sealed record OutboxDispatchResult(int Dispatched, int Failed, int PendingBacklog);
