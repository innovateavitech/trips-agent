using System.Text.Json;
using MassTransit;
using TripsAgent.Application.Messaging;

namespace TripsAgent.Infrastructure.Messaging;

/// <summary>
/// <see cref="IOutboxPublisher"/> over MassTransit: turns an outbox row back into the event it was,
/// and publishes it to RabbitMQ.
/// </summary>
/// <remarks>
/// <para>
/// The payload is deserialised to its original .NET type before publishing because MassTransit
/// routes by type: a consumer of <c>OrderPlaced</c> receives it because it <em>is</em> an
/// <c>OrderPlaced</c>, not a blob of JSON with a label on it.
/// </para>
/// <para>
/// The outbox row's id becomes MassTransit's <c>MessageId</c>, on every attempt. That is the id a
/// consumer hands to <see cref="IInbox"/>, so a redelivery after a crash is recognised as the same
/// message rather than processed as a new one.
/// </para>
/// </remarks>
public sealed class MassTransitOutboxPublisher(IPublishEndpoint publishEndpoint, OutboxOptions options)
    : IOutboxPublisher
{
    /// <summary>Header carrying the trace id of the request that produced the message.</summary>
    public const string CorrelationIdHeader = "X-Correlation-Id";

    /// <summary>Header carrying the agency the message concerns.</summary>
    public const string AgencyIdHeader = "X-Agency-Id";

    /// <inheritdoc />
    public async Task PublishAsync(OutboxEnvelope envelope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        var messageType = Type.GetType(envelope.MessageType, throwOnError: false)
            ?? throw new InvalidOperationException(
                $"Outbox message {envelope.MessageId} has type '{envelope.MessageType}', which this build does "
                + "not contain. Was the event class renamed or moved while messages of it were still waiting?");

        var message = JsonSerializer.Deserialize(envelope.Payload, messageType, OutboxMessage.JsonOptions)
            ?? throw new InvalidOperationException($"Outbox message {envelope.MessageId} has an empty payload.");

        // The dispatcher holds a row lock while this runs, so it must not wait forever on a broker
        // that has gone away. A timeout counts as a failed attempt and is retried with backoff.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.PublishTimeout);

        await publishEndpoint.Publish(
            message,
            messageType,
            context =>
            {
                context.MessageId = envelope.MessageId;

                if (envelope.CorrelationId is not null)
                {
                    context.Headers.Set(CorrelationIdHeader, envelope.CorrelationId);
                }

                if (envelope.AgencyId is { } agencyId)
                {
                    context.Headers.Set(AgencyIdHeader, agencyId);
                }
            },
            timeout.Token);
    }
}
