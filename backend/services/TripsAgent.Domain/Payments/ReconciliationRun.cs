using TripsAgent.Domain.Auditing;
using TripsAgent.Domain.Common;

namespace TripsAgent.Domain.Payments;

/// <summary>Which reconciler produced a run.</summary>
public enum ReconciliationRunType
{
    /// <summary>A day of the gateway's settlements, matched against the ledger.</summary>
    GatewaySettlement = 1,
}

/// <summary>How a run ended.</summary>
public enum ReconciliationRunStatus
{
    /// <summary>Started and not yet finished. A row stuck here is a run that died.</summary>
    Running = 1,

    /// <summary>Finished. Whether it found anything is in the counts.</summary>
    Completed = 2,

    /// <summary>Stopped on an error. Deliberately a row, not an absence.</summary>
    Failed = 3,
}

/// <summary>
/// One day's reconciliation of the gateway against our books.
/// </summary>
/// <remarks>
/// <para>
/// <b>A run that did not happen must look different from a run that found nothing.</b> That is
/// most of why this table exists: the counts of a clean run and the silence of a job that stopped
/// being scheduled are the same thing to anybody reading a list of exceptions, and the second one
/// is the dangerous one. So a run is written when it starts, and finished — or failed — in place.
/// </para>
/// <para>
/// <see cref="BusinessDate"/> is a <i>Lagos</i> day, not a UTC one. A settlement that lands at
/// 00:30 West Africa Time belongs to the day the people looking at it call today, and reconciling
/// on UTC days would split every Nigerian evening across two runs.
/// </para>
/// <para>
/// Platform-wide, like <see cref="ReconciliationException"/>: a settlement that matches nothing is
/// attributable to no agency, which is exactly what makes it worth looking at. Reached only
/// through <c>IPlatformScope</c>.
/// </para>
/// </remarks>
public sealed class ReconciliationRun : Entity, IAuditableEntity, IAuditLogged
{
    private ReconciliationRun()
    {
        Gateway = string.Empty;
        Currency = string.Empty;
    }

    /// <summary>Opens a run. Written before any work is done.</summary>
    /// <param name="type">Which reconciler this is.</param>
    /// <param name="gateway">Whose settlements, by name — one row per gateway per day.</param>
    /// <param name="businessDate">The Lagos day being reconciled.</param>
    /// <param name="windowStart">The first instant of that day, in UTC.</param>
    /// <param name="windowEnd">The first instant of the next day, in UTC. Exclusive.</param>
    /// <param name="currency">ISO 4217. One currency per run.</param>
    /// <param name="startedAt">When the run began.</param>
    public static ReconciliationRun Start(
        ReconciliationRunType type,
        string gateway,
        DateOnly businessDate,
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd,
        string currency,
        DateTimeOffset startedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gateway);

        return new ReconciliationRun
        {
            Type = type,
            Gateway = gateway,
            BusinessDate = businessDate,
            WindowStart = windowStart,
            WindowEnd = windowEnd,
            Currency = (currency ?? string.Empty).Trim().ToUpperInvariant(),
            StartedAt = startedAt,
            Status = ReconciliationRunStatus.Running,
        };
    }

    public ReconciliationRunType Type { get; private set; }

    public string Gateway { get; private set; }

    /// <summary>The Lagos day this run covers. Unique with the gateway and the type.</summary>
    public DateOnly BusinessDate { get; private set; }

    public DateTimeOffset WindowStart { get; private set; }

    /// <summary>Exclusive: the first instant of the following day.</summary>
    public DateTimeOffset WindowEnd { get; private set; }

    public string Currency { get; private set; }

    public ReconciliationRunStatus Status { get; private set; }

    public DateTimeOffset StartedAt { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    /// <summary>How many gateway records were looked at.</summary>
    public int RecordsExamined { get; private set; }

    /// <summary>How many matched a payment of ours, to the kobo.</summary>
    public int RecordsMatched { get; private set; }

    /// <summary>How many discrepancies this run raised or re-raised.</summary>
    public int ExceptionsRaised { get; private set; }

    /// <summary>What the gateway says it settled, gross.</summary>
    public Money GatewayGrossMinor { get; private set; }

    /// <summary>What the gateway charged in fees.</summary>
    public Money GatewayFeesMinor { get; private set; }

    /// <summary>What the gateway actually paid out: gross less fees.</summary>
    public Money GatewayNetMinor { get; private set; }

    /// <summary>What our own books say the matched payments came to, gross.</summary>
    public Money LedgerGrossMinor { get; private set; }

    /// <summary>Why the run stopped, when it stopped badly.</summary>
    public string? FailureReason { get; private set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Gross less fees less what we recorded. Zero is the only acceptable answer.</summary>
    public Money DifferenceMinor => GatewayGrossMinor - LedgerGrossMinor;

    /// <summary>True when the gateway and the ledger agree and nothing was raised.</summary>
    public bool IsClean => Status == ReconciliationRunStatus.Completed
                           && ExceptionsRaised == 0
                           && DifferenceMinor.IsZero;

    /// <summary>Records what the run found and closes it.</summary>
    public void Complete(
        int recordsExamined,
        int recordsMatched,
        int exceptionsRaised,
        Money gatewayGross,
        Money gatewayFees,
        Money gatewayNet,
        Money ledgerGross,
        DateTimeOffset at)
    {
        RecordsExamined = recordsExamined;
        RecordsMatched = recordsMatched;
        ExceptionsRaised = exceptionsRaised;
        GatewayGrossMinor = gatewayGross;
        GatewayFeesMinor = gatewayFees;
        GatewayNetMinor = gatewayNet;
        LedgerGrossMinor = ledgerGross;
        CompletedAt = at;
        FailureReason = null;
        Status = ReconciliationRunStatus.Completed;
    }

    /// <summary>Records that the run stopped badly. The row stays, which is the point.</summary>
    public void Fail(string reason, DateTimeOffset at)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        FailureReason = reason;
        CompletedAt = at;
        Status = ReconciliationRunStatus.Failed;
    }

    /// <summary>Re-opens a finished run so the same day can be reconciled again in place.</summary>
    /// <remarks>
    /// Re-running yesterday is the first thing anybody does when a figure looks wrong, and it must
    /// not leave two rows claiming to be the same day. The counts are overwritten by the new run;
    /// the exceptions it finds are matched on their own subject and bumped rather than duplicated.
    /// </remarks>
    public void Restart(DateTimeOffset at)
    {
        Status = ReconciliationRunStatus.Running;
        StartedAt = at;
        CompletedAt = null;
        FailureReason = null;
        RecordsExamined = 0;
        RecordsMatched = 0;
        ExceptionsRaised = 0;
    }
}
