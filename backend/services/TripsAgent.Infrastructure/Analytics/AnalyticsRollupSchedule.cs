using Hangfire;
using TripsAgent.Application.Analytics;

namespace TripsAgent.Infrastructure.Analytics;

/// <summary>
/// Puts the analytics rollup on Hangfire's clock — twice, on two different cadences.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every five minutes, incrementally.</b> The FRD asks for metrics no more than ten minutes
/// stale, and plan §3 job 23 sets the cadence at 5–10 minutes. Five is half the budget, so a
/// screen is always inside it even when a run takes a few minutes. A run with nothing to do reads
/// three small indexed ranges and stops, which is the common case and costs almost nothing.
/// </para>
/// <para>
/// <b>Every night, in full.</b> An incremental run is only ever as right as its watermark, and a
/// watermark is state that can be wrong: a clock skewed between two servers, a transaction that
/// committed rows carrying older timestamps, a bug in the change detection. The nightly run reads
/// no watermark and rebuilds the window from source, so a day that drifted is corrected before
/// anybody reconciles against it. It is also the criterion issue 67 cares about most — rebuilding
/// from source must reproduce identical numbers — running in production, every night, rather than
/// only in a test.
/// </para>
/// <para>
/// 01:30 UTC is 02:30 in Lagos: after the quietest hour, and clear of the ledger integrity audit at
/// 02:30 UTC and the audit-log maintenance at 03:00, both of which read broadly.
/// </para>
/// <para>
/// Registered by the Worker only. The API may enqueue but must never execute, or every API instance
/// would run this at once.
/// </para>
/// </remarks>
public static class AnalyticsRollupSchedule
{
    /// <summary>The incremental job's id, as it appears in the Hangfire dashboard.</summary>
    public const string IncrementalJobId = "analytics-rollup-incremental";

    /// <summary>The nightly rebuild's id.</summary>
    public const string FullRebuildJobId = "analytics-rollup-full-rebuild";

    /// <summary>Every five minutes. Half the FRD's ten-minute staleness budget.</summary>
    public const string IncrementalCronExpression = "*/5 * * * *";

    /// <summary>01:30 UTC — 02:30 in Lagos.</summary>
    public const string FullRebuildCronExpression = "30 1 * * *";

    public static void Register(IRecurringJobManager recurringJobs)
    {
        ArgumentNullException.ThrowIfNull(recurringJobs);

        recurringJobs.AddOrUpdate<IAnalyticsRollup>(
            IncrementalJobId,

            // Hangfire swaps CancellationToken.None for its own token when the job runs, so a
            // Worker shutting down can stop a rebuild cleanly between days.
            rollup => rollup.RunIncrementalAsync(CancellationToken.None),
            IncrementalCronExpression,
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

        recurringJobs.AddOrUpdate<IAnalyticsRollup>(
            FullRebuildJobId,
            rollup => rollup.RunFullRebuildAsync(CancellationToken.None),
            FullRebuildCronExpression,
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });
    }
}
