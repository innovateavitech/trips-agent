using TripsAgent.Application.Messaging;

namespace TripsAgent.IntegrationTests.Messaging;

/// <summary>
/// Records every publish instead of sending anything to a broker.
/// </summary>
/// <remarks>
/// No RabbitMQ Testcontainer runs anywhere in this project — matching
/// <c>MessagingRegistrationTests</c>' own boundary in the unit test project, "anything that needs
/// a real RabbitMQ belongs in TripsAgent.IntegrationTests" was written before the outbox existed
/// and does not mean <em>this</em> broker has to be real. What these tests prove is the outbox's
/// own mechanism — a message committed with a state change survives a restart and eventually
/// reaches <see cref="IMessageBus"/> — and that guarantee has nothing to do with whether
/// MassTransit's own RabbitMQ transport is reliable, which is MassTransit's test suite to prove,
/// not this project's.
/// </remarks>
public sealed class RecordingMessageBus : IMessageBus
{
    private readonly List<PublishedMessage> published = [];

    /// <summary>Every message this bus would have published, in the order it happened.</summary>
    public IReadOnlyList<PublishedMessage> Published => published;

    /// <summary>
    /// When set, every call throws this — for a test proving <c>OutboxDispatcher</c>'s own
    /// failure handling rather than a real broker's.
    /// </summary>
    public Exception? FailWith { get; set; }

    public Task PublishAsync<TEvent>(TEvent message, CancellationToken cancellationToken = default)
        where TEvent : class
    {
        ArgumentNullException.ThrowIfNull(message);

        if (FailWith is { } exception)
        {
            throw exception;
        }

        published.Add(new PublishedMessage(message, typeof(TEvent), null));
        return Task.CompletedTask;
    }

    public Task SendAsync<TCommand>(
        TCommand command,
        MessageQueue queue,
        CancellationToken cancellationToken = default)
        where TCommand : class
        => throw new NotSupportedException("The outbox dispatcher only publishes events, never sends commands.");

    public Task PublishAsync(
        object message,
        Type messageType,
        Guid? messageId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(messageType);

        if (FailWith is { } exception)
        {
            throw exception;
        }

        published.Add(new PublishedMessage(message, messageType, messageId));
        return Task.CompletedTask;
    }
}

/// <summary>One call to <see cref="RecordingMessageBus"/>.</summary>
public sealed record PublishedMessage(object Message, Type MessageType, Guid? MessageId);
