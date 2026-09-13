namespace TripsAgent.Contracts.Billing;

// ---------------------------------------------------------------------- the back office

/// <summary>One entitlement in the catalogue a tier picks from.</summary>
/// <param name="Code">What a feature checks against — <c>custom_domain</c>, <c>max_sub_agents</c>.</param>
/// <param name="ValueType"><c>Flag</c>, <c>Limit</c> or <c>Rate</c>. Decides what the tier builder shows.</param>
/// <param name="DefaultValue">
/// The JSON scalar an agency with no plan gets: <c>false</c>, <c>0</c>, <c>-1</c> for unlimited.
/// </param>
public sealed record EntitlementCatalogueItem(
    string Code,
    string Name,
    string Description,
    string ValueType,
    string DefaultValue);

/// <summary>One price on a tier.</summary>
/// <param name="AmountMinor">In minor units. ₦25,000.00 is 2,500,000.</param>
/// <param name="EffectiveTo">Null while it is the price being charged.</param>
public sealed record TierPriceResponse(
    Guid Id,
    string Currency,
    string Interval,
    long AmountMinor,
    bool IsPromotional,
    DateTimeOffset EffectiveFrom,
    DateTimeOffset? EffectiveTo);

/// <summary>One entitlement as a tier grants it.</summary>
/// <param name="Value">The JSON scalar stored against the tier.</param>
/// <param name="Display">The same value for a person: "on", "25", "unlimited", "1.5%".</param>
public sealed record TierEntitlementResponse(string Code, string Name, string ValueType, string Value, string Display);

/// <summary>A subscription tier, as the back office sees it.</summary>
/// <param name="Status"><c>Draft</c>, <c>Published</c> or <c>Archived</c>.</param>
/// <param name="Subscribers">How many agencies are on it. Non-zero means it can only be archived.</param>
/// <param name="IsFallback">True for the free plan a failed payment falls back to. At most one tier.</param>
public sealed record TierResponse(
    Guid Id,
    string Code,
    string Name,
    string? CustomerDescription,
    string Status,
    int TrialDays,
    int SortOrder,
    bool IsFallback,
    int Subscribers,
    DateTimeOffset? PublishedAt,
    DateTimeOffset? ArchivedAt,
    IReadOnlyList<TierPriceResponse> Prices,
    IReadOnlyList<TierEntitlementResponse> Entitlements);

/// <summary>Creating or editing a tier. Every write carries a reason, like every back-office action.</summary>
/// <param name="Code">Lowercase, letters, digits, hyphens and underscores. Ignored when editing.</param>
/// <param name="TrialDays">0 to 90. Zero for no trial.</param>
public sealed record SaveTierRequest(
    string Code,
    string Name,
    string? CustomerDescription,
    int TrialDays,
    int SortOrder,
    bool IsFallback,
    string Reason);

/// <summary>Setting a tier's price for one currency and interval.</summary>
/// <param name="Interval"><c>Monthly</c> or <c>Annual</c>. The MVP only bills monthly.</param>
/// <param name="AmountMinor">In minor units, never a decimal.</param>
public sealed record SetTierPriceRequest(string Currency, string Interval, long AmountMinor, string Reason);

/// <summary>
/// Setting everything a tier grants.
/// </summary>
/// <remarks>
/// The whole truth, not a patch: an entitlement left out of <paramref name="Entitlements"/> is
/// revoked and falls back to the catalogue's default.
/// </remarks>
public sealed record SetTierEntitlementsRequest(IReadOnlyList<TierEntitlementInput> Entitlements, string Reason);

/// <summary>One entitlement being set on a tier.</summary>
/// <param name="Value">A JSON scalar matching the entitlement's type: <c>true</c>, <c>25</c>, <c>-1</c>, <c>150</c>.</param>
public sealed record TierEntitlementInput(string Code, string Value);

/// <summary>A back-office action that only needs saying why.</summary>
public sealed record TierReasonRequest(string Reason);

/// <summary>Moving a tier's existing subscribers onto another tier, after the notice period.</summary>
public sealed record MigrateSubscribersRequest(Guid ToTierId, string Reason);

/// <summary>What a tier change did.</summary>
/// <param name="SubscribersScheduled">How many agencies were told they are moving. Zero for most changes.</param>
public sealed record TierChangeResponse(TierResponse Tier, int SubscribersScheduled);

