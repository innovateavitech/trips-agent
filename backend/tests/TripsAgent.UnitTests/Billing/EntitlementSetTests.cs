using FluentAssertions;
using TripsAgent.Domain.Billing;

namespace TripsAgent.UnitTests.Billing;

/// <summary>
/// What a plan allows, and — more importantly — what it refuses.
/// </summary>
/// <remarks>
/// Every test here is about a direction of failure. An entitlement that is wrongly restrictive
/// annoys an agency; one that is wrongly permissive gives away the product, or charges a fee
/// nobody agreed. So the defaults are asserted explicitly rather than assumed.
/// </remarks>
public class EntitlementSetTests
{
    private static readonly Guid TierId = Guid.Parse("0197b000-0000-7000-8000-00000000000a");

    [Fact]
    public void An_agency_with_no_plan_gets_the_restrictive_answer_to_everything()
    {
        var fallback = EntitlementSet.Fallback;

        fallback.IsFallback.Should().BeTrue();
        fallback.IsEnabled(EntitlementCodes.CustomDomain).Should().BeFalse();
        fallback.IsEnabled(EntitlementCodes.LoyaltyProgram).Should().BeFalse();
        fallback.IsEnabled(EntitlementCodes.ApiAccess).Should().BeFalse();
        fallback.Ceiling(EntitlementCodes.MaxSubAgents).Should().Be(0);

        // Zero, not a guessed rate. A fee we never agreed would come out of a real agency's margin
        // on every quote, and a quote cannot be corrected afterwards.
        fallback.BasisPoints(EntitlementCodes.TransactionFeeBasisPoints).Should().Be(0);
    }

    [Fact]
    public void An_entitlement_the_tier_does_not_grant_falls_back_rather_than_opening_up()
    {
        // A tier that grants exactly one thing. The question is what happens to the other five.
        var set = EntitlementSet.From(TierId, "Starter",
            [KeyValuePair.Create(EntitlementCodes.CustomDomain, EntitlementValue.Flag(true))]);

        set.IsEnabled(EntitlementCodes.CustomDomain).Should().BeTrue();

        set.IsEnabled(EntitlementCodes.ApiAccess).Should().BeFalse(
            "forgetting to add an entitlement to a tier must not hand every subscriber the feature");
        set.Ceiling(EntitlementCodes.MaxSubAgents).Should().Be(0);
    }

    [Fact]
    public void A_flag_that_is_on_allows_and_one_that_is_off_refuses_with_a_reason()
    {
        var granted = EntitlementSet.From(TierId, "Growth",
            [KeyValuePair.Create(EntitlementCodes.CustomDomain, EntitlementValue.Flag(true))]);

        granted.MayUse(EntitlementCodes.CustomDomain).IsAllowed.Should().BeTrue();

        var refusal = EntitlementSet.Fallback.MayUse(EntitlementCodes.CustomDomain);

        refusal.IsAllowed.Should().BeFalse();
        refusal.Code.Should().Be(EntitlementCodes.CustomDomain);
        refusal.Detail.Should().NotBeEmpty("an agency told 'no' has to be told what would make it yes");
    }

    /// <summary>
    /// The loyalty flag, which is all of F13 that the MVP ships (issue 70, open question 24).
    /// Nothing is built behind it; the point of the test is that the flag is a real entitlement the
    /// same check answers, so the feature has a switch waiting when it is specified.
    /// </summary>
    [Fact]
    public void The_loyalty_flag_is_off_by_default_and_on_when_a_tier_grants_it()
    {
        EntitlementCatalog.Definition(EntitlementCodes.LoyaltyProgram).ValueType
            .Should().Be(EntitlementValueType.Flag);

        EntitlementSet.Fallback.MayUse(EntitlementCodes.LoyaltyProgram).IsAllowed.Should().BeFalse();

        var enterprise = EntitlementSet.From(TierId, "Enterprise",
            [KeyValuePair.Create(EntitlementCodes.LoyaltyProgram, EntitlementValue.Flag(true))]);

        enterprise.MayUse(EntitlementCodes.LoyaltyProgram).IsAllowed.Should().BeTrue();
        enterprise.IsEnabled(EntitlementCodes.LoyaltyProgram).Should().BeTrue();
    }

