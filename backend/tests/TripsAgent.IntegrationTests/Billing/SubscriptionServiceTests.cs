using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using TripsAgent.Application.Billing;
using TripsAgent.Domain.Billing;
using TripsAgent.IntegrationTests.Persistence;

namespace TripsAgent.IntegrationTests.Billing;

/// <summary>
/// The half of the module an agency actually reaches: the plan picker, its own plan, its invoices,
/// and changing plan.
/// </summary>
/// <remarks>
/// Every test here acts as the agency, through the policed application role — so the tenant filter
/// and the row-level security policies are in the way exactly as they are in production.
/// </remarks>
[Collection(PostgresCollection.Name)]
public class SubscriptionServiceTests
{
    private const string CallbackUrl = "https://console.test/billing/return";

    private readonly PostgresFixture _postgres;

    public SubscriptionServiceTests(PostgresFixture postgres) => _postgres = postgres;

    [Fact]
    public async Task The_plan_picker_shows_published_plans_priced_in_the_agencys_currency()
    {
        await using var world = await BillingWorld.CreateAsync(_postgres);

        var agencyId = await world.AddAgencyAsync("Acme Travel Limited", "acme");

        await world.AddPublishedTierAsync("starter", "Starter", 500_000,
            [new EntitlementGrant(EntitlementCodes.MaxCatalogListings, "10")]);
        await world.AddPublishedTierAsync("growth", "Growth", 2_500_000,
            [new EntitlementGrant(EntitlementCodes.CustomDomain, "true")]);

        // Drafts are nobody's business but ours.
        await world.Tiers.CreateAsync(new TierDraft("secret", "Secret", null, 0, 0, false), "Not published yet.");

        var (service, db) = world.ServiceFor(agencyId);
        await using var _ = db;

        var plans = await service.PlansAsync();

        plans.Select(plan => plan.Code).Should().BeEquivalentTo(["starter", "growth"]);
        plans.Should().OnlyContain(plan => plan.Currency == "NGN");

        var growth = plans.Single(plan => plan.Code == "growth");

        growth.AmountMinor.Should().Be(2_500_000);
        growth.Features.Should().Contain(feature => feature.Code == EntitlementCodes.CustomDomain && feature.Display == "on");

        // Not granted by this tier, so the picker shows the restrictive default rather than a blank.
        growth.Features.Should().Contain(feature => feature.Code == EntitlementCodes.MaxSubAgents && feature.Display == "0");
    }

    [Fact]
    public async Task Choosing_a_paid_plan_raises_an_invoice_and_sends_the_agency_to_the_hosted_page()
    {
        await using var world = await BillingWorld.CreateAsync(_postgres);

        var agencyId = await world.AddAgencyAsync("Acme Travel Limited", "acme");
        var tierId = await world.AddPublishedTierAsync("growth", "Growth", 2_500_000);

        var (service, db) = world.ServiceFor(agencyId);
        await using var _ = db;

        var outcome = await service.ChoosePlanAsync(tierId, CallbackUrl);

        var payment = outcome.Should().BeOfType<PlanChangeOutcome.PaymentRequired>().Which;

        payment.AmountMinor.Should().Be(2_500_000);
        payment.AuthorizationUrl.Should().StartWith("https://checkout.test/");
        payment.Reference.Should().StartWith("TRIPS-INV-");

        // Nothing is granted until the money is verified: an unpaid upgrade that granted
        // entitlements would simply be free.
        var invoices = await world.InvoicesOfAsync(agencyId);
        invoices.Should().ContainSingle().Which.Status.Should().Be(SubscriptionInvoiceStatus.Open);
    }

    [Fact]
    public async Task Paying_on_the_hosted_page_activates_the_plan_and_keeps_the_card_for_renewals()
    {
        await using var world = await BillingWorld.CreateAsync(_postgres);

        var agencyId = await world.AddAgencyAsync("Acme Travel Limited", "acme");
        var tierId = await world.AddPublishedTierAsync("growth", "Growth", 2_500_000,
            [new EntitlementGrant(EntitlementCodes.CustomDomain, "true")]);

        var (service, db) = world.ServiceFor(agencyId);
        await using var _ = db;

        var started = (PlanChangeOutcome.PaymentRequired)await service.ChoosePlanAsync(tierId, CallbackUrl);

        world.Gateway.WillSucceed(2_500_000);

        var finished = await service.CompleteCheckoutAsync(started.Reference);

        var plan = finished.Should().BeOfType<PlanChangeOutcome.Applied>().Which.Subscription;

        plan.Status.Should().Be(SubscriptionStatus.Active);
        plan.PlanName.Should().Be("Growth");

        // The card the agency just paid with is the card the next renewal charges. Never a card
        // number — an opaque token, plus the four digits they need to recognise it.
        plan.CardOnFile.Should().Be("Visa •••• 4242");

        var invoices = await world.InvoicesOfAsync(agencyId);
        invoices.Should().ContainSingle();
        invoices[0].Status.Should().Be(SubscriptionInvoiceStatus.Paid);
        invoices[0].ReceiptNumber.Should().StartWith("TRIPS-RCT-");
        invoices[0].TotalMinor.AmountMinor.Should().Be(invoices[0].Lines.Sum(line => line.AmountMinor.AmountMinor));

        world.Entitlements.Forget(agencyId);
        (await world.Entitlements.ForAsync(agencyId)).IsEnabled(EntitlementCodes.CustomDomain).Should().BeTrue();
    }

