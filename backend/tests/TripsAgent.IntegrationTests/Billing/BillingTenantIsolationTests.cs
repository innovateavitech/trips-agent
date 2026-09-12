using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using TripsAgent.Domain.Billing;
using TripsAgent.IntegrationTests.Persistence;

namespace TripsAgent.IntegrationTests.Billing;

/// <summary>
/// One agency never sees another's plan, invoices or card.
/// </summary>
/// <remarks>
/// CLAUDE.md rule 3 and ADR-0006. These tests connect as the policed application role, so the
/// row-level security policies are doing the work here and not only the EF query filter — a filter
/// somebody forgets is exactly what the backstop is for. What leaks here is what an agency pays and
/// what its card is, which is about as bad as a leak gets.
/// </remarks>
[Collection(PostgresCollection.Name)]
public class BillingTenantIsolationTests
{
    private readonly PostgresFixture _postgres;

    public BillingTenantIsolationTests(PostgresFixture postgres) => _postgres = postgres;

    [Fact]
    public async Task An_agency_sees_its_own_subscription_and_no_other()
    {
        await using var world = await BillingWorld.CreateAsync(_postgres);

        var mine = await world.AddAgencyAsync("Mine Travel Limited", "mine");
        var theirs = await world.AddAgencyAsync("Theirs Travel Limited", "theirs");

        var growth = await world.AddPublishedTierAsync("growth", "Growth", 2_500_000);
        var starter = await world.AddPublishedTierAsync("starter", "Starter", 500_000);

        await world.SubscribeAsync(mine, growth);
        await world.SubscribeAsync(theirs, starter);

        await using var asMine = world.ActingAs(mine);

        var visible = await asMine.Subscriptions.ToListAsync();

        visible.Should().ContainSingle().Which.AgencyId.Should().Be(mine);
    }

    [Fact]
    public async Task An_agency_sees_its_own_invoices_and_lines_and_no_other()
    {
        await using var world = await BillingWorld.CreateAsync(_postgres);

        var mine = await world.AddAgencyAsync("Mine Travel Limited", "mine");
        var theirs = await world.AddAgencyAsync("Theirs Travel Limited", "theirs");

        var growth = await world.AddPublishedTierAsync("growth", "Growth", 2_500_000);

        await world.SubscribeAsync(mine, growth);
        await world.SubscribeAsync(theirs, growth);
        await world.AddCardAsync(mine);
        await world.AddCardAsync(theirs);

        world.Gateway.Default = _ => throw new InvalidOperationException("unused");
        world.Gateway.WillSucceed();
        world.Gateway.WillSucceed();

        world.Clock.Advance(TimeSpan.FromDays(32));
        await world.Billing.RunAsync();

        await using var asMine = world.ActingAs(mine);

        (await asMine.SubscriptionInvoices.ToListAsync())
            .Should().ContainSingle().Which.AgencyId.Should().Be(mine);

        (await asMine.SubscriptionInvoiceLines.ToListAsync())
            .Should().OnlyContain(line => line.AgencyId == mine);

        (await asMine.SubscriptionChargeAttempts.ToListAsync())
            .Should().OnlyContain(attempt => attempt.AgencyId == mine);
    }

    [Fact]
    public async Task An_agency_never_sees_another_agencys_stored_card()
    {
        await using var world = await BillingWorld.CreateAsync(_postgres);

        var mine = await world.AddAgencyAsync("Mine Travel Limited", "mine");
        var theirs = await world.AddAgencyAsync("Theirs Travel Limited", "theirs");

        await world.AddCardAsync(mine);
        await world.AddCardAsync(theirs);

        await using var asMine = world.ActingAs(mine);

        var visible = await asMine.PaymentAuthorizations.ToListAsync();

        visible.Should().ContainSingle().Which.AgencyId.Should().Be(mine);
    }

    /// <summary>
    /// The tier catalogue is the platform's, so an agency may read it — it has to, to choose a plan.
    /// What it must never be able to do is change one.
    /// </summary>
    [Fact]
    public async Task An_agency_can_read_the_tier_catalogue_but_not_change_it()
    {
        await using var world = await BillingWorld.CreateAsync(_postgres);

        var agencyId = await world.AddAgencyAsync("Acme Travel Limited", "acme");
        var tierId = await world.AddPublishedTierAsync("growth", "Growth", 2_500_000);

        await using var asAgency = world.ActingAs(agencyId);

        (await asAgency.SubscriptionTiers.CountAsync()).Should().Be(1);
        (await asAgency.Entitlements.CountAsync()).Should().Be(EntitlementCatalog.All.Count);

        // The application role has no DELETE on a priced tier, and the trigger refuses besides.
        var deleting = async () => await asAgency.Database.ExecuteSqlRawAsync(
            "DELETE FROM billing.subscription_tiers WHERE id = {0};", tierId);

        await deleting.Should().ThrowAsync<Npgsql.PostgresException>();
    }

    [Fact]
    public async Task An_agency_cannot_write_a_subscription_row_for_somebody_else()
    {
        await using var world = await BillingWorld.CreateAsync(_postgres);

        var mine = await world.AddAgencyAsync("Mine Travel Limited", "mine");
        var theirs = await world.AddAgencyAsync("Theirs Travel Limited", "theirs");
        var growth = await world.AddPublishedTierAsync("growth", "Growth", 2_500_000);

        await using var asMine = world.ActingAs(mine);

        var now = world.Clock.GetUtcNow();

        // The policy's WITH CHECK is what refuses this, not the query filter: the filter shapes
        // reads, and an INSERT reads nothing.
        var writing = async () => await asMine.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO billing.subscriptions
                (id, agency_id, tier_id, currency, status, current_period_start, current_period_end,
                 dunning_retries, created_at, updated_at)
            VALUES ({0}, {1}, {2}, 'NGN', 'Active', {3}, {4}, 0, {3}, {3});
            """,
            Guid.CreateVersion7(), theirs, growth, now, now.AddMonths(1));

        await writing.Should().ThrowAsync<Npgsql.PostgresException>()
            .Where(exception => exception.SqlState == "42501");
    }
}
