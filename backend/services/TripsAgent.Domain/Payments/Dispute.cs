using TripsAgent.Domain.Auditing;
using TripsAgent.Domain.Common;

namespace TripsAgent.Domain.Payments;

/// <summary>Where a chargeback is in its life.</summary>
public enum DisputeStatus
{
    /// <summary>The gateway has told us, and the agency has been asked for evidence.</summary>
    Open = 1,

    /// <summary>Evidence has been filed with the gateway. Waiting on the bank.</summary>
    EvidenceSubmitted = 2,

    /// <summary>The deadline passed with nothing filed. Lost by default unless the bank says otherwise.</summary>
    Expired = 3,

    /// <summary>The charge stood. The frozen money goes back to the agency.</summary>
    Won = 4,

    /// <summary>The cardholder's bank took the money.</summary>
    Lost = 5,
}

/// <summary>Whether the disputed money could actually be frozen.</summary>
public enum DisputeHoldOutcome
{
    /// <summary>Not attempted yet.</summary>
    None = 0,

    /// <summary>Frozen: debited out of the agency's wallet and parked.</summary>
    Held = 1,

    /// <summary>
    /// The agency did not have it, or their wallet was frozen. Nothing was debited and somebody
    /// was told.
    /// </summary>
    /// <remarks>
    /// Recorded rather than retried. If the chargeback stands, the platform is out of pocket and
    /// has to collect from the agency — a commercial problem, and one nobody can act on if the
    /// only record of it is an exception in a log.
    /// </remarks>
    Uncovered = 2,

    /// <summary>The hold has been unwound — the dispute was won, or the money went to the cardholder.</summary>
    Settled = 3,
}

/// <summary>
/// A cardholder's bank taking money back, and the clock that starts when it does.
/// </summary>
/// <remarks>
/// <para>
/// <b>Not a refund.</b> A refund is us deciding to give money back on rules we agreed to. A
/// dispute is somebody else deciding, on a deadline we do not set, and the money moves whether we
/// answer or not. The two share a shape and nothing else.
/// </para>
/// <para>
/// Three things happen the moment one arrives, and none of them is optional: the money is frozen
/// out of the agency's wallet, the agency is told what evidence is wanted and by when, and a
/// platform alert is raised. A dispute that sits in a table doing nothing is one the deadline
/// decides for us.
/// </para>
/// <para>
/// <see cref="EvidenceDueAt"/> is an absolute instant in UTC. A deadline missed by an hour loses
/// the money regardless of the merits, so it is not a field to be clever about time zones with.
/// </para>
/// </remarks>
public sealed class Dispute : Entity, IAuditableEntity, ITenantScoped, IAuditLogged
{
    private Dispute()
    {
        GatewayDisputeId = string.Empty;
        Currency = string.Empty;
        PaymentReference = string.Empty;
    }

    /// <summary>Records a chargeback the gateway has told us about.</summary>
    /// <param name="agencyId">Whose payment it was, resolved from the payment transaction.</param>
    /// <param name="paymentTransactionId">The payment being disputed.</param>
    /// <param name="paymentReference">Our reference for it, for support and for the gateway.</param>
    /// <param name="gatewayDisputeId">The gateway's id for the dispute. Unique; it is the dedup key.</param>
    /// <param name="amount">How much is being taken back — not always the whole payment.</param>
    /// <param name="currency">ISO 4217.</param>
    /// <param name="category">The gateway's category, verbatim: chargeback, fraud, and so on.</param>
    /// <param name="reason">What the cardholder said, verbatim.</param>
    /// <param name="openedAt">When the gateway says it was raised.</param>
    /// <param name="evidenceDueAt">The deadline, in UTC.</param>
    /// <param name="orderId">The order it was for, where there is one.</param>
    public static Dispute Open(
        Guid agencyId,
        Guid paymentTransactionId,
        string paymentReference,
        string gatewayDisputeId,
        Money amount,
        string currency,
        string? category,
        string? reason,
        DateTimeOffset openedAt,
        DateTimeOffset evidenceDueAt,
        Guid? orderId)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(agencyId, Guid.Empty);
        ArgumentException.ThrowIfNullOrWhiteSpace(gatewayDisputeId);

