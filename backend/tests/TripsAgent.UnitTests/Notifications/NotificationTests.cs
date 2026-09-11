using FluentAssertions;
using TripsAgent.Domain.Notifications;

namespace TripsAgent.UnitTests.Notifications;

/// <summary>The notification's own state rules, which the dispatcher relies on.</summary>
public class NotificationTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 11, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_new_notification_is_queued_with_no_attempts()
    {
        var notification = New();

        notification.Status.Should().Be(NotificationStatus.Queued);
        notification.Attempts.Should().Be(0);
        notification.IsFinished.Should().BeFalse();
    }

    [Fact]
    public void Sending_records_the_version_and_the_providers_id()
    {
        var notification = New();

        notification.MarkSent(templateVersion: 2, "<abc@mail>", Now);

        notification.Status.Should().Be(NotificationStatus.Sent);
        notification.TemplateVersion.Should().Be(2);
        notification.ProviderMessageId.Should().Be("<abc@mail>");
        notification.Attempts.Should().Be(1);
    }

    [Fact]
    public void A_failure_that_will_be_retried_leaves_it_queued_with_the_error()
    {
        var notification = New();

        notification.RecordFailure("relay unreachable", giveUp: false);

        notification.Status.Should().Be(NotificationStatus.Queued);
        notification.Attempts.Should().Be(1);
        notification.LastError.Should().Be("relay unreachable");
    }

    [Fact]
    public void Giving_up_marks_it_failed()
    {
        var notification = New();

        notification.RecordFailure("relay unreachable", giveUp: true);

        notification.Status.Should().Be(NotificationStatus.Failed);
        notification.IsFinished.Should().BeTrue();
    }

    [Fact]
    public void An_error_longer_than_the_column_is_trimmed()
    {
        var notification = New();

        notification.RecordFailure(new string('x', Notification.MaxErrorLength + 50), giveUp: false);

        notification.LastError.Should().HaveLength(Notification.MaxErrorLength);
    }

    [Fact]
    public void A_bounce_is_terminal()
    {
        var notification = New();

        notification.MarkBounced("550 no such user", Now);

        notification.Status.Should().Be(NotificationStatus.Bounced);
        notification.IsFinished.Should().BeTrue();
    }

    [Fact]
    public void Only_a_sent_notification_can_become_delivered()
    {
        var bounced = New();
        bounced.MarkBounced("550", Now);
        bounced.MarkDelivered(Now);

        var sent = New();
        sent.MarkSent(1, null, Now);
        sent.MarkDelivered(Now);

        bounced.Status.Should().Be(NotificationStatus.Bounced, "a late delivery report does not undo a bounce");
        sent.Status.Should().Be(NotificationStatus.Delivered);
    }

    [Fact]
    public void A_notification_belongs_to_an_agency()
    {
        var act = () => Notification.Queue(
            Guid.Empty, "kyb.approved", NotificationChannel.Email, NotificationTemplate.DefaultLocale,
            NotificationRecipientType.AgencyUser, "a@b.test", "Ada", "{}", "key");

        act.Should().Throw<ArgumentException>();
    }

    private static Notification New() =>
        Notification.Queue(
            Guid.CreateVersion7(),
            "kyb.approved",
            NotificationChannel.Email,
            NotificationTemplate.DefaultLocale,
            NotificationRecipientType.AgencyUser,
            "owner@acme.test",
            "Ada",
            "{}",
            "kyb.approved:1");
}
