using FluentAssertions;
using TripsAgent.Domain.Common;
using TripsAgent.Domain.Pricing;

namespace TripsAgent.UnitTests.Pricing;

/// <summary>
/// Everything after the rule is chosen: VAT, the platform fee, the gross, what the quote stores
/// and when it stops being chargeable.
/// </summary>
public class PriceCalculationTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 11, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Agency = Guid.Parse("0197a000-0000-7000-8000-00000000000a");
    private static readonly Guid Parent = Guid.Parse("0197a000-0000-7000-8000-00000000000b");

    private static readonly PricingSubject Flight = new(PricedProductType.Flight, "NGN", supplierCode: "trips_africa");

    private static readonly PricingRates NigerianVat = new(750, 0);

    // ------------------------------------------------------------------ VAT

    [Fact]
    public void Vat_is_charged_on_the_markup_and_not_on_the_net_fare()
    {
        var price = Price(new Money(1_000_000), NigerianVat, Percent(1_000));

        price.MarkupAmountMinor.Should().Be(new Money(100_000));

        // 7.5% of the ₦1,000 markup — not of the ₦11,000 sell price, which would be ₦825.
        price.TaxAmountMinor.Should().Be(new Money(7_500));
        price.GrossAmountMinor.Should().Be(new Money(1_107_500));
    }

    [Fact]
    public void With_no_markup_there_is_no_vat_and_the_traveller_pays_the_net_fare()
    {
        var price = Price(new Money(1_000_000), NigerianVat);

        price.TaxAmountMinor.Should().Be(Money.Zero);
        price.GrossAmountMinor.Should().Be(new Money(1_000_000));
    }

    [Theory]
    [InlineData(20, 2, "7.5% of 20 kobo is 1.5 — exactly half rounds up")]
    [InlineData(19, 1, "7.5% of 19 kobo is 1.425 — below half rounds down")]
    [InlineData(60, 5, "7.5% of 60 kobo is 4.5 — exactly half rounds up")]
    [InlineData(6, 0, "7.5% of 6 kobo is 0.45 — below half rounds down, to nothing")]
    [InlineData(7, 1, "7.5% of 7 kobo is 0.525 — above half rounds up")]
    public void Vat_rounds_half_up_to_the_kobo(long markupMinor, long expectedVatMinor, string because)
    {
        var price = Price(new Money(1_000), NigerianVat, Fixed(markupMinor));

        price.TaxAmountMinor.Should().Be(new Money(expectedVatMinor), because);
        price.GrossAmountMinor.Should().Be(new Money(1_000 + markupMinor + expectedVatMinor));
    }

    [Fact]
    public void The_sellers_own_vat_rate_is_used()
    {
        var price = Price(new Money(1_000_000), new PricingRates(500, 0), Percent(1_000));

        price.VatRateBasisPoints.Should().Be(500);
        price.TaxAmountMinor.Should().Be(new Money(5_000));
    }

    // ------------------------------------------------------------------ the platform fee

    [Fact]
    public void The_platform_fee_comes_out_of_the_margin_and_never_reaches_the_travellers_price()
    {
        var withoutFee = Price(new Money(1_000_000), NigerianVat, Percent(1_000));
        var withFee = Price(new Money(1_000_000), new PricingRates(750, 200), Percent(1_000));

        // 2% of the pre-VAT sell price, ₦11,000.
        withFee.PlatformFeeMinor.Should().Be(new Money(22_000));
        withFee.PlatformFeeBaseMinor.Should().Be(new Money(1_100_000));

        withFee.GrossAmountMinor.Should().Be(withoutFee.GrossAmountMinor, "the traveller pays the same either way");
        withFee.AgentMarginMinor.Should().Be(new Money(78_000));
    }

    [Fact]
    public void A_fee_larger_than_the_markup_is_reported_as_a_negative_margin_not_hidden()
    {
        var price = Price(new Money(1_000_000), new PricingRates(0, 1_000), Fixed(100));

        price.PlatformFeeMinor.Should().Be(new Money(100_010));
        price.AgentMarginMinor.Should().Be(new Money(-99_910));
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(10_001, 0)]
    [InlineData(0, -1)]
    [InlineData(0, 10_001)]
    public void A_rate_outside_nought_to_a_hundred_percent_is_refused(int vat, int fee)
    {
        var act = () => Price(new Money(1_000), new PricingRates(vat, fee));

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Every_price_is_in_the_base_currency_so_the_fx_rate_is_exactly_one()
    {
        Price(new Money(1_000), NigerianVat).FxRate.Should().Be(1m);
    }

    // ------------------------------------------------------------------ describing a rate and a rule

    [Theory]
    [InlineData(1_000, "10%")]
    [InlineData(750, "7.5%")]
    [InlineData(1_234, "12.34%")]
    [InlineData(1_050, "10.5%")]
    [InlineData(5, "0.05%")]
    [InlineData(0, "0%")]
    public void Basis_points_read_as_a_percentage(int basisPoints, string expected)
    {
        BasisPoints.Format(basisPoints).Should().Be(expected);
    }

    [Fact]
    public void A_percentage_rule_describes_itself_with_its_caps()
    {
        var terms = Percent(1_000).Terms with { MinMarkupMinor = new Money(2_000), MaxMarkupMinor = new Money(50_000) };

        terms.Describe().Should().Be("10% of the net rate, at least NGN 20.00, at most NGN 500.00");
    }

    [Fact]
    public void A_fixed_rule_describes_itself()
    {
        Fixed(150_000).Terms.Describe().Should().Be("a fixed NGN 1500.00");
    }

    // ------------------------------------------------------------------ the quote

    [Fact]
    public void A_quote_stores_every_figure_and_the_whole_calculation()
    {
        var rule = Percent(1_000);
        var price = Price(new Money(1_000_000), new PricingRates(750, 200), rule);

        var quote = PriceQuote.Record(Agency, Flight, price, Now, TimeSpan.FromMinutes(30));

        quote.TaxAmountMinor.Should().Be(new Money(7_500));
        quote.PlatformFeeMinor.Should().Be(new Money(22_000));
        quote.GrossAmountMinor.Should().Be(new Money(1_107_500));
        quote.FxRate.Should().Be(1m);

        var breakdown = quote.ReadBreakdown();
        breakdown.Version.Should().Be(PriceQuoteBreakdown.CurrentVersion);
        breakdown.Currency.Should().Be("NGN");
        breakdown.NetAmountMinor.Should().Be(1_000_000);
        breakdown.MarkupAmountMinor.Should().Be(100_000);
        breakdown.MarkupRule!.Id.Should().Be(rule.Id);
        breakdown.MarkupRule.PercentBasisPoints.Should().Be(1_000);
        breakdown.MarkupRule.Summary.Should().Be("10% of the net rate");
        breakdown.VatRateBasisPoints.Should().Be(750);
        breakdown.VatBaseMinor.Should().Be(100_000, "VAT is charged on the markup");
        breakdown.TaxAmountMinor.Should().Be(7_500);
        breakdown.PlatformFeeBasisPoints.Should().Be(200);
        breakdown.PlatformFeeBaseMinor.Should().Be(1_100_000);
        breakdown.PlatformFeeMinor.Should().Be(22_000);
        breakdown.AgentMarginMinor.Should().Be(78_000);
        breakdown.GrossAmountMinor.Should().Be(1_107_500);
        breakdown.FxRate.Should().Be(1m);
    }

    [Fact]
    public void A_quote_priced_by_an_inherited_rule_says_so_in_its_breakdown()
    {
        var principals = Percent(1_000, agency: Parent);
        var price = MarkupEngine.Price(Flight, new Money(1_000), Now, Agency, Parent, [principals], NigerianVat);

        var quote = PriceQuote.Record(Agency, Flight, price, Now, TimeSpan.FromMinutes(30));

        quote.ReadBreakdown().MarkupRuleInherited.Should().BeTrue();
    }

    [Fact]
    public void A_quote_expires_exactly_its_validity_after_it_was_priced()
    {
        var quote = PriceQuote.Record(Agency, Flight, Price(new Money(1_000), NigerianVat), Now, TimeSpan.FromMinutes(30));

        quote.ExpiresAt.Should().Be(Now.AddMinutes(30));
        quote.IsExpiredAt(Now.AddMinutes(30).AddTicks(-1)).Should().BeFalse();
        quote.IsExpiredAt(Now.AddMinutes(30)).Should().BeTrue("the boundary itself is expired");
    }

    [Fact]
    public void An_expired_quote_refuses_to_be_used()
    {
        var quote = PriceQuote.Record(Agency, Flight, Price(new Money(1_000), NigerianVat), Now, TimeSpan.FromMinutes(30));

        quote.Invoking(q => q.EnsureUsableAt(Now.AddMinutes(29))).Should().NotThrow();
        quote.Invoking(q => q.EnsureUsableAt(Now.AddMinutes(30)))
            .Should().Throw<PriceQuoteExpiredException>()
            .Which.ExpiredAt.Should().Be(Now.AddMinutes(30));
    }

    [Fact]
    public void A_quote_must_be_valid_for_some_time()
    {
        var price = Price(new Money(1_000), NigerianVat);

        var act = () => PriceQuote.Record(Agency, Flight, price, Now, TimeSpan.Zero);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    // ------------------------------------------------------------------ helpers

    private static PriceBreakdown Price(Money net, PricingRates rates, params MarkupRuleDefinition[] rules) =>
        MarkupEngine.Price(Flight, net, Now, Agency, null, rules, rates);

    private static MarkupRuleDefinition Percent(int basisPoints, Guid? agency = null) =>
        new(Guid.CreateVersion7(), agency ?? Agency, new MarkupRuleTerms
        {
            Scope = MarkupScope.Global,
            Currency = "NGN",
            CalculationType = MarkupCalculationType.Percentage,
            PercentBasisPoints = basisPoints,
            EffectiveFrom = Now.AddDays(-1),
        });

    private static MarkupRuleDefinition Fixed(long valueMinor) =>
        new(Guid.CreateVersion7(), Agency, new MarkupRuleTerms
        {
            Scope = MarkupScope.Global,
            Currency = "NGN",
            CalculationType = MarkupCalculationType.Fixed,
            ValueMinor = new Money(valueMinor),
            EffectiveFrom = Now.AddDays(-1),
        });
}
