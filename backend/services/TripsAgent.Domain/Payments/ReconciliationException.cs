using TripsAgent.Domain.Auditing;
using TripsAgent.Domain.Common;

namespace TripsAgent.Domain.Payments;

/// <summary>Which integrity check found a problem.</summary>
public enum ReconciliationCheck
{
    /// <summary>A transaction group whose debits and credits do not match.</summary>
    UnbalancedTransaction = 1,

    /// <summary>A wallet whose stored balance disagrees with its ledger account.</summary>
    WalletBalanceDrift = 2,

    /// <summary>A ledger entry pointing at an account that does not exist.</summary>
    OrphanedLedgerEntry = 3,

    /// <summary>A hold still held after its deadline.</summary>
    ExpiredHoldOutstanding = 4,

    /// <summary>
    /// A payment the gateway confirmed that never reached the wallet: posting failed, or it is
    /// held for review. Somebody was charged and has not been credited.
    /// </summary>
    PaymentNotPosted = 5,

    /// <summary>
    /// The gateway settled a transaction we have no record of. Money arrived that our books
    /// cannot explain.
    /// </summary>
    GatewayTransactionUnknown = 6,

    /// <summary>The gateway and our books disagree about what a payment was worth.</summary>
    GatewayAmountMismatch = 7,

    /// <summary>
    /// We recorded a payment as succeeded and the gateway never settled it in the window it
    /// should have.
    /// </summary>
    GatewayPaymentUnsettled = 8,

    /// <summary>
    /// A settlement's own arithmetic does not hold: gross less fees is not the net it paid, or
    /// its constituent transactions do not add up to its total.
    /// </summary>
    GatewaySettlementUnbalanced = 9,

    /// <summary>
    /// A chargeback landed on an agency that could not cover it. Whatever the bank decides, the
    /// platform is exposed for the amount.
    /// </summary>
    DisputeUncovered = 10,

    /// <summary>
    /// A payout was sent and the gateway will not say what became of it. Somebody has to look in
    /// the gateway's dashboard, because nothing here may send it again.
    /// </summary>
    PayoutOutcomeUnknown = 11,
}

/// <summary>How much attention a discrepancy needs.</summary>
public enum ReconciliationSeverity
{
    /// <summary>
    /// The books are wrong. Wake someone up.
    /// </summary>
    /// <remarks>
    /// Reserved for the checks that mean money is misstated: the books disagree with themselves,
    /// or a payer was charged and not credited. None of these should ever fire, so a P1 here means
    /// something bypassed the controls meant to prevent it.
    /// </remarks>
    P1 = 1,

    /// <summary>
    /// Something is behind or stuck, but no figure is wrong. Look at it in the morning.
    /// </summary>
    P2 = 2,
}

public enum ReconciliationStatus
{
    /// <summary>Found and not yet dealt with.</summary>
    Open = 1,

    /// <summary>Someone has seen it and is working on it.</summary>
    Acknowledged = 2,

    /// <summary>Dealt with, with a note saying how.</summary>
    Resolved = 3,

    /// <summary>
    /// Accepted as a loss and closed, with a stated reason. Not the same as resolved: nothing was
    /// put right, somebody decided it was not worth putting right, and that decision is a fact
    /// about the books that a reader deserves to see.
    /// </summary>
    WrittenOff = 4,
}

