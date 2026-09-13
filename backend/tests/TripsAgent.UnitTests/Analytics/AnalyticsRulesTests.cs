using FluentAssertions;
using TripsAgent.Application.Analytics;
using TripsAgent.Domain.Analytics;
using TripsAgent.Domain.Orders;

namespace TripsAgent.UnitTests.Analytics;

/// <summary>What counts as a sale, and what counts as growth.</summary>
public class BookingFactRulesTests
{
    [Theory]
    [InlineData(OrderStatus.Paid, FulfilmentStatus.Confirmed, true)]
    [InlineData(OrderStatus.Confirmed, FulfilmentStatus.Confirmed, true)]
    [InlineData(OrderStatus.PartiallyFulfilled, FulfilmentStatus.Reserved, true)]

    // Paid for and not delivered is still a sale: the agency has the money and owes a ticket. It
    // shows up in the failure count beside it, which is where somebody acts on it.
    [InlineData(OrderStatus.PartiallyFailed, FulfilmentStatus.FailedNeedsResolution, true)]

    // Nobody has paid yet. An order at PendingPayment is a hope.
    [InlineData(OrderStatus.PendingPayment, FulfilmentStatus.Pending, false)]

    // Given back, either at the order or at the line.
    [InlineData(OrderStatus.Refunded, FulfilmentStatus.Confirmed, false)]
    [InlineData(OrderStatus.Cancelled, FulfilmentStatus.Cancelled, false)]
    [InlineData(OrderStatus.Confirmed, FulfilmentStatus.Refunded, false)]
    [InlineData(OrderStatus.Confirmed, FulfilmentStatus.Cancelled, false)]
    public void A_sale_is_money_taken_and_not_given_back(
        OrderStatus orderStatus,
        FulfilmentStatus fulfilmentStatus,
        bool expected) =>
        BookingFactRules.CountsAsSale(orderStatus, fulfilmentStatus).Should().Be(expected);

    [Fact]
    public void A_refund_is_never_also_a_sale()
    {
        BookingFactRules.CountsAsRefund(OrderStatus.Refunded, FulfilmentStatus.Confirmed).Should().BeTrue();
        BookingFactRules.CountsAsSale(OrderStatus.Refunded, FulfilmentStatus.Confirmed).Should().BeFalse();
    }

    [Fact]
    public void A_line_needing_resolution_is_a_failure()
    {
        BookingFactRules.CountsAsFailure(FulfilmentStatus.FailedNeedsResolution).Should().BeTrue();
        BookingFactRules.CountsAsFailure(FulfilmentStatus.Confirmed).Should().BeFalse();
    }
}

/// <summary>When a report is too big to answer in a request.</summary>
public class ReportScopeRulesTests
{
    private static readonly DateOnly Start = new(2026, 1, 1);

    [Fact]
    public void A_short_agency_report_runs_in_the_request()
    {
        ReportScopeRules.ModeFor(ReportScope.Agency, Start, Start.AddDays(29))
            .Should().Be(ReportRunMode.Synchronous);
    }

    [Fact]
    public void Exactly_ninety_days_is_still_synchronous()
    {
        // The rule is "more than 90 days", so 90 is inside it. Pinned because an off-by-one here
        // silently changes which reports arrive by email.
        ReportScopeRules.DaysCovered(Start, Start.AddDays(89)).Should().Be(90);

        ReportScopeRules.ModeFor(ReportScope.Agency, Start, Start.AddDays(89))
            .Should().Be(ReportRunMode.Synchronous);
    }

    [Fact]
    public void Ninety_one_days_goes_to_the_background()
    {
        ReportScopeRules.ModeFor(ReportScope.Agency, Start, Start.AddDays(90))
            .Should().Be(ReportRunMode.Asynchronous);
    }

    [Fact]
    public void A_platform_report_is_always_asynchronous_however_short()
    {
        // One day, and still queued: a cross-tenant read is unbounded by definition — it grows with
        // the business, not with the request.
        ReportScopeRules.ModeFor(ReportScope.Platform, Start, Start)
            .Should().Be(ReportRunMode.Asynchronous);
    }

    [Fact]
    public void One_day_covers_one_day()
    {
        ReportScopeRules.DaysCovered(Start, Start).Should().Be(1);
    }

    [Fact]
    public void A_backwards_window_covers_nothing()
    {
        ReportScopeRules.DaysCovered(Start.AddDays(5), Start).Should().Be(0);
    }
}

/// <summary>Period-over-period change, computed in integers.</summary>
public class AnalyticsChangeTests
{
    [Fact]
    public void A_doubling_is_ten_thousand_basis_points()
    {
        AnalyticsChange.BasisPoints(200_000, 100_000).Should().Be(10_000);
    }

    [Fact]
    public void A_halving_is_minus_five_thousand()
    {
        AnalyticsChange.BasisPoints(50_000, 100_000).Should().Be(-5_000);
    }

    [Fact]
    public void No_change_is_zero()
    {
        AnalyticsChange.BasisPoints(100_000, 100_000).Should().Be(0);
    }

    [Fact]
    public void There_is_no_change_from_nothing()
    {
        // Not "infinite growth", and not zero either. A dashboard has to say "no comparison".
        AnalyticsChange.BasisPoints(100_000, 0).Should().BeNull();
    }

    [Fact]
    public void Falling_to_nothing_is_minus_one_hundred_percent()
    {
        AnalyticsChange.BasisPoints(0, 100_000).Should().Be(-10_000);
    }

    [Fact]
    public void A_tiny_previous_window_cannot_overflow_the_answer()
    {
        // A kobo against a billion naira. Without the clamp this is not an int.
        AnalyticsChange.BasisPoints(100_000_000_000, 1).Should().Be(int.MaxValue);
    }
}
