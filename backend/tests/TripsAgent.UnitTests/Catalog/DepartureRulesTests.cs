using FluentAssertions;
using TripsAgent.Domain.Catalog;
using TripsAgent.Domain.Common;

namespace TripsAgent.UnitTests.Catalog;

/// <summary>
/// What a departure has to look like before it can be stored, and the status its seats give it.
/// Everything wrong at once, keyed by the field the console names (build plan F6).
/// </summary>
public class DepartureRulesTests
{
    private static readonly DateOnly Today = new(2026, 3, 1);

    [Fact]
    public void A_complete_departure_can_be_saved()
    {
        DepartureRules.Validate(Sample(), Today).Should().BeEmpty();
    }

    [Fact]
    public void A_departure_has_to_leave_in_the_future()
    {
        Fields(Sample() with { DepartureDate = Today }).Should().Contain("departureDate");
        Fields(Sample() with { DepartureDate = Today.AddDays(-1) }).Should().Contain("departureDate");
        Fields(Sample() with { DepartureDate = Today.AddDays(1) }).Should().NotContain("departureDate");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(DepartureRules.MaxCapacity + 1)]
    public void A_capacity_outside_the_bounds_is_refused(int capacity)
    {
        Fields(Sample() with { CapacityTotal = capacity }).Should().Contain("capacityTotal");
    }

    [Fact]
    public void A_minimum_party_larger_than_the_seats_is_refused_only_on_a_group_departure()
    {
        Fields(Sample() with { IsGroupDeparture = true, MinPax = 40, CapacityTotal = 20 })
            .Should().Contain("minPax");

        // Not a group departure, so the minimum is meaningless and Normalised() clears it anyway.
        Fields(Sample() with { IsGroupDeparture = false, MinPax = 40, CapacityTotal = 20 })
            .Should().NotContain("minPax");
    }

    [Fact]
    public void Price_tiers_have_to_follow_on_from_one_with_no_gaps_or_overlaps()
    {
        Fields(Sample() with { PriceTiers = [Tier(2, 4, 100)] }).Should().Contain("priceTiers");
        Fields(Sample() with { PriceTiers = [Tier(1, 4, 100), Tier(6, null, 90)] }).Should().Contain("priceTiers");
        Fields(Sample() with { PriceTiers = [Tier(1, 4, 100), Tier(3, null, 90)] }).Should().Contain("priceTiers");

        Fields(Sample() with { PriceTiers = [Tier(1, 3, 100), Tier(4, 7, 95), Tier(8, null, 90)] })
            .Should().NotContain("priceTiers");
    }

    [Fact]
    public void Only_the_last_price_tier_may_be_open_ended()
    {
        Fields(Sample() with { PriceTiers = [Tier(1, null, 100), Tier(2, null, 90)] })
            .Should().Contain("priceTiers.0.maxPax");
    }

    [Fact]
    public void A_price_tier_needs_a_price_above_zero()
    {
        Fields(Sample() with { PriceTiers = [Tier(1, null, 0)] }).Should().Contain("priceTiers.0.price");
    }

    [Fact]
    public void A_percentage_deposit_is_more_than_nothing_and_at_most_everything()
    {
        Fields(Sample() with { DepositType = DepositType.Percent, DepositPercentBasisPoints = 0 })
            .Should().Contain("deposit");
        Fields(Sample() with { DepositType = DepositType.Percent, DepositPercentBasisPoints = 10_001 })
            .Should().Contain("deposit");
        Fields(Sample() with { DepositType = DepositType.Percent, DepositPercentBasisPoints = 2_500 })
            .Should().BeEmpty();
    }

    [Fact]
    public void A_fixed_deposit_is_never_more_than_the_cheapest_seat()
    {
        var terms = Sample() with
        {
            DepositType = DepositType.Fixed,
            PriceTiers = [Tier(1, 3, 100_000), Tier(4, null, 90_000)],
        };

        Fields(terms with { DepositAmountMinor = new Money(90_001) }).Should().Contain("deposit");
        Fields(terms with { DepositAmountMinor = new Money(90_000) }).Should().BeEmpty();
    }

    [Fact]
    public void The_installments_add_up_to_the_whole_balance()
    {
        Fields(Sample() with { Installments = [Payment(1, 4_000), Payment(2, 5_000)] })
            .Should().Contain("installments");

        Fields(Sample() with { Installments = [Payment(1, 4_000), Payment(2, 6_000)] })
            .Should().BeEmpty();
    }

    [Fact]
    public void No_installments_at_all_is_the_balance_at_the_cutoff_and_is_perfectly_valid()
    {
        DepartureRules.Validate(Sample() with { Installments = [] }, Today).Should().BeEmpty();
    }

