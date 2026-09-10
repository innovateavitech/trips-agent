namespace TripsAgent.Domain.Messaging;

/// <summary>
/// A record that one consumer has already processed one message — the other half of the outbox
/// pattern (issue #30).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="OutboxMessage"/> guarantees a message goes out <em>at least</em> once, never zero
/// times — which means a consumer must expect to see it more than once: RabbitMQ redelivers an
/// unacknowledged message, and the dispatcher itself republishes anything it cannot confirm was
/// sent. This row is how a consumer tells "I have already done this" from "this is new", so
/// redelivery does not repeat work that must only happen once — charging a wallet twice for the
/// same top-up, say.
/// </para>
/// <para>
/// The key is <em>(message, consumer)</em>, not just the message. Several consumers legitimately
/// see the same published event — that is what publish/subscribe means — so a row here says one
/// specific consumer has handled one specific message, and says nothing about any other consumer.
/// </para>
/// <para>
/// Not an <see cref="TripsAgent.Domain.Common.Entity"/>: its identity is the composite key
/// below, not a single generated id, and it is never looked up by anything else.
/// </para>
/// </remarks>
public sealed class InboxMessage
{
    /// <summary>Longest a consumer's name may be. It is a type name, not free text.</summary>
    public const int MaxConsumerNameLength = 200;

    /// <summary>EF Core materialises through this, using its own reflection — not the API below.</summary>
    private InboxMessage()
    {
    }

    /// <summary>
    /// The message that was processed. This is the <see cref="OutboxMessage.Id"/> of the row
    /// that produced it — the dispatcher publishes the message with that id as MassTransit's
    /// <c>MessageId</c>, so a consumer reading it off the envelope needs no extra plumbing to
    /// find this value.
    /// </summary>
    public required Guid MessageId { get; init; }

    /// <summary>
    /// Which consumer processed it. The consumer's own type name is the natural choice — stable,
    /// already unique per queue, and readable in a support query without a lookup table.
    /// </summary>
    public required string ConsumerName { get; init; }

    /// <summary>When this consumer finished with this message.</summary>
    public required DateTimeOffset ProcessedAt { get; init; }

    /// <summary>Builds a row recording that <paramref name="consumerName"/> has handled <paramref name="messageId"/>.</summary>
    public static InboxMessage Create(Guid messageId, string consumerName, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(consumerName);

        if (messageId == Guid.Empty)
        {
            throw new ArgumentException("A message id of all zeros cannot be a real message.", nameof(messageId));
        }

        if (consumerName.Length > MaxConsumerNameLength)
        {
            throw new ArgumentException(
                $"'{consumerName}' is {consumerName.Length} characters; consumer names are capped at " +
                $"{MaxConsumerNameLength} because this is a type name, not free text.",
                nameof(consumerName));
        }

        return new InboxMessage
        {
            MessageId = messageId,
            ConsumerName = consumerName,
            ProcessedAt = now,
        };
    }
}
