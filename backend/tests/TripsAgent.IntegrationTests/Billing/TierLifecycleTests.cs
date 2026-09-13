using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using TripsAgent.Application.Billing;
using TripsAgent.Domain.Billing;
using TripsAgent.IntegrationTests.Persistence;

namespace TripsAgent.IntegrationTests.Billing;

/// <summary>
/// A tier with subscribers is archived, never deleted (FRD RS-6).
/// </summary>
/// <remarks>
/// Tested at both levels, because they fail differently. The service refuses with a sentence an
/// admin can act on; the database refuses whatever reaches the table another way. The second is
/// the one that still holds after somebody edits the first.
/// </remarks>
[Collection(PostgresCollection.Name)]
public class TierLifecycleTests
{
    private const string Reason = "Set up by an integration test.";

    private readonly PostgresFixture _postgres;

    public TierLifecycleTests(PostgresFixture postgres) => _postgres = postgres;

    [Fact]
    public async Task A_tier_with_subscribers_cannot_be_deleted()
    {
        await using var world = await BillingWorld.CreateAsync(_postgres);

        var agencyId = await world.AddAgencyAsync("Acme Travel Limited", "acme");
        var tierId = await world.AddPublishedTierAsync("growth", "Growth", 2_500_000);

        await world.SubscribeAsync(agencyId, tierId);

        var outcome = await world.Tiers.DeleteAsync(tierId, "The sales team asked for it to go away.");

        outcome.Should().BeOfType<TierChangeOutcome.Refused>()
            .Which.Reason.Should().Contain("subscriber").And.Contain("Archive");

        (await world.Tiers.GetAsync(tierId)).Should().NotBeNull();
    }

    [Fact]
    public async Task A_tier_with_subscribers_can_be_archived_and_its_subscribers_keep_it()
    {
        await using var world = await BillingWorld.CreateAsync(_postgres);

        var agencyId = await world.AddAgencyAsync("Acme Travel Limited", "acme");
        var tierId = await world.AddPublishedTierAsync("legacy", "Legacy", 1_000_000,
            [new EntitlementGrant(EntitlementCodes.CustomDomain, "true")]);

        await world.SubscribeAsync(agencyId, tierId);

        var outcome = await world.Tiers.ArchiveAsync(tierId, "Replaced by the new Growth plan.");

        var archived = outcome.Should().BeOfType<TierChangeOutcome.Saved>().Which.Tier;
        archived.Status.Should().Be(TierStatus.Archived);
        archived.Subscribers.Should().Be(1, "archiving takes a tier out of the picker and moves nobody");

        // The agency is untouched, and still gets what it pays for.
        var subscription = await world.SubscriptionOfAsync(agencyId);
        subscription!.Status.Should().Be(SubscriptionStatus.Active);
        subscription.TierId.Should().Be(tierId);

        world.Entitlements.Forget(agencyId);
        (await world.Entitlements.ForAsync(agencyId)).IsEnabled(EntitlementCodes.CustomDomain).Should().BeTrue();
    }