    [Fact]
    public void An_unknown_due_basis_is_reported_at_its_own_index()
    {
        Fields(Sample() with { Installments = [Payment(1, 10_000) with { DueBasis = default }] })
            .Should().Contain("installments.0.dueBasis");
    }

    [Fact]
    public void Everything_wrong_is_reported_at_once()
    {
        var problems = DepartureRules.Validate(
            new DepartureTerms
            {
                DepartureDate = Today.AddDays(-5),
                CapacityTotal = 0,
                CutoffDaysBefore = -1,
                DepositType = DepositType.Percent,
                PriceTiers = [],
            },
            Today);

        problems.Select(problem => problem.Field).Should()
            .Contain(["departureDate", "capacityTotal", "cutoffDaysBefore", "priceTiers", "deposit"]);
    }

    // ------------------------------------------------------------------ the price ladder

    [Theory]
    [InlineData(1, 100_000)]
    [InlineData(3, 100_000)]
    [InlineData(4, 95_000)]
    [InlineData(7, 95_000)]
    [InlineData(8, 90_000)]
    [InlineData(400, 90_000)]
    public void A_party_pays_the_tier_that_covers_it(int pax, long expected)
    {
        IReadOnlyList<PriceTierTerms> tiers = [Tier(1, 3, 100_000), Tier(4, 7, 95_000), Tier(8, null, 90_000)];

        DepartureRules.PriceForParty(tiers, pax).Should().Be(new Money(expected));
    }

    [Fact]
    public void A_party_no_tier_covers_has_no_price()
    {
        DepartureRules.PriceForParty([Tier(2, 4, 100_000)], 1).Should().BeNull();
        DepartureRules.PriceForParty([Tier(1, 4, 100_000)], 5).Should().BeNull();
    }

    // ------------------------------------------------------------------ status from seats

    [Theory]
    [InlineData(0, 0, DepartureStatus.Open)]
    [InlineData(0, 3, DepartureStatus.Open)]
    [InlineData(0, 4, DepartureStatus.Guaranteed)]
    [InlineData(11, 6, DepartureStatus.NearlyFull)]
    [InlineData(0, 17, DepartureStatus.NearlyFull)]
    [InlineData(3, 17, DepartureStatus.SoldOut)]
    [InlineData(20, 0, DepartureStatus.SoldOut)]
    public void The_seats_give_a_group_departure_its_status(int reserved, int confirmed, DepartureStatus expected)
    {
        DepartureStatusRules.FromSeats(new SeatCount(20, reserved, confirmed), isGroupDeparture: true, minPax: 4)
            .Should().Be(expected);
    }

    [Fact]
    public void A_departure_that_always_runs_is_guaranteed_from_the_moment_it_opens()
    {
        DepartureStatusRules.FromSeats(new SeatCount(20, 0, 0), isGroupDeparture: false, minPax: 1)
            .Should().Be(DepartureStatus.Guaranteed);
    }

    [Fact]
    public void Seventeen_of_twenty_is_nearly_full_in_integers_on_every_machine()
    {
        // 17 * 100 >= 20 * 85. The arithmetic that decides whether something is for sale never
        // touches a float.
        DepartureStatusRules.FromSeats(new SeatCount(20, 0, 16), isGroupDeparture: true, minPax: 1)
            .Should().Be(DepartureStatus.Guaranteed);
        DepartureStatusRules.FromSeats(new SeatCount(20, 0, 17), isGroupDeparture: true, minPax: 1)
            .Should().Be(DepartureStatus.NearlyFull);
    }

    [Theory]
    [InlineData(DepartureStatus.Closed)]
    [InlineData(DepartureStatus.Cancelled)]
    public void Closed_and_cancelled_are_the_agents_own(DepartureStatus status)
    {
        DepartureStatusRules.IsManual(status).Should().BeTrue();
    }

    // ------------------------------------------------------------------ helpers

    internal static DepartureTerms Sample() => new()
    {
        DepartureDate = new DateOnly(2026, 9, 14),
        IsGroupDeparture = true,
        MinPax = 4,
        CapacityTotal = 20,
        CutoffDaysBefore = 14,
        DepositType = DepositType.None,
        PriceTiers = [Tier(1, 3, 100_000), Tier(4, null, 90_000)],
        Installments = [Payment(1, 4_000), Payment(2, 6_000)],
    };

    internal static PriceTierTerms Tier(int minPax, int? maxPax, long price) =>
        new(minPax, maxPax, new Money(price));

    internal static InstallmentTerms Payment(int sequence, int basisPoints) =>
        new(sequence, InstallmentDueBasis.BeforeDeparture, 30, basisPoints);

    private static List<string> Fields(DepartureTerms terms) =>
        DepartureRules.Validate(terms, Today).Select(problem => problem.Field).ToList();
}
