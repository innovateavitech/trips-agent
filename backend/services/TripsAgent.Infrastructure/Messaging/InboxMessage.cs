namespace TripsAgent.Infrastructure.Messaging;

/// <summary>
/// A record that <see cref="Consumer"/> has finished processing message <see cref="MessageId"/>.
/// Written by <see cref="EfInbox"/> in the same transaction as the consumer's work.
/// </summary>
public sealed class InboxMessage
{
    /// <summary>Records that <paramref name="consumer"/> processed <paramref name="messageId"/>.</summary>
    public InboxMessage(Guid messageId, string consumer, DateTimeOffset processedAt)
    {
        MessageId = messageId;
        Consumer = consumer;
        ProcessedAt = processedAt;
    }

    /// <summary>The id the message was published with.</summary>
    public Guid MessageId { get; private set; }

    /// <summary>Which consumer processed it. A message may be processed once by each of several.</summary>
    public string Consumer { get; private set; }

    /// <summary>When the consumer's transaction committed.</summary>
    public DateTimeOffset ProcessedAt { get; private set; }
}
