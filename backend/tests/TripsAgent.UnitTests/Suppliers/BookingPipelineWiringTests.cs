using FluentAssertions;
using Hangfire;
using Hangfire.Common;
using TripsAgent.Application.Messaging;
using TripsAgent.Application.Notifications;
using TripsAgent.Application.Suppliers;
using TripsAgent.Domain.Common;
using TripsAgent.Domain.Notifications;
using TripsAgent.Domain.Suppliers;
using TripsAgent.Infrastructure.Messaging;
using TripsAgent.Infrastructure.Suppliers;

namespace TripsAgent.UnitTests.Suppliers;

/// <summary>
/// How the booking pipeline is wired into the Worker (#36, #37, #38), and the decisions in it that
/// are pure enough to pin down without a database.
/// </summary>
public class BookingPipelineWiringTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 11, 10, 0, 0, TimeSpan.Zero);

    // --------------------------------------------------------------------------- the schedules

    [Fact]
    public void The_status_poller_runs_every_thirty_seconds_in_UTC()
    {
        var jobs = new RecordingRecurringJobManager();

        SupplierBookingStatusPollSchedule.Register(jobs);

        var registration = jobs.Registrations.Should().ContainSingle().Subject;
        registration.Id.Should().Be(SupplierBookingStatusPollSchedule.JobId);
        registration.Cron.Should().Be("*/30 * * * * *", "six fields: the first is seconds");
        registration.Options.TimeZone.Should().Be(TimeZoneInfo.Utc);
        registration.Job.Type.Should().Be<SupplierBookingStatusPollJob>();
    }

    [Fact]
    public void The_time_limit_monitor_runs_every_minute_in_UTC()
    {
        var jobs = new RecordingRecurringJobManager();

        TicketTimeLimitMonitorSchedule.Register(jobs);

        var registration = jobs.Registrations.Should().ContainSingle().Subject;
        registration.Cron.Should().Be("* * * * *");
        registration.Options.TimeZone.Should().Be(TimeZoneInfo.Utc);
        registration.Job.Type.Should().Be<TicketTimeLimitMonitorJob>();
    }

    [Fact]
    public void Neither_job_is_retried_by_Hangfire_because_the_next_run_is_moments_away()
    {
        foreach (var method in new[]
                 {
                     typeof(SupplierBookingStatusPollJob).GetMethod(nameof(SupplierBookingStatusPollJob.RunAsync))!,
                     typeof(TicketTimeLimitMonitorJob).GetMethod(nameof(TicketTimeLimitMonitorJob.RunAsync))!,
                 })
        {
            method.GetCustomAttributes(typeof(AutomaticRetryAttribute), inherit: false)
                .Cast<AutomaticRetryAttribute>()
                .Should().ContainSingle()
                .Which.Attempts.Should().Be(0);
        }
    }

    [Fact]
    public void Issue_messages_are_consumed_from_the_booking_saga_queue_and_nowhere_else()
    {
        MessagingRegistration.ConsumerRoutes
            .Where(route => route.Consumer == typeof(IssueSupplierTicketConsumer))
            .Should().ContainSingle()
            .Which.Queue.Should().Be(MessageQueue.BookingSaga);
    }

    // ------------------------------------------------------------ what an issue answer means

    [Fact]
    public void An_issue_answer_with_a_ticket_finishes_the_booking()
    {
        var booking = IssuingBooking();

        TicketIssuanceService.Apply(booking, Answer(SupplierIssueOutcome.Accepted, SupplierBookingStatus.Ticketed, 2), Now);

        booking.Status.Should().Be(SupplierBookingStatus.Ticketed);
    }

    [Fact]
    public void An_accepted_answer_with_no_status_is_treated_as_pending()
    {
        var booking = IssuingBooking();

        TicketIssuanceService.Apply(booking, Answer(SupplierIssueOutcome.Accepted, status: null, code: null), Now);

        booking.Status.Should().Be(SupplierBookingStatus.TicketPending);
        booking.NextPollAt.Should().Be(Now.AddSeconds(30));
    }

    [Theory]
    [InlineData(SupplierIssueOutcome.Accepted, SupplierBookingStatus.Failed, 0)]
    [InlineData(SupplierIssueOutcome.Rejected, null, null)]
    public void An_answer_the_reversal_rules_want_confirmed_goes_straight_to_the_status_query(
        SupplierIssueOutcome outcome, SupplierBookingStatus? status, int? code)
    {
        // HTTP 200 with 0, 1 or 11, and HTTP 400: both mean "reverse" only on the evidence of a status
        // query, which the poller records. Nothing is reversed on the issue answer alone.
        var booking = IssuingBooking();

        TicketIssuanceService.Apply(booking, Answer(outcome, status, code, http: outcome == SupplierIssueOutcome.Rejected ? 400 : 200), Now);

        booking.Status.Should().Be(SupplierBookingStatus.IssueOutcomeUnknown);
        booking.NextPollAt.Should().Be(Now, "polled at once");
        booking.PullDomainEvents().Should().BeEmpty();
    }

    [Fact]
    public void An_unknown_outcome_waits_thirty_seconds_for_the_first_poll()
    {
        var booking = IssuingBooking();

        TicketIssuanceService.Apply(booking, Answer(SupplierIssueOutcome.Unknown, status: null, code: null, http: null), Now);

        booking.Status.Should().Be(SupplierBookingStatus.IssueOutcomeUnknown);
        booking.NextPollAt.Should().Be(Now.AddSeconds(30));
        booking.FailureReason.Should().Contain("ADR-0003");
    }

    [Fact]
    public void The_issue_lock_is_named_for_the_order_line()
    {
        var line = Guid.CreateVersion7();

        TicketIssuanceService.LockKey(line).Should().Be($"supplier:issue:{line:N}");
    }

    // --------------------------------------------------------------------- the agent's emails

    [Theory]
    [InlineData(NotificationTemplateCatalog.BookingTimeLimitWarning)]
    [InlineData(NotificationTemplateCatalog.BookingExpired)]
    public void The_time_limit_emails_exist_and_go_to_the_agent_not_the_traveller(string key)
    {
        var template = NotificationTemplateCatalog.Find(key, NotificationChannel.Email);

        template.Should().NotBeNull();
        template!.Audience.Should().Be(NotificationAudience.AgencyStaff);
        template.Tokens.Should().Contain(["bookingReference", "itinerarySummary", "deadline"]);
    }

    [Fact]
    public void A_deadline_is_written_on_the_agencys_own_clock()
    {
        TicketTimeLimitMonitor.FormatDeadline(Now, "Africa/Lagos").Should().Be("11 Sep 2026, 11:00 (Africa/Lagos)");
        TicketTimeLimitMonitor.FormatDeadline(Now, "Not/AZone").Should().Be("11 Sep 2026, 10:00 (UTC)");
    }

    // ---------------------------------------------------------------------------- building

    private static SupplierBooking IssuingBooking()
    {
        var booking = SupplierBooking.Create(
            Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), null, SupplierProductType.Flight,
            "International", "Flight", "session-1", "NGN", $"order-line:{Guid.CreateVersion7()}");

        booking.RecordPriceConfirmation(
            [new PriceConfirmationLine("36516|12QFDT", new Money(1_000_00), new Money(1_000_00), Now.AddMinutes(45), "ab", "ab")],
            Now.AddMinutes(-2));
        booking.BeginIssue(Now.AddMinutes(-1));
        booking.PullDomainEvents();

        return booking;
    }

    private static SupplierIssueResult Answer(SupplierIssueOutcome outcome, SupplierBookingStatus? status, int? code, int? http = 200) =>
        new(outcome, http, code, status, Pnr: "RE6MIK", Message: "answer");

    private sealed class RecordingRecurringJobManager : IRecurringJobManager
    {
        public List<(string Id, Job Job, string Cron, RecurringJobOptions Options)> Registrations { get; } = [];

        public void AddOrUpdate(string recurringJobId, Job job, string cronExpression, RecurringJobOptions options) =>
            Registrations.Add((recurringJobId, job, cronExpression, options));

        public void Trigger(string recurringJobId) => throw new NotSupportedException();

        public void RemoveIfExists(string recurringJobId) => throw new NotSupportedException();
    }
}
