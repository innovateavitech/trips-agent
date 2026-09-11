using MassTransit;
using TripsAgent.Application.Notifications;

namespace TripsAgent.Infrastructure.Notifications;

/// <summary>A notification failed and the broker should redeliver it, or dead-letter it.</summary>
public sealed class NotificationDeliveryException : Exception
{
    public NotificationDeliveryException(string message)
        : base(message)
    {
    }

    public NotificationDeliveryException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public NotificationDeliveryException()
        : base("The notification could not be delivered.")
    {
    }
}

/// <summary>
/// Sends the notification a <see cref="NotificationQueued"/> message points at. Bound to
/// <c>notifications.email</c> in <c>MessagingRegistration</c>.
/// </summary>
/// <remarks>
/// A thin adapter on purpose: <see cref="NotificationDispatcher"/> holds the logic and is tested
/// against real PostgreSQL without a broker. This only translates its outcome into the one thing
/// MassTransit understands — throw to be retried, return to be done with.
/// </remarks>
public sealed class NotificationQueuedConsumer(NotificationDispatcher dispatcher) : IConsumer<NotificationQueued>
{
    public async Task Consume(ConsumeContext<NotificationQueued> context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var id = context.Message.NotificationId;
        var outcome = await dispatcher.DispatchAsync(id, context.CancellationToken);

        switch (outcome)
        {
            case NotificationDispatchOutcome.RetryLater:
                throw new NotificationDeliveryException(
                    $"Notification {id} failed and will be retried. Its last error is on the row.");

            case NotificationDispatchOutcome.GaveUp:
                // Thrown after the row is already marked failed, so the retry that follows finds
                // nothing to do and the message ends up in notifications.email_error — where a
                // person looking at the broker expects a dead letter to be.
                throw new NotificationDeliveryException(
                    $"Notification {id} has been marked failed after {NotificationDispatcher.MaxAttempts} attempts, "
                    + "or could never be sent. See notifications.notifications for the reason.");

            default:
                return;
        }
    }
}
