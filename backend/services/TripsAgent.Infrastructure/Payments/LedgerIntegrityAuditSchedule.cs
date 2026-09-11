using Hangfire;
using TripsAgent.Application.Payments;

namespace TripsAgent.Infrastructure.Payments;

/// <summary>
/// Puts the ledger integrity audit on Hangfire's clock.
/// </summary>
/// <remarks>
/// <para>
/// 02:30 UTC, which is 03:30 in Lagos — after the quietest hour and before anyone is working.
/// Half past rather than on the hour so it does not contend with the audit-log maintenance job
/// at 03:00; both read broadly and there is no reason to make them queue behind each other.
/// </para>
/// <para>
/// The run is idempotent. It only reads the ledger and writes exceptions keyed by
/// <c>(check, subject)</c>, so running it twice finds the same things and bumps a counter rather
/// than duplicating rows. That matters because a redeploy re-registers the job and an operator
/// may well trigger it by hand from the dashboard while investigating.
/// </para>
/// <para>
/// Registered by the Worker only — the API can enqueue but must never execute, or every API
/// instance would run this at once.
/// </para>
/// </remarks>
public static class LedgerIntegrityAuditSchedule
{
    /// <summary>The recurring job's id, as it appears in the Hangfire dashboard.</summary>
    public const string JobId = "ledger-integrity-audit";

    public const string CronExpression = "30 2 * * *";

    public static void Register(IRecurringJobManager recurringJobs)
    {
        ArgumentNullException.ThrowIfNull(recurringJobs);

        recurringJobs.AddOrUpdate<ILedgerIntegrityAudit>(
            JobId,

            // Hangfire swaps CancellationToken.None for its own token when the job runs, so a
            // Worker shutting down can still stop it cleanly.
            audit => audit.RunAsync(CancellationToken.None),
            CronExpression,
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });
    }
}
