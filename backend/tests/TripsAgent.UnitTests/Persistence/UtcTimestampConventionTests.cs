using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using TripsAgent.Infrastructure.Persistence.Conventions;

namespace TripsAgent.UnitTests.Persistence;

/// <summary>
/// Issue #8: <c>DateTimeOffset</c> everywhere, database stores UTC.
///
/// The bug this prevents is quiet and expensive. A <see cref="DateTime"/> carries a
/// <see cref="DateTimeKind"/> that does not survive a round-trip through PostgreSQL, so a
/// ticket time limit written in Lagos and read back on a server in another zone can be hours
/// out — and a ticket time limit that expires early kills a real booking.
/// </summary>
public class UtcTimestampConventionTests
{
    // Entities under test live in TestEntities.cs.

    // --- the rule holds ------------------------------------------------------------------

    [Fact]
    public void A_DateTimeOffset_should_map_to_timestamp_with_time_zone()
    {
        var property = ModelHarness.Property<Booking>(nameof(Booking.CreatedAt));

        property.GetColumnType().Should().Be(UtcTimestampConvention.TimestampColumnType);
    }

    [Fact]
    public void A_nullable_DateTimeOffset_should_map_to_timestamp_with_time_zone()
    {
        var property = ModelHarness.Property<Booking>(nameof(Booking.CancelledAt));

        property.GetColumnType().Should().Be(UtcTimestampConvention.TimestampColumnType);
        property.IsNullable.Should().BeTrue();
    }

    // --- the rule bites ------------------------------------------------------------------

    [Fact]
    public void A_DateTime_should_fail_model_building()
    {
        var exception = Record.Exception(() => ModelHarness.BuildEntityType<DateTimeBooking>());

        exception.Should().NotBeNull("DateTime loses its Kind on a round-trip and must not be mapped");

        var message = ModelHarness.FlattenMessages(exception!);
        message.Should().Contain("CreatedAt");
        message.Should().Contain("DateTimeOffset", "the message has to say what to use instead");
    }

    [Fact]
    public void A_nullable_DateTime_should_fail_model_building()
    {
        var exception = Record.Exception(() => ModelHarness.BuildEntityType<NullableDateTimeBooking>());

        exception.Should().NotBeNull("making it optional does not make it unambiguous");
        ModelHarness.FlattenMessages(exception!).Should().Contain("CancelledAt");
    }

    // --- the rule stays in its lane ------------------------------------------------------

    [Fact]
    public void DateOnly_and_TimeOnly_should_be_left_alone()
    {
        var act = () => ModelHarness.BuildEntityType<Departure>();

        act.Should().NotThrow(
            "a departure date is a calendar value, not an instant — it has no offset to preserve");
    }

    [Fact]
    public void A_DateOnly_should_not_be_mapped_as_a_timestamp()
    {
        var property = ModelHarness.Property<Departure>(nameof(Departure.DepartureDate));

        property.GetColumnType().Should().NotBe(UtcTimestampConvention.TimestampColumnType);
    }
}
