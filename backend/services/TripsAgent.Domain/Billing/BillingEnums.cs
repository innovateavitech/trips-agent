namespace TripsAgent.Domain.Billing;

/// <summary>
/// Where a subscription tier is in its life.
/// </summary>
/// <remarks>
/// There is no <c>Deleted</c>, and there is no delete anywhere in this module. A tier that has ever
/// had a subscriber is evidence of what somebody was charged and why, so it is archived — hidden
/// from the plan picker, still readable by every invoice that points at it. FRD RS-6.
/// </remarks>
public enum TierStatus
{
    /// <summary>Being written. Nobody can subscribe to it and nobody outside the back office sees it.</summary>
    Draft = 1,

    /// <summary>Live: it appears in the plan picker and agencies may subscribe.</summary>
    Published = 2,

    /// <summary>Retired. Existing subscribers keep it; nobody new may join.</summary>
    Archived = 3,
}

/// <summary>How often a tier's price is charged.</summary>
/// <remarks>
/// <c>Annual</c> is modelled because the schema and the screens are the same shape either way, but
/// the MVP only ever bills monthly — see the build plan, "What the MVP leaves out", F9. The
/// renewal job refuses an annual price rather than guessing at a proration rule nobody has agreed.
/// </remarks>
public enum BillingInterval
{
    Monthly = 1,
    Annual = 2,
}

/// <summary>Where an agency's subscription has got to.</summary>
public enum SubscriptionStatus
{
    /// <summary>Inside a free trial. Entitlements apply in full; nothing has been charged.</summary>
    Trialing = 1,

    /// <summary>Paid up to <c>current_period_end</c>.</summary>
    Active = 2,

    /// <summary>A charge failed and the dunning schedule is running. Entitlements still apply.</summary>
    /// <remarks>
    /// Deliberately not a downgrade on the first failure: the commonest cause is an expired card,
    /// and taking a working agency's sub-agents away over one declined charge is a worse outcome
    /// than carrying them for a week. The schedule decides when patience runs out.
    /// </remarks>
    PastDue = 3,

    /// <summary>Ended by the agency or by us. Entitlements are whatever the free tier gives.</summary>
    Cancelled = 4,

    /// <summary>Ran past its period with nothing to renew it — an unconverted trial, most often.</summary>
    Expired = 5,
}

/// <summary>Where one subscription invoice has got to.</summary>
public enum SubscriptionInvoiceStatus
{
    /// <summary>Raised and payable. The first charge attempt has not answered yet.</summary>
    Open = 1,

    /// <summary>Paid in full. A receipt number is allocated at this point and never changes.</summary>
    Paid = 2,

    /// <summary>At least one charge failed; the dunning schedule owns it until it is paid or written off.</summary>
    PastDue = 3,

    /// <summary>Dunning ran out. The subscription was downgraded or suspended and this is not chased again.</summary>
    Uncollectible = 4,

    /// <summary>Cancelled before it was ever payable — a plan change that superseded it.</summary>
    Void = 5,
}

/// <summary>What one attempt to charge an invoice did.</summary>
public enum ChargeAttemptOutcome
{
    /// <summary>The money arrived.</summary>
    Succeeded = 1,

    /// <summary>The gateway declined. Worth trying again on the schedule.</summary>
    Failed = 2,

    /// <summary>The gateway could not be asked, or would not say. Not a decline; retried sooner.</summary>
    Unknown = 3,

    /// <summary>There was nothing to charge with — no stored authorisation.</summary>
    NoAuthorization = 4,
}

/// <summary>What happens to an existing subscriber when a tier they are on changes.</summary>
public enum TierMigrationPolicy
{
    /// <summary>Only agencies subscribing from now on get the new terms. Existing subscribers keep theirs.</summary>
    NewOnly = 1,

    /// <summary>Existing subscribers move too, after the notice period.</summary>
    MigrateExisting = 2,
}

/// <summary>Why a subscription is moving from one tier to another.</summary>
public enum SubscriptionChangeReason
{
    /// <summary>The agency chose a more expensive tier.</summary>
    Upgrade = 1,

    /// <summary>The agency chose a cheaper tier. Build-plan decision 15 applies.</summary>
    Downgrade = 2,

    /// <summary>An admin moved this tier's subscribers (<see cref="TierMigrationPolicy.MigrateExisting"/>).</summary>
    AdminMigration = 3,

    /// <summary>Dunning ran out and the agency fell back to the free tier.</summary>
    DunningFallback = 4,
}
