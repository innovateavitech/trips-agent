using FluentAssertions;
using TripsAgent.Domain.Common;
using TripsAgent.Domain.Suppliers;

namespace TripsAgent.UnitTests.Suppliers;

/// <summary>
/// The rules on a supplier booking that stand between a price confirmation and a real ticket.
/// </summary>
/// <remarks>
/// Each of these is a way to issue a ticket nobody vouched for, or to issue one twice. They are
/// domain rules so they hold whichever adapter, saga or poller drives the booking.
/// </remarks>
public class SupplierBookingTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 11, 10, 0, 0, TimeSpan.Zero);

    private const string HashA = "3f2a9c";
    private const string HashB = "77b0e1";

    [Fact]
    public void A_new_booking_is_pending_confirmation_and_unverified()
    {
        var booking = NewBooking();

        booking.Status.Should().Be(SupplierBookingStatus.PendingConfirmation);
        booking.HashVerified.Should().BeFalse();
        booking.Confirmations.Should().BeEmpty();
    }

    [Fact]
    public void Every_element_of_an_array_confirmation_is_verified_on_its_own()
    {
        // A domestic round trip: Trips Africa returns one element per direction, each with its own
        // code, price and hash.
        var booking = NewBooking();

        booking.RecordPriceConfirmation(
            [
                Line("OUT-1", 50_000_00, 50_000_00, HashA, HashA, Now.AddMinutes(40)),
                Line("RET-1", 45_000_00, 45_000_00, HashB, HashB, Now.AddMinutes(25)),
            ],
            Now);

        booking.Status.Should().Be(SupplierBookingStatus.PriceConfirmed);
        booking.HashVerified.Should().BeTrue();
        booking.Confirmations.Should().HaveCount(2).And.OnlyContain(confirmation => confirmation.HashVerified);
        booking.Confirmations.Select(confirmation => confirmation.Sequence).Should().Equal(0, 1);

        booking.NewPriceMinor.Should().Be(new Money(95_000_00), "the booking total is every element's price");
        booking.ConfirmationCode.Should().Be("OUT-1");
        booking.TicketTimeLimit.Should().Be(Now.AddMinutes(25), "the earliest limit is the one that kills the booking");
    }

    [Fact]
    public void One_mismatched_element_rejects_the_whole_booking()
    {
        // The first element verifies and the second does not. Accepting the first alone would issue
        // half a journey at a price nobody vouched for.
        var booking = NewBooking();

        booking.RecordPriceConfirmation(
            [
                Line("OUT-1", 50_000_00, 50_000_00, HashA, HashA),
                Line("RET-1", 45_000_00, 45_000_00, HashB, "tampered"),
            ],
            Now);

        booking.Status.Should().Be(SupplierBookingStatus.PriceRejected);
        booking.HashVerified.Should().BeFalse();
        booking.Confirmations[0].HashVerified.Should().BeTrue();
        booking.Confirmations[1].HashVerified.Should().BeFalse();

        var issue = () => booking.BeginIssue(Now);
        issue.Should().Throw<InvalidOperationException>();
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", "")]
    [InlineData("   ", "   ")]
    [InlineData("abc", "")]
    [InlineData("", "abc")]
    public void A_missing_hash_never_counts_as_a_match(string? expected, string? received)
    {
        // Two empty strings are equal. "The supplier sent no hash" must never read as "it matched".
        SupplierBookingConfirmation.HashesMatch(expected, received).Should().BeFalse();
    }

    [Fact]
    public void Hex_digests_compare_regardless_of_case()
    {
        SupplierBookingConfirmation.HashesMatch("ABCDEF0123", "abcdef0123").Should().BeTrue();
    }

    [Fact]
    public void An_empty_confirmation_is_refused_rather_than_verified_vacuously()
    {
        var booking = NewBooking();

        var record = () => booking.RecordPriceConfirmation([], Now);

        record.Should().Throw<ArgumentException>();
        booking.HashVerified.Should().BeFalse();
    }

    [Fact]
    public void A_price_that_moved_is_flagged_for_the_customer_to_consent_again()
    {
        var booking = NewBooking();

        booking.RecordPriceConfirmation([Line("OUT-1", 50_000_00, 52_500_00, HashA, HashA)], Now);

        booking.PriceChanged.Should().BeTrue();
        booking.OldPriceMinor.Should().Be(new Money(50_000_00));
        booking.NewPriceMinor.Should().Be(new Money(52_500_00));
    }

    [Fact]
    public void Issuing_can_begin_exactly_once()
    {
        // ADR-0003 as a domain rule: whatever retries a message or restarts a worker, the booking
        // itself refuses to go through the issue step a second time.
        var booking = ConfirmedBooking();

        booking.BeginIssue(Now);
        booking.Status.Should().Be(SupplierBookingStatus.Issuing);

        var again = () => booking.BeginIssue(Now.AddSeconds(5));

        again.Should().Throw<InvalidOperationException>().WithMessage("*0003-never-retry-ticket-issuance*");
    }

    [Fact]
    public void Issuing_cannot_begin_before_the_price_is_confirmed()
    {
        var booking = NewBooking();

        var issue = () => booking.BeginIssue(Now);

        issue.Should().Throw<InvalidOperationException>();
        booking.Status.Should().Be(SupplierBookingStatus.PendingConfirmation);
    }

    [Fact]
    public void Issuing_cannot_begin_once_the_ticket_time_limit_has_passed()
    {
        var booking = NewBooking();
        booking.RecordPriceConfirmation([Line("OUT-1", 1_000_00, 1_000_00, HashA, HashA, Now.AddMinutes(10))], Now);

        var issue = () => booking.BeginIssue(Now.AddMinutes(10));

        issue.Should().Throw<InvalidOperationException>().WithMessage("*ticket time limit*");
    }

    [Fact]
    public void The_price_is_frozen_once_issuing_has_begun()
    {
        var booking = ConfirmedBooking();
        booking.BeginIssue(Now);

        var reprice = () => booking.RecordPriceConfirmation([Line("OUT-2", 1_00, 1_00, HashA, HashA)], Now);

        reprice.Should().Throw<InvalidOperationException>();
        booking.NewPriceMinor.Should().Be(new Money(1_000_00));
    }

    [Fact]
    public void A_booking_needs_an_order_line_and_an_idempotency_key()
    {
        var noLine = () => SupplierBooking.Create(
            Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.Empty, null,
            SupplierProductType.Flight, null, null, "session", "NGN", "key");

        var noKey = () => SupplierBooking.Create(
            Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), null,
            SupplierProductType.Flight, null, null, "session", "NGN", " ");

        noLine.Should().Throw<ArgumentOutOfRangeException>();
        noKey.Should().Throw<ArgumentException>();
    }

    private static SupplierBooking NewBooking() =>
        SupplierBooking.Create(
            agencyId: Guid.CreateVersion7(),
            supplierId: Guid.CreateVersion7(),
            orderLineId: Guid.CreateVersion7(),
            supplierOfferId: null,
            productType: SupplierProductType.Flight,
            tripType: "Domestic",
            tripMode: "Flight",
            supplierSessionId: "session-1",
            currency: "ngn",
            idempotencyKey: $"order-line:{Guid.CreateVersion7()}");

    private static SupplierBooking ConfirmedBooking()
    {
        var booking = NewBooking();
        booking.RecordPriceConfirmation([Line("OUT-1", 1_000_00, 1_000_00, HashA, HashA)], Now);
        return booking;
    }

    private static PriceConfirmationLine Line(
        string code,
        long oldMinor,
        long newMinor,
        string expected,
        string received,
        DateTimeOffset? ticketTimeLimit = null) =>
        new(code, new Money(oldMinor), new Money(newMinor), ticketTimeLimit ?? Now.AddMinutes(30), expected, received);
}
