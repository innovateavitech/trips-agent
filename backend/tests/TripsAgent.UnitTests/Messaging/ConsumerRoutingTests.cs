using FluentAssertions;
using TripsAgent.Application.Messaging;
using TripsAgent.Domain.Documents;
using TripsAgent.Infrastructure.Documents;
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
    public void Documents_are_rendered_from_the_documents_queue_and_nowhere_else()
    {
        MessagingRegistration.ConsumerRoutes
            .Where(route => route.Consumer == typeof(DocumentRenderConsumer))
            .Should().ContainSingle()
            .Which.Queue.Should().Be(MessageQueue.DocumentsRender);
    }

    [Fact]
    public void No_consumer_is_routed_twice()
    {
        MessagingRegistration.ConsumerRoutes
            .GroupBy(route => route.Consumer)
            .Should().OnlyContain(group => group.Count() == 1);
    }

    // ------------------------------------------------------------------ retries (#45, #46)

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(10)]
    public void An_email_is_dead_lettered_exactly_when_its_row_is_marked_failed_whatever_the_setting(int configured)
    {
        // Five attempts: the first delivery and four retries. Fewer, and the broker would give up on
        // a row that still says "queued"; more, and it would keep delivering a row already failed.
        MessagingRegistration.RetryLimitFor(MessageQueue.NotificationsEmail, new MessageRetryOptions { RetryLimit = configured })
            .Should().Be(NotificationDispatcher.MaxAttempts - 1);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10)]
    public void A_document_is_dead_lettered_exactly_when_its_row_is_marked_failed(int configured)
    {
        MessagingRegistration.RetryLimitFor(MessageQueue.DocumentsRender, new MessageRetryOptions { RetryLimit = configured })
            .Should().Be(GeneratedDocument.MaxRenderAttempts - 1);
    }

    [Fact]
    public void Every_other_queue_follows_the_configured_limit()
    {
        MessagingRegistration.RetryLimitFor(MessageQueue.BookingSaga, new MessageRetryOptions { RetryLimit = 7 })
            .Should().Be(7);
    }
}
