namespace TripsAgent.Application.Messaging;

/// <summary>
/// Hands one message from the outbox to the message broker. The seam between the outbox and
/// whatever transport carries messages.
/// </summary>
/// <remarks>
/// <para>
/// Implemented over MassTransit by <c>MassTransitOutboxPublisher</c> in Infrastructure. Moving to SQS
/// or Service Bus means writing one more implementation of this interface — the outbox, the
/// dispatcher and every handler stay exactly as they are.
/// </para>
/// <para>An implementation must:</para>
/// <list type="bullet">
///   <item>Return only once the broker has accepted the message, and throw if it has not. A throw
///   is not a crisis — the dispatcher records it and tries again later, with backoff.</item>
///   <item>Publish with <see cref="OutboxEnvelope.MessageId"/> as the broker's message id, every
///   time. That id is what <see cref="IInbox"/> deduplicates on, so a new id per attempt would turn
///   a harmless redelivery into a double-processed payment.</item>
///   <item>Apply its own timeout. The dispatcher holds a row lock while it waits.</item>
/// </list>
/// </remarks>
public interface IOutboxPublisher
{
    /// <summary>Publishes <paramref name="envelope"/>, returning once the broker has it.</summary>
    public Task PublishAsync(OutboxEnvelope envelope, CancellationToken cancellationToken = default);
}

/// <summary>One message on its way out of the outbox.</summary>
/// <param name="MessageId">Identical on every delivery attempt of this message. Consumers deduplicate on it.</param>
/// <param name="MessageType">
/// The .NET type the payload was serialised from, as <c>"Namespace.Type, Assembly"</c> —
/// <see cref="Type.GetType(string)"/> resolves it.
/// </param>
/// <param name="Payload">The message as camelCase JSON.</param>
/// <param name="OccurredAt">When the change was committed — not when it is being published.</param>
/// <param name="CorrelationId">The trace id of the request that caused it, when there was one.</param>
/// <param name="AgencyId">The agency the message concerns, when there is one.</param>
public sealed record OutboxEnvelope(
    Guid MessageId,
    string MessageType,
    string Payload,
    DateTimeOffset OccurredAt,
    string? CorrelationId,
    Guid? AgencyId);
