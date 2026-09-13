using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using TripsAgent.Application.Billing;
using TripsAgent.Domain.Billing;
using TripsAgent.IntegrationTests.Persistence;

namespace TripsAgent.IntegrationTests.Billing;

/// <summary>
/// A published tier's entitlements are enforced at runtime.
/// </summary>
/// <remarks>
/// The acceptance test for issue 64's enforcement box. Every case here goes through the same
/// <see cref="IEntitlements"/> a real feature calls, against a real tier in a real database, so
/// what passes here is what a feature gets.
/// </remarks>
[Collection(PostgresCollection.Name)]
public class EntitlementEnforcementTests
{
    private readonly PostgresFixture _postgres;

    public EntitlementEnforcementTests(PostgresFixture postgres) => _postgres = postgres;

    [Fact]
    public async Task An_agency_gets_exactly_what_its_published_tier_grants()
    {
        await using var world = await BillingWorld.CreateAsync(_postgres);

        var agencyId = await world.AddAgencyAsync("Acme Travel Limited", "acme");

        var tierId = await world.AddPublishedTierAsync("growth", "Growth", 2_500_000,
        [
            new EntitlementGrant(EntitlementCodes.MaxSubAgents, "5"),
            new EntitlementGrant(EntitlementCodes.CustomDomain, "true"),
            new EntitlementGrant(EntitlementCodes.TransactionFeeBasisPoints, "150"),
            new EntitlementGrant(EntitlementCodes.MaxCatalogListings, "-1"),
        ]);

        await world.SubscribeAsync(agencyId, tierId);

        var entitlements = await world.Entitlements.ForAsync(agencyId);

        entitlements.TierId.Should().Be(tierId);
        entitlements.TierName.Should().Be("Growth");
        entitlements.Ceiling(EntitlementCodes.MaxSubAgents).Should().Be(5);
        entitlements.IsEnabled(EntitlementCodes.CustomDomain).Should().BeTrue();
        entitlements.BasisPoints(EntitlementCodes.TransactionFeeBasisPoints).Should().Be(150);
        entitlements.Value(EntitlementCodes.MaxCatalogListings).IsUnlimited.Should().BeTrue();

        // Not granted by this tier, so it falls back to the catalogue's default rather than opening up.
        entitlements.IsEnabled(EntitlementCodes.ApiAccess).Should().BeFalse();
        entitlements.IsEnabled(EntitlementCodes.LoyaltyProgram).Should().BeFalse();
    }

    [Fact]
    public async Task An_agency_with_no_subscription_gets_the_restrictive_defaults()
    {
        await using var world = await BillingWorld.CreateAsync(_postgres);

        var agencyId = await world.AddAgencyAsync("Unsubscribed Travel Limited", "unsubscribed");

        var entitlements = await world.Entitlements.ForAsync(agencyId);

        entitlements.IsFallback.Should().BeTrue();
        entitlements.IsEnabled(EntitlementCodes.CustomDomain).Should().BeFalse();
        entitlements.Ceiling(EntitlementCodes.MaxSubAgents).Should().Be(0);
        entitlements.BasisPoints(EntitlementCodes.TransactionFeeBasisPoints).Should().Be(0);
    }

    /// <summary>
    /// The <c>max_sub_agents</c> ceiling, counted against the agency hierarchy that really exists.
    /// F10's invitation flow is not built yet; the ceiling is, and this is what it will call.
    /// </summary>
    [Fact]
    public async Task The_sub_agent_ceiling_counts_the_agencies_that_are_really_there()
    {
        await using var world = await BillingWorld.CreateAsync(_postgres);

        var principalId = await world.AddAgencyAsync("Principal Travel Limited", "principal");

        var tierId = await world.AddPublishedTierAsync("starter", "Starter", 500_000,
            [new EntitlementGrant(EntitlementCodes.MaxSubAgents, "2")]);

        await world.SubscribeAsync(principalId, tierId);

        (await world.SubAgents.MayAddAsync(principalId)).IsAllowed.Should().BeTrue();

        await world.AddSubAgentAsync(principalId, "Branch One Limited", "branch-one");
        await world.AddSubAgentAsync(principalId, "Branch Two Limited", "branch-two");

        (await world.SubAgents.CountAsync(principalId)).Should().Be(2);

        var refused = await world.SubAgents.MayAddAsync(principalId);

        refused.IsAllowed.Should().BeFalse();
        refused.Limit.Should().Be(2);
        refused.Current.Should().Be(2);
        refused.Detail.Should().Contain("Starter");
    }

    /// <summary>
    /// The transaction fee is real enforcement, not a lookup: it is the rate every quote is priced
    /// with. Build-plan decision 4 keeps it out of the traveller's price, which is
    /// <c>MarkupEngine</c>'s job; this is the part that says what the rate is.
    /// </summary>
    [Fact]
    public async Task The_platform_fee_comes_from_the_agencys_tier()
    {
        await using var world = await BillingWorld.CreateAsync(_postgres);

        var payingId = await world.AddAgencyAsync("Paying Travel Limited", "paying");
        var freeId = await world.AddAgencyAsync("Free Travel Limited", "free-agency");

        var tierId = await world.AddPublishedTierAsync("growth", "Growth", 2_500_000,
            [new EntitlementGrant(EntitlementCodes.TransactionFeeBasisPoints, "250")]);

        await world.SubscribeAsync(payingId, tierId);

        (await world.PlatformFees.FeeBasisPointsAsync(payingId)).Should().Be(250);

        // No plan means no fee. A guessed rate would come out of a real agency's margin on every
        // quote, and a quote can never be corrected afterwards.
        (await world.PlatformFees.FeeBasisPointsAsync(freeId)).Should().Be(0);
    }