    [Fact]
    public async Task A_completed_checkout_asked_about_twice_does_not_pay_twice()
    {
        await using var world = await BillingWorld.CreateAsync(_postgres);

        var agencyId = await world.AddAgencyAsync("Acme Travel Limited", "acme");
        var tierId = await world.AddPublishedTierAsync("growth", "Growth", 2_500_000);

        var (service, db) = world.ServiceFor(agencyId);
        await using var _ = db;

        var started = (PlanChangeOutcome.PaymentRequired)await service.ChoosePlanAsync(tierId, CallbackUrl);

        world.Gateway.WillSucceed(2_500_000);
        await service.CompleteCheckoutAsync(started.Reference);

        // The agency refreshed the callback page. The gateway is not asked again, and the receipt
        // number does not move.
        var again = await service.CompleteCheckoutAsync(started.Reference);

        again.Should().BeOfType<PlanChangeOutcome.Applied>();

        var invoices = await world.InvoicesOfAsync(agencyId);
        invoices.Should().ContainSingle().Which.Status.Should().Be(SubscriptionInvoiceStatus.Paid);
    }

    /// <summary>
    /// Build-plan decision 15. A downgrade is agreed now and applied at the end of the period the
    /// agency has already paid for — it keeps what it paid for, and gets notice of the change.
    /// </summary>
    [Fact]
    public async Task Choosing_a_cheaper_plan_is_scheduled_rather_than_applied()
    {
        await using var world = await BillingWorld.CreateAsync(_postgres);

        var agencyId = await world.AddAgencyAsync("Acme Travel Limited", "acme");

        var growth = await world.AddPublishedTierAsync("growth", "Growth", 2_500_000,
            [new EntitlementGrant(EntitlementCodes.CustomDomain, "true")]);
        var starter = await world.AddPublishedTierAsync("starter", "Starter", 500_000);

        await world.SubscribeAsync(agencyId, growth);

        var (service, db) = world.ServiceFor(agencyId);
        await using var _ = db;

        var outcome = await service.ChoosePlanAsync(starter, CallbackUrl);

        var scheduled = outcome.Should().BeOfType<PlanChangeOutcome.Scheduled>().Which.Change;

        scheduled.ToPlan.Should().Be("Starter");
        scheduled.Reason.Should().Be(SubscriptionChangeReason.Downgrade);
        scheduled.CanCancel.Should().BeTrue();

        // Nothing has changed yet: they keep what they paid for until the period ends.
        var plan = await service.MineAsync();

        plan.TierId.Should().Be(growth);
        plan.ScheduledChange!.MigrationId.Should().Be(scheduled.MigrationId);

        world.Entitlements.Forget(agencyId);
        (await world.Entitlements.ForAsync(agencyId)).IsEnabled(EntitlementCodes.CustomDomain).Should().BeTrue();
    }

    [Fact]
    public async Task An_agency_can_change_its_mind_about_a_downgrade_it_asked_for()
    {
        await using var world = await BillingWorld.CreateAsync(_postgres);

        var agencyId = await world.AddAgencyAsync("Acme Travel Limited", "acme");
        var growth = await world.AddPublishedTierAsync("growth", "Growth", 2_500_000);
        var starter = await world.AddPublishedTierAsync("starter", "Starter", 500_000);

        await world.SubscribeAsync(agencyId, growth);

        var (service, db) = world.ServiceFor(agencyId);
        await using var _ = db;

        var scheduled = (PlanChangeOutcome.Scheduled)await service.ChoosePlanAsync(starter, CallbackUrl);

        var cancelled = await service.CancelScheduledChangeAsync(scheduled.Change.MigrationId);

        cancelled.Should().BeOfType<PlanChangeOutcome.Applied>()
            .Which.Subscription.ScheduledChange.Should().BeNull();
    }

    /// <summary>
    /// A change Trips made is not the agency's to cancel. Cancelling an admin migration would put
    /// the agency back on a plan we have retired.
    /// </summary>
    [Fact]
    public async Task An_agency_cannot_cancel_a_migration_trips_scheduled()
    {
        await using var world = await BillingWorld.CreateAsync(_postgres);

        var agencyId = await world.AddAgencyAsync("Acme Travel Limited", "acme");
        var legacy = await world.AddPublishedTierAsync("legacy", "Legacy", 1_000_000);
        var growth = await world.AddPublishedTierAsync("growth", "Growth", 2_500_000);

        await world.SubscribeAsync(agencyId, legacy);
        await world.Tiers.MigrateSubscribersAsync(legacy, growth, "The Legacy plan is being retired.");

        var (service, db) = world.ServiceFor(agencyId);
        await using var _ = db;

        var plan = await service.MineAsync();

        plan.ScheduledChange.Should().NotBeNull();
        plan.ScheduledChange!.CanCancel.Should().BeFalse();

        var refused = await service.CancelScheduledChangeAsync(plan.ScheduledChange.MigrationId);

        refused.Should().BeOfType<PlanChangeOutcome.Refused>()
            .Which.Reason.Should().Contain("made by Trips");
    }

