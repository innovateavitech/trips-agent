using FluentAssertions;
using TripsAgent.Domain.Common;
using TripsAgent.Domain.Suppliers;

namespace TripsAgent.UnitTests.Suppliers;

/// <summary>
/// What happens to a booking after the issue call: its answer, every status poll, and the ticket time
/// limit (#36, #37, #38). Domain rules, so they hold whichever service drives the booking.
/// </summary>
public class SupplierBookingOutcomeTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 11, 10, 0, 0, TimeSpan.Zero);

    private const string Hash = "3f2a9c";

    // ------------------------------------------------------------------------ the issue answer

    [Fact]
    public void Beginning_to_issue_leaves_a_trail_the_poller_will_follow_if_nothing_else_does()
    {
        // A worker killed mid-call records nothing. The booking was saved as Issuing before the call
        // left, and this is when the poller takes it over — to ask, never to issue again.
        var booking = ConfirmedBooking();
        var version = booking.Version;

        booking.BeginIssue(Now);

        booking.NextPollAt.Should().Be(Now + SupplierPollSchedule.IssueRecoveryDelay);
        booking.IsAwaitingOutcome.Should().BeTrue();
        booking.Version.Should().BeGreaterThan(version, "every change is compared on write");
    }

    [Fact]
    public void A_ticket_in_the_issue_answer_is_final_and_announced()
    {
        var booking = IssuingBooking();

        booking.RecordIssueTicketed("RE6MIK", 2, Now).Should().BeTrue();

        booking.Status.Should().Be(SupplierBookingStatus.Ticketed);
        booking.Pnr.Should().Be("RE6MIK");
        booking.NextPollAt.Should().BeNull("a finished booking is never polled");
        booking.PullDomainEvents().Should().ContainSingle()
            .Which.Should().BeOfType<BookingTicketed>()
            .Which.Should().Match<BookingTicketed>(ticketed =>
                ticketed.SupplierBookingId == booking.Id && ticketed.Pnr == "RE6MIK" && ticketed.SupplierStatusPollId == null);
    }

    [Fact]
    public void A_pending_ticket_is_asked_about_again_in_thirty_seconds()
    {
        var booking = IssuingBooking();

        booking.RecordIssuePending("RE6MIK", 3, Now).Should().BeTrue();

        booking.Status.Should().Be(SupplierBookingStatus.TicketPending);
        booking.NextPollAt.Should().Be(Now.AddSeconds(30));
        booking.PullDomainEvents().Should().BeEmpty("nothing is final yet");
    }

    [Fact]
    public void A_timeout_waits_for_the_first_poll_but_an_answer_to_confirm_is_polled_at_once()
    {
        var timedOut = IssuingBooking();
        timedOut.RecordIssueUnresolved("No response within 45 seconds.", null, null, Now, pollNow: false);

        var refused = IssuingBooking();
        refused.RecordIssueUnresolved("HTTP 400", null, null, Now, pollNow: true);

        timedOut.Status.Should().Be(SupplierBookingStatus.IssueOutcomeUnknown);
        timedOut.NextPollAt.Should().Be(Now.AddSeconds(30), "the supplier may still be working on it");
        refused.NextPollAt.Should().Be(Now, "the supplier answered; the status query can settle it now");
    }

    [Fact]
    public void An_unknown_outcome_can_never_be_issued_again()
    {
        // ADR-0003 as a domain rule: whatever went wrong, the booking never goes back to PriceConfirmed.
        var booking = IssuingBooking();
        booking.RecordIssueUnresolved("Connection reset.", null, null, Now, pollNow: false);

        var again = () => booking.BeginIssue(Now.AddMinutes(1));

        again.Should().Throw<InvalidOperationException>().WithMessage("*0003-never-retry-ticket-issuance*");
    }

    [Fact]
    public void An_issue_answer_arriving_after_the_poller_settled_the_booking_changes_nothing()
    {
        var booking = IssuingBooking();
        booking.RecordStatusPoll(Answered(SupplierBookingStatus.Ticketed, 2), Now);
        booking.PullDomainEvents();

        booking.RecordIssuePending("LATE", 3, Now.AddSeconds(1)).Should().BeFalse();

        booking.Status.Should().Be(SupplierBookingStatus.Ticketed);
        booking.PullDomainEvents().Should().BeEmpty();
    }

    // ------------------------------------------------------------------------------ polling

    [Fact]
    public void Status_2_marks_the_booking_ticketed_and_names_the_poll_that_learned_it()
    {
        var booking = PendingBooking();

        var recorded = booking.RecordStatusPoll(Answered(SupplierBookingStatus.Ticketed, 2), Now);

        booking.Status.Should().Be(SupplierBookingStatus.Ticketed);
        recorded.Poll.ActionTaken.Should().Be(SupplierPollAction.MarkedTicketed);
        recorded.Alert.Should().BeNull();
        booking.PullDomainEvents().OfType<BookingTicketed>().Should().ContainSingle()
            .Which.SupplierStatusPollId.Should().Be(recorded.Poll.Id);
    }

    [Theory]
    [InlineData(0, SupplierBookingStatus.Failed)]
    [InlineData(1, SupplierBookingStatus.Cancelled)]
    [InlineData(11, SupplierBookingStatus.Failed)]
    public void Statuses_0_1_and_11_ask_for_the_payment_back_with_the_poll_as_evidence(int code, SupplierBookingStatus reported)
    {
        var booking = PendingBooking();

        var recorded = booking.RecordStatusPoll(Answered(reported, code), Now);

        booking.Status.Should().Be(reported);
        booking.NextPollAt.Should().BeNull();
        recorded.Poll.ActionTaken.Should().Be(SupplierPollAction.ReversalRequested);
        recorded.Poll.SupplierStatusCode.Should().Be(code);

        var reversal = booking.PullDomainEvents().OfType<PaymentReversalRequired>().Should().ContainSingle().Subject;
        reversal.SupplierStatusPollId.Should().Be(recorded.Poll.Id, "the reversal worker never reverses without this row");
        reversal.SupplierStatusCode.Should().Be(code);
        reversal.OrderLineId.Should().Be(booking.OrderLineId);
    }

    [Fact]
    public void A_pending_ticket_is_never_resolved_by_a_timeout()
    {
        // #37: money stays held and polling continues — however long it takes, the limit included.
        var booking = PendingBooking(ticketTimeLimit: Now.AddMinutes(10));
        var at = Now;
        var alerts = new List<SupplierPollAlert?>();

        for (var poll = 0; poll < 12; poll++)
        {
            at = booking.NextPollAt!.Value;
            alerts.Add(booking.RecordStatusPoll(Answered(SupplierBookingStatus.TicketPending, 3), at).Alert);
        }

        at.Should().BeAfter(Now.AddHours(5), "twelve polls on the back-off run well past the limit");
        booking.Status.Should().Be(SupplierBookingStatus.TicketPending);
        booking.NextPollAt.Should().NotBeNull("it is still being polled");
        booking.PullDomainEvents().Should().BeEmpty("nothing was decided on its behalf");
        alerts.Where(alert => alert is not null).Should().ContainSingle()
            .Which.Should().Be(SupplierPollAlert.UnresolvedPastTimeLimit, "a person is told once, and the polling goes on");
    }

    [Fact]
    public void A_supplier_error_is_put_in_front_of_a_person_once_and_asked_about_again()
    {
        var booking = IssuingBooking();

        var first = booking.RecordStatusPoll(Answered(reported: null, code: 100), Now);
        var second = booking.RecordStatusPoll(Answered(reported: null, code: 100), booking.NextPollAt!.Value);

        first.Alert.Should().Be(SupplierPollAlert.SupplierReportedError);
        first.Poll.ActionTaken.Should().Be(SupplierPollAction.AlertRaised);
        second.Alert.Should().BeNull("one alert per booking");
        second.Poll.ActionTaken.Should().Be(SupplierPollAction.Rescheduled);

        booking.Status.Should().Be(SupplierBookingStatus.IssueOutcomeUnknown, "the issuer is gone and nothing is settled");
        booking.NextPollAt.Should().NotBeNull();
        booking.PullDomainEvents().Should().BeEmpty("nothing is reversed on an error");
    }

    [Fact]
    public void No_answer_is_asked_again_on_the_back_off()
    {
        var booking = PendingBooking(ticketTimeLimit: Now.AddDays(1));
        var at = Now;

        foreach (var expected in (TimeSpan[])[TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5)])
        {
            var recorded = booking.RecordStatusPoll(NoAnswer(), at);

            recorded.Poll.Outcome.Should().Be(SupplierPollOutcome.Timeout);
            recorded.Poll.ActionTaken.Should().Be(SupplierPollAction.Rescheduled);
            booking.NextPollAt.Should().Be(at + expected);
            at = booking.NextPollAt!.Value;
        }

        booking.PollAttempts.Should().Be(3);
    }

    [Fact]
    public void A_booking_with_no_outcome_owed_cannot_be_polled()
    {
        var booking = ConfirmedBooking();

        var poll = () => booking.RecordStatusPoll(Answered(SupplierBookingStatus.Ticketed, 2), Now);

        poll.Should().Throw<InvalidOperationException>();
    }

    // -------------------------------------------------------------------- the ticket time limit

    [Fact]
    public void Only_a_held_booking_past_its_limit_can_lapse()
    {
        var held = ConfirmedBooking(ticketTimeLimit: Now);
        var early = ConfirmedBooking(ticketTimeLimit: Now.AddMinutes(1));
        var issuing = IssuingBooking(ticketTimeLimit: Now.AddMinutes(1));

        held.Expire(Now).Should().BeTrue();
        early.Expire(Now).Should().BeFalse("the limit has not passed");
        issuing.Expire(Now.AddMinutes(5)).Should().BeFalse("the confirmation is consumed: a ticket may exist");

        held.Status.Should().Be(SupplierBookingStatus.Expired);
        held.PullDomainEvents().OfType<SupplierBookingExpired>().Should().ContainSingle();
        issuing.Status.Should().Be(SupplierBookingStatus.Issuing);
    }

    [Fact]
    public void Each_warning_is_recorded_once()
    {
        var booking = ConfirmedBooking(ticketTimeLimit: Now.AddMinutes(50));

        booking.RecordTimeLimitWarning(TicketTimeLimitWarning.SixtyMinutes, Now).Should().BeTrue();
        booking.RecordTimeLimitWarning(TicketTimeLimitWarning.SixtyMinutes, Now.AddMinutes(1)).Should().BeFalse();
        booking.RecordTimeLimitWarning(TicketTimeLimitWarning.FifteenMinutes, Now.AddMinutes(40)).Should().BeTrue();
        booking.RecordTimeLimitWarning(TicketTimeLimitWarning.FifteenMinutes, Now.AddMinutes(41)).Should().BeFalse();

        booking.SixtyMinuteWarningSentAt.Should().Be(Now);
        booking.FifteenMinuteWarningSentAt.Should().Be(Now.AddMinutes(40));
    }

    // ---------------------------------------------------------------------------- building

    private static SupplierBooking ConfirmedBooking(DateTimeOffset? ticketTimeLimit = null)
    {
        var booking = SupplierBooking.Create(
            agencyId: Guid.CreateVersion7(),
            supplierId: Guid.CreateVersion7(),
            orderLineId: Guid.CreateVersion7(),
            supplierOfferId: null,
            productType: SupplierProductType.Flight,
            tripType: "Domestic",
            tripMode: "Flight",
            supplierSessionId: "session-1",
            currency: "NGN",
            idempotencyKey: $"order-line:{Guid.CreateVersion7()}");

        booking.RecordPriceConfirmation(
            [new PriceConfirmationLine("36516|12QFDT", new Money(50_000_00), new Money(50_000_00), ticketTimeLimit ?? Now.AddMinutes(45), Hash, Hash)],
            Now.AddMinutes(-1));

        booking.PullDomainEvents();
        return booking;
    }

    private static SupplierBooking IssuingBooking(DateTimeOffset? ticketTimeLimit = null)
    {
        var booking = ConfirmedBooking(ticketTimeLimit);
        booking.BeginIssue(Now.AddSeconds(-5));
        return booking;
    }

    private static SupplierBooking PendingBooking(DateTimeOffset? ticketTimeLimit = null)
    {
        var booking = IssuingBooking(ticketTimeLimit);
        booking.RecordIssuePending("RE6MIK", 3, Now.AddSeconds(-1));
        booking.PullDomainEvents();
        return booking;
    }

    private static SupplierStatusObservation Answered(SupplierBookingStatus? reported, int code) =>
        new(SupplierPollOutcome.Answered, reported, code, HttpStatusCode: 200, Pnr: null, SupplierApiCallId: Guid.CreateVersion7(), Message: null);

    private static SupplierStatusObservation NoAnswer() =>
        new(SupplierPollOutcome.Timeout, ReportedStatus: null, SupplierStatusCode: null, HttpStatusCode: null, Pnr: null, SupplierApiCallId: null, "No response.");
}
