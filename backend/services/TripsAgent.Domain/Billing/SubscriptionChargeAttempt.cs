using TripsAgent.Domain.Auditing;
using TripsAgent.Domain.Common;

namespace TripsAgent.Domain.Billing;

/// <summary>
/// One attempt to take money for a subscription invoice.
/// </summary>
/// <remarks>
/// Every attempt gets a row, successful or not — the failures are the record of the dunning
/// schedule actually running, and they are what anyone answering "why was I downgraded?" needs.
/// Append-only: a trigger and a REVOKE both refuse an update.
/// </remarks>
public sealed class SubscriptionChargeAttempt : Entity, IAuditableEntity, ITenantScoped, IAuditLogged
{
    private SubscriptionChargeAttempt() => Reference = string.Empty;

    private SubscriptionChargeAttempt(
        Guid agencyId,
        Guid invoiceId,
        int attemptNumber,
        Money amount,
        string reference,
        ChargeAttemptOutcome outcome,
        DateTimeOffset attemptedAt,
        string? failureReason,
        DateTimeOffset? nextAttemptAt)
    {
        AgencyId = agencyId;
        InvoiceId = invoiceId;
        AttemptNumber = attemptNumber;
        AmountMinor = amount;
        Reference = reference;
        Outcome = outcome;
        AttemptedAt = attemptedAt;
        FailureReason = Truncate(failureReason);
        NextAttemptAt = nextAttemptAt;
    }

    /// <summary>Longer than this and it is a stack trace, not a reason a person can read.</summary>
    public const int MaxFailureReasonLength = 500;

    public static SubscriptionChargeAttempt Record(
        Guid agencyId,
        Guid invoiceId,
        int attemptNumber,
        Money amount,
        string reference,
        ChargeAttemptOutcome outcome,
        DateTimeOffset attemptedAt,
        string? failureReason = null,
        DateTimeOffset? nextAttemptAt = null)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(agencyId, Guid.Empty);
        ArgumentOutOfRangeException.ThrowIfNegative(attemptNumber);
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);

        return new SubscriptionChargeAttempt(
            agencyId, invoiceId, attemptNumber, amount, reference, outcome, attemptedAt, failureReason, nextAttemptAt);
    }

    public Guid AgencyId { get; private set; }

    public Guid InvoiceId { get; private set; }

    /// <summary>Zero for the charge on the due date; 1 to 4 for the dunning retries.</summary>
    public int AttemptNumber { get; private set; }

    public Money AmountMinor { get; private set; }

    /// <summary>The gateway reference, which is also this attempt's idempotency key.</summary>
    public string Reference { get; private set; }

    public ChargeAttemptOutcome Outcome { get; private set; }

    public DateTimeOffset AttemptedAt { get; private set; }

    /// <summary>The gateway's own words, kept verbatim for support. Null when it worked.</summary>
    public string? FailureReason { get; private set; }

    /// <summary>When the schedule says to try again. Null when it worked or the schedule is spent.</summary>
    public DateTimeOffset? NextAttemptAt { get; private set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    private static string? Truncate(string? reason) =>
        reason is null or { Length: <= MaxFailureReasonLength } ? reason : reason[..MaxFailureReasonLength];
}

/// <summary>
/// A reusable card authorisation from Paystack, so a renewal can be charged without asking again.
/// </summary>
/// <remarks>
/// <para>
/// <b>There is no card number here and there never can be.</b> The agency types its card on
/// Paystack's hosted page (build-plan decision 18), Paystack hands back an opaque authorisation
/// code, and this row holds that code plus the four digits and expiry the agency needs to recognise
/// which card it is. None of that is card data in the PCI sense, which is exactly why the design
/// keeps us in SAQ-A.
/// </para>
/// <para>
/// Captured from the agency's <i>first</i> payment — the hosted checkout that starts the
/// subscription — and reused for every renewal after it.
/// </para>
/// </remarks>
public sealed class PaymentAuthorization : Entity, IAuditableEntity, ITenantScoped, IAuditLogged
{
    private PaymentAuthorization()
    {
        AuthorizationCode = string.Empty;
        Gateway = string.Empty;
    }

