using TripsAgent.Domain.Analytics;

namespace TripsAgent.Application.Analytics;

/// <summary>What one rollup run rebuilt.</summary>
/// <param name="Kind">Incremental or a full rebuild.</param>
/// <param name="DaysRebuilt">How many Lagos days were thrown away and made again from source.</param>
/// <param name="FactRows">How many fact rows were written.</param>
/// <param name="FromDay">The earliest day rebuilt, or null when there was nothing to do.</param>
/// <param name="ToDay">The latest day rebuilt.</param>
/// <param name="Duration">How long it took, so the 5–10 minute cadence can be watched.</param>
public sealed record RollupResult(
    RollupKind Kind,
    int DaysRebuilt,
    int FactRows,
    DateOnly? FromDay,
    DateOnly? ToDay,
    TimeSpan Duration)
{
    /// <summary>True when the run found no changed source rows. The common case.</summary>
    public bool FoundNothingToDo => DaysRebuilt == 0;
}

/// <summary>
/// Builds the analytics read models from the tables they are derived from.
/// </summary>
/// <remarks>
/// <para>
/// Two entry points on the same machinery, which is the whole design. The incremental run rebuilds
/// only the days whose source rows changed since the last successful run; the nightly run rebuilds
/// every day in the window regardless. Both end up calling <see cref="RebuildRangeAsync"/>, so the
/// numbers a fast run leaves behind and the numbers a full rebuild leaves behind are produced by
/// the same code reading the same rows — which is what issue 67's hardest criterion asks for.
/// </para>
/// <para>
/// A day is rebuilt by deleting it and building it again, never by adding to it. Nothing here
/// increments a counter: there is no arithmetic that a second run could apply twice.
/// </para>
/// </remarks>
public interface IAnalyticsRollup
{
    /// <summary>
    /// Rebuilds the days whose source rows changed since the last successful run.
    /// </summary>
    /// <remarks>
    /// Scheduled every five minutes. The FRD asks for metrics no more than ten minutes stale, and
    /// half the budget leaves room for a run that takes a while.
    /// </remarks>
    public Task<RollupResult> RunIncrementalAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Rebuilds every day in the rebuild window from source, whatever the watermark says.
    /// </summary>
    /// <remarks>
    /// The backstop. An incremental run can only ever be as right as its watermark, and a watermark
    /// is a piece of state that can be wrong — a clock skewed between two application servers, a
    /// long transaction that committed rows with old timestamps, a bug in the change detection
    /// itself. The nightly rebuild reads no watermark at all, so a day that drifted is corrected
    /// before anyone reconciles against it in the morning.
    /// </remarks>
    public Task<RollupResult> RunFullRebuildAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Rebuilds an explicit range of Lagos days, inclusive, from source.
    /// </summary>
    /// <remarks>
    /// The operation both scheduled runs are made of, and the one a test drives directly to prove
    /// that rebuilding reproduces identical numbers.
    /// </remarks>
    public Task<RollupResult> RebuildRangeAsync(
        DateOnly fromDay,
        DateOnly toDay,
        RollupKind kind,
        CancellationToken cancellationToken = default);
}
