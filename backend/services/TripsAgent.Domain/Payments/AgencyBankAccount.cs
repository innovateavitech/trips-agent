using TripsAgent.Domain.Auditing;
using TripsAgent.Domain.Common;

namespace TripsAgent.Domain.Payments;

/// <summary>Where a bank account is in its life.</summary>
public enum BankAccountStatus
{
    /// <summary>Captured, and the bank has not yet been asked who owns it.</summary>
    PendingVerification = 1,

    /// <summary>The bank returned a name. Money may be sent here.</summary>
    Verified = 2,

    /// <summary>The bank would not resolve it, or the platform refused it. Nothing may be sent.</summary>
    Rejected = 3,

    /// <summary>Retired by the agency. Kept, because payouts point at it.</summary>
    Removed = 4,
}

/// <summary>
/// A Nigerian bank account an agency withdraws to.
/// </summary>
/// <remarks>
/// <para>
/// <b>The name on this record is the bank's, never the agent's.</b> An account number is ten
/// digits with no checksum a human notices, so a transposed pair is an ordinary typing mistake
/// that sends somebody else's money to a stranger — and a transfer, once made, is not coming
/// back. So the number is resolved against the bank before the account is usable, and what the
/// bank said is what is stored. What the agent typed is kept beside it only so a mismatch can be
/// shown to them.
/// </para>
/// <para>
/// Verification is deliberately not a boolean. "Nobody has asked yet" and "the bank says this
/// account does not exist" are different situations for whoever is looking at the screen, and
/// collapsing them into <c>is_verified = false</c> loses the difference exactly when it matters.
/// </para>
/// </remarks>
public sealed class AgencyBankAccount : Entity, IAuditableEntity, ITenantScoped, IAuditLogged
{
    /// <summary>A NUBAN is exactly this many digits. Anything else is not an account number.</summary>
    public const int NubanLength = 10;

    private AgencyBankAccount()
    {
        BankCode = string.Empty;
        BankName = string.Empty;
        AccountNumber = string.Empty;
        AccountNameProvided = string.Empty;
        Currency = string.Empty;
    }

    /// <summary>
    /// Captures an account, unverified.
    /// </summary>
    /// <param name="agencyId">Whose account it is.</param>
    /// <param name="bankCode">The bank's own code, as the gateway lists it.</param>
    /// <param name="bankName">The bank's name, for display.</param>
    /// <param name="accountNumber">Ten digits. Refused otherwise.</param>
    /// <param name="accountNameProvided">What the agent typed, kept only to show a mismatch.</param>
    /// <param name="currency">ISO 4217. The wallet it draws from must match.</param>
    /// <param name="addedByUserId">Who added it, for the audit trail.</param>
    public static AgencyBankAccount Capture(
        Guid agencyId,
        string bankCode,
        string bankName,
        string accountNumber,
        string accountNameProvided,
        string currency,
        Guid? addedByUserId)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(agencyId, Guid.Empty);
        ArgumentException.ThrowIfNullOrWhiteSpace(bankCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(bankName);

        var digits = (accountNumber ?? string.Empty).Trim();

        if (digits.Length != NubanLength || !digits.All(char.IsAsciiDigit))
        {
            throw new ArgumentException(
                $"'{accountNumber}' is not a NUBAN. A Nigerian account number is {NubanLength} digits.",
                nameof(accountNumber));
        }

        var normalisedCurrency = (currency ?? string.Empty).Trim().ToUpperInvariant();

        if (normalisedCurrency.Length != 3)
        {
            throw new ArgumentException($"'{currency}' is not an ISO 4217 currency code.", nameof(currency));
        }

        return new AgencyBankAccount
        {
            AgencyId = agencyId,
            BankCode = bankCode.Trim(),
            BankName = bankName.Trim(),
            AccountNumber = digits,
            AccountNameProvided = (accountNameProvided ?? string.Empty).Trim(),
            Currency = normalisedCurrency,
            Status = BankAccountStatus.PendingVerification,
            AddedByUserId = addedByUserId,
        };
    }

