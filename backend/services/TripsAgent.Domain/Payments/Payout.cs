using TripsAgent.Domain.Auditing;
using TripsAgent.Domain.Common;

namespace TripsAgent.Domain.Payments;

/// <summary>Where a withdrawal is in its life.</summary>
/// <remarks>
/// Every step is separate on purpose. The interesting states are the ones in the middle:
/// <see cref="Approved"/> is money authorised and not yet sent, which is where every control
/// lives, and <see cref="OutcomeUnknown"/> is a transfer we asked for and got no answer to,
/// which is the one state that must never be resolved by asking again.
/// </remarks>
public enum PayoutStatus
{
    /// <summary>The agent has asked. The money is already out of their spendable balance.</summary>
    Requested = 1,

    /// <summary>A platform Finance user has authorised it. Nothing has been sent.</summary>
    Approved = 2,

    /// <summary>Refused before anything was sent. The money went back to the wallet.</summary>
    Rejected = 3,

    /// <summary>The transfer has been handed to the gateway and is on its way.</summary>
    Sending = 4,

    /// <summary>
    /// The gateway was asked to transfer and did not answer. <b>Unknown, not failed.</b>
    /// </summary>
    /// <remarks>
    /// Resolved only by asking the gateway what became of our reference. Sending again could put
    /// the money in a stranger's bank account twice, and unlike a duplicated ticket there is
    /// nobody to ring. See ADR-0008.
    /// </remarks>
    OutcomeUnknown = 5,

    /// <summary>The money reached the bank.</summary>
    Paid = 6,

    /// <summary>The gateway would not send it. The money went back to the wallet.</summary>
    Failed = 7,

    /// <summary>
    /// It left, and days later the bank sent it back — a closed account, a name mismatch.
    /// </summary>
    /// <remarks>
    /// Not <see cref="Failed"/>. The money made a round trip and both legs belong in the books;
    /// collapsing them would leave the gateway balance overstated by the amount.
    /// </remarks>
    Reversed = 8,
}

/// <summary>
/// An agency withdrawing its own money to its own bank.
/// </summary>
/// <remarks>
/// <para>
/// <b>The ledger moves first, the bank second.</b> Asking for a payout debits the wallet there and
/// then and parks the money in the platform's payout-payable account, so the balance an agent sees
/// is immediately the truth and two requests cannot spend the same naira. The transfer is what
/// happens to money that has already left the wallet — not the event that takes it out.
/// </para>
/// <para>
/// The reverse order is the obvious one and it is wrong: a transfer sent first and recorded
/// afterwards is, for as long as the recording takes, money that has left the building and is
/// still on somebody's balance.
/// </para>
/// <para>
/// Everything here is append-forward. A rejection, a failure and a reversal each write their own
/// balanced entries putting the money back; none of them edits the entries that took it out.
/// </para>
/// </remarks>
public sealed class Payout : Entity, IAuditableEntity, ITenantScoped, IAuditLogged
{
    private Payout()
    {
        Reference = string.Empty;
        Currency = string.Empty;
    }