    /// <summary>
    /// The database's own refusal, with the service bypassed entirely — because the rule has to
    /// hold against a migration, a console session, or a bug in a service somebody writes later.
    /// </summary>
    [Fact]
    public async Task The_database_refuses_to_delete_a_tier_with_subscribers()
    {
        await using var world = await BillingWorld.CreateAsync(_postgres);

        var agencyId = await world.AddAgencyAsync("Acme Travel Limited", "acme");
        var tierId = await world.AddPublishedTierAsync("growth", "Growth", 2_500_000);

        await world.SubscribeAsync(agencyId, tierId);

        using var scope = world.Tenancy.Scope.Enter("test — tries to delete a tier straight through the database");

        var deleting = async () => await world.Db.Database.ExecuteSqlRawAsync(
            "DELETE FROM billing.subscription_tiers WHERE id = {0};", tierId);

        await deleting.Should().ThrowAsync<Npgsql.PostgresException>()
            .Where(exception => exception.SqlState == "2F004" || exception.MessageText.Contains("cannot be deleted", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_database_refuses_to_delete_a_tier_that_has_ever_been_published()
    {
        await using var world = await BillingWorld.CreateAsync(_postgres);

        var tierId = await world.AddPublishedTierAsync("growth", "Growth", 2_500_000);

        using var scope = world.Tenancy.Scope.Enter("test — tries to delete a published tier with no subscribers");

        var deleting = async () => await world.Db.Database.ExecuteSqlRawAsync(
            "DELETE FROM billing.subscription_tiers WHERE id = {0};", tierId);

        await deleting.Should().ThrowAsync<Npgsql.PostgresException>();

        (await world.Tiers.GetAsync(tierId)).Should().NotBeNull();
    }

    /// <summary>
    /// The one delete that is allowed: a draft nobody ever saw. Somebody has to be able to throw
    /// away a typo without a database console.
    /// </summary>
    [Fact]
    public async Task An_unpublished_draft_with_no_subscribers_can_be_deleted()
    {
        await using var world = await BillingWorld.CreateAsync(_postgres);

        var created = await world.Tiers.CreateAsync(
            new TierDraft("typo", "Grwoth", null, 0, 0, false), "Created by mistake, wrong name.");
        var tierId = ((TierChangeOutcome.Saved)created).Tier.Id;

        var outcome = await world.Tiers.DeleteAsync(tierId, "Created by mistake — the name was a typo.");

        outcome.Should().BeOfType<TierChangeOutcome.Saved>();
        (await world.Tiers.GetAsync(tierId)).Should().BeNull();
    }

    [Fact]
    public async Task Every_tier_change_is_recorded_with_its_reason()
    {
        await using var world = await BillingWorld.CreateAsync(_postgres);

        var tierId = await world.AddPublishedTierAsync("growth", "Growth", 2_500_000);
        await world.Tiers.SetPriceAsync(tierId, "NGN", BillingInterval.Monthly, 3_000_000, "Annual price review for 2027.");

        using var scope = world.Tenancy.Scope.Enter("test assertion — reads the tier change log");

        var log = await world.Db.TierChangeLog
            .Where(entry => entry.TierId == tierId)
            .OrderBy(entry => entry.OccurredAt)
            .ToListAsync();

        log.Select(entry => entry.Action).Should().Contain(
        [
            TierAdminService.Actions.Created,
            TierAdminService.Actions.Repriced,
            TierAdminService.Actions.Published,
        ]);

        log.Should().OnlyContain(entry => entry.Reason.Length >= TierAdminService.MinReasonLength);
        log.Should().OnlyContain(entry => entry.ActorUserId == world.AdminUserId);
    }

    [Fact]
    public async Task A_change_with_no_usable_reason_is_refused_before_anything_is_written()
    {
        await using var world = await BillingWorld.CreateAsync(_postgres);

        var outcome = await world.Tiers.CreateAsync(new TierDraft("growth", "Growth", null, 0, 0, false), "oops");

        outcome.Should().BeOfType<TierChangeOutcome.Invalid>()
            .Which.Reason.Should().Contain("Say why");

        (await world.Tiers.ListAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Repricing_leaves_existing_subscribers_on_the_price_they_agreed()
    {
        await using var world = await BillingWorld.CreateAsync(_postgres);

        var agencyId = await world.AddAgencyAsync("Acme Travel Limited", "acme");
        var tierId = await world.AddPublishedTierAsync("growth", "Growth", 2_500_000);

        var subscription = await world.SubscribeAsync(agencyId, tierId);
        var agreedPriceId = subscription.TierPriceId;

        world.Clock.Advance(TimeSpan.FromDays(10));
        await world.Tiers.SetPriceAsync(tierId, "NGN", BillingInterval.Monthly, 4_000_000, "Annual price review for 2027.");

        var after = await world.SubscriptionOfAsync(agencyId);

        after!.TierPriceId.Should().Be(agreedPriceId, "a subscriber points at the row it agreed to");

        using var scope = world.Tenancy.Scope.Enter("test assertion — reads the tier's price history");

        var agreed = await world.Db.TierPrices.FirstAsync(price => price.Id == agreedPriceId);

        agreed.AmountMinor.AmountMinor.Should().Be(2_500_000);
        agreed.EffectiveTo.Should().NotBeNull("the old price is closed, not rewritten");
    }

    /// <summary>
    /// FRD RS-7: moving existing subscribers gives them notice. Nothing moves on the day the admin
    /// clicks the button — each subscriber gets a dated, explained change they can see coming.
    /// </summary>
    [Fact]
    public async Task Migrating_subscribers_schedules_the_change_with_notice_rather_than_applying_it()
    {
        await using var world = await BillingWorld.CreateAsync(_postgres);

        var first = await world.AddAgencyAsync("First Travel Limited", "first");
        var second = await world.AddAgencyAsync("Second Travel Limited", "second");

        var legacy = await world.AddPublishedTierAsync("legacy", "Legacy", 1_000_000);
        var growth = await world.AddPublishedTierAsync("growth", "Growth", 2_500_000);

        await world.SubscribeAsync(first, legacy);
        await world.SubscribeAsync(second, legacy);

        var outcome = await world.Tiers.MigrateSubscribersAsync(
            legacy, growth, "The Legacy plan is being retired at the end of the quarter.");

        outcome.Should().BeOfType<TierChangeOutcome.Saved>().Which.SubscribersScheduled.Should().Be(2);

        using var scope = world.Tenancy.Scope.Enter("test assertion — reads the scheduled migrations");

        var scheduled = await world.Db.SubscriptionMigrations.ToListAsync();

        scheduled.Should().HaveCount(2);
        scheduled.Should().OnlyContain(migration => migration.AppliedAt == null);
        scheduled.Should().OnlyContain(migration => migration.NotifiedAt == null);
        scheduled.Should().OnlyContain(
            migration => migration.ScheduledFor >= world.Clock.GetUtcNow().AddDays(29));

        // Nobody has moved.
        (await world.SubscriptionOfAsync(first))!.TierId.Should().Be(legacy);
        (await world.SubscriptionOfAsync(second))!.TierId.Should().Be(legacy);

        var log = await world.Db.TierChangeLog
            .Where(entry => entry.MigrationPolicy == TierMigrationPolicy.MigrateExisting)
            .ToListAsync();

        log.Should().ContainSingle().Which.SubscribersAffected.Should().Be(2);
        log[0].NoticeSentAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Subscribers_cannot_be_migrated_onto_a_tier_nobody_can_join()
    {
        await using var world = await BillingWorld.CreateAsync(_postgres);

        var agencyId = await world.AddAgencyAsync("Acme Travel Limited", "acme");
        var legacy = await world.AddPublishedTierAsync("legacy", "Legacy", 1_000_000);

        await world.SubscribeAsync(agencyId, legacy);

        var draft = await world.Tiers.CreateAsync(
            new TierDraft("draft", "Draft", null, 0, 0, false), "Not published yet, on purpose.");
        var draftId = ((TierChangeOutcome.Saved)draft).Tier.Id;

        var outcome = await world.Tiers.MigrateSubscribersAsync(
            legacy, draftId, "Trying to move everyone onto a draft.");

        outcome.Should().BeOfType<TierChangeOutcome.Refused>()
            .Which.Reason.Should().Contain("Publish it first");
    }
}