    private PaymentAuthorization(Guid agencyId, string gateway, string authorizationCode)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(agencyId, Guid.Empty);
        ArgumentException.ThrowIfNullOrWhiteSpace(gateway);
        ArgumentException.ThrowIfNullOrWhiteSpace(authorizationCode);

        AgencyId = agencyId;
        Gateway = gateway;
        AuthorizationCode = authorizationCode;
    }

    public static PaymentAuthorization Capture(
        Guid agencyId,
        string gateway,
        string authorizationCode,
        string? cardBrand,
        string? last4,
        string? expiryMonth,
        string? expiryYear,
        string? bank,
        DateTimeOffset at)
    {
        var authorization = new PaymentAuthorization(agencyId, gateway, authorizationCode);
        authorization.Describe(cardBrand, last4, expiryMonth, expiryYear, bank);
        authorization.LastUsedAt = null;
        authorization.CapturedAt = at;
        authorization.IsDefault = true;

        return authorization;
    }

    public Guid AgencyId { get; private set; }

    /// <summary>Which gateway issued it — <c>paystack</c> today.</summary>
    public string Gateway { get; private set; }

    /// <summary>The opaque token the gateway charges against. Never a card number.</summary>
    public string AuthorizationCode { get; private set; }

    /// <summary>"visa", "mastercard", "verve". For the screen only.</summary>
    public string? CardBrand { get; private set; }

    /// <summary>The last four digits, so the agency can tell one card from another.</summary>
    public string? Last4 { get; private set; }

    /// <summary>Two digits. Kept so the console can warn before a card expires.</summary>
    public string? ExpiryMonth { get; private set; }

    /// <summary>Four digits.</summary>
    public string? ExpiryYear { get; private set; }

    /// <summary>The issuing bank, when the gateway says. For the screen only.</summary>
    public string? Bank { get; private set; }

    /// <summary>True for the one a renewal charges. One per agency, enforced by a partial unique index.</summary>
    public bool IsDefault { get; private set; }

    /// <summary>Set when the agency replaces or removes it. A revoked authorisation is never charged.</summary>
    public DateTimeOffset? RevokedAt { get; private set; }

    public DateTimeOffset CapturedAt { get; private set; }

    public DateTimeOffset? LastUsedAt { get; private set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>True when a renewal may be charged against it.</summary>
    public bool IsChargeable => RevokedAt is null;

    /// <summary>"Visa •••• 4242", or "Card on file" when the gateway told us nothing useful.</summary>
    public string Display =>
        Last4 is { Length: > 0 }
            ? $"{Describe(CardBrand)} •••• {Last4}"
            : "Card on file";

    public void RecordUse(DateTimeOffset at) => LastUsedAt = at;

    public void Revoke(DateTimeOffset at)
    {
        RevokedAt ??= at;
        IsDefault = false;
    }

    public void Demote() => IsDefault = false;

    private void Describe(string? cardBrand, string? last4, string? expiryMonth, string? expiryYear, string? bank)
    {
        CardBrand = Clean(cardBrand, 30);
        Last4 = Clean(last4, 4);
        ExpiryMonth = Clean(expiryMonth, 2);
        ExpiryYear = Clean(expiryYear, 4);
        Bank = Clean(bank, 120);
    }

    private static string Describe(string? brand) =>
        string.IsNullOrWhiteSpace(brand)
            ? "Card"
            : string.Concat(char.ToUpperInvariant(brand[0]), brand[1..].ToLowerInvariant());

    private static string? Clean(string? value, int maxLength) =>
        string.IsNullOrWhiteSpace(value)
            ? null
            : value.Trim() is { } trimmed && trimmed.Length > maxLength ? trimmed[..maxLength] : value.Trim();
}
