using FluentAssertions;
using TripsAgent.Domain.Billing;
using TripsAgent.Domain.Pricing;

namespace TripsAgent.UnitTests.Billing;

/// <summary>
/// How an entitlement's value is stored and read back.
/// </summary>
/// <remarks>
/// The stored form is a JSON scalar in a jsonb column, so a round trip has to be exact. A rate read
/// back wrong is charged against a real agency's margin on every quote.
/// </remarks>
public class EntitlementValueTests
{
    [Theory]
    [InlineData(true, "true")]
    [InlineData(false, "false")]
    public void A_flag_is_stored_as_a_json_boolean(bool enabled, string json)
    {
        var value = EntitlementValue.Flag(enabled);

        value.ToJson().Should().Be(json);
        EntitlementValue.FromJson(EntitlementValueType.Flag, json).Should().Be(value);
        value.IsEnabled.Should().Be(enabled);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(25)]
    [InlineData(EntitlementValue.Unlimited)]
    public void A_limit_round_trips_through_its_json(int ceiling)
    {
        var value = EntitlementValue.Limit(ceiling);

        EntitlementValue.FromJson(EntitlementValueType.Limit, value.ToJson()).Should().Be(value);
        EntitlementValue.FromJson(EntitlementValueType.Limit, value.ToJson()).Ceiling.Should().Be(ceiling);
    }

    [Fact]
    public void A_limit_below_minus_one_is_not_a_limit()
    {
        FluentActions.Invoking(() => EntitlementValue.Limit(-2))
            .Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(150)]
    [InlineData(BasisPoints.PerWhole)]
    public void A_rate_is_whole_basis_points_from_zero_to_one_hundred_percent(int basisPoints)
    {
        var value = EntitlementValue.Rate(basisPoints);

        value.BasisPoints.Should().Be(basisPoints);
        EntitlementValue.FromJson(EntitlementValueType.Rate, value.ToJson()).Should().Be(value);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(10_001)]
    public void A_rate_outside_zero_to_one_hundred_percent_is_refused(int basisPoints)
    {
        FluentActions.Invoking(() => EntitlementValue.Rate(basisPoints))
            .Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void A_stored_value_of_the_wrong_shape_is_a_format_error_rather_than_a_wrong_answer()
    {
        FluentActions.Invoking(() => EntitlementValue.FromJson(EntitlementValueType.Flag, "1"))
            .Should().Throw<FormatException>();

        FluentActions.Invoking(() => EntitlementValue.FromJson(EntitlementValueType.Limit, "true"))
            .Should().Throw<FormatException>();

        FluentActions.Invoking(() => EntitlementValue.FromJson(EntitlementValueType.Rate, "2.5"))
            .Should().Throw<FormatException>("a rate is basis points, and 2.5 of them is not a thing");
    }

    [Fact]
    public void A_value_describes_itself_the_way_a_screen_shows_it()
    {
        EntitlementValue.Flag(true).ToString().Should().Be("on");
        EntitlementValue.Flag(false).ToString().Should().Be("off");
        EntitlementValue.Limit(25).ToString().Should().Be("25");
        EntitlementValue.Limit(EntitlementValue.Unlimited).ToString().Should().Be("unlimited");
        EntitlementValue.Rate(150).ToString().Should().Be("1.5%");
    }

    [Fact]
    public void Every_catalogue_entry_has_a_fallback_of_its_own_declared_type()
    {
        foreach (var definition in EntitlementCatalog.All)
        {
            definition.Fallback.Type.Should().Be(
                definition.ValueType, $"{definition.Code}'s default must be readable as its own type");
        }

        EntitlementCatalog.All.Select(definition => definition.Code).Should().OnlyHaveUniqueItems();
    }
}
