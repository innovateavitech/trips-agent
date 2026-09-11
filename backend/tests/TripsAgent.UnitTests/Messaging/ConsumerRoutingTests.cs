using FluentAssertions;
using TripsAgent.Application.Messaging;
using TripsAgent.Infrastructure.Messaging;
using TripsAgent.Infrastructure.Notifications;

namespace TripsAgent.UnitTests.Messaging;

/// <summary>
/// Each consumer listens on exactly one queue. Bound to every queue, one published message would be
/// consumed once per queue — for notifications, one email per queue.
/// </summary>
public class ConsumerRoutingTests
{
    [Fact]
    public void Notifications_are_consumed_from_the_email_queue_and_nowhere_else()
    {
        MessagingRegistration.ConsumerRoutes
            .Where(route => route.Consumer == typeof(NotificationQueuedConsumer))
            .Should().ContainSingle()
            .Which.Queue.Should().Be(MessageQueue.NotificationsEmail);
    }

    [Fact]
    public void No_consumer_is_routed_twice()
    {
        MessagingRegistration.ConsumerRoutes
            .GroupBy(route => route.Consumer)
            .Should().OnlyContain(group => group.Count() == 1);
    }
}