/// <summary>Which checks mean a number is wrong, and which mean work is merely behind.</summary>
public static class ReconciliationCheckExtensions
{
    /// <summary>
    /// How loudly a check should complain.
    /// </summary>
    /// <remarks>
    /// All but one mean a figure is wrong, which is as bad as it gets here. A payment that was
    /// charged and never credited counts: the agency's balance is short by money it paid. An expired hold
    /// is different in kind: the money is still accounted for, a release job is simply behind.
    /// Paging someone at 03:00 for that would teach them that a P1 from this job is usually
    /// nothing — which is the one thing that must never be true of it.
    /// </remarks>
    public static ReconciliationSeverity Severity(this ReconciliationCheck check) => check switch
    {
        ReconciliationCheck.UnbalancedTransaction => ReconciliationSeverity.P1,
        ReconciliationCheck.WalletBalanceDrift => ReconciliationSeverity.P1,
        ReconciliationCheck.OrphanedLedgerEntry => ReconciliationSeverity.P1,
        ReconciliationCheck.ExpiredHoldOutstanding => ReconciliationSeverity.P2,
        ReconciliationCheck.PaymentNotPosted => ReconciliationSeverity.P1,

        // The gateway's money and ours disagreeing is a wrong figure by definition, and an
        // uncovered chargeback is money the platform is on the hook for. Both are P1.
        ReconciliationCheck.GatewayTransactionUnknown => ReconciliationSeverity.P1,
        ReconciliationCheck.GatewayAmountMismatch => ReconciliationSeverity.P1,
        ReconciliationCheck.GatewaySettlementUnbalanced => ReconciliationSeverity.P1,
        ReconciliationCheck.DisputeUncovered => ReconciliationSeverity.P1,

        // A payment the gateway has not settled yet is usually the settlement lag, not a wrong
        // figure — the reconciler already skips the days still in flight, so one that reaches here
        // is late rather than missing. A payout with no answer is the same shape: the money is
        // accounted for on both sides, and what is outstanding is somebody looking it up.
        ReconciliationCheck.GatewayPaymentUnsettled => ReconciliationSeverity.P2,
        ReconciliationCheck.PayoutOutcomeUnknown => ReconciliationSeverity.P2,

        _ => throw new ArgumentOutOfRangeException(nameof(check), check, "Unknown reconciliation check."),
    };
}

/// <summary>
/// A discrepancy the nightly audit found in the books.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately <b>not</b> <see cref="ITenantScoped"/>, which is worth explaining because almost
/// every other table here is. Two reasons. An unbalanced transaction group spans accounts that
/// may belong to different agencies and to the platform, so there is no single agency it belongs
/// to. And this table is read by platform admins investigating the platform's own books — an
/// agency must never see it, so a tenant filter would be the wrong shape of protection. Access is
/// controlled by permission instead, and <see cref="AgencyId"/> is a hint for whoever is looking,
/// not a partition key.
/// </para>
/// <para>
/// Rows are kept after resolution. "This happened once in March and here is what we did" is the
/// entire value of the table.
/// </para>
/// </remarks>
public sealed class ReconciliationException : Entity, IAuditableEntity, IAuditLogged
{
    private ReconciliationException()
    {
        Subject = string.Empty;
        Detail = string.Empty;
    }

    /// <summary>Records a discrepancy.</summary>
    /// <param name="check">Which check found it.</param>
    /// <param name="subject">
    /// What it is about — a transaction group id, a wallet id, an entry id. Together with
    /// <paramref name="check"/> this identifies the problem, so re-running the audit recognises
    /// an exception it has already raised instead of raising it again every night.
    /// </param>
    /// <param name="detail">Plain English, for whoever is woken up by it.</param>
    /// <param name="expectedMinor">What the figure should have been.</param>
    /// <param name="actualMinor">What it was.</param>
    /// <param name="agencyId">Whose it is, where that is meaningful.</param>
    /// <param name="detectedAt">When the audit ran.</param>
    /// <param name="reconciliationRunId">The run that found it, where one reconciler owns it.</param>
    public static ReconciliationException Record(
        ReconciliationCheck check,
        string subject,
        string detail,
        Money expectedMinor,
        Money actualMinor,
        Guid? agencyId,
        DateTimeOffset detectedAt,
        Guid? reconciliationRunId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);

