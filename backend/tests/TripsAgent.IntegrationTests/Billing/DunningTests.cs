using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using TripsAgent.Application.Billing;
using TripsAgent.Domain.Billing;
using TripsAgent.Domain.Tenancy;
using TripsAgent.IntegrationTests.Persistence;

namespace TripsAgent.IntegrationTests.Billing;

/// <summary>
/// A failed charge runs the dunning schedule and ends in the stated outcome, with entitlements
/// re-evaluated.
/// </summary>
/// <remarks>
/// The acceptance test for issue 65. Every case runs the real <see cref="SubscriptionBillingRun"/>
/// against a real database, moving a clock rather than waiting, so the schedule that passes here is
/// the schedule an agency gets.
/// </remarks>
[Collection(PostgresCollection.Name)]
public class DunningTests
{
    private readonly PostgresFixture _postgres;

    public DunningTests(PostgresFixture postgres) => _postgres = postgres;

    [Fact]
    public async Task A_renewal_that_comes_due_is_invoiced_and_charged()
    {
        await using var world = await BillingWorld.CreateAsync(_postgres);

        var agencyId = await world.AddAgencyAsync("Acme Travel Limited", "acme");
        var tierId = await world.AddPublishedTierAsync("growth", "Growth", 2_500_000);

        await world.SubscribeAsync(agencyId, tierId);
        await world.AddCardAsync(agencyId);

        world.Gateway.WillSucceed();
        world.Clock.Advance(TimeSpan.FromDays(32));

        var result = await world.Billing.RunAsync();

        result.Renewed.Should().Be(1);
        result.Charged.Should().Be(1);
        result.Failed.Should().Be(0);

        var invoices = await world.InvoicesOfAsync(agencyId);

        invoices.Should().ContainSingle();
        invoices[0].Status.Should().Be(SubscriptionInvoiceStatus.Paid);
        invoices[0].ReceiptNumber.Should().StartWith("TRIPS-RCT-");
        invoices[0].InvoiceNumber.Should().StartWith("TRIPS-INV-");
        invoices[0].TotalMinor.AmountMinor.Should().Be(2_500_000);

        var subscription = await world.SubscriptionOfAsync(agencyId);
        subscription!.Status.Should().Be(SubscriptionStatus.Active);
        subscription.CurrentPeriodEnd.Should().BeAfter(world.Clock.GetUtcNow());
    }