        if (amount.AmountMinor <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(amount), amount.AmountMinor, "A dispute must be for a positive amount.");
        }

        return new Dispute
        {
            AgencyId = agencyId,
            PaymentTransactionId = paymentTransactionId,
            PaymentReference = paymentReference,
            OrderId = orderId,
            GatewayDisputeId = gatewayDisputeId,
            AmountMinor = amount,
            Currency = (currency ?? string.Empty).Trim().ToUpperInvariant(),
            Category = category,
            Reason = reason,
            OpenedAt = openedAt,
            EvidenceDueAt = evidenceDueAt,
            Status = DisputeStatus.Open,
            HoldOutcome = DisputeHoldOutcome.None,
        };
    }

    public Guid AgencyId { get; private set; }

    public Guid PaymentTransactionId { get; private set; }

    public string PaymentReference { get; private set; }

    /// <summary>The order the payment was for, where we know it.</summary>
    public Guid? OrderId { get; private set; }

    /// <summary>The gateway's id. Unique — the same delivery five times produces one row.</summary>
    public string GatewayDisputeId { get; private set; }

    public Money AmountMinor { get; private set; }

    public string Currency { get; private set; }

    /// <summary>The gateway's category, verbatim. Categories change; ours would go stale.</summary>
    public string? Category { get; private set; }

    /// <summary>What the cardholder told their bank, verbatim.</summary>
    public string? Reason { get; private set; }

    public DisputeStatus Status { get; private set; }

    public DisputeHoldOutcome HoldOutcome { get; private set; }

    /// <summary>Why the money could not be frozen, when it could not.</summary>
    public string? HoldFailureReason { get; private set; }

    public DateTimeOffset OpenedAt { get; private set; }

    /// <summary>The deadline, in UTC. Missing it loses the money.</summary>
    public DateTimeOffset EvidenceDueAt { get; private set; }

    public DateTimeOffset? EvidenceSubmittedAt { get; private set; }

    /// <summary>
    /// Exactly what was sent to the gateway, as JSON.
    /// </summary>
    /// <remarks>
    /// Kept because when a dispute is lost, "what did we actually submit" is the first question
    /// and the one nobody can answer from a status field.
    /// </remarks>
    public string? EvidencePayload { get; private set; }

    /// <summary>The agency's own account of the sale, in their words.</summary>
    public string? EvidenceNote { get; private set; }

    /// <summary>Documents the agency attached, as a JSON array of asset ids.</summary>
    public string? EvidenceAssetIds { get; private set; }

    public Guid? EvidenceSubmittedByUserId { get; private set; }

    public DateTimeOffset? ResolvedAt { get; private set; }

    /// <summary>The gateway's own resolution string, verbatim.</summary>
    public string? Resolution { get; private set; }

    /// <summary>The entries that froze the money. Null when it could not be frozen.</summary>
    public Guid? HoldLedgerTransactionGroupId { get; private set; }

    /// <summary>The entries that unwound the hold, once the dispute was decided.</summary>
    public Guid? ResolutionLedgerTransactionGroupId { get; private set; }

    /// <summary>When the agency was last reminded that the clock is running.</summary>
    public DateTimeOffset? LastReminderAt { get; private set; }

    /// <summary>Bumped on every change, so two workers cannot both resolve one dispute.</summary>
    public int Version { get; private set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>True while evidence may still be filed.</summary>
    public bool AcceptsEvidence(DateTimeOffset now) =>
        Status is DisputeStatus.Open or DisputeStatus.EvidenceSubmitted && now < EvidenceDueAt;

    /// <summary>True when the gateway has decided.</summary>
    public bool IsResolved => Status is DisputeStatus.Won or DisputeStatus.Lost;

    /// <summary>Records that the money was frozen out of the wallet.</summary>
    public void RecordHeld(Guid ledgerTransactionGroupId)
    {
        HoldOutcome = DisputeHoldOutcome.Held;
        HoldLedgerTransactionGroupId = ledgerTransactionGroupId;
        HoldFailureReason = null;
        Version++;
    }

    /// <summary>Records that the money could not be frozen, and why.</summary>
    public void RecordUncovered(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        HoldOutcome = DisputeHoldOutcome.Uncovered;
        HoldFailureReason = reason;
        Version++;
    }

    /// <summary>Records what was filed with the gateway, and when.</summary>
    public void RecordEvidence(
        string? note,
        string? assetIdsJson,
        string payload,
        Guid? submittedByUserId,
        DateTimeOffset at)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payload);

        if (!AcceptsEvidence(at))
        {
            throw new InvalidOperationException(
                Status is DisputeStatus.Won or DisputeStatus.Lost or DisputeStatus.Expired
                    ? $"This dispute is {Status}; evidence can no longer change anything."
                    : $"The deadline for evidence passed at {EvidenceDueAt:u}. Filing it now would be refused "
                      + "by the gateway, and telling the agent it was sent would be worse than telling them it was late.");
        }

        EvidenceNote = note;
        EvidenceAssetIds = assetIdsJson;
        EvidencePayload = payload;
        EvidenceSubmittedByUserId = submittedByUserId;
        EvidenceSubmittedAt = at;
        Status = DisputeStatus.EvidenceSubmitted;
        Version++;
    }

    /// <summary>Records that the deadline passed with nothing filed.</summary>
    public void MarkExpired(DateTimeOffset at)
    {
        if (Status != DisputeStatus.Open)
        {
            return;
        }

        Status = DisputeStatus.Expired;
        LastReminderAt = at;
        Version++;
    }

    /// <summary>Records a reminder, so the escalation job does not send the same one every hour.</summary>
    public void RecordReminder(DateTimeOffset at)
    {
        LastReminderAt = at;
        Version++;
    }

    /// <summary>
    /// The charge stood. The caller unwinds the hold back into the wallet.
    /// </summary>
    public void MarkWon(string resolution, DateTimeOffset at, Guid? resolutionLedgerTransactionGroupId)
    {
        RequireUnresolved("won");

        Status = DisputeStatus.Won;
        Resolution = resolution;
        ResolvedAt = at;
        ResolutionLedgerTransactionGroupId = resolutionLedgerTransactionGroupId;
        SettleHold();
        Version++;
    }

    /// <summary>
    /// The cardholder's bank took the money. The caller sends the held money after it.
    /// </summary>
    public void MarkLost(string resolution, DateTimeOffset at, Guid? resolutionLedgerTransactionGroupId)
    {
        RequireUnresolved("lost");

        Status = DisputeStatus.Lost;
        Resolution = resolution;
        ResolvedAt = at;
        ResolutionLedgerTransactionGroupId = resolutionLedgerTransactionGroupId;
        SettleHold();
        Version++;
    }

    private void SettleHold()
    {
        if (HoldOutcome == DisputeHoldOutcome.Held)
        {
            HoldOutcome = DisputeHoldOutcome.Settled;
        }
    }

    private void RequireUnresolved(string verb)
    {
        if (IsResolved)
        {
            throw new InvalidOperationException(
                $"This dispute is already {Status} and cannot be marked {verb}. Its money has been moved once; "
                + "moving it again on a redelivered webhook is how a chargeback gets paid twice.");
        }
    }
}
