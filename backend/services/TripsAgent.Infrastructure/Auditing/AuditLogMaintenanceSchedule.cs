using Hangfire;
using TripsAgent.Application.Auditing;

namespace TripsAgent.Infrastructure.Auditing;

/// <summary>
/// Puts audit log maintenance on Hangfire's clock, so the retention policy runs without anyone
/// having to remember a cron entry.
/// </summary>
/// <remarks>
/// <para>
/// Daily rather than monthly, although a month is all the partitions need. The work is idempotent
/// — an existing partition is left alone, and only whole expired months are dropped — so running
/// it more often costs nothing, and a failure is retried tomorrow instead of next month.
/// </para>
/// <para>
/// Called by the Worker only, which is the one process that runs a Hangfire server. The API can
/// schedule jobs but must never execute them, or every API instance would run this at 03:00.
/// </para>
/// </remarks>
public static class AuditLogMaintenanceSchedule
{
    /// <summary>The recurring job's id, as it appears in the Hangfire dashboard.</summary>
    public const string JobId = "audit-log-maintenance";

    /// <summary>03:00 UTC daily — the quietest hour for Nigerian traffic, which runs on UTC+1.</summary>
    public const string CronExpression = "0 3 * * *";

    /// <summary>
    /// Registers the job, or updates it in place if it already exists. Safe on every start: a
    /// redeploy changes the schedule rather than adding a second copy of it.
    /// </summary>
    public static void Register(IRecurringJobManager recurringJobs)
    {
        ArgumentNullException.ThrowIfNull(recurringJobs);

        recurringJobs.AddOrUpdate<IAuditLogMaintenance>(
            JobId,

            // Hangfire swaps CancellationToken.None for its own token when the job runs, so a
            // Worker shutting down can still stop the job cleanly.
            maintenance => maintenance.RunAsync(CancellationToken.None),
            CronExpression,
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });
    }
}
