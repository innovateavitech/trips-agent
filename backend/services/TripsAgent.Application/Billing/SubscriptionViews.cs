using TripsAgent.Domain.Billing;

namespace TripsAgent.Application.Billing;

/// <summary>A plan as an agency choosing one sees it. Nothing internal, no other tier's terms.</summary>
public sealed record PlanView(
    Guid TierId,
    string Code,
    string Name,
    string? Description,
    string Currency,
    long? AmountMinor,
    BillingInterval Interval,
    int TrialDays,
    bool IsCurrent,
    bool IsFallback,
    IReadOnlyList<PlanFeature> Features);

/// <summary>One line of a plan's "what you get" list.</summary>
/// <param name="Display">"25 sub-agents", "unlimited published listings", "on".</param>
public sealed record PlanFeature(string Code, string Name, string Display);

/// <summary>An agency's own plan, as its console shows it.</summary>
/// <param name="CardOnFile">
/// The card renewals are charged to, described but never quoted — "Visa •••• 4242". Null when
/// there is none, which is the thing the screen has to warn about before a renewal.
/// </param>
public sealed record MySubscriptionView(
    Guid? SubscriptionId,
    Guid? TierId,
    string PlanName,
    SubscriptionStatus? Status,
    string? StatusReason,
    string Currency,
    long? AmountMinor,
    BillingInterval? Interval,
    DateTimeOffset? CurrentPeriodStart,
    DateTimeOffset? CurrentPeriodEnd,
    DateTimeOffset? TrialEndsAt,
    DateTimeOffset? NextChargeAt,
    string? CardOnFile,
    int DunningRetries,
    DateTimeOffset? NextDunningAttemptAt,
    IReadOnlyList<PlanFeature> Features,
    ScheduledChangeView? ScheduledChange);

/// <summary>A plan change the agency has been told about and that has not landed yet.</summary>
public sealed record ScheduledChangeView(
    Guid MigrationId,
    string FromPlan,
    string ToPlan,
    SubscriptionChangeReason Reason,
    string Explanation,
    DateTimeOffset EffectiveAt,
    bool CanCancel);

/// <summary>One subscription invoice, with the lines that add up to its total.</summary>
public sealed record SubscriptionInvoiceView(
    Guid Id,
    string InvoiceNumber,
    string? ReceiptNumber,
    SubscriptionInvoiceStatus Status,
    string? StatusReason,
    string Currency,
    long TotalMinor,
    DateTimeOffset IssuedAt,
    DateTimeOffset DueAt,
    DateTimeOffset? PaidAt,
    DateTimeOffset PeriodStart,
    DateTimeOffset PeriodEnd,
    IReadOnlyList<SubscriptionInvoiceLineView> Lines);

/// <summary>One line of a subscription invoice.</summary>
public sealed record SubscriptionInvoiceLineView(string Description, int Quantity, long UnitAmountMinor, long AmountMinor);

/// <summary>How an agency's attempt to start or change a plan went.</summary>
public abstract record PlanChangeOutcome
{
    private PlanChangeOutcome() { }

    /// <summary>It is done: the agency is on the new plan now, and nothing needs paying.</summary>
    public sealed record Applied(MySubscriptionView Subscription) : PlanChangeOutcome;

    /// <summary>
    /// Send the agency to <paramref name="AuthorizationUrl"/> to pay. Card entry happens there and
    /// only there — build-plan decision 18.
    /// </summary>
    public sealed record PaymentRequired(string AuthorizationUrl, string Reference, long AmountMinor)
        : PlanChangeOutcome;

    /// <summary>
    /// Agreed and scheduled, not applied. A downgrade takes effect at the end of the period the
    /// agency has already paid for — build-plan decision 15.
    /// </summary>
    public sealed record Scheduled(ScheduledChangeView Change) : PlanChangeOutcome;

    /// <summary>The request does not make sense.</summary>
    public sealed record Invalid(string Reason) : PlanChangeOutcome;

    /// <summary>No such plan, or it is not one anybody can join.</summary>
    public sealed record NotFound : PlanChangeOutcome;

    /// <summary>The agency's current state forbids it, and the reason says why.</summary>
    public sealed record Refused(string Reason) : PlanChangeOutcome;

    /// <summary>The gateway could not be reached. Nothing was charged and nothing changed.</summary>
    public sealed record GatewayUnavailable(string Reason) : PlanChangeOutcome;
}