        return new ReconciliationException
        {
            ReconciliationRunId = reconciliationRunId,
            Check = check,
            Severity = check.Severity(),
            Subject = subject,
            Detail = detail,
            ExpectedMinor = expectedMinor,
            ActualMinor = actualMinor,
            AgencyId = agencyId,
            DetectedAt = detectedAt,
            Status = ReconciliationStatus.Open,
        };
    }

    public ReconciliationCheck Check { get; private set; }

    public ReconciliationSeverity Severity { get; private set; }

    /// <summary>What the discrepancy is about. Unique per check while the exception is open.</summary>
    public string Subject { get; private set; }

    public string Detail { get; private set; }

    public Money ExpectedMinor { get; private set; }

    public Money ActualMinor { get; private set; }

    /// <summary>How far out it is. Stored rather than derived, so a report can sort by it.</summary>
    public Money DifferenceMinor => ActualMinor - ExpectedMinor;

    /// <summary>A hint about whose it is. Not a partition key — see the class remarks.</summary>
    public Guid? AgencyId { get; private set; }

    /// <summary>
    /// The run that raised it, for the reconcilers that have runs. Null for the nightly ledger
    /// audit, which is a check rather than a windowed run.
    /// </summary>
    public Guid? ReconciliationRunId { get; private set; }

    /// <summary>Who closed it, for the two closing states that need a person behind them.</summary>
    public Guid? ResolvedByUserId { get; private set; }

    public DateTimeOffset DetectedAt { get; private set; }

    /// <summary>How many nightly runs have seen it. Rising every night means nobody is looking.</summary>
    public int TimesSeen { get; private set; } = 1;

    public DateTimeOffset? LastSeenAt { get; private set; }

    public ReconciliationStatus Status { get; private set; }

    public DateTimeOffset? ResolvedAt { get; private set; }

    public string? ResolutionNote { get; private set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>
    /// Records that tonight's run found this same problem again.
    /// </summary>
    /// <remarks>
    /// The figures are refreshed because a drift that is growing is a different problem from one
    /// that is static, and the difference matters to whoever is diagnosing it.
    /// </remarks>
    public void SeenAgain(Money expectedMinor, Money actualMinor, DateTimeOffset at)
    {
        TimesSeen++;
        LastSeenAt = at;
        ExpectedMinor = expectedMinor;
        ActualMinor = actualMinor;

        if (Status is ReconciliationStatus.Resolved or ReconciliationStatus.WrittenOff)
        {
            // It was closed and it is back. That is worse than never having been closed.
            Status = ReconciliationStatus.Open;
            ResolvedAt = null;
            ResolutionNote = null;
            ResolvedByUserId = null;
        }
    }

    /// <summary>Records that the run found it again, and which run that was.</summary>
    public void SeenAgain(Money expectedMinor, Money actualMinor, DateTimeOffset at, Guid? reconciliationRunId)
    {
        SeenAgain(expectedMinor, actualMinor, at);

        if (reconciliationRunId is not null)
        {
            ReconciliationRunId = reconciliationRunId;
        }
    }

    public void Acknowledge() => Status = ReconciliationStatus.Acknowledged;

    public void Resolve(string note, DateTimeOffset at, Guid? resolvedByUserId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(note);

        Status = ReconciliationStatus.Resolved;
        ResolutionNote = note;
        ResolvedAt = at;
        ResolvedByUserId = resolvedByUserId;
    }

    /// <summary>
    /// Closes it as a loss rather than as a fix.
    /// </summary>
    /// <remarks>
    /// Kept distinct from <see cref="Resolve"/> because they are different facts. "We found the
    /// missing entry and posted it" and "we gave up on ₦40" both empty the queue, and only one of
    /// them means the books are now right.
    /// </remarks>
    public void WriteOff(string note, DateTimeOffset at, Guid? resolvedByUserId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(note);

        Status = ReconciliationStatus.WrittenOff;
        ResolutionNote = note;
        ResolvedAt = at;
        ResolvedByUserId = resolvedByUserId;
    }
}