    /// <summary>
    /// Four retries on days 1, 3, 5 and 7 after the first failure, then the outcome. The clock moves
    /// a day at a time so that a retry firing early or late shows up as a count that is wrong.
    /// </summary>
    [Fact]
    public async Task A_failed_charge_is_retried_on_days_one_three_five_and_seven_then_downgraded()
    {
        await using var world = await BillingWorld.CreateAsync(_postgres);

        var agencyId = await world.AddAgencyAsync("Acme Travel Limited", "acme");

        var free = await world.AddPublishedTierAsync("free", "Free", 0, isFallback: true);
        var growth = await world.AddPublishedTierAsync("growth", "Growth", 2_500_000,
        [
            new EntitlementGrant(EntitlementCodes.CustomDomain, "true"),
            new EntitlementGrant(EntitlementCodes.MaxSubAgents, "10"),
        ]);

        await world.SubscribeAsync(agencyId, growth);
        await world.AddCardAsync(agencyId);

        // Everything the gateway is asked declines, which is what an expired card looks like.
        world.Clock.Advance(TimeSpan.FromDays(32));

        var first = await world.Billing.RunAsync();

        first.Renewed.Should().Be(1);
        first.Failed.Should().Be(1);

        var subscription = await world.SubscriptionOfAsync(agencyId);
        subscription!.Status.Should().Be(SubscriptionStatus.PastDue);
        subscription.DunningRetries.Should().Be(0, "the charge that failed is not a retry");

        // Past due still grants what the agency pays for: one declined charge must not take a
        // working business's features away.
        world.Entitlements.Forget(agencyId);
        (await world.Entitlements.ForAsync(agencyId))
            .IsEnabled(EntitlementCodes.CustomDomain).Should().BeTrue();

        // A day at a time for a fortnight. Only the four scheduled days may produce an attempt.
        for (var day = 1; day <= 10; day++)
        {
            world.Clock.Advance(TimeSpan.FromDays(1));
            await world.Billing.RunAsync();
        }

        var attempts = await world.AttemptsOfAsync(agencyId);

        attempts.Should().HaveCount(5, "the charge that failed, then four retries — and no more");
        attempts.Select(attempt => attempt.AttemptNumber).Should().Equal(0, 1, 2, 3, 4);
        attempts.Should().OnlyContain(attempt => attempt.Outcome == ChargeAttemptOutcome.Failed);

        // The gateway saw one reference per attempt, all different — that is what makes a replayed
        // run safe rather than a second charge.
        world.Gateway.Charges.Should().HaveCount(5).And.OnlyHaveUniqueItems();

        var after = await world.SubscriptionOfAsync(agencyId);

        after!.TierId.Should().Be(free, "there is a fallback plan, so the agency lands on it");
        after.Status.Should().Be(SubscriptionStatus.Active);

        var agency = await AgencyStatusAsync(world, agencyId);
        agency.Should().Be(AgencyStatus.Verified, "a fallback plan means nobody needs suspending");

        // Entitlements are re-evaluated: the paid plan's features are gone, the free plan's remain.
        world.Entitlements.Forget(agencyId);
        var entitlements = await world.Entitlements.ForAsync(agencyId);

        entitlements.IsEnabled(EntitlementCodes.CustomDomain).Should().BeFalse();
        entitlements.Ceiling(EntitlementCodes.MaxSubAgents).Should().Be(0);

        var invoices = await world.InvoicesOfAsync(agencyId);
        invoices.Should().ContainSingle().Which.Status.Should().Be(SubscriptionInvoiceStatus.Uncollectible);
    }

    /// <summary>
    /// The other outcome: with no free plan to fall back to, the agency is suspended. Build-plan
    /// decision 14 — existing bookings stand, the storefront goes offline.
    /// </summary>
    [Fact]
    public async Task With_no_fallback_plan_a_spent_dunning_schedule_suspends_the_agency()
    {
        await using var world = await BillingWorld.CreateAsync(_postgres);

        var agencyId = await world.AddAgencyAsync("Acme Travel Limited", "acme");
        var growth = await world.AddPublishedTierAsync("growth", "Growth", 2_500_000);

        await world.SubscribeAsync(agencyId, growth);
        await world.AddCardAsync(agencyId);

        world.Clock.Advance(TimeSpan.FromDays(32));

        for (var day = 0; day <= 10; day++)
        {
            await world.Billing.RunAsync();
            world.Clock.Advance(TimeSpan.FromDays(1));
        }

        (await AgencyStatusAsync(world, agencyId)).Should().Be(AgencyStatus.Suspended);

        var subscription = await world.SubscriptionOfAsync(agencyId);
        subscription!.Status.Should().Be(SubscriptionStatus.Cancelled);

        world.Entitlements.Forget(agencyId);
        (await world.Entitlements.ForAsync(agencyId)).IsFallback.Should().BeTrue();
    }

