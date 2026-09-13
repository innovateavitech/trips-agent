using TripsAgent.Domain.Auditing;
using TripsAgent.Domain.Common;

namespace TripsAgent.Domain.Billing;

/// <summary>
/// One agency's plan: which tier, at which price, paid up to when.
/// </summary>
/// <remarks>
/// <para>
/// Tenant-scoped, so the EF query filter and the row-level security policy both apply and an agency
/// can only ever see its own. Exactly one may be live per agency, which a partial unique index
/// enforces in the database rather than in a check somebody can forget.
/// </para>
/// <para>
/// It points at a <see cref="TierPrice"/> and not merely at a tier. The price row is closed rather
/// than edited when it changes, so a subscriber keeps paying what they agreed even after the tier
/// is repriced — the advertised rate moving is not the same as this agency's rate moving, and
/// conflating the two rewrites what somebody already agreed to.
/// </para>
/// </remarks>
public sealed class Subscription : Entity, IAuditableEntity, ITenantScoped, IAuditLogged
{
    private Subscription() => Currency = string.Empty;

    private Subscription(
        Guid agencyId,
        Guid tierId,
        Guid? tierPriceId,
        string currency,
        SubscriptionStatus status,
        DateTimeOffset periodStart,
        DateTimeOffset periodEnd,
        DateTimeOffset? trialEndsAt)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(agencyId, Guid.Empty);
        ArgumentException.ThrowIfNullOrWhiteSpace(currency);