    public Guid AgencyId { get; private set; }

    public string BankCode { get; private set; }

    public string BankName { get; private set; }

    /// <summary>Ten digits. Shown masked everywhere except to the agency that owns it.</summary>
    public string AccountNumber { get; private set; }

    /// <summary>What the agent typed. Never used to address money.</summary>
    public string AccountNameProvided { get; private set; }

    /// <summary>
    /// What the bank said the account is called. Null until it has been asked.
    /// </summary>
    /// <remarks>
    /// This is the name on statements, on the payout screen and in support conversations, because
    /// it is the only one that came from outside this system.
    /// </remarks>
    public string? AccountNameResolved { get; private set; }

    public string Currency { get; private set; }

    public BankAccountStatus Status { get; private set; }

    /// <summary>
    /// The gateway's handle for this account, created once and reused by every transfer.
    /// </summary>
    /// <remarks>
    /// Paystack calls it a transfer recipient. Creating one per payout would work and would leave
    /// the gateway's dashboard unreadable, so it is created with the account and kept.
    /// </remarks>
    public string? GatewayRecipientCode { get; private set; }

    public DateTimeOffset? VerifiedAt { get; private set; }

    /// <summary>Why the bank or the platform refused it, in the bank's own words where there are any.</summary>
    public string? RejectionReason { get; private set; }

    /// <summary>
    /// The one an agent's payouts default to. At most one per agency; the database enforces it.
    /// </summary>
    public bool IsDefault { get; private set; }

    public Guid? AddedByUserId { get; private set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>True when money may be sent here.</summary>
    public bool CanReceiveMoney => Status == BankAccountStatus.Verified && GatewayRecipientCode is { Length: > 0 };

    /// <summary>The last four digits, for showing an account without exposing it.</summary>
    public string MaskedNumber => $"******{AccountNumber[^4..]}";

    /// <summary>
    /// Records the name the bank returned, and the gateway handle transfers will use.
    /// </summary>
    /// <remarks>
    /// The resolved name is stored whatever it says. A mismatch against what the agent typed is
    /// shown to them rather than acted on: agents routinely type "Lagos Travel" for an account
    /// registered as "LAGOS TRAVEL LIMITED", and refusing that would be refusing the common case.
    /// What must never happen is the typed name being treated as fact, and it never is.
    /// </remarks>
    public void MarkVerified(string accountNameResolved, string gatewayRecipientCode, DateTimeOffset at)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountNameResolved);
        ArgumentException.ThrowIfNullOrWhiteSpace(gatewayRecipientCode);

        AccountNameResolved = accountNameResolved.Trim();
        GatewayRecipientCode = gatewayRecipientCode.Trim();
        Status = BankAccountStatus.Verified;
        VerifiedAt = at;
        RejectionReason = null;
    }

    /// <summary>The bank would not resolve it, or the platform will not use it.</summary>
    public void MarkRejected(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        Status = BankAccountStatus.Rejected;
        RejectionReason = reason;
        VerifiedAt = null;
        IsDefault = false;
    }

    /// <summary>Makes this the account payouts default to.</summary>
    public void MakeDefault()
    {
        if (Status != BankAccountStatus.Verified)
        {
            throw new InvalidOperationException(
                $"An account that is {Status} cannot be the default — the default is what a payout is addressed to.");
        }

        IsDefault = true;
    }

    /// <summary>Stops being the default, without being removed.</summary>
    public void ClearDefault() => IsDefault = false;

    /// <summary>
    /// Retires the account.
    /// </summary>
    /// <remarks>
    /// Never deleted. Payouts name the account they were sent to, and a payout whose destination
    /// has vanished cannot be explained to anyone asking where their money went.
    /// </remarks>
    public void Remove()
    {
        Status = BankAccountStatus.Removed;
        IsDefault = false;
    }
}