    [Fact]
    public async Task A_retry_that_works_clears_the_schedule_and_puts_the_agency_back()
    {
        await using var world = await BillingWorld.CreateAsync(_postgres);

        var agencyId = await world.AddAgencyAsync("Acme Travel Limited", "acme");
        var growth = await world.AddPublishedTierAsync("growth", "Growth", 2_500_000,
            [new EntitlementGrant(EntitlementCodes.CustomDomain, "true")]);

        await world.SubscribeAsync(agencyId, growth);
        await world.AddCardAsync(agencyId);

        world.Gateway.WillDecline("Insufficient funds.");
        world.Gateway.WillSucceed();

        world.Clock.Advance(TimeSpan.FromDays(32));
        await world.Billing.RunAsync();

        (await world.SubscriptionOfAsync(agencyId))!.Status.Should().Be(SubscriptionStatus.PastDue);

        world.Clock.Advance(TimeSpan.FromDays(1));
        var second = await world.Billing.RunAsync();

        second.Charged.Should().Be(1);

        var subscription = await world.SubscriptionOfAsync(agencyId);

        subscription!.Status.Should().Be(SubscriptionStatus.Active);
        subscription.DunningRetries.Should().Be(0);
        subscription.NextDunningAttemptAt.Should().BeNull();

        var invoices = await world.InvoicesOfAsync(agencyId);
        invoices.Should().ContainSingle().Which.Status.Should().Be(SubscriptionInvoiceStatus.Paid);
        invoices[0].ReceiptNumber.Should().NotBeNull();
    }

    /// <summary>
    /// A gateway that cannot be reached is an unknown outcome, not a decline. Spending a retry on a
    /// payment provider's bad afternoon would shorten the week the agency was promised.
    /// </summary>
    [Fact]
    public async Task An_unreachable_gateway_does_not_spend_a_retry()
    {
        await using var world = await BillingWorld.CreateAsync(_postgres);

        var agencyId = await world.AddAgencyAsync("Acme Travel Limited", "acme");
        var growth = await world.AddPublishedTierAsync("growth", "Growth", 2_500_000);

        await world.SubscribeAsync(agencyId, growth);
        await world.AddCardAsync(agencyId);

        world.Gateway.WillBeUnreachable();

        world.Clock.Advance(TimeSpan.FromDays(32));
        await world.Billing.RunAsync();

        var attempts = await world.AttemptsOfAsync(agencyId);

        attempts.Should().ContainSingle().Which.Outcome.Should().Be(ChargeAttemptOutcome.Unknown);

        var subscription = await world.SubscriptionOfAsync(agencyId);

        subscription!.Status.Should().Be(SubscriptionStatus.Active, "nothing is known to have failed");
        subscription.DunningStartedAt.Should().BeNull();
        subscription.DunningRetries.Should().Be(0);
    }

    [Fact]
    public async Task A_renewal_with_no_card_on_file_goes_past_due_rather_than_silently_lapsing()
    {
        await using var world = await BillingWorld.CreateAsync(_postgres);

        var agencyId = await world.AddAgencyAsync("Acme Travel Limited", "acme");
        var growth = await world.AddPublishedTierAsync("growth", "Growth", 2_500_000);

        await world.SubscribeAsync(agencyId, growth);

        world.Clock.Advance(TimeSpan.FromDays(32));
        await world.Billing.RunAsync();

        var attempts = await world.AttemptsOfAsync(agencyId);

        attempts.Should().ContainSingle().Which.Outcome.Should().Be(ChargeAttemptOutcome.NoAuthorization);
        world.Gateway.Charges.Should().BeEmpty("there was nothing to charge with");

        (await world.SubscriptionOfAsync(agencyId))!.Status.Should().Be(SubscriptionStatus.PastDue);
        (await world.InvoicesOfAsync(agencyId)).Should().ContainSingle()
            .Which.Status.Should().Be(SubscriptionInvoiceStatus.PastDue);
    }

