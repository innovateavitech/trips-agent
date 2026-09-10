namespace TripsAgent.Application.Messaging;

/// <summary>
/// How the rest of the application talks to the message broker.
///
/// This interface exists so that RabbitMQ is a decision we can change. Cloud is not chosen yet
/// (see CLAUDE.md), and moving to SQS or Azure Service Bus later should mean writing one new class
/// in Infrastructure — not editing every handler that ever published an event.
///
/// Nothing in this file mentions MassTransit or RabbitMQ, and nothing ever should. That is the
/// whole point of a port: Application says <em>what</em> happens, Infrastructure decides
/// <em>how</em>. <c>LayeringTests</c> fails the build if that ever reverses.
/// </summary>
public interface IMessageBus
{
    /// <summary>
    /// Announces that something happened, to whoever cares. Zero subscribers is normal and is not
    /// an error — publishing <c>OrderPlaced</c> does not require anything to be listening.
    ///
    /// Use this for events: past tense, a statement of fact, no expectation of a reply.
    /// </summary>
    /// <typeparam name="TEvent">The event type. Its name is what the broker routes on.</typeparam>
    /// <param name="message">The event. Must be a class or record — a struct will not serialise.</param>
    /// <param name="cancellationToken">Cancels the publish, not the work the event triggers.</param>
    public Task PublishAsync<TEvent>(TEvent message, CancellationToken cancellationToken = default)
        where TEvent : class;

    /// <summary>
    /// Asks one specific queue to do one specific thing.
    ///
    /// Use this for commands: imperative, addressed to a single consumer. If nothing is consuming
    /// that queue the message sits there until something does — which is the behaviour you want
    /// for work that must eventually happen.
    /// </summary>
    /// <typeparam name="TCommand">The command type.</typeparam>
    /// <param name="command">The command. Must be a class or record.</param>
    /// <param name="queue">Which queue does the work. Pick one from <see cref="MessageQueue"/>.</param>
    /// <param name="cancellationToken">Cancels the send, not the work the command triggers.</param>
    public Task SendAsync<TCommand>(
        TCommand command,
        MessageQueue queue,
        CancellationToken cancellationToken = default)
        where TCommand : class;

    /// <summary>
    /// Announces that something happened, exactly like <see cref="PublishAsync{TEvent}"/> — but
    /// takes the event's type as a runtime value rather than a compile-time generic parameter.
    /// </summary>
    /// <remarks>
    /// Exists for <c>OutboxDispatcher</c> (issue #30), which reads an event back from storage as
    /// a <c>string</c> type name plus a JSON payload and has no compile-time type to hand the
    /// generic overload. Application code with a real event in hand should use
    /// <see cref="PublishAsync{TEvent}"/> instead — it is the same publish, with the compiler
    /// checking that <paramref name="message"/> actually is a <paramref name="messageType"/>.
    /// </remarks>
    /// <param name="message">The event, already an instance of <paramref name="messageType"/>.</param>
    /// <param name="messageType">The event's runtime type. What the broker routes on.</param>
    /// <param name="messageId">
    /// Sets the envelope's <c>MessageId</c> explicitly instead of letting the transport assign a
    /// random one. <c>OutboxDispatcher</c> passes the outbox row's own id, so a consumer reading
    /// <c>ConsumeContext.MessageId</c> has a stable value to hand <see cref="IInboxDeduplicator"/>
    /// that survives redelivery unchanged. Leave null for an ordinary publish with no outbox
    /// behind it.
    /// </param>
    /// <param name="cancellationToken">Cancels the publish, not the work the event triggers.</param>
    public Task PublishAsync(
        object message,
        Type messageType,
        Guid? messageId = null,
        CancellationToken cancellationToken = default);
}
