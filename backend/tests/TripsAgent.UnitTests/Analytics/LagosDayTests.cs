using FluentAssertions;
using TripsAgent.Domain.Analytics;

namespace TripsAgent.UnitTests.Analytics;

/// <summary>
/// The rollup groups by the day an agent means, which is a Lagos day, not a UTC one. These tests
/// pin the two edges where that distinction shows up and where getting it wrong moves a booking to
/// the wrong day of somebody's week.
/// </summary>
public class LagosDayTests
{
    [Fact]
    public void Just_after_midnight_in_Lagos_is_still_the_previous_day_in_UTC()
    {
        // 00:30 Monday in Lagos is 23:30 Sunday in UTC. The agent sold it on Monday.
        var instant = new DateTimeOffset(2026, 3, 8, 23, 30, 0, TimeSpan.Zero);

        LagosDay.Of(instant).Should().Be(new DateOnly(2026, 3, 9));
    }

    [Fact]
    public void Just_before_midnight_in_Lagos_is_already_the_next_day_in_UTC()
    {
        // 23:30 Monday in Lagos is 22:30 Monday in UTC — same day either way, so this one checks
        // the other edge: 23:30 UTC on Monday is Tuesday in Lagos.
        var instant = new DateTimeOffset(2026, 3, 9, 22, 30, 0, TimeSpan.Zero);

        LagosDay.Of(instant).Should().Be(new DateOnly(2026, 3, 9));
    }

    [Fact]
    public void A_day_starts_at_23_00_UTC_the_evening_before()
    {
        var start = LagosDay.StartOfUtc(new DateOnly(2026, 3, 9));

        start.Should().Be(new DateTimeOffset(2026, 3, 8, 23, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void A_day_boundary_is_UTC_so_Npgsql_will_accept_it()
    {
        // Npgsql refuses a DateTimeOffset whose offset is not zero, deliberately. Converting here,
        // at the boundary, is the whole point of this helper.
        LagosDay.StartOfUtc(new DateOnly(2026, 3, 9)).Offset.Should().Be(TimeSpan.Zero);
        LagosDay.EndOfUtc(new DateOnly(2026, 3, 9)).Offset.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void A_days_range_is_half_open_so_nothing_is_counted_twice()
    {
        var day = new DateOnly(2026, 3, 9);

        LagosDay.EndOfUtc(day).Should().Be(LagosDay.StartOfUtc(day.AddDays(1)));
    }

    [Fact]
    public void Every_instant_inside_a_day_maps_back_to_it()
    {
        var day = new DateOnly(2026, 3, 9);
        var start = LagosDay.StartOfUtc(day);
        var lastMoment = LagosDay.EndOfUtc(day).AddTicks(-1);

        LagosDay.Of(start).Should().Be(day);
        LagosDay.Of(lastMoment).Should().Be(day);
        LagosDay.Of(start.AddHours(12)).Should().Be(day);
    }

    [Fact]
    public void Range_is_inclusive_at_both_ends()
    {
        var days = LagosDay.Range(new DateOnly(2026, 3, 9), new DateOnly(2026, 3, 11)).ToList();

        days.Should().Equal(
            new DateOnly(2026, 3, 9),
            new DateOnly(2026, 3, 10),
            new DateOnly(2026, 3, 11));
    }

    [Fact]
    public void Range_of_one_day_is_one_day()
    {
        LagosDay.Range(new DateOnly(2026, 3, 9), new DateOnly(2026, 3, 9)).Should().ContainSingle();
    }

    [Fact]
    public void Range_of_a_backwards_window_is_empty()
    {
        LagosDay.Range(new DateOnly(2026, 3, 11), new DateOnly(2026, 3, 9)).Should().BeEmpty();
    }
}
