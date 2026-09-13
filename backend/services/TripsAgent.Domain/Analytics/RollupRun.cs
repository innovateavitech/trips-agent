using TripsAgent.Domain.Common;

namespace TripsAgent.Domain.Analytics;

/// <summary>
/// <c>analytics.rollup_runs</c> — what the rollup did, and how far it had got when it did it.
/// </summary>
/// <remarks>
/// <para>
/// The incremental rollup needs to know where it stopped last time, and that watermark has to
/// survive a restart. Rather than a one-row settings table nobody can audit, each run records its
/// own window: the next incremental run reads the newest successful run and carries on from its
/// <see cref="WatermarkTo"/>. The same rows double as the answer to "is the rollup keeping up",
/// which is otherwise a question only a log search can answer.
/// </para>
/// <para>
/// <b>The overlap.</b> A run's watermark starts a little before the last one ended
/// (<see cref="WatermarkOverlap"/>). A row written inside a transaction that committed after the
/// previous run read the table carries a timestamp from before that read, so without the overlap
/// it would never be seen again until the nightly rebuild. Rebuilding a day twice costs a few
/// milliseconds and produces the same numbers, so the overlap is free; missing a booking for a day
/// is not.
/// </para>
/// <para>
/// Platform-wide. No agency owns it, no agency may read it.
/// </para>
/// </remarks>
public sealed class RollupRun : Entity
{
    /// <summary>
    /// How far back an incremental run reaches before the last run's watermark. See the remarks.
    /// </summary>
    public static readonly TimeSpan WatermarkOverlap = TimeSpan.FromMinutes(15);

    private RollupRun()
    {
    }

    /// <summary>Opens a run. The window is known up front; the counts are not.</summary>
    public static RollupRun Start(
        RollupKind kind,
        DateTimeOffset watermarkFrom,
        DateTimeOffset watermarkTo,
        DateTimeOffset startedAt) =>
        new()
        {
            Kind = kind,
            Status = RollupRunStatus.Running,
            WatermarkFrom = watermarkFrom,
            WatermarkTo = watermarkTo,
            StartedAt = startedAt,
        };

    public RollupKind Kind { get; private set; }

    public RollupRunStatus Status { get; private set; }

    /// <summary>Source rows changed at or after this instant were considered.</summary>
    public DateTimeOffset WatermarkFrom { get; private set; }

    /// <summary>
    /// Source rows changed before this instant were considered. The next incremental run starts
    /// here, less the overlap.
    /// </summary>
    public DateTimeOffset WatermarkTo { get; private set; }

    public DateTimeOffset StartedAt { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    /// <summary>The earliest Lagos day rebuilt. Null when the run found nothing to do.</summary>
    public DateOnly? FromDay { get; private set; }

    /// <summary>The latest Lagos day rebuilt.</summary>
    public DateOnly? ToDay { get; private set; }

    /// <summary>How many distinct days were rebuilt from source.</summary>
    public int DaysRebuilt { get; private set; }

    /// <summary>How many fact rows were written.</summary>
    public int FactRows { get; private set; }

    public string? ErrorMessage { get; private set; }

    /// <summary>Records what the run rebuilt.</summary>
    public void Succeeded(
        DateOnly? fromDay,
        DateOnly? toDay,
        int daysRebuilt,
        int factRows,
        DateTimeOffset completedAt)
    {
        Status = RollupRunStatus.Succeeded;
        FromDay = fromDay;
        ToDay = toDay;
        DaysRebuilt = daysRebuilt;
        FactRows = factRows;
        CompletedAt = completedAt;
    }

    /// <summary>
    /// Records that the run threw. A failed run never becomes a watermark, so the next run covers
    /// its window again — the whole reason the watermark is read from the newest <i>successful</i>
    /// run rather than the newest run.
    /// </summary>
    public void Failed(string error, DateTimeOffset completedAt)
    {
        Status = RollupRunStatus.Failed;
        ErrorMessage = Truncate(error);
        CompletedAt = completedAt;
    }

    private static string Truncate(string value) =>
        value.Length <= 2000 ? value : value[..2000];
}