    [Fact]
    public async Task A_draft_tier_grants_nothing_because_nobody_can_be_on_it()
    {
        await using var world = await BillingWorld.CreateAsync(_postgres);

        var agencyId = await world.AddAgencyAsync("Acme Travel Limited", "acme");

        var created = await world.Tiers.CreateAsync(
            new TierDraft("secret", "Secret", null, 0, 0, false), "A draft nobody has published yet.");
        var tierId = ((TierChangeOutcome.Saved)created).Tier.Id;

        await world.Tiers.SetPriceAsync(tierId, "NGN", BillingInterval.Monthly, 100, "Priced but not published.");
        await world.Tiers.SetEntitlementsAsync(
            tierId, [new EntitlementGrant(EntitlementCodes.ApiAccess, "true")], "Granted but not published.");

        // Nothing subscribes an agency to a draft — the plan picker will not offer it — so the
        // agency stays on the fallback set.
        var entitlements = await world.Entitlements.ForAsync(agencyId);

        entitlements.IsFallback.Should().BeTrue();
        entitlements.IsEnabled(EntitlementCodes.ApiAccess).Should().BeFalse();
    }

    /// <summary>
    /// Build-plan decision 15: a downgrade keeps existing usage and blocks new usage. Nothing is
    /// deleted; the agency is simply refused one more until it is back inside the new ceiling.
    /// </summary>
    [Fact]
    public async Task A_downgraded_agency_keeps_what_it_has_and_is_refused_one_more()
    {
        await using var world = await BillingWorld.CreateAsync(_postgres);

        var principalId = await world.AddAgencyAsync("Principal Travel Limited", "principal");

        var big = await world.AddPublishedTierAsync("enterprise", "Enterprise", 10_000_000,
            [new EntitlementGrant(EntitlementCodes.MaxSubAgents, "-1")]);

        await world.SubscribeAsync(principalId, big);

        await world.AddSubAgentAsync(principalId, "Branch One Limited", "branch-one");
        await world.AddSubAgentAsync(principalId, "Branch Two Limited", "branch-two");
        await world.AddSubAgentAsync(principalId, "Branch Three Limited", "branch-three");

        var small = await world.AddPublishedTierAsync("starter", "Starter", 500_000,
            [new EntitlementGrant(EntitlementCodes.MaxSubAgents, "1")]);

        var subscription = await world.SubscriptionOfAsync(principalId);
        var tier = await world.Db.SubscriptionTiers
            .Include(candidate => candidate.Prices)
            .FirstAsync(candidate => candidate.Id == small);

        using (var scope = world.Tenancy.Scope.Enter("test — applies a downgrade the way the billing run does"))
        {
            subscription!.MoveTo(tier, tier.PriceAt("NGN", BillingInterval.Monthly, world.Clock.GetUtcNow()),
                world.Clock.GetUtcNow(), "Downgraded by the test.");
            world.Db.Subscriptions.Update(subscription);
            await world.Db.SaveChangesAsync();
        }

        world.Entitlements.Forget(principalId);

        // The three it already has are still there.
        (await world.SubAgents.CountAsync(principalId)).Should().Be(3);

        var refused = await world.SubAgents.MayAddAsync(principalId);

        refused.IsAllowed.Should().BeFalse();
        refused.Current.Should().Be(3);
        refused.Limit.Should().Be(1);
    }

    /// <summary>
    /// The whole of F13 that the MVP ships (issue 70, open question 24): a flag a tier can carry,
    /// off by default, answered by the same enforcement point as every other flag. Points,
    /// redemption and reviews are not built.
    /// </summary>
    [Fact]
    public async Task The_loyalty_flag_is_carried_by_a_tier_and_enforced_like_any_other_flag()
    {
        await using var world = await BillingWorld.CreateAsync(_postgres);

        var withLoyalty = await world.AddAgencyAsync("Loyal Travel Limited", "loyal");
        var without = await world.AddAgencyAsync("Plain Travel Limited", "plain");

        var enterprise = await world.AddPublishedTierAsync("enterprise", "Enterprise", 10_000_000,
            [new EntitlementGrant(EntitlementCodes.LoyaltyProgram, "true")]);

        var starter = await world.AddPublishedTierAsync("starter", "Starter", 500_000);

        await world.SubscribeAsync(withLoyalty, enterprise);
        await world.SubscribeAsync(without, starter);

        (await world.Entitlements.MayUseAsync(withLoyalty, EntitlementCodes.LoyaltyProgram))
            .IsAllowed.Should().BeTrue();

        (await world.Entitlements.MayUseAsync(without, EntitlementCodes.LoyaltyProgram))
            .IsAllowed.Should().BeFalse("a tier that does not grant it leaves it off");
    }

    [Fact]
    public async Task Forgetting_an_agency_makes_the_next_question_read_the_database_again()
    {
        await using var world = await BillingWorld.CreateAsync(_postgres);

        var agencyId = await world.AddAgencyAsync("Acme Travel Limited", "acme");
        var tierId = await world.AddPublishedTierAsync("starter", "Starter", 500_000,
            [new EntitlementGrant(EntitlementCodes.MaxSubAgents, "1")]);

        await world.SubscribeAsync(agencyId, tierId);

        (await world.Entitlements.ForAsync(agencyId)).Ceiling(EntitlementCodes.MaxSubAgents).Should().Be(1);

        await world.Tiers.SetEntitlementsAsync(
            tierId, [new EntitlementGrant(EntitlementCodes.MaxSubAgents, "9")], "The plan now includes more.");

        world.Entitlements.Forget(agencyId);

        (await world.Entitlements.ForAsync(agencyId)).Ceiling(EntitlementCodes.MaxSubAgents).Should().Be(9);
    }
}