/// <summary>One agency on a tier, for the back office's subscriber list.</summary>
public sealed record SubscriberResponse(
    Guid AgencyId,
    string AgencyName,
    string AgencyStatus,
    Guid SubscriptionId,
    string TierName,
    string Status,
    string Currency,
    long? AmountMinor,
    DateTimeOffset CurrentPeriodStart,
    DateTimeOffset CurrentPeriodEnd,
    DateTimeOffset? TrialEndsAt,
    int DunningRetries,
    DateTimeOffset? NextDunningAttemptAt,
    long OutstandingMinor);

// ---------------------------------------------------------------------- the agency

/// <summary>One line of a plan's "what you get" list.</summary>
public sealed record PlanFeatureResponse(string Code, string Name, string Display);

/// <summary>A plan an agency may choose.</summary>
/// <param name="AmountMinor">Null for a plan with no price in the agency's currency.</param>
public sealed record PlanResponse(
    Guid TierId,
    string Code,
    string Name,
    string? Description,
    string Currency,
    long? AmountMinor,
    string Interval,
    int TrialDays,
    bool IsCurrent,
    bool IsFallback,
    IReadOnlyList<PlanFeatureResponse> Features);

/// <summary>A plan change that has been agreed and has not landed yet.</summary>
public sealed record ScheduledChangeResponse(
    Guid MigrationId,
    string FromPlan,
    string ToPlan,
    string Reason,
    string Explanation,
    DateTimeOffset EffectiveAt,
    bool CanCancel);

/// <summary>The agency's own plan.</summary>
/// <param name="CardOnFile">"Visa •••• 4242", or null when there is none to charge.</param>
/// <param name="NextChargeAt">When the next renewal will be taken. Null on a free plan.</param>
public sealed record MyPlanResponse(
    Guid? SubscriptionId,
    Guid? TierId,
    string PlanName,
    string? Status,
    string? StatusReason,
    string Currency,
    long? AmountMinor,
    string? Interval,
    DateTimeOffset? CurrentPeriodStart,
    DateTimeOffset? CurrentPeriodEnd,
    DateTimeOffset? TrialEndsAt,
    DateTimeOffset? NextChargeAt,
    string? CardOnFile,
    int DunningRetries,
    DateTimeOffset? NextDunningAttemptAt,
    IReadOnlyList<PlanFeatureResponse> Features,
    ScheduledChangeResponse? ScheduledChange);

/// <summary>One line of a subscription invoice.</summary>
public sealed record SubscriptionInvoiceLineResponse(
    string Description,
    int Quantity,
    long UnitAmountMinor,
    long AmountMinor);

/// <summary>
/// One subscription invoice, with the lines that add up to its total.
/// </summary>
/// <remarks>
/// <paramref name="TotalMinor"/> always equals the sum of the lines' <c>AmountMinor</c>. The
/// database enforces it; a client may rely on it.
/// </remarks>
public sealed record SubscriptionInvoiceResponse(
    Guid Id,
    string InvoiceNumber,
    string? ReceiptNumber,
    string Status,
    string? StatusReason,
    string Currency,
    long TotalMinor,
    DateTimeOffset IssuedAt,
    DateTimeOffset DueAt,
    DateTimeOffset? PaidAt,
    DateTimeOffset PeriodStart,
    DateTimeOffset PeriodEnd,
    IReadOnlyList<SubscriptionInvoiceLineResponse> Lines);

/// <summary>Choosing a plan.</summary>
/// <param name="CallbackUrl">Where the gateway sends the agency back after paying.</param>
public sealed record ChoosePlanRequest(Guid TierId, string CallbackUrl);

/// <summary>
/// What happened when the agency chose a plan.
/// </summary>
/// <param name="Result">
/// <c>Applied</c> — done, nothing to pay. <c>PaymentRequired</c> — send them to
/// <paramref name="AuthorizationUrl"/>. <c>Scheduled</c> — agreed for later, see
/// <paramref name="ScheduledChange"/>.
/// </param>
public sealed record PlanChangeResponse(
    string Result,
    MyPlanResponse? Plan,
    string? AuthorizationUrl,
    string? Reference,
    long? AmountMinor,
    ScheduledChangeResponse? ScheduledChange);

/// <summary>Finishing a hosted-page payment, with the reference the gateway sent the agency back with.</summary>
public sealed record CompleteCheckoutRequest(string Reference);

/// <summary>
/// What the agency's plan allows, so a console can grey out what it cannot do.
/// </summary>
/// <remarks>
/// Advisory only. The answer here and the answer the server gives when the agency actually tries
/// come from the same place, but a client must never treat this as the enforcement.
/// </remarks>
public sealed record MyEntitlementsResponse(
    Guid? TierId,
    string PlanName,
    IReadOnlyList<PlanFeatureResponse> Entitlements);