    /// <summary>
    /// Records a request. The caller has already debited the wallet and posted the entries.
    /// </summary>
    /// <param name="agencyId">Who is withdrawing.</param>
    /// <param name="bankAccountId">Where it goes. Verified, or this should never have been called.</param>
    /// <param name="amount">How much, in minor units.</param>
    /// <param name="currency">Must match the wallet and the bank account.</param>
    /// <param name="reference">
    /// Ours, and the gateway's: it is sent as the transfer's reference, so a second initiation of
    /// the same payout is refused by the gateway rather than duplicated by it.
    /// </param>
    /// <param name="requestedByUserId">Who asked.</param>
    /// <param name="requestedAt">When.</param>
    /// <param name="ledgerTransactionGroupId">The entries that took the money out of the wallet.</param>
    public static Payout Request(
        Guid agencyId,
        Guid bankAccountId,
        Money amount,
        string currency,
        string reference,
        Guid requestedByUserId,
        DateTimeOffset requestedAt,
        Guid ledgerTransactionGroupId)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(agencyId, Guid.Empty);
        ArgumentOutOfRangeException.ThrowIfEqual(bankAccountId, Guid.Empty);
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);

        if (amount.AmountMinor <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(amount), amount.AmountMinor, "A payout must be positive.");
        }

        return new Payout
        {
            AgencyId = agencyId,
            BankAccountId = bankAccountId,
            AmountMinor = amount,
            Currency = (currency ?? string.Empty).Trim().ToUpperInvariant(),
            Reference = reference,
            Status = PayoutStatus.Requested,
            RequestedByUserId = requestedByUserId,
            RequestedAt = requestedAt,
            RequestLedgerTransactionGroupId = ledgerTransactionGroupId,
        };
    }

    public Guid AgencyId { get; private set; }

    public Guid BankAccountId { get; private set; }

    public Money AmountMinor { get; private set; }

    public string Currency { get; private set; }

    /// <summary>Ours and the gateway's. Unique platform-wide.</summary>
    public string Reference { get; private set; }

    public PayoutStatus Status { get; private set; }

    public Guid RequestedByUserId { get; private set; }

    public DateTimeOffset RequestedAt { get; private set; }

    /// <summary>
    /// Who approved it. Never the requester — the service enforces that.
    /// </summary>
    public Guid? ApprovedByUserId { get; private set; }

    public DateTimeOffset? ApprovedAt { get; private set; }

    public Guid? DecidedByUserId { get; private set; }

    public DateTimeOffset? RejectedAt { get; private set; }

    /// <summary>Why Finance refused, in words the agent is shown.</summary>
    public string? RejectionReason { get; private set; }

    /// <summary>When the transfer was handed to the gateway. Set once, never again.</summary>
    public DateTimeOffset? SentAt { get; private set; }

    /// <summary>The gateway's own handle for the transfer, once it has given us one.</summary>
    public string? GatewayTransferCode { get; private set; }

    /// <summary>The gateway's own status string, kept verbatim for support.</summary>
    public string? GatewayStatus { get; private set; }

    /// <summary>How many times the status poller has asked. Bounded, then a human is told.</summary>
    public int StatusQueries { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    /// <summary>Why it failed or was returned, in the gateway's words.</summary>
    public string? FailureReason { get; private set; }

    /// <summary>The entries that took the money out of the wallet at request time.</summary>
    public Guid RequestLedgerTransactionGroupId { get; private set; }

    /// <summary>The entries that recorded the money leaving the platform. Null until paid.</summary>
    public Guid? SettlementLedgerTransactionGroupId { get; private set; }

    /// <summary>The entries that put the money back — rejected, failed or reversed.</summary>
    public Guid? ReturnLedgerTransactionGroupId { get; private set; }

    /// <summary>
    /// Bumped on every change and compared on write.
    /// </summary>
    /// <remarks>
    /// The poller and a transfer webhook can reach the same payout at the same moment and both
    /// decide to post its settlement. The second save matches no row and rolls back, so exactly
    /// one set of entries is written — which is the realistic race here, not a theoretical one.
    /// </remarks>
    public int Version { get; private set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>True when nothing more will happen to this payout.</summary>
    public bool IsSettled => Status is PayoutStatus.Paid or PayoutStatus.Failed
        or PayoutStatus.Rejected or PayoutStatus.Reversed;

    /// <summary>True when the money is out of the wallet and has not come back.</summary>
    public bool IsInFlight => Status is PayoutStatus.Requested or PayoutStatus.Approved
        or PayoutStatus.Sending or PayoutStatus.OutcomeUnknown;

    /// <summary>True when the gateway still owes us an answer about it.</summary>
    public bool AwaitsGatewayAnswer => Status is PayoutStatus.Sending or PayoutStatus.OutcomeUnknown;

    /// <summary>Authorises the transfer. A different person from the one who asked.</summary>
    public void Approve(Guid approvedByUserId, DateTimeOffset at)
    {
        RequireStatus(PayoutStatus.Requested, "approved");

        if (approvedByUserId == RequestedByUserId)
        {
            throw new InvalidOperationException(
                "A payout cannot be approved by the person who requested it. Two pairs of eyes is the whole "
                + "control; one person who can both ask for money and send it is not an approval step.");
        }

        ApprovedByUserId = approvedByUserId;
        ApprovedAt = at;
        Status = PayoutStatus.Approved;
        Version++;
    }

    /// <summary>Refuses it before anything is sent. The caller puts the money back.</summary>
    public void Reject(Guid decidedByUserId, string reason, DateTimeOffset at, Guid returnLedgerTransactionGroupId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        RequireStatus(PayoutStatus.Requested, "rejected");

        DecidedByUserId = decidedByUserId;
        RejectionReason = reason;
        RejectedAt = at;
        CompletedAt = at;
        ReturnLedgerTransactionGroupId = returnLedgerTransactionGroupId;
        Status = PayoutStatus.Rejected;
        Version++;
    }

    /// <summary>
    /// Marks the transfer as handed over, <i>before</i> the call is made.
    /// </summary>
    /// <remarks>
    /// Written first and committed first, so a process that dies between this and the gateway's
    /// answer leaves a payout that says "I may have been sent" rather than one that says
    /// "approved, send me". The sender refuses anything that is not <see cref="PayoutStatus.Approved"/>,
    /// so a crash cannot produce a second call.
    /// </remarks>
    public void MarkSending(DateTimeOffset at)
    {
        RequireStatus(PayoutStatus.Approved, "sent");

        SentAt = at;
        Status = PayoutStatus.Sending;
        Version++;
    }

    /// <summary>
    /// The gateway did not answer. The money may or may not be on its way.
    /// </summary>
    /// <remarks>
    /// Deliberately not a failure. A failure would put the money back in the wallet, and if the
    /// transfer did in fact go through, the agency would have been paid twice — once by the bank
    /// and once by the books.
    /// </remarks>
    public void MarkOutcomeUnknown(string reason, DateTimeOffset at)
    {
        if (Status is not (PayoutStatus.Sending or PayoutStatus.OutcomeUnknown))
        {
            throw new InvalidOperationException($"A payout that is {Status} has no unknown outcome to record.");
        }

        FailureReason = reason;
        Status = PayoutStatus.OutcomeUnknown;
        Version++;
    }

    /// <summary>Records the gateway's own handle for the transfer.</summary>
    public void RecordGatewayTransfer(string? transferCode, string? gatewayStatus)
    {
        if (transferCode is { Length: > 0 })
        {
            GatewayTransferCode = transferCode;
        }

        if (gatewayStatus is { Length: > 0 })
        {
            GatewayStatus = gatewayStatus;
        }

        Version++;
    }

    /// <summary>Counts one status query, so an endless poll can be escalated to a person.</summary>
    public void RecordStatusQuery()
    {
        StatusQueries++;
        Version++;
    }

    /// <summary>The money reached the bank.</summary>
    public void MarkPaid(string gatewayStatus, DateTimeOffset at, Guid settlementLedgerTransactionGroupId)
    {
        RequireGatewayAnswerPending("paid");

        GatewayStatus = gatewayStatus;
        CompletedAt = at;
        SettlementLedgerTransactionGroupId = settlementLedgerTransactionGroupId;
        FailureReason = null;
        Status = PayoutStatus.Paid;
        Version++;
    }

    /// <summary>The gateway refused or the transfer failed. The caller puts the money back.</summary>
    public void MarkFailed(string reason, DateTimeOffset at, Guid returnLedgerTransactionGroupId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        RequireGatewayAnswerPending("failed");

        FailureReason = reason;
        CompletedAt = at;
        ReturnLedgerTransactionGroupId = returnLedgerTransactionGroupId;
        Status = PayoutStatus.Failed;
        Version++;
    }

    /// <summary>It was paid and the bank sent it back. The caller puts the money back.</summary>
    public void MarkReversed(string reason, DateTimeOffset at, Guid returnLedgerTransactionGroupId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        if (Status is not (PayoutStatus.Paid or PayoutStatus.Sending or PayoutStatus.OutcomeUnknown))
        {
            throw new InvalidOperationException($"A payout that is {Status} cannot be reversed.");
        }

        FailureReason = reason;
        CompletedAt = at;
        ReturnLedgerTransactionGroupId = returnLedgerTransactionGroupId;
        Status = PayoutStatus.Reversed;
        Version++;
    }

    private void RequireGatewayAnswerPending(string verb)
    {
        if (!AwaitsGatewayAnswer)
        {
            throw new InvalidOperationException(
                $"A payout that is {Status} cannot be marked {verb}: the gateway owes us no answer about it.");
        }
    }

    private void RequireStatus(PayoutStatus expected, string verb)
    {
        if (Status != expected)
        {
            throw new InvalidOperationException($"Only a {expected} payout can be {verb}; this one is {Status}.");
        }
    }
}
