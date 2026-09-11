using FluentAssertions;
using TripsAgent.Domain.Common;
using TripsAgent.Domain.Pricing;

namespace TripsAgent.UnitTests.Pricing;

/// <summary>Editing a rule means replacing it, so the id stored on a price keeps meaning something.</summary>
public class MarkupRuleTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly Guid Agency = Guid.CreateVersion7();

    [Fact]
    public void Replacing_a_rule_retires_it_and_points_at_the_replacement()
    {
        var original = MarkupRule.Create(Agency, Terms(1_000));
        var at = Start.AddDays(10);

        var replacement = original.ReplaceWith(Terms(1_500), at);

        replacement.Id.Should().NotBe(original.Id, "a new id is what lets old quotes keep naming the old terms");
        replacement.PercentBasisPoints.Should().Be(1_500);
        replacement.AgencyId.Should().Be(Agency);
        replacement.EffectiveFrom.Should().Be(at);

        original.EffectiveTo.Should().Be(at);
        original.SupersededById.Should().Be(replacement.Id);
        original.PercentBasisPoints.Should().Be(1_000, "the original's terms are untouched");
    }

    [Fact]
    public void The_replacement_takes_over_at_the_instant_the_original_stops()
    {
        var original = MarkupRule.Create(Agency, Terms(1_000));
        var at = Start.AddDays(10);

        var replacement = original.ReplaceWith(Terms(1_500), at);

        // No gap in which nothing applies, and no overlap in which both do.
        original.Terms.IsEffectiveAt(at).Should().BeFalse();
        replacement.Terms.IsEffectiveAt(at).Should().BeTrue();
        original.Terms.IsEffectiveAt(at.AddTicks(-1)).Should().BeTrue();
    }

    [Fact]
    public void A_replacement_cannot_be_backdated()
    {
        var original = MarkupRule.Create(Agency, Terms(1_000));
        var at = Start.AddDays(10);

        var replacement = original.ReplaceWith(Terms(1_500) with { EffectiveFrom = Start }, at);

        replacement.EffectiveFrom.Should().Be(at);
    }

    [Fact]
    public void A_rule_can_only_be_replaced_once()
    {
        var original = MarkupRule.Create(Agency, Terms(1_000));
        original.ReplaceWith(Terms(1_500), Start.AddDays(1));

        var act = () => original.ReplaceWith(Terms(2_000), Start.AddDays(2));

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Invalid_new_terms_leave_the_original_in_force()
    {
        var original = MarkupRule.Create(Agency, Terms(1_000));

        var act = () => original.ReplaceWith(Terms(-5), Start.AddDays(1));

        act.Should().Throw<ArgumentException>();
        original.EffectiveTo.Should().BeNull();
        original.SupersededById.Should().BeNull();
    }

    [Fact]
    public void Retiring_a_rule_that_has_not_started_leaves_it_never_having_applied()
    {
        var future = MarkupRule.Create(Agency, Terms(1_000) with { EffectiveFrom = Start.AddDays(30) });

        future.Retire(Start);

        future.EffectiveTo.Should().Be(future.EffectiveFrom);
        future.Terms.IsEffectiveAt(future.EffectiveFrom).Should().BeFalse();
    }

    [Fact]
    public void Retiring_never_extends_a_rule()
    {
        var rule = MarkupRule.Create(Agency, Terms(1_000) with { EffectiveTo = Start.AddDays(5) });

        rule.Retire(Start.AddDays(20));

        rule.EffectiveTo.Should().Be(Start.AddDays(5));
    }

    [Fact]
    public void A_quote_records_the_rule_that_priced_it()
    {
        var subject = new PricingSubject(PricedProductType.Tour, "NGN", Guid.CreateVersion7());
        var rule = MarkupRule.Create(Agency, Terms(1_000));

        var price = MarkupEngine.Price(subject, new Money(100_000), Start.AddDays(1), Agency, null, [rule.ToDefinition()]);
        var quote = PriceQuote.Record(Agency, subject, price);

        quote.MarkupRuleId.Should().Be(rule.Id);
        quote.NetAmountMinor.Should().Be(new Money(100_000));
        quote.MarkupAmountMinor.Should().Be(new Money(10_000));
        quote.GrossAmountMinor.Should().Be(new Money(110_000));
        quote.ProductId.Should().Be(subject.ProductId);
    }

    [Fact]
    public void A_quote_refuses_a_price_in_another_currency()
    {
        var naira = new PricingSubject(PricedProductType.Tour, "NGN");
        var dollars = new PricingSubject(PricedProductType.Tour, "USD");

        var price = MarkupEngine.Price(dollars, new Money(100), Start, Agency, null, []);

        var act = () => PriceQuote.Record(Agency, naira, price);

        act.Should().Throw<ArgumentException>();
    }

    private static MarkupRuleTerms Terms(int basisPoints) => new()
    {
        Scope = MarkupScope.Global,
        Currency = "NGN",
        CalculationType = MarkupCalculationType.Percentage,
        PercentBasisPoints = basisPoints,
        EffectiveFrom = Start,
    };
}