        AgencyId = agencyId;
        TierId = tierId;
        TierPriceId = tierPriceId;
        Currency = currency.Trim().ToUpperInvariant();
        Status = status;
        CurrentPeriodStart = periodStart;
        CurrentPeriodEnd = periodEnd;
        TrialEndsAt = trialEndsAt;
    }

    /// <summary>
    /// Starts a subscription, in a trial when the tier offers one.
    /// </summary>
    /// <param name="price">
    /// The exact price row the agency agreed to. Null only for a tier with no price at all — the
    /// free fallback plan.
    /// </param>
    public static Subscription Start(
        Guid agencyId,
        SubscriptionTier tier,
        TierPrice? price,
        string currency,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(tier);

        if (tier.TrialDays > 0)
        {
            var trialEnds = now.AddDays(tier.TrialDays);

            return new Subscription(
                agencyId, tier.Id, price?.Id, currency, SubscriptionStatus.Trialing, now, trialEnds, trialEnds);
        }

        return new Subscription(
            agencyId, tier.Id, price?.Id, currency, SubscriptionStatus.Active, now, NextPeriodEnd(now, price), null);
    }

    public Guid AgencyId { get; private set; }

    public Guid TierId { get; private set; }

    /// <summary>The exact price row being charged. Null on a free tier, which has no price.</summary>
    public Guid? TierPriceId { get; private set; }

    /// <summary>The currency the agency is billed in — its base currency (decision 17).</summary>
    public string Currency { get; private set; }

    public SubscriptionStatus Status { get; private set; }

    public DateTimeOffset CurrentPeriodStart { get; private set; }

    /// <summary>When the current paid period runs out. The renewal job reads this.</summary>
    public DateTimeOffset CurrentPeriodEnd { get; private set; }

    /// <summary>When a trial ends. Null once it has, and for a tier with no trial.</summary>
    public DateTimeOffset? TrialEndsAt { get; private set; }

    /// <summary>
    /// The gateway's own handle for the arrangement — Paystack's customer code.
    /// </summary>
    /// <remarks>
    /// Never a card number and never anything that could become one. The reusable authorisation
    /// itself lives in <see cref="PaymentAuthorization"/>; this is only for finding the same
    /// customer in Paystack's dashboard when support needs to.
    /// </remarks>
    public string? ExternalRef { get; private set; }

    /// <summary>When the agency asked to stop. The subscription still runs to <see cref="CurrentPeriodEnd"/>.</summary>
    public DateTimeOffset? CancelledAt { get; private set; }

    /// <summary>Why it is in its current state, for the agency's own plan screen.</summary>
    public string? StatusReason { get; private set; }

    /// <summary>When the current run of failed charges started. Null when nothing is failing.</summary>
    /// <remarks>The dunning schedule is measured from here, not from the last attempt.</remarks>
    public DateTimeOffset? DunningStartedAt { get; private set; }

    /// <summary>
    /// How many retries this run of failures has already made — not how many charges have failed.
    /// The initial failure is the charge, not a retry, so this is one less than the failure count.
    /// </summary>
    public int DunningRetries { get; private set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>
    /// True when the tier's entitlements apply. A cancelled or expired subscription grants nothing.
    /// </summary>
    /// <remarks>
    /// <c>PastDue</c> is deliberately included. Taking an agency's entitlements away the moment a
    /// card declines punishes a working business for a bank's bad afternoon; the dunning schedule
    /// is what decides when patience runs out, and until it does the agency keeps trading.
    /// </remarks>
    public bool GrantsEntitlements =>
        Status is SubscriptionStatus.Trialing or SubscriptionStatus.Active or SubscriptionStatus.PastDue;

    /// <summary>True when the renewal job should bill this subscription at all.</summary>
    public bool IsBillable => TierPriceId is not null;

    /// <summary>Records the gateway's handle for this agency, once its first payment gives us one.</summary>
    public void LinkToGateway(string externalRef)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(externalRef);
        ExternalRef = externalRef;
    }

    /// <summary>Converts a trial into a paying subscription, starting a fresh period at <paramref name="now"/>.</summary>
    public void ConvertFromTrial(DateTimeOffset now, TierPrice? price)
    {
        TrialEndsAt = null;
        Status = SubscriptionStatus.Active;
        StatusReason = null;
        CurrentPeriodStart = now;
        CurrentPeriodEnd = NextPeriodEnd(now, price);
    }

    /// <summary>Ends a trial nobody converted. Entitlements stop; the account stays open.</summary>
    public void ExpireTrial(DateTimeOffset now)
    {
        Status = SubscriptionStatus.Expired;
        TrialEndsAt = null;
        CurrentPeriodEnd = now;
        StatusReason = "The trial ended without a payment method on file.";
    }

    /// <summary>Rolls the period forward after a successful renewal charge.</summary>
    public void Renew(DateTimeOffset periodStart, TierPrice? price)
    {
        Status = SubscriptionStatus.Active;
        StatusReason = null;
        CurrentPeriodStart = periodStart;
        CurrentPeriodEnd = NextPeriodEnd(periodStart, price);
        ClearDunning();
    }

    /// <summary>Records a failed charge and returns when to try again, or null when the schedule is spent.</summary>
    /// <remarks>
    /// The <i>first</i> failure is not a retry — it is the charge that failed — so it starts the
    /// clock without moving the counter. Counting it as a retry would spend one of the four the
    /// agency was promised, and end the schedule on day 5 instead of day 7.
    /// </remarks>
    public DateTimeOffset? RecordChargeFailure(DateTimeOffset now, string reason)
    {
        if (DunningStartedAt is null)
        {
            DunningStartedAt = now;
        }
        else
        {
            DunningRetries++;
        }

        Status = SubscriptionStatus.PastDue;
        StatusReason = reason;

        return DunningSchedule.NextAttemptAt(DunningStartedAt.Value, DunningRetries);
    }

    /// <summary>True when every retry in the schedule has been made and none of them worked.</summary>
    public bool DunningIsExhausted => DunningSchedule.IsExhausted(DunningRetries);

    /// <summary>When the next dunning retry is due, or null when there is none.</summary>
    public DateTimeOffset? NextDunningAttemptAt =>
        DunningStartedAt is { } started ? DunningSchedule.NextAttemptAt(started, DunningRetries) : null;

    /// <summary>Moves the subscription onto another tier, from <paramref name="now"/>.</summary>
    public void MoveTo(SubscriptionTier tier, TierPrice? price, DateTimeOffset now, string reason)
    {
        ArgumentNullException.ThrowIfNull(tier);

        TierId = tier.Id;
        TierPriceId = price?.Id;
        Status = SubscriptionStatus.Active;
        StatusReason = reason;
        CurrentPeriodStart = now;
        CurrentPeriodEnd = NextPeriodEnd(now, price);
        TrialEndsAt = null;
        ClearDunning();
    }

    /// <summary>Ends the subscription. Entitlements stop at once; the agency keeps everything it made.</summary>
    public void Cancel(DateTimeOffset now, string reason)
    {
        Status = SubscriptionStatus.Cancelled;
        CancelledAt = now;
        CurrentPeriodEnd = now;
        StatusReason = reason;
        ClearDunning();
    }

    private void ClearDunning()
    {
        DunningStartedAt = null;
        DunningRetries = 0;
    }

    /// <summary>
    /// One billing period on from <paramref name="start"/>.
    /// </summary>
    /// <remarks>
    /// <c>AddMonths</c>, not 30 days: billed on the 31st of January, the next charge is the 28th of
    /// February and the one after that the 31st of March, which is what a person expects. Thirty-day
    /// periods drift a whole cycle earlier every year.
    /// </remarks>
    private static DateTimeOffset NextPeriodEnd(DateTimeOffset start, TierPrice? price) =>
        price?.Interval == BillingInterval.Annual ? start.AddYears(1) : start.AddMonths(1);
}
