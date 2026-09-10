using MassTransit;
using TripsAgent.Application.Messaging;

namespace TripsAgent.Infrastructure.Messaging;

/// <summary>
/// The only class in the codebase that implements <see cref="IMessageBus"/> over MassTransit.
///
/// It is deliberately thin. All it does is translate our two verbs — publish an event, send a
/// command to a named queue — into MassTransit's. When the transport changes, this file is
/// replaced and nothing else moves.
/// </summary>
/// <param name="publishEndpoint">MassTransit's fan-out endpoint.</param>
/// <param name="sendEndpointProvider">Resolves a queue name to something we can send to.</param>
public sealed class MassTransitMessageBus(
    IPublishEndpoint publishEndpoint,
    ISendEndpointProvider sendEndpointProvider) : IMessageBus
{
    /// <inheritdoc />
    public Task PublishAsync<TEvent>(TEvent message, CancellationToken cancellationToken = default)
        where TEvent : class
    {
        ArgumentNullException.ThrowIfNull(message);

        return publishEndpoint.Publish(message, cancellationToken);
    }

    /// <inheritdoc />
    public async Task SendAsync<TCommand>(
        TCommand command,
        MessageQueue queue,
        CancellationToken cancellationToken = default)
        where TCommand : class
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(queue);

        // "queue:name" is MassTransit's short-form address. It resolves against whichever
        // transport is configured, which is why this class never spells out amqp:// anywhere.
        var endpoint = await sendEndpointProvider.GetSendEndpoint(new Uri($"queue:{queue.Name}"));

        await endpoint.Send(command, cancellationToken);
    }
}
