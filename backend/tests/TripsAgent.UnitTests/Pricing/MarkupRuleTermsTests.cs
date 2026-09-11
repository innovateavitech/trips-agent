using FluentAssertions;
using TripsAgent.Domain.Common;
using TripsAgent.Domain.Pricing;

namespace TripsAgent.UnitTests.Pricing;

/// <summary>How much a rule adds, and which rules are allowed to exist.</summary>
public class MarkupRuleTermsTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);

    // ------------------------------------------------------------------ percentage

    [Fact]
    public void A_percentage_rule_adds_that_share_of_the_net_rate()
    {
        // 10% of ₦1,500.00 is ₦150.00.
        Percentage(1_000).CalculateMarkup(new Money(150_000)).Should().Be(new Money(15_000));
    }

    [Theory]
    [InlineData(100_005, 10_001)]   // 10,000.5 kobo rounds up
    [InlineData(100_004, 10_000)]   // 10,000.4 kobo rounds down
    [InlineData(100_006, 10_001)]   // 10,000.6 kobo rounds up
    public void A_percentage_that_lands_between_kobo_rounds_to_the_nearest_half_up(long net, long expected)
    {
        Percentage(1_000).CalculateMarkup(new Money(net)).Should().Be(new Money(expected));
    }

    [Fact]
    public void Basis_points_allow_fractional_percentages()
    {
        // 7.5% of ₦2,000.00 is ₦150.00 — a rate a decimal percentage field would have to round.
        Percentage(750).CalculateMarkup(new Money(200_000)).Should().Be(new Money(15_000));
    }

    [Fact]
    public void A_minimum_cap_lifts_a_small_markup()
    {
        // 10% of ₦100.00 is ₦10.00; the floor is ₦2,000.00.
        var terms = Percentage(1_000) with { MinMarkupMinor = new Money(200_000) };

        terms.CalculateMarkup(new Money(10_000)).Should().Be(new Money(200_000));
    }

    [Fact]
    public void A_maximum_cap_limits_a_large_markup()
    {
        // 10% of ₦100,000.00 is ₦10,000.00; the ceiling is ₦5,000.00.
        var terms = Percentage(1_000) with { MaxMarkupMinor = new Money(500_000) };

        terms.CalculateMarkup(new Money(10_000_000)).Should().Be(new Money(500_000));
    }

    [Fact]
    public void Caps_leave_a_markup_between_them_alone()
    {
        var terms = Percentage(1_000) with { MinMarkupMinor = new Money(1_000), MaxMarkupMinor = new Money(100_000) };

        terms.CalculateMarkup(new Money(150_000)).Should().Be(new Money(15_000));
    }

    [Fact]
    public void A_percentage_too_large_for_a_long_throws_rather_than_wrapping()
    {
        // A wrapped result would be a negative price. Loud is the only acceptable failure.
        var act = () => Percentage(MarkupRuleTerms.MaxPercentBasisPoints).CalculateMarkup(new Money(long.MaxValue));

        act.Should().Throw<OverflowException>();
    }

    // ------------------------------------------------------------------ fixed

    [Theory]
    [InlineData(0)]
    [InlineData(1_000)]
    [InlineData(99_999_999)]
    public void A_fixed_rule_adds_the_same_amount_whatever_the_net_rate(long net)
    {
        Fixed(250_000).CalculateMarkup(new Money(net)).Should().Be(new Money(250_000));
    }

    // ------------------------------------------------------------------ validation

    public static TheoryData<string, MarkupRuleTerms> InvalidTerms => new()
    {
        { "fixed with a minimum cap", Fixed(1_000) with { MinMarkupMinor = new Money(10) } },
        { "fixed with a maximum cap", Fixed(1_000) with { MaxMarkupMinor = new Money(10) } },
        { "fixed and a percentage", Fixed(1_000) with { PercentBasisPoints = 100 } },
        { "fixed and negative", Fixed(-1) },
        { "fixed with no amount", Fixed(1_000) with { ValueMinor = null } },
        { "percentage with no percentage", Percentage(100) with { PercentBasisPoints = null } },
        { "percentage below zero", Percentage(-1) },
        { "percentage above the ceiling", Percentage(MarkupRuleTerms.MaxPercentBasisPoints + 1) },
        { "percentage and a fixed amount", Percentage(100) with { ValueMinor = new Money(1) } },
        { "minimum above maximum", Percentage(100) with { MinMarkupMinor = new Money(500), MaxMarkupMinor = new Money(499) } },
        { "negative minimum", Percentage(100) with { MinMarkupMinor = new Money(-1) } },
        { "global naming a product type", Percentage(100) with { ProductType = PricedProductType.Bus } },
        { "supplier with no supplier", Percentage(100) with { Scope = MarkupScope.Supplier } },
        { "supplier naming a product type", Percentage(100) with { Scope = MarkupScope.Supplier, SupplierCode = "trips_africa", ProductType = PricedProductType.Bus } },
        { "product type with no type", Percentage(100) with { Scope = MarkupScope.ProductType } },
        { "product type naming a product", Percentage(100) with { Scope = MarkupScope.ProductType, ProductType = PricedProductType.Tour, ProductId = Guid.CreateVersion7() } },
        { "product with no product", Percentage(100) with { Scope = MarkupScope.Product, ProductType = PricedProductType.Tour } },
        { "product with no type", Percentage(100) with { Scope = MarkupScope.Product, ProductId = Guid.CreateVersion7() } },
        { "product naming a supplier", Percentage(100) with { Scope = MarkupScope.Product, ProductType = PricedProductType.Tour, ProductId = Guid.CreateVersion7(), SupplierCode = "trips_africa" } },
        { "ends when it starts", Percentage(100) with { EffectiveTo = Start } },
        { "ends before it starts", Percentage(100) with { EffectiveTo = Start.AddDays(-1) } },
        { "not a currency", Percentage(100) with { Currency = "NAIRA" } },
        { "supplier code with spaces", Percentage(100) with { Scope = MarkupScope.Supplier, SupplierCode = "trips africa" } },
        { "unknown scope", Percentage(100) with { Scope = (MarkupScope)99 } },
    };

    [Theory]
    [MemberData(nameof(InvalidTerms))]
    public void Terms_that_do_not_hold_together_are_refused(string because, MarkupRuleTerms terms)
    {
        ArgumentNullException.ThrowIfNull(terms);

        var act = () => terms.Validated();

        act.Should().Throw<ArgumentException>(because);
    }

    [Fact]
    public void Validation_normalises_the_currency_and_supplier_code()
    {
        var terms = (Percentage(100) with { Scope = MarkupScope.Supplier, SupplierCode = "  Trips_Africa ", Currency = "ngn" })
            .Validated();

        terms.Currency.Should().Be("NGN");
        terms.SupplierCode.Should().Be("trips_africa");
    }

    [Fact]
    public void A_percentage_rule_with_both_caps_is_valid()
    {
        var act = () => (Percentage(1_000) with { MinMarkupMinor = new Money(100), MaxMarkupMinor = new Money(100) }).Validated();

        act.Should().NotThrow("a floor equal to the ceiling is a fixed markup in disguise, but not a contradiction");
    }

    // ------------------------------------------------------------------ helpers

    private static MarkupRuleTerms Percentage(int basisPoints) => new()
    {
        Scope = MarkupScope.Global,
        Currency = "NGN",
        CalculationType = MarkupCalculationType.Percentage,
        PercentBasisPoints = basisPoints,
        EffectiveFrom = Start,
    };

    private static MarkupRuleTerms Fixed(long valueMinor) => new()
    {
        Scope = MarkupScope.Global,
        Currency = "NGN",
        CalculationType = MarkupCalculationType.Fixed,
        ValueMinor = new Money(valueMinor),
        EffectiveFrom = Start,
    };
}
