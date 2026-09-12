using FluentAssertions;
using TripsAgent.Domain.Catalog;
using TripsAgent.Domain.Common;
using static TripsAgent.UnitTests.Catalog.DepartureRulesTests;

namespace TripsAgent.UnitTests.Catalog;

/// <summary>
/// A departure's own rules: what a save replaces, what the version guards, and whose decision the
/// status is. Build plan F6, issue #57.
/// </summary>
public class DepartureLifecycleTests
{
    private static readonly DateTimeOffset Now = new(2026, 3, 1, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Cutoff = new(2026, 8, 31, 23, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_new_departure_opens_and_keeps_everything_it_was_given()
    {
        var departure = New();

        departure.Status.Should().Be(DepartureStatus.Open);
        departure.Version.Should().Be(1);
        departure.CapacityTotal.Should().Be(20);
        departure.MaxPax.Should().Be(20, "max_pax tracks capacity until something needs them to differ");
        departure.CutoffAt.Should().Be(Cutoff);
        departure.PriceTiers.Should().HaveCount(2);
        departure.Installments!.Items.Should().HaveCount(2);
        departure.ToTerms().Should().BeEquivalentTo(Sample().Normalised());
    }

    [Fact]
    public void A_departure_that_always_runs_is_guaranteed_from_the_start()
    {
        New(Sample() with { IsGroupDeparture = false }).Status.Should().Be(DepartureStatus.Guaranteed);
    }

    [Fact]
    public void A_save_replaces_everything_and_bumps_the_version()
    {
        var departure = New();

        departure.Revise(
            Sample() with { CapacityTotal = 30, PriceTiers = [Tier(1, null, 80_000)], Installments = [] },
            Cutoff,
            Now.AddDays(1));

        departure.Version.Should().Be(2);
        departure.CapacityTotal.Should().Be(30);
        departure.PriceTiers.Should().ContainSingle();
        departure.Installments!.Items.Should().BeEmpty();
    }

    [Fact]
    public void A_saves_installment_plan_keeps_its_own_id_so_anything_pointing_at_it_still_resolves()
    {
        var departure = New();
        var planId = departure.Installments!.Id;

        departure.Revise(Sample() with { Installments = [Payment(1, 10_000)] }, Cutoff, Now.AddDays(1));

        departure.Installments!.Id.Should().Be(planId);
    }

    [Fact]
    public void The_capacity_can_never_drop_below_the_seats_already_taken()
    {
        var departure = New();
        Sell(departure, reserved: 3, confirmed: 9);

        var act = () => departure.Revise(Sample() with { CapacityTotal = 11 }, Cutoff, Now);

        act.Should().Throw<InvalidOperationException>().WithMessage("*12 seats are already taken*");

        // Exactly the seats taken is allowed: nobody loses a seat they have been sold.
        departure.Revise(Sample() with { CapacityTotal = 12 }, Cutoff, Now);
        departure.CapacityTotal.Should().Be(12);
    }

    [Fact]
    public void Closing_stops_new_bookings_and_survives_the_seats_moving()
    {
        var departure = New();
        departure.Close(Now);

        departure.Status.Should().Be(DepartureStatus.Closed);
        departure.Version.Should().Be(2);

        Sell(departure, reserved: 0, confirmed: 20);
        departure.RefreshStatus().Should().BeFalse("the agent's decision outranks the arithmetic");
        departure.Status.Should().Be(DepartureStatus.Closed);
    }

    [Fact]
    public void Reopening_gives_it_back_the_status_its_seats_earn()
    {
        var departure = New();
        Sell(departure, reserved: 0, confirmed: 18);
        departure.Close(Now);

        departure.Reopen(Now);

        departure.Status.Should().Be(DepartureStatus.NearlyFull);
    }

    [Fact]
    public void Only_a_closed_departure_can_be_reopened()
    {
        var act = () => New().Reopen(Now);

        act.Should().Throw<InvalidOperationException>().WithMessage("*not closed*");
    }

    [Fact]
    public void Closing_twice_and_cancelling_twice_are_both_refused()
    {
        var departure = New();
        departure.Close(Now);

        var closeAgain = () => departure.Close(Now);
        closeAgain.Should().Throw<InvalidOperationException>().WithMessage("*already closed*");

        departure.Cancel(Now);

        var cancelAgain = () => departure.Cancel(Now);
        cancelAgain.Should().Throw<InvalidOperationException>().WithMessage("*already cancelled*");
    }

    [Fact]
    public void A_cancelled_departure_can_never_be_edited_again()
    {
        var departure = New();
        departure.Cancel(Now);

        var act = () => departure.Revise(Sample(), Cutoff, Now);

        act.Should().Throw<InvalidOperationException>().WithMessage("*cancelled*");
    }

    [Fact]
    public void The_status_follows_the_seats_while_the_agent_has_decided_nothing()
    {
        var departure = New();

        Sell(departure, reserved: 4, confirmed: 0);
        departure.RefreshStatus();
        departure.Status.Should().Be(DepartureStatus.Open, "holding a seat is not paying for it");

        Sell(departure, reserved: 0, confirmed: 4);
        departure.RefreshStatus();
        departure.Status.Should().Be(DepartureStatus.Guaranteed);

        Sell(departure, reserved: 0, confirmed: 17);
        departure.RefreshStatus();
        departure.Status.Should().Be(DepartureStatus.NearlyFull);

        Sell(departure, reserved: 0, confirmed: 20);
        departure.RefreshStatus();
        departure.Status.Should().Be(DepartureStatus.SoldOut);
    }

    [Fact]
    public void A_departure_is_sellable_only_while_it_is_open_before_its_cutoff_and_has_the_seats()
    {
        var departure = New();

        departure.IsSellable(Now, 2).Should().BeTrue();
        departure.IsSellable(Cutoff, 2).Should().BeFalse("the cutoff has passed");
        departure.IsSellable(Now, 21).Should().BeFalse("there are only 20 seats");

        Sell(departure, reserved: 0, confirmed: 19);
        departure.IsSellable(Now, 2).Should().BeFalse();
        departure.IsSellable(Now, 1).Should().BeTrue();

        departure.Close(Now);
        departure.IsSellable(Now, 1).Should().BeFalse();
    }

    // ------------------------------------------------------------------ holds and the waitlist

    [Fact]
    public void A_hold_is_released_or_converted_once_and_says_when()
    {
        var hold = DepartureHold.Place(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 2, Now, TimeSpan.FromMinutes(20));

        hold.ExpiresAt.Should().Be(Now.AddMinutes(20));

        hold.Release(Now.AddMinutes(5)).Should().BeTrue();
        hold.Release(Now.AddMinutes(6)).Should().BeFalse();
        hold.Convert(Now.AddMinutes(7)).Should().BeFalse("it is no longer held");
        hold.Status.Should().Be(DepartureHoldStatus.Released);
        hold.SettledAt.Should().Be(Now.AddMinutes(5));
    }

    [Fact]
    public void An_offer_has_a_deadline_and_expiring_takes_it_away()
    {
        var entry = DepartureWaitlistEntry.Join(
            Guid.NewGuid(), Guid.NewGuid(), " Ada Obi ", " ADA@example.test ", 2, Now);

        entry.Name.Should().Be("Ada Obi");
        entry.Email.Should().Be("ADA@example.test");
        entry.Status.Should().Be(WaitlistStatus.Waiting);

        entry.Offer(Now, TimeSpan.FromHours(48));
        entry.ExpiresAt.Should().Be(Now.AddHours(48));

        var offerAgain = () => entry.Offer(Now, TimeSpan.FromHours(48));
        offerAgain.Should().Throw<InvalidOperationException>();

        entry.Expire().Should().BeTrue();
        entry.Expire().Should().BeFalse();
        entry.ExpiresAt.Should().BeNull();
    }

    [Fact]
    public void A_converted_entry_never_expires_out_from_under_somebody()
    {
        var entry = DepartureWaitlistEntry.Join(Guid.NewGuid(), Guid.NewGuid(), "Ada", "ada@example.test", 1, Now);
        entry.Offer(Now, TimeSpan.FromHours(48));
        entry.Convert();

        entry.Expire().Should().BeFalse();
        entry.Status.Should().Be(WaitlistStatus.Converted);
    }

    private static Departure New(DepartureTerms? terms = null) =>
        Departure.Create(Guid.NewGuid(), Guid.NewGuid(), terms ?? Sample(), Cutoff, Now);

    /// <summary>
    /// Puts seats on the departure the way the database does, since the entity deliberately has no
    /// setter for them: only <c>DepartureSeats</c>' atomic UPDATE moves capacity.
    /// </summary>
    private static void Sell(Departure departure, int reserved, int confirmed)
    {
        typeof(Departure).GetProperty(nameof(Departure.CapacityReserved))!.SetValue(departure, reserved);
        typeof(Departure).GetProperty(nameof(Departure.CapacityConfirmed))!.SetValue(departure, confirmed);
    }
}
