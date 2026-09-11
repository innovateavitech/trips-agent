using FluentAssertions;
using TripsAgent.Domain.Common;
using TripsAgent.Domain.Orders;
using TripsAgent.Domain.Pricing;

namespace TripsAgent.UnitTests.Pricing;

/// <summary>
/// A catalog product can be a Package, so pricing can aim a rule at packages, and a package can be
/// quoted and sold like anything else.
/// </summary>
public class PackagePricingTests
{
    private static readonly Guid Agency = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 9, 11, 9, 30, 0, TimeSpan.Zero);

    [Fact]
    public void A_rule_for_all_packages_prices_a_package_and_nothing_else()
    {
        var packages = Terms(MarkupScope.ProductType, PricedProductType.Package).Validated();

        packages.Matches(new PricingSubject(PricedProductType.Package, "NGN")).Should().BeTrue();
        packages.Matches(new PricingSubject(PricedProductType.Tour, "NGN")).Should().BeFalse(
            "a package is not a tour, so a tour rule and a package rule never compete");
    }

    [Fact]
    public void A_rule_for_one_package_prices_that_package_only()
    {
        var packageId = Guid.CreateVersion7();
        var onePackage = Terms(MarkupScope.Product, PricedProductType.Package, packageId).Validated();

        onePackage.Matches(new PricingSubject(PricedProductType.Package, "NGN", packageId)).Should().BeTrue();
        onePackage.Matches(new PricingSubject(PricedProductType.Package, "NGN", Guid.CreateVersion7())).Should().BeFalse();
    }

    [Fact]
    public void The_most_specific_rule_wins_for_a_package_as_for_everything_else()
    {
        var packageId = Guid.CreateVersion7();
        var subject = new PricingSubject(PricedProductType.Package, "NGN", packageId);

        var everyPackage = MarkupRule.Create(Agency, Terms(MarkupScope.ProductType, PricedProductType.Package)).ToDefinition();
        var thisPackage = MarkupRule.Create(Agency, Terms(MarkupScope.Product, PricedProductType.Package, packageId)).ToDefinition();

        var winner = MarkupEngine.Resolve(subject, Now, Agency, parentAgencyId: null, [everyPackage, thisPackage]);

        winner.Rule!.Id.Should().Be(thisPackage.Id);
    }

    [Fact]
    public void A_quoted_package_becomes_an_order_line_for_a_package()
    {
        var quote = PriceQuote.Record(
            Agency,
            new PricingSubject(PricedProductType.Package, "NGN", Guid.CreateVersion7()),
            new PriceBreakdown(
                new Money(1_000_000),
                Money.Zero,
                Money.Zero,
                Money.Zero,
                new Money(1_000_000),
                "NGN",
                MarkupRule: null,
                MarkupRuleInherited: false,
                VatRateBasisPoints: 750,
                PlatformFeeBasisPoints: 0),
            Now,
            TimeSpan.FromMinutes(30));

        var line = OrderLine.FromQuote(quote, "Zanzibar honeymoon package", """{"adults":2}""", Now);

        line.ItemType.Should().Be(OrderLineItemType.Package);
    }

    [Fact]
    public void Every_priced_product_type_can_be_sold_on_an_order_line()
    {
        // The two enums are separate on purpose, but a type pricing can quote and orders cannot
        // sell would fail at checkout. Same names, so this is one comparison.
        Enum.GetNames<PricedProductType>().Should().BeEquivalentTo(Enum.GetNames<OrderLineItemType>());
    }

    private static MarkupRuleTerms Terms(MarkupScope scope, PricedProductType productType, Guid? productId = null) => new()
    {
        Scope = scope,
        ProductType = productType,
        ProductId = productId,
        Currency = "NGN",
        CalculationType = MarkupCalculationType.Percentage,
        PercentBasisPoints = 1_000,
        EffectiveFrom = Now.AddDays(-1),
    };
}
