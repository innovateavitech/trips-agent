using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using TripsAgent.Infrastructure.Persistence.Conventions;

namespace TripsAgent.UnitTests.Persistence;

/// <summary>
/// CLAUDE.md rule 2: money is <c>bigint</c> minor units, never a decimal.
///
/// These tests exist because the rule is only worth as much as its enforcement. A convention
/// that silently stopped firing would let a <c>decimal</c> price column reach a migration, and
/// the first sign of trouble would be a ledger that no longer balances.
/// </summary>
public class MoneyMinorConventionTests
{
    // Entities under test live in TestEntities.cs.

    // --- the rule holds ------------------------------------------------------------------

    [Fact]
    public void A_long_Minor_property_should_map_to_bigint()
    {
        var property = ModelHarness.Property<OrderLine>(nameof(OrderLine.SellPriceMinor));

        property.GetColumnType().Should().Be(MoneyMinorConvention.MoneyColumnType);
    }

    [Fact]
    public void A_nullable_long_Minor_property_should_map_to_bigint()
    {
        var property = ModelHarness.Property<OrderLine>(nameof(OrderLine.DiscountMinor));

        property.GetColumnType().Should().Be(MoneyMinorConvention.MoneyColumnType);
        property.IsNullable.Should().BeTrue("an optional amount must stay optional in the database");
    }

    [Fact]
    public void A_Minor_property_should_get_a_snake_case_column_name()
    {
        var property = ModelHarness.Property<OrderLine>(nameof(OrderLine.SellPriceMinor));

        property.GetColumnName().Should().Be("sell_price_minor");
    }

    // --- the rule bites ------------------------------------------------------------------

    [Fact]
    public void A_decimal_Minor_property_should_fail_model_building()
    {
        var exception = Record.Exception(() => ModelHarness.BuildEntityType<DecimalMoneyOrderLine>());

        exception.Should().NotBeNull("a decimal money column must never reach a migration");

        var message = ModelHarness.FlattenMessages(exception!);
        message.Should().Contain("SellPriceMinor", "the developer needs to know which property");
        message.Should().Contain("Decimal", "and what it currently is");
        message.Should().Contain("long", "and what to change it to");
    }

    [Fact]
    public void A_double_Minor_property_should_fail_model_building()
    {
        var exception = Record.Exception(() => ModelHarness.BuildEntityType<DoubleMoneyOrderLine>());

        exception.Should().NotBeNull();
        ModelHarness.FlattenMessages(exception!).Should().Contain("TaxMinor");
    }

    // --- the rule stays in its lane ------------------------------------------------------

    [Fact]
    public void A_decimal_that_is_not_money_should_be_left_alone()
    {
        var act = () => ModelHarness.BuildEntityType<Departure>();

        act.Should().NotThrow(
            "the convention keys off the 'Minor' suffix — a rating is not an amount of money");
    }

    [Fact]
    public void A_non_money_property_should_not_be_forced_to_bigint()
    {
        var property = ModelHarness.Property<Departure>(nameof(Departure.SeatsRemaining));

        property.GetColumnType().Should().NotBe(MoneyMinorConvention.MoneyColumnType);
    }
}
