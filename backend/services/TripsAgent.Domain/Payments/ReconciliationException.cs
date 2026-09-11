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
}

/// <summary>How much attention a discrepancy needs.</summary>
public enum ReconciliationSeverity
{
    /// <summary>
    /// The books are wrong. Wake someone up.
    /// </summary>
    /// <remarks>
    /// Reserved for the three checks that mean money is misstated. These should never fire: the
    /// balance trigger refuses an unbalanced commit and the entries table is append-only, so a
    /// P1 here means something bypassed both.
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
}

/// <summary>Which checks mean a number is wrong, and which mean work is merely behind.</summary>
public static class ReconciliationCheckExtensions
{
    /// <summary>
    /// How loudly a check should complain.
    /// </summary>
    /// <remarks>
    /// The first three mean a figure is wrong, which is as bad as it gets here. An expired hold
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
    public static ReconciliationException Record(
        ReconciliationCheck check,
        string subject,
        string detail,
        Money expectedMinor,
        Money actualMinor,
        Guid? agencyId,
        DateTimeOffset detectedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);

        return new ReconciliationException
        {
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

        if (Status == ReconciliationStatus.Resolved)
        {
            // It was declared fixed and it is back. That is worse than never having been closed.
            Status = ReconciliationStatus.Open;
            ResolvedAt = null;
            ResolutionNote = null;
        }
    }

    public void Acknowledge() => Status = ReconciliationStatus.Acknowledged;

    public void Resolve(string note, DateTimeOffset at)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(note);

        Status = ReconciliationStatus.Resolved;
        ResolutionNote = note;
        ResolvedAt = at;
    }
}
