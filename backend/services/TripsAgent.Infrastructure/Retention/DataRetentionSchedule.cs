using Hangfire;
using TripsAgent.Application.Retention;

namespace TripsAgent.Infrastructure.Retention;

/// <summary>
/// Puts the retention purge on Hangfire's clock.
/// </summary>
/// <remarks>
/// <para>
/// 03:10 UTC daily: after the audit log's maintenance at 03:00, and just before the supplier call
/// log's at 03:15 — so the dry-run row for <c>supplier_api_calls</c> reports what that job is about to
/// drop, rather than finding it already gone.
/// </para>
/// <para>
/// Safe to run twice, and to trigger by hand from the dashboard: see <see cref="DataRetentionPurge"/>.
/// Registered by the Worker only — the API can enqueue but must never execute, or every instance would
/// run it at once.
/// </para>
/// </remarks>
public static class DataRetentionSchedule
{
    /// <summary>The recurring job's id, as it appears in the Hangfire dashboard.</summary>
    public const string JobId = "data-retention";

    public const string CronExpression = "10 3 * * *";

    public static void Register(IRecurringJobManager recurringJobs)
    {
        ArgumentNullException.ThrowIfNull(recurringJobs);

        recurringJobs.AddOrUpdate<IDataRetentionPurge>(
            JobId,

            // Hangfire swaps CancellationToken.None for its own token when the job runs.
            purge => purge.RunAsync(CancellationToken.None),
            CronExpression,
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });
    }
}