    [Theory]
    [InlineData(0, 1, true)]
    [InlineData(4, 1, true)]
    [InlineData(5, 1, false)]
    [InlineData(3, 2, true)]
    [InlineData(4, 2, false)]
    public void A_ceiling_of_five_allows_exactly_five(int current, int adding, bool allowed)
    {
        var set = EntitlementSet.From(TierId, "Growth",
            [KeyValuePair.Create(EntitlementCodes.MaxSubAgents, EntitlementValue.Limit(5))]);

        set.MayAdd(EntitlementCodes.MaxSubAgents, current, adding).IsAllowed.Should().Be(allowed);
    }

    [Fact]
    public void Unlimited_means_unlimited()
    {
        var set = EntitlementSet.From(TierId, "Enterprise",
            [KeyValuePair.Create(EntitlementCodes.MaxSubAgents, EntitlementValue.Limit(EntitlementValue.Unlimited))]);

        set.MayAdd(EntitlementCodes.MaxSubAgents, current: 10_000, adding: 500).IsAllowed.Should().BeTrue();
    }

    /// <summary>
    /// Build-plan decision 15: a downgrade keeps existing usage, blocks new usage and gives notice.
    /// The "keeps existing usage" half is that nothing here removes anything — the only thing an
    /// over-ceiling agency is refused is one more.
    /// </summary>
    [Fact]
    public void An_agency_over_its_new_ceiling_is_refused_one_more_and_told_where_it_stands()
    {
        var downgraded = EntitlementSet.From(TierId, "Starter",
            [KeyValuePair.Create(EntitlementCodes.MaxCatalogListings, EntitlementValue.Limit(3))]);

        var decision = downgraded.MayAdd(EntitlementCodes.MaxCatalogListings, current: 9);

        decision.IsAllowed.Should().BeFalse();
        decision.Limit.Should().Be(3);
        decision.Current.Should().Be(9, "the nine it already published are kept, not deleted");
        decision.Detail.Should().Contain("9").And.Contain("3");
    }

    [Fact]
    public void Asking_a_limit_question_of_a_flag_is_a_bug_and_says_so()
    {
        var set = EntitlementSet.Fallback;

        set.Invoking(entitlements => entitlements.MayAdd(EntitlementCodes.CustomDomain, 0))
            .Should().Throw<ArgumentException>().WithMessage("*Flag*");

        set.Invoking(entitlements => entitlements.MayUse(EntitlementCodes.MaxSubAgents))
            .Should().Throw<ArgumentException>().WithMessage("*Limit*");
    }

    [Fact]
    public void An_entitlement_the_catalogue_does_not_define_cannot_be_granted_or_asked_about()
    {
        var set = EntitlementSet.From(TierId, "Growth",
            [KeyValuePair.Create("free_ponies", EntitlementValue.Flag(true))]);

        set.All.Should().NotContainKey("free_ponies");

        set.Invoking(entitlements => entitlements.MayUse("free_ponies"))
            .Should().Throw<ArgumentException>();
    }

    [Fact]
    public void A_refusal_can_be_thrown_by_a_caller_with_nowhere_to_put_it()
    {
        var refusal = EntitlementSet.Fallback.MayUse(EntitlementCodes.ApiAccess);

        refusal.Invoking(decision => decision.EnsureAllowed())
            .Should().Throw<EntitlementRefusedException>()
            .Which.Code.Should().Be(EntitlementCodes.ApiAccess);

        EntitlementDecision.Allowed(EntitlementCodes.ApiAccess)
            .Invoking(decision => decision.EnsureAllowed()).Should().NotThrow();
    }
}