    [Fact]
    public async Task A_trial_that_runs_out_is_invoiced_for_its_first_real_period()
    {
        await using var world = await BillingWorld.CreateAsync(_postgres);

        var agencyId = await world.AddAgencyAsync("Acme Travel Limited", "acme");
        var growth = await world.AddPublishedTierAsync("growth", "Growth", 2_500_000, trialDays: 14);

        await world.SubscribeAsync(agencyId, growth);
        await world.AddCardAsync(agencyId);

        (await world.SubscriptionOfAsync(agencyId))!.Status.Should().Be(SubscriptionStatus.Trialing);

        world.Gateway.WillSucceed();
        world.Clock.Advance(TimeSpan.FromDays(15));

        var result = await world.Billing.RunAsync();

        result.TrialsEnded.Should().Be(1);
        result.Charged.Should().Be(1);

        var subscription = await world.SubscriptionOfAsync(agencyId);
        subscription!.Status.Should().Be(SubscriptionStatus.Active);
        subscription.TrialEndsAt.Should().BeNull();

        (await world.InvoicesOfAsync(agencyId)).Should().ContainSingle()
            .Which.Status.Should().Be(SubscriptionInvoiceStatus.Paid);
    }

    /// <summary>
    /// Running the job twice on the same day must not charge the same card twice. The attempt
    /// number is unique per invoice in the database, so the second run finds nothing due.
    /// </summary>
    [Fact]
    public async Task Running_the_job_twice_in_a_day_charges_once()
    {
        await using var world = await BillingWorld.CreateAsync(_postgres);

        var agencyId = await world.AddAgencyAsync("Acme Travel Limited", "acme");
        var growth = await world.AddPublishedTierAsync("growth", "Growth", 2_500_000);

        await world.SubscribeAsync(agencyId, growth);
        await world.AddCardAsync(agencyId);

        world.Gateway.WillSucceed();
        world.Clock.Advance(TimeSpan.FromDays(32));

        await world.Billing.RunAsync();
        var second = await world.Billing.RunAsync();

        second.Renewed.Should().Be(0);
        second.Charged.Should().Be(0);

        world.Gateway.Charges.Should().ContainSingle();
        (await world.InvoicesOfAsync(agencyId)).Should().ContainSingle();
    }

    /// <summary>
    /// A migration that comes due is applied before anything is charged, so the invoice is for the
    /// plan the agency is actually on that morning.
    /// </summary>
    [Fact]
    public async Task A_scheduled_migration_lands_before_the_renewal_it_affects()
    {
        await using var world = await BillingWorld.CreateAsync(_postgres);

        var agencyId = await world.AddAgencyAsync("Acme Travel Limited", "acme");

        var legacy = await world.AddPublishedTierAsync("legacy", "Legacy", 1_000_000);
        var growth = await world.AddPublishedTierAsync("growth", "Growth", 2_500_000,
            [new EntitlementGrant(EntitlementCodes.CustomDomain, "true")]);

        await world.SubscribeAsync(agencyId, legacy);
        await world.AddCardAsync(agencyId);

        await world.Tiers.MigrateSubscribersAsync(legacy, growth, "The Legacy plan is being retired.");

        world.Gateway.WillSucceed();
        world.Clock.Advance(TimeSpan.FromDays(31));

        var result = await world.Billing.RunAsync();

        result.MigrationsApplied.Should().Be(1);

        var subscription = await world.SubscriptionOfAsync(agencyId);
        subscription!.TierId.Should().Be(growth);

        world.Entitlements.Forget(agencyId);
        (await world.Entitlements.ForAsync(agencyId))
            .IsEnabled(EntitlementCodes.CustomDomain).Should().BeTrue("entitlements are re-evaluated after the move");

        using var scope = world.Tenancy.Scope.Enter("test assertion — reads the applied migration");

        var migration = await world.Db.SubscriptionMigrations.FirstAsync();
        migration.AppliedAt.Should().NotBeNull();
    }

    private static async Task<AgencyStatus> AgencyStatusAsync(BillingWorld world, Guid agencyId)
    {
        using var scope = world.Tenancy.Scope.Enter("test assertion — reads an agency's status");

        world.Db.ChangeTracker.Clear();

        return await world.Db.Agencies
            .Where(agency => agency.Id == agencyId)
            .Select(agency => agency.Status)
            .FirstAsync();
    }
}
