namespace TripsAgent.Application.Messaging;

/// <summary>
/// Lets a consumer tell a first delivery of a message from a redelivery of one it already
/// handled — the other half of the outbox pattern (issue #30).
/// </summary>
/// <remarks>
/// An outbox guarantees a message is published <em>at least</em> once, never zero times, which
/// means it can be published more than once — the dispatcher republishes anything it cannot
/// confirm went out, and RabbitMQ itself redelivers an unacknowledged message. A consumer whose
/// work is not naturally safe to repeat — crediting a wallet, issuing a ticket — must call this
/// first and skip its own work when told to.
/// </remarks>
public interface IInboxDeduplicator
{
    /// <summary>
    /// Atomically checks whether <paramref name="consumerName"/> has already processed
    /// <paramref name="messageId"/>, and if not, records that it now has.
    /// </summary>
    /// <remarks>
    /// One database round trip, and the check-and-record happens as a single statement — there
    /// is no window between "is this new" and "mark it seen" for two redeliveries handled
    /// concurrently to both read "new" and both proceed.
    /// </remarks>
    /// <param name="messageId">The message's id, read off the envelope MassTransit hands the consumer.</param>
    /// <param name="consumerName">
    /// Which consumer is asking. Use the consumer's own type name — stable, already unique per
    /// queue, and what a support query would look for.
    /// </param>
    /// <returns>
    /// <see langword="true"/> if this is the first time this consumer has seen this message — go
    /// ahead and process it. <see langword="false"/> if it was already recorded — this is a
    /// redelivery; skip the work.
    /// </returns>
    public Task<bool> TryBeginProcessingAsync(
        Guid messageId,
        string consumerName,
        CancellationToken cancellationToken = default);
}
