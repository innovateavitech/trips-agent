using FluentAssertions;
using TripsAgent.Domain.Billing;
using TripsAgent.Domain.Common;

namespace TripsAgent.UnitTests.Billing;

/// <summary>The subscription's own state machine, with no database in the way.</summary>
public class SubscriptionTests
{
    private static readonly Guid Agency = Guid.Parse("0197b000-0000-7000-8000-00000000000a");
    private static readonly DateTimeOffset Now = new(2026, 1, 31, 4, 0, 0, TimeSpan.Zero);

    private static SubscriptionTier Tier(int trialDays = 0)
    {
        var tier = SubscriptionTier.Draft("growth", "Growth", trialDays: trialDays);
        tier.SetPrice("NGN", BillingInterval.Monthly, new Money(2_500_000), Now.AddDays(-30));

        return tier;
    }

    private static TierPrice PriceOf(SubscriptionTier tier) =>
        tier.PriceAt("NGN", BillingInterval.Monthly, Now)!;

    [Fact]
    public void A_tier_with_a_trial_starts_the_subscription_in_the_trial()
    {
        var tier = Tier(trialDays: 14);

        var subscription = Subscription.Start(Agency, tier, PriceOf(tier), "NGN", Now);

        subscription.Status.Should().Be(SubscriptionStatus.Trialing);
        subscription.TrialEndsAt.Should().Be(Now.AddDays(14));
        subscription.GrantsEntitlements.Should().BeTrue("a trial is the plan, free");
    }

    /// <summary>
    /// Periods move by calendar month, not by thirty days. Billed on the 31st of January, the next
    /// charge is the 28th of February — thirty-day periods drift a whole cycle earlier every year.
    /// </summary>
    [Fact]
    public void A_monthly_period_moves_by_calendar_month()
    {
        var tier = Tier();

        var subscription = Subscription.Start(Agency, tier, PriceOf(tier), "NGN", Now);

        subscription.Status.Should().Be(SubscriptionStatus.Active);
        subscription.CurrentPeriodEnd.Should().Be(new DateTimeOffset(2026, 2, 28, 4, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void A_past_due_subscription_still_grants_its_entitlements()
    {
        var tier = Tier();
        var subscription = Subscription.Start(Agency, tier, PriceOf(tier), "NGN", Now);

        subscription.RecordChargeFailure(Now, "The bank declined the payment.");

        subscription.Status.Should().Be(SubscriptionStatus.PastDue);
        subscription.GrantsEntitlements.Should().BeTrue(
            "taking a working agency's features away over one declined charge is worse than carrying it for a week");
    }

    [Fact]
    public void The_dunning_schedule_runs_from_the_first_failure_and_then_stops()
    {
        var tier = Tier();
        var subscription = Subscription.Start(Agency, tier, PriceOf(tier), "NGN", Now);

        subscription.RecordChargeFailure(Now, "declined").Should().Be(Now.AddDays(1));
        subscription.RecordChargeFailure(Now.AddDays(1), "declined").Should().Be(Now.AddDays(3));
        subscription.RecordChargeFailure(Now.AddDays(3), "declined").Should().Be(Now.AddDays(5));
        subscription.RecordChargeFailure(Now.AddDays(5), "declined").Should().Be(Now.AddDays(7));

        subscription.DunningIsExhausted.Should().BeFalse("the day-7 attempt has not been made yet");

        subscription.RecordChargeFailure(Now.AddDays(7), "declined").Should().BeNull();
        subscription.DunningIsExhausted.Should().BeTrue();
        subscription.DunningRetries.Should().Be(4, "four retries were made; the fifth failure was the last of them");
    }

    [Fact]
    public void A_successful_renewal_clears_the_dunning_counters()
    {
        var tier = Tier();
        var subscription = Subscription.Start(Agency, tier, PriceOf(tier), "NGN", Now);
        subscription.RecordChargeFailure(Now, "declined");
        subscription.RecordChargeFailure(Now.AddDays(1), "declined");

        subscription.Renew(Now.AddDays(2), PriceOf(tier));

        subscription.Status.Should().Be(SubscriptionStatus.Active);
        subscription.DunningRetries.Should().Be(0);
        subscription.NextDunningAttemptAt.Should().BeNull();
        subscription.StatusReason.Should().BeNull();
    }

    [Fact]
    public void Converting_a_trial_starts_a_fresh_paid_period()
    {
        var tier = Tier(trialDays: 14);
        var subscription = Subscription.Start(Agency, tier, PriceOf(tier), "NGN", Now);

        var converted = Now.AddDays(14);
        subscription.ConvertFromTrial(converted, PriceOf(tier));

        subscription.Status.Should().Be(SubscriptionStatus.Active);
        subscription.TrialEndsAt.Should().BeNull();
        subscription.CurrentPeriodStart.Should().Be(converted);
        subscription.CurrentPeriodEnd.Should().Be(converted.AddMonths(1));
    }

    [Fact]
    public void A_cancelled_or_expired_subscription_grants_nothing()
    {
        var tier = Tier();
        var cancelled = Subscription.Start(Agency, tier, PriceOf(tier), "NGN", Now);
        cancelled.Cancel(Now.AddDays(3), "The agency asked to stop.");

        cancelled.GrantsEntitlements.Should().BeFalse();

        var trial = Subscription.Start(Agency, Tier(trialDays: 7), null, "NGN", Now);
        trial.ExpireTrial(Now.AddDays(7));

        trial.Status.Should().Be(SubscriptionStatus.Expired);
        trial.GrantsEntitlements.Should().BeFalse();
    }

    [Fact]
    public void A_free_plan_has_no_price_and_is_not_billable()
    {
        var free = SubscriptionTier.Draft("free", "Free");

        var subscription = Subscription.Start(Agency, free, null, "NGN", Now);

        subscription.IsBillable.Should().BeFalse();
        subscription.GrantsEntitlements.Should().BeTrue();
    }
}