    [Fact]
    public async Task Choosing_the_free_fallback_plan_needs_no_payment()
    {
        await using var world = await BillingWorld.CreateAsync(_postgres);

        var agencyId = await world.AddAgencyAsync("Acme Travel Limited", "acme");
        var free = await world.AddPublishedTierAsync("free", "Free", 0, isFallback: true);

        var (service, db) = world.ServiceFor(agencyId);
        await using var _ = db;

        var outcome = await service.ChoosePlanAsync(free, CallbackUrl);

        outcome.Should().BeOfType<PlanChangeOutcome.Applied>()
            .Which.Subscription.PlanName.Should().Be("Free");

        (await world.InvoicesOfAsync(agencyId)).Should().BeEmpty("nothing was charged, so nothing was invoiced");
    }

    [Fact]
    public async Task A_tier_with_a_trial_starts_the_trial_rather_than_charging()
    {
        await using var world = await BillingWorld.CreateAsync(_postgres);

        var agencyId = await world.AddAgencyAsync("Acme Travel Limited", "acme");
        var growth = await world.AddPublishedTierAsync("growth", "Growth", 2_500_000, trialDays: 14);

        var (service, db) = world.ServiceFor(agencyId);
        await using var _ = db;

        var outcome = await service.ChoosePlanAsync(growth, CallbackUrl);

        var plan = outcome.Should().BeOfType<PlanChangeOutcome.Applied>().Which.Subscription;

        plan.Status.Should().Be(SubscriptionStatus.Trialing);
        plan.TrialEndsAt.Should().BeCloseTo(world.Clock.GetUtcNow().AddDays(14), TimeSpan.FromMinutes(1));
        plan.CardOnFile.Should().BeNull("nothing has been paid, so there is nothing on file");

        (await world.InvoicesOfAsync(agencyId)).Should().BeEmpty();
    }

    [Fact]
    public async Task An_agency_cannot_join_a_plan_that_is_not_published()
    {
        await using var world = await BillingWorld.CreateAsync(_postgres);

        var agencyId = await world.AddAgencyAsync("Acme Travel Limited", "acme");

        var draft = await world.Tiers.CreateAsync(
            new TierDraft("secret", "Secret", null, 0, 0, false), "Not published yet, on purpose.");
        var draftId = ((TierChangeOutcome.Saved)draft).Tier.Id;

        var (service, db) = world.ServiceFor(agencyId);
        await using var _ = db;

        var outcome = await service.ChoosePlanAsync(draftId, CallbackUrl);

        outcome.Should().BeOfType<PlanChangeOutcome.Refused>()
            .Which.Reason.Should().Contain("not available to join");
    }

    [Fact]
    public async Task An_agency_reads_only_its_own_invoices()
    {
        await using var world = await BillingWorld.CreateAsync(_postgres);

        var mine = await world.AddAgencyAsync("Mine Travel Limited", "mine");
        var theirs = await world.AddAgencyAsync("Theirs Travel Limited", "theirs");
        var growth = await world.AddPublishedTierAsync("growth", "Growth", 2_500_000);

        await world.SubscribeAsync(mine, growth);
        await world.SubscribeAsync(theirs, growth);
        await world.AddCardAsync(mine);
        await world.AddCardAsync(theirs);

        world.Gateway.WillSucceed();
        world.Gateway.WillSucceed();
        world.Clock.Advance(TimeSpan.FromDays(32));
        await world.Billing.RunAsync();

        var (service, db) = world.ServiceFor(mine);
        await using var _ = db;

        var invoices = await service.InvoicesAsync();

        invoices.Should().ContainSingle();
        invoices[0].TotalMinor.Should().Be(invoices[0].Lines.Sum(line => line.AmountMinor));
    }

    [Fact]
    public async Task A_gateway_that_cannot_be_reached_changes_nothing()
    {
        await using var world = await BillingWorld.CreateAsync(_postgres);

        var agencyId = await world.AddAgencyAsync("Acme Travel Limited", "acme");
        var tierId = await world.AddPublishedTierAsync("growth", "Growth", 2_500_000);

        var (service, db) = world.ServiceFor(agencyId);
        await using var _ = db;

        var started = (PlanChangeOutcome.PaymentRequired)await service.ChoosePlanAsync(tierId, CallbackUrl);

        world.Gateway.WillBeUnreachable();

        var outcome = await service.CompleteCheckoutAsync(started.Reference);

        outcome.Should().BeOfType<PlanChangeOutcome.GatewayUnavailable>();

        var invoices = await world.InvoicesOfAsync(agencyId);

        invoices.Should().ContainSingle().Which.Status.Should().Be(
            SubscriptionInvoiceStatus.Open,
            "an unknown answer is not a failed payment, and must not be recorded as one");
    }
}
