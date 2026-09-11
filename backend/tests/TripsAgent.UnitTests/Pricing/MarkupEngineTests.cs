using FluentAssertions;
using TripsAgent.Domain.Common;
using TripsAgent.Domain.Pricing;

namespace TripsAgent.UnitTests.Pricing;

/// <summary>
/// Which rule wins. Every test here would pass against a wrong engine only by coincidence, so each
/// one gives the rule that should <i>lose</i> every other advantage it can.
/// </summary>
public class MarkupEngineTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 11, 12, 0, 0, TimeSpan.Zero);

    private static readonly Guid Agency = Guid.Parse("0197a000-0000-7000-8000-00000000000a");
    private static readonly Guid Parent = Guid.Parse("0197a000-0000-7000-8000-00000000000b");
    private static readonly Guid Stranger = Guid.Parse("0197a000-0000-7000-8000-00000000000c");

    private static readonly Guid ProductId = Guid.Parse("0197a000-0000-7000-8000-0000000000f1");

    private static readonly Guid SmallestId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid LargestId = Guid.Parse("ffffffff-ffff-ffff-ffff-fffffffffffe");

    /// <summary>Something every scope can match: a specific flight product, from a supplier.</summary>
    private static readonly PricingSubject Subject =
        new(PricedProductType.Flight, "NGN", ProductId, "trips_africa");

    // ------------------------------------------------------------------ precedence

    public static TheoryData<MarkupScope, MarkupScope> EachLevelAndTheLevelBelowIt => new()
    {
        { MarkupScope.Product, MarkupScope.ProductType },
        { MarkupScope.ProductType, MarkupScope.Supplier },
        { MarkupScope.Supplier, MarkupScope.Global },
    };

    [Theory]
    [MemberData(nameof(EachLevelAndTheLevelBelowIt))]
    public void Each_precedence_level_beats_the_one_below_it(MarkupScope higher, MarkupScope lower)
    {
        // The lower rule gets every tie-breaker going: far higher priority, the more recent
        // start and the larger id. Scope must still decide it.
        var lowerRule = Rule(lower, priority: 1_000, from: Now.AddDays(-1), id: LargestId);
        var higherRule = Rule(higher, priority: 0, from: Now.AddDays(-30), id: SmallestId);

        Resolve(lowerRule, higherRule).Rule!.Id.Should().Be(higherRule.Id);
        Resolve(higherRule, lowerRule).Rule!.Id.Should().Be(higherRule.Id, "the order rules arrive in must not matter");
    }

    public static TheoryData<MarkupScope[], MarkupScope> RulesPresentAndTheExpectedWinner => new()
    {
        { [MarkupScope.Global, MarkupScope.Supplier, MarkupScope.ProductType, MarkupScope.Product], MarkupScope.Product },
        { [MarkupScope.Global, MarkupScope.Supplier, MarkupScope.ProductType], MarkupScope.ProductType },
        { [MarkupScope.Global, MarkupScope.Supplier], MarkupScope.Supplier },
        { [MarkupScope.Global], MarkupScope.Global },
    };

    [Theory]
    [MemberData(nameof(RulesPresentAndTheExpectedWinner))]
    public void The_narrowest_rule_present_wins(MarkupScope[] present, MarkupScope expected)
    {
        var rules = present.Select(scope => Rule(scope, percent: 100 * (int)scope)).ToArray();

        Resolve(rules).Rule!.Terms.Scope.Should().Be(expected);
    }

    [Fact]
    public void Within_a_scope_the_higher_priority_wins()
    {
        // The loser started more recently, so a tie-break on dates alone would pick it.
        var low = Rule(MarkupScope.Global, priority: 1, from: Now.AddHours(-1), id: LargestId);
        var high = Rule(MarkupScope.Global, priority: 5, from: Now.AddDays(-10), id: SmallestId);

        Resolve(low, high).Rule!.Id.Should().Be(high.Id);
        Resolve(high, low).Rule!.Id.Should().Be(high.Id);
    }

    [Fact]
    public void With_equal_priority_the_most_recent_start_wins()
    {
        var older = Rule(MarkupScope.ProductType, priority: 3, from: Now.AddDays(-10), id: LargestId);
        var newer = Rule(MarkupScope.ProductType, priority: 3, from: Now.AddDays(-2), id: SmallestId);

        Resolve(older, newer).Rule!.Id.Should().Be(newer.Id);
        Resolve(newer, older).Rule!.Id.Should().Be(newer.Id);
    }

    [Fact]
    public void Two_otherwise_identical_rules_always_resolve_to_the_same_one()
    {
        var from = Now.AddDays(-1);
        var a = Rule(MarkupScope.Global, priority: 0, from: from, id: SmallestId);
        var b = Rule(MarkupScope.Global, priority: 0, from: from, id: LargestId);

        // Deterministic means the same answer on every server, every time — not whichever row the
        // database happened to return first.
        Resolve(a, b).Rule!.Id.Should().Be(LargestId);
        Resolve(b, a).Rule!.Id.Should().Be(LargestId);
    }

    // ------------------------------------------------------------------ what counts as matching

    [Fact]
    public void A_rule_that_has_not_started_is_ignored()
    {
        var future = Rule(MarkupScope.Product, from: Now.AddMinutes(1));
        var current = Rule(MarkupScope.Global);

        Resolve(future, current).Rule!.Id.Should().Be(current.Id);
    }

    [Fact]
    public void A_rule_whose_end_is_now_has_ended()
    {
        // The end is exclusive. At the instant a replacement starts, the rule it replaced must not
        // also be in force, or both would claim the same sale.
        var ended = Rule(MarkupScope.Product, from: Now.AddDays(-5), to: Now);
        var current = Rule(MarkupScope.Global);

        Resolve(ended, current).Rule!.Id.Should().Be(current.Id);
    }

    [Fact]
    public void A_rule_that_starts_now_applies_now()
    {
        var starting = Rule(MarkupScope.Product, from: Now);

        Resolve(starting).Rule!.Id.Should().Be(starting.Id);
    }

    [Fact]
    public void A_rule_in_another_currency_is_ignored()
    {
        var dollars = Rule(MarkupScope.Product, currency: "USD");

        Resolve(dollars).Rule.Should().BeNull();
    }

    [Fact]
    public void A_supplier_rule_for_another_supplier_is_ignored()
    {
        var other = Rule(MarkupScope.Supplier, supplierCode: "another_supplier");

        Resolve(other).Rule.Should().BeNull();
    }

    [Fact]
    public void A_product_rule_for_another_product_is_ignored()
    {
        var other = Rule(MarkupScope.Product, productId: Guid.CreateVersion7());

        Resolve(other).Rule.Should().BeNull();
    }

    [Fact]
    public void A_product_type_rule_for_another_type_is_ignored()
    {
        var buses = Rule(MarkupScope.ProductType, productType: PricedProductType.Bus);

        Resolve(buses).Rule.Should().BeNull();
    }

    [Fact]
    public void A_rule_belonging_to_an_unrelated_agency_is_ignored()
    {
        // Passing too many rules must be safe. The engine is the last line if a query ever
        // returned another agency's rows.
        var theirs = Rule(MarkupScope.Product, agency: Stranger);

        Resolve(theirs).Should().Be(MarkupResolution.None);
    }

    // ------------------------------------------------------------------ sub-agents

    [Fact]
    public void A_sub_agent_with_no_matching_rule_inherits_its_principals()
    {
        var principals = Rule(MarkupScope.Global, agency: Parent);

        var resolution = MarkupEngine.Resolve(Subject, Now, Agency, Parent, [principals]);

        resolution.Rule!.Id.Should().Be(principals.Id);
        resolution.IsInherited.Should().BeTrue();
    }

    [Fact]
    public void A_sub_agents_own_rule_overrides_even_a_narrower_principal_rule()
    {
        // The sub-agent's decision about its own prices beats the principal's, whatever scope the
        // principal wrote it at. Otherwise a sub-agent could never override a product rule.
        var principalsProductRule = Rule(MarkupScope.Product, agency: Parent, priority: 100);
        var ownGlobal = Rule(MarkupScope.Global, agency: Agency);

        var resolution = MarkupEngine.Resolve(Subject, Now, Agency, Parent, [principalsProductRule, ownGlobal]);

        resolution.Rule!.Id.Should().Be(ownGlobal.Id);
        resolution.IsInherited.Should().BeFalse();
    }

    [Fact]
    public void Precedence_still_applies_among_inherited_rules()
    {
        var global = Rule(MarkupScope.Global, agency: Parent, priority: 50);
        var product = Rule(MarkupScope.Product, agency: Parent);

        MarkupEngine.Resolve(Subject, Now, Agency, Parent, [global, product])
            .Rule!.Id.Should().Be(product.Id);
    }

    [Fact]
    public void A_principal_rule_kept_from_sub_agents_is_not_inherited()
    {
        var principalOnly = Rule(MarkupScope.Global, agency: Parent, appliesToSubAgents: false);

        MarkupEngine.Resolve(Subject, Now, Agency, Parent, [principalOnly])
            .Should().Be(MarkupResolution.None);
    }

    [Fact]
    public void A_principal_does_not_inherit_from_its_sub_agents()
    {
        var subAgents = Rule(MarkupScope.Product, agency: Agency);

        // Pricing as the principal, with no parent of its own.
        MarkupEngine.Resolve(Subject, Now, Parent, null, [subAgents])
            .Should().Be(MarkupResolution.None);
    }

    [Fact]
    public void A_principal_keeps_its_own_rules_even_when_they_are_not_for_sub_agents()
    {
        var principalOnly = Rule(MarkupScope.Global, agency: Parent, appliesToSubAgents: false);

        MarkupEngine.Resolve(Subject, Now, Parent, null, [principalOnly])
            .Rule!.Id.Should().Be(principalOnly.Id);
    }

    // ------------------------------------------------------------------ the price

    [Fact]
    public void The_price_carries_the_winning_rule_id_and_adds_its_markup()
    {
        var global = Rule(MarkupScope.Global, percent: 500);
        var product = Rule(MarkupScope.Product, percent: 1_000);

        var price = MarkupEngine.Price(Subject, new Money(200_000), Now, Agency, null, [global, product]);

        price.MarkupRuleId.Should().Be(product.Id);
        price.MarkupAmountMinor.Should().Be(new Money(20_000));
        price.GrossAmountMinor.Should().Be(new Money(220_000));
        price.NetAmountMinor.Should().Be(new Money(200_000));
        price.Currency.Should().Be("NGN");
        price.MarkupRuleInherited.Should().BeFalse();
    }

    [Fact]
    public void With_no_rule_the_markup_is_zero_and_no_rule_is_named()
    {
        var price = MarkupEngine.Price(Subject, new Money(200_000), Now, Agency, null, []);

        price.MarkupAmountMinor.Should().Be(Money.Zero);
        price.GrossAmountMinor.Should().Be(new Money(200_000));
        price.MarkupRuleId.Should().BeNull();
    }

    [Fact]
    public void An_inherited_price_says_so()
    {
        var principals = Rule(MarkupScope.Global, agency: Parent);

        var price = MarkupEngine.Price(Subject, new Money(10_000), Now, Agency, Parent, [principals]);

        price.MarkupRuleId.Should().Be(principals.Id);
        price.MarkupRuleInherited.Should().BeTrue();
    }

    [Fact]
    public void A_negative_net_rate_is_refused()
    {
        var act = () => MarkupEngine.Price(Subject, new Money(-1), Now, Agency, null, []);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    // ------------------------------------------------------------------ helpers

    private static MarkupResolution Resolve(params MarkupRuleDefinition[] rules) =>
        MarkupEngine.Resolve(Subject, Now, Agency, null, rules);

    private static MarkupRuleDefinition Rule(
        MarkupScope scope,
        int percent = 1_000,
        int priority = 0,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null,
        Guid? id = null,
        Guid? agency = null,
        bool appliesToSubAgents = true,
        string currency = "NGN",
        string supplierCode = "trips_africa",
        Guid? productId = null,
        PricedProductType productType = PricedProductType.Flight)
    {
        var terms = new MarkupRuleTerms
        {
            Scope = scope,
            ProductType = scope is MarkupScope.ProductType or MarkupScope.Product ? productType : null,
            ProductId = scope == MarkupScope.Product ? productId ?? ProductId : null,
            SupplierCode = scope == MarkupScope.Supplier ? supplierCode : null,
            Currency = currency,
            CalculationType = MarkupCalculationType.Percentage,
            PercentBasisPoints = percent,
            Priority = priority,
            AppliesToSubAgents = appliesToSubAgents,
            EffectiveFrom = from ?? Now.AddDays(-7),
            EffectiveTo = to,
        }.Validated();

        return new MarkupRuleDefinition(id ?? Guid.CreateVersion7(), agency ?? Agency, terms);
    }
}
