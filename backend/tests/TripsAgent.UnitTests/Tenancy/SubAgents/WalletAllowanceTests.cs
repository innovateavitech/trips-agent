using FluentAssertions;
using TripsAgent.Domain.Common;
using TripsAgent.Domain.Payments;

namespace TripsAgent.UnitTests.Tenancy.SubAgents;

/// <summary>
/// The allowance's own arithmetic. Feature F10, issue 63.
/// </summary>
/// <remarks>
/// What makes concurrent reservations safe is a single conditional UPDATE in PostgreSQL, and that
/// has its own integration test. These are about the rules around it: what a reset does, what
/// lowering a cap does, and what is left to spend — all of which have to agree with the SQL, and
/// all of which are far cheaper to pin down here.
/// </remarks>
public class WalletAllowanceTests
{
    private static readonly Guid Principal = Guid.CreateVersion7();
    private static readonly Guid SubAgent = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 9, 12, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void An_allowance_starts_with_nothing_spent()
    {
        var allowance = Open(1_000_000);

        allowance.SpentMinor.Should().Be(Money.Zero);
        allowance.RemainingMinor.Should().Be(new Money(1_000_000));
        allowance.Status.Should().Be(AllowanceStatus.Active);
    }

    [Fact]
    public void Spending_up_to_the_cap_is_allowed_and_a_kobo_more_is_not()
    {
        var allowance = Open(1_000_000);

        allowance.TryReserve(new Money(999_999)).Should().BeTrue();
        allowance.TryReserve(new Money(1)).Should().BeTrue();
        allowance.TryReserve(new Money(1)).Should().BeFalse();

        allowance.SpentMinor.Should().Be(new Money(1_000_000));
    }

    [Fact]
    public void A_frozen_allowance_refuses_everything()
    {
        var allowance = Open(1_000_000);
        allowance.Freeze();

        allowance.TryReserve(new Money(1)).Should().BeFalse();

        allowance.Unfreeze();
        allowance.TryReserve(new Money(1)).Should().BeTrue();
    }

    [Fact]
    public void A_zero_cap_means_it_may_not_spend_at_all()
    {
        Open(0).TryReserve(new Money(1)).Should().BeFalse();
    }

    [Fact]
    public void Releasing_more_than_was_reserved_lands_on_zero_rather_than_below_it()
    {
        var allowance = Open(1_000_000);
        allowance.TryReserve(new Money(100_000));

        // A reversal racing a lapse. Below zero would quietly hand the sub-agent extra room.
        allowance.Release(new Money(100_000));
        allowance.Release(new Money(100_000));

        allowance.SpentMinor.Should().Be(Money.Zero);
    }

    [Fact]
    public void Lowering_the_cap_below_what_is_spent_claws_nothing_back_and_blocks_the_next_booking()
    {
        var allowance = Open(1_000_000);
        allowance.TryReserve(new Money(800_000));

        allowance.ChangeLimit(new Money(500_000));

        allowance.SpentMinor.Should().Be(new Money(800_000), "the money is spent; the cap only governs the next one");
        allowance.RemainingMinor.Should().Be(Money.Zero, "never negative, however far the cap was lowered");
        allowance.TryReserve(new Money(1)).Should().BeFalse();
    }

    [Fact]
    public void A_negative_cap_is_refused_outright()
    {
        var allowance = Open(1_000_000);

        var lower = () => allowance.ChangeLimit(new Money(-1));

        lower.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Reserving_a_non_positive_amount_is_a_programming_error()
    {
        var allowance = Open(1_000_000);

        var zero = () => allowance.TryReserve(Money.Zero);

        zero.Should().Throw<ArgumentOutOfRangeException>(
            "an amount of nothing means the caller has lost track of what it is booking");
    }

    // ------------------------------------------------------------------ periods

    [Fact]
    public void A_lifetime_allowance_never_resets()
    {
        var allowance = Open(1_000_000, AllowancePeriod.Lifetime);
        allowance.TryReserve(new Money(400_000));

        allowance.ResetsAt.Should().BeNull();
        allowance.ResetIfDue(Now.AddYears(5)).Should().BeFalse();
        allowance.SpentMinor.Should().Be(new Money(400_000));
    }

    [Fact]
    public void A_monthly_allowance_starts_again_at_the_turn_of_the_month()
    {
        var allowance = Open(1_000_000);
        allowance.TryReserve(new Money(900_000));

        allowance.ResetsAt.Should().Be(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero));

        allowance.ResetIfDue(Now.AddDays(1)).Should().BeFalse("the month has not turned over yet");
        allowance.ResetIfDue(new DateTimeOffset(2026, 10, 1, 0, 0, 1, TimeSpan.Zero)).Should().BeTrue();

        allowance.SpentMinor.Should().Be(Money.Zero);
        allowance.ResetsAt.Should().Be(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void Resetting_twice_in_one_period_resets_once()
    {
        var allowance = Open(1_000_000);
        allowance.TryReserve(new Money(900_000));

        var due = new DateTimeOffset(2026, 10, 1, 0, 0, 1, TimeSpan.Zero);

        allowance.ResetIfDue(due).Should().BeTrue();
        allowance.TryReserve(new Money(100_000));

        // The job runs again in the same hour, or an operator triggers it by hand while
        // investigating. Neither may wipe what has been spent since.
        allowance.ResetIfDue(due).Should().BeFalse();
        allowance.SpentMinor.Should().Be(new Money(100_000));
    }

    [Fact]
    public void A_job_that_did_not_run_for_weeks_resets_once_and_lands_on_the_next_boundary()
    {
        var allowance = Open(1_000_000);
        allowance.TryReserve(new Money(900_000));

        // Two months late. Catching up boundary by boundary would loop; it resets once instead.
        allowance.ResetIfDue(new DateTimeOffset(2026, 12, 20, 0, 0, 0, TimeSpan.Zero)).Should().BeTrue();

        allowance.SpentMinor.Should().Be(Money.Zero);
        allowance.ResetsAt.Should().Be(new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero));
    }

    [Theory]
    [InlineData(AllowancePeriod.Daily, "2026-09-13T00:00:00Z")]

    // 12 September 2026 is a Saturday, so the next week starts the following day. A week runs
    // Sunday to Saturday here because that is what DayOfWeek counts from, and an allowance period
    // only has to be consistent — not to match anybody's idea of a working week.
    [InlineData(AllowancePeriod.Weekly, "2026-09-13T00:00:00Z")]
    [InlineData(AllowancePeriod.Monthly, "2026-10-01T00:00:00Z")]
    public void Each_period_lands_on_the_next_boundary_in_UTC(AllowancePeriod period, string expected)
    {
        // UTC throughout, not the agency's timezone: the reset job is one query over every
        // allowance, and a per-agency boundary would make it a query per timezone.
        WalletAllowance.NextResetAfter(Now, period)
            .Should().Be(DateTimeOffset.Parse(expected, System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void An_agency_cannot_hold_an_allowance_against_itself()
    {
        var itself = () => WalletAllowance.Open(
            Principal, Principal, "NGN", new Money(1), AllowancePeriod.Monthly, Now);

        itself.Should().Throw<InvalidOperationException>();
    }

    private static WalletAllowance Open(long limitMinor, AllowancePeriod period = AllowancePeriod.Monthly) =>
        WalletAllowance.Open(Principal, SubAgent, "NGN", new Money(limitMinor), period, Now);
}
