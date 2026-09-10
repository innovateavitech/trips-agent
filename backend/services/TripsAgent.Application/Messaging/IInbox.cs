namespace TripsAgent.Application.Messaging;

/// <summary>
/// Makes a message consumer idempotent: for a given message, the work runs and commits at most
/// once per consumer, however many times the broker delivers it.
/// </summary>
/// <remarks>
/// <para>
/// The outbox guarantees <em>at-least-once</em> delivery, which is a polite way of saying
/// "sometimes twice". A Worker that publishes a message and dies before recording that it did
/// will publish it again after restart. Without this, the second copy would credit a wallet twice.
/// </para>
/// <para>
/// How it works: the id of every processed message is written to <c>platform.inbox_messages</c>
/// <b>in the same transaction</b> as the work itself. A duplicate either finds that row and skips,
/// or — if two copies arrive at the same instant — loses the race on the primary key, and its
/// whole transaction, work included, is rolled back.
/// </para>
/// <para>Two rules for the <c>work</c> delegate:</para>
/// <list type="number">
///   <item>Stage database changes but do not save them. <see cref="ProcessOnceAsync"/> saves, so
///   the work and the inbox row commit together or not at all.</item>
///   <item>Anything outside the database — an email, a supplier call — is not rolled back and can
///   happen twice. Pass the message id to that system as its idempotency key, or trigger it from
///   a separate message.</item>
/// </list>
/// </remarks>
public interface IInbox
{
    /// <summary>Runs <paramref name="work"/> unless this consumer has already processed this message.</summary>
    /// <param name="messageId">The id the message was published with — <see cref="OutboxEnvelope.MessageId"/>.</param>
    /// <param name="consumer">
    /// A stable name for the consumer, e.g. <c>"wallet.credit-on-top-up"</c>. One published event
    /// can have several consumers and each must process it once, so a message id is recorded once
    /// per consumer name. Renaming a consumer makes it forget everything it has seen.
    /// </param>
    /// <param name="work">The handler's work. Stages changes; does not save.</param>
    /// <param name="cancellationToken">Cancels the work and the save.</param>
    /// <returns>
    /// <c>true</c> if the work ran and committed; <c>false</c> if the message was a duplicate and
    /// nothing was done.
    /// </returns>
    public Task<bool> ProcessOnceAsync(
        Guid messageId,
        string consumer,
        Func<CancellationToken, Task> work,
        CancellationToken cancellationToken = default);
}
