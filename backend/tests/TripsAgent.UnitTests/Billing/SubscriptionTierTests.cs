using FluentAssertions;
using TripsAgent.Domain.Billing;
using TripsAgent.Domain.Common;

namespace TripsAgent.UnitTests.Billing;

/// <summary>
/// A tier's lifecycle, and the two rules that protect what an invoice refers to: it is archived
/// rather than deleted, and its prices are closed rather than edited.
/// </summary>
public class SubscriptionTierTests
{
    private static readonly DateTimeOffset Now = new(2026, 3, 1, 4, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_tier_is_created_in_draft_and_cannot_be_subscribed_to()
    {
        var tier = SubscriptionTier.Draft("growth", "Growth");

        tier.Status.Should().Be(TierStatus.Draft);
        tier.AcceptsNewSubscribers.Should().BeFalse();
    }

    [Fact]
    public void There_is_no_way_to_delete_a_tier_from_the_domain_at_all()
    {
        typeof(SubscriptionTier).GetMethods()
            .Select(method => method.Name)
            .Should().NotContain(name => name.Contains("Delete", StringComparison.Ordinal),
                "archiving is the operation; a tier is what an invoice says the agency was charged for");
    }

    [Fact]
    public void A_tier_with_no_price_cannot_be_published()
    {
        var tier = SubscriptionTier.Draft("growth", "Growth");

        tier.Invoking(candidate => candidate.Publish(Now))
            .Should().Throw<InvalidOperationException>().WithMessage("*no price*");
    }

    [Fact]
    public void The_free_fallback_plan_may_be_published_without_a_price()
    {
        var tier = SubscriptionTier.Draft("free", "Free");
        tier.SetFallback(true);

        tier.Invoking(candidate => candidate.Publish(Now)).Should().NotThrow();
        tier.AcceptsNewSubscribers.Should().BeTrue();
    }

    [Fact]
    public void Archiving_stops_new_subscribers_and_nothing_else()
    {
        var tier = SubscriptionTier.Draft("legacy", "Legacy");
        tier.SetPrice("NGN", BillingInterval.Monthly, new Money(1_000_000), Now);
        tier.Publish(Now);

        tier.Archive(Now.AddDays(1));

        tier.Status.Should().Be(TierStatus.Archived);
        tier.AcceptsNewSubscribers.Should().BeFalse();
        tier.Prices.Should().NotBeEmpty("existing subscribers still point at the price they agreed");
    }

    [Fact]
    public void The_fallback_plan_cannot_be_archived_while_it_is_where_failed_payments_land()
    {
        var tier = SubscriptionTier.Draft("free", "Free");
        tier.SetFallback(true);
        tier.Publish(Now);

        tier.Invoking(candidate => candidate.Archive(Now.AddDays(1)))
            .Should().Throw<InvalidOperationException>().WithMessage("*fallback*");
    }

    [Fact]
    public void An_archived_tier_cannot_be_published_again_without_being_restored_first()
    {
        var tier = SubscriptionTier.Draft("legacy", "Legacy");
        tier.SetPrice("NGN", BillingInterval.Monthly, new Money(1_000_000), Now);
        tier.Publish(Now);
        tier.Archive(Now.AddDays(1));

        tier.Invoking(candidate => candidate.Publish(Now.AddDays(2)))
            .Should().Throw<InvalidOperationException>();

        tier.Restore();
        tier.Status.Should().Be(TierStatus.Published, "it had been published before it was archived");
    }

    /// <summary>
    /// CLAUDE.md rule 5, one level up from an order line: a subscriber points at the exact price row
    /// it agreed to, so repricing a tier must not rewrite what that subscriber was told they'd pay.
    /// </summary>
    [Fact]
    public void Repricing_closes_the_old_price_rather_than_changing_it()
    {
        var tier = SubscriptionTier.Draft("growth", "Growth");
        var original = tier.SetPrice("NGN", BillingInterval.Monthly, new Money(2_500_000), Now);

        var replacement = tier.SetPrice("NGN", BillingInterval.Monthly, new Money(3_000_000), Now.AddDays(30));

        original.AmountMinor.AmountMinor.Should().Be(2_500_000, "the old price is evidence, not a draft");
        original.EffectiveTo.Should().Be(Now.AddDays(30));
        replacement.AmountMinor.AmountMinor.Should().Be(3_000_000);

        tier.PriceAt("NGN", BillingInterval.Monthly, Now.AddDays(1)).Should().Be(original);
        tier.PriceAt("NGN", BillingInterval.Monthly, Now.AddDays(31)).Should().Be(replacement);
    }

    [Fact]
    public void Prices_are_kept_per_currency_and_interval()
    {
        var tier = SubscriptionTier.Draft("growth", "Growth");
        tier.SetPrice("NGN", BillingInterval.Monthly, new Money(2_500_000), Now);
        tier.SetPrice("NGN", BillingInterval.Annual, new Money(25_000_000), Now);

        tier.PriceAt("ngn", BillingInterval.Monthly, Now)!.AmountMinor.AmountMinor.Should().Be(2_500_000);
        tier.PriceAt("NGN", BillingInterval.Annual, Now)!.AmountMinor.AmountMinor.Should().Be(25_000_000);
        tier.PriceAt("USD", BillingInterval.Monthly, Now).Should().BeNull();
    }

    [Fact]
    public void A_grant_whose_shape_does_not_match_the_entitlement_is_refused()
    {
        var tier = SubscriptionTier.Draft("growth", "Growth");
        var customDomain = new Entitlement(
            EntitlementCodes.CustomDomain, "Custom domain", "…", EntitlementValueType.Flag);

        tier.Invoking(candidate => candidate.Grant(customDomain, EntitlementValue.Limit(5)))
            .Should().Throw<ArgumentException>().WithMessage("*Flag*");

        tier.Invoking(candidate => candidate.Grant(customDomain, EntitlementValue.Flag(true)))
            .Should().NotThrow();
    }

    [Fact]
    public void Granting_the_same_entitlement_twice_changes_it_rather_than_adding_a_second_row()
    {
        var tier = SubscriptionTier.Draft("growth", "Growth");
        var subAgents = new Entitlement(
            EntitlementCodes.MaxSubAgents, "Sub-agents", "…", EntitlementValueType.Limit);

        tier.Grant(subAgents, EntitlementValue.Limit(5));
        tier.Grant(subAgents, EntitlementValue.Limit(25));

        tier.Entitlements.Should().ContainSingle()
            .Which.TypedValue.Ceiling.Should().Be(25);
    }

    [Theory]
    [InlineData("Growth Plan")]
    [InlineData("growth plan")]
    [InlineData("GROWTH!")]
    public void A_tier_code_is_lowercase_and_url_safe(string code)
    {
        FluentActions.Invoking(() => SubscriptionTier.Draft(code, "Growth"))
            .Should().Throw<ArgumentException>();
    }

    [Fact]
    public void A_trial_cannot_be_longer_than_ninety_days()
    {
        FluentActions.Invoking(() => SubscriptionTier.Draft("growth", "Growth", trialDays: 91))
            .Should().Throw<ArgumentOutOfRangeException>();
    }
}
