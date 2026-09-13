using Hangfire;
using TripsAgent.Application.Tenancy.SubAgents;

namespace TripsAgent.Infrastructure.Tenancy;

/// <summary>
/// Puts the sub-agent allowance reset on Hangfire's clock.
/// </summary>
/// <remarks>
/// <para>
/// Every hour, on the hour. Not once a day, because a daily allowance turns over at midnight UTC
/// and a weekly one on Sunday: an hourly pass means the longest an agency waits for its new period
/// is an hour, and the job finds nothing to do on the other twenty-three.
/// </para>
/// <para>
/// The run is idempotent — each allowance carries the instant it is next due, and resetting moves
/// that past now — so running it twice in an hour resets once. That matters because an operator
/// will trigger it by hand from the dashboard while investigating.
/// </para>
/// <para>
/// Registered by the Worker only. The API may enqueue, but must never execute, or every API
/// instance would run this at once.
/// </para>
/// </remarks>
public static class AllowanceResetSchedule
{
    /// <summary>The recurring job's id, as it appears in the Hangfire dashboard.</summary>
    public const string JobId = "sub-agent-allowance-reset";

    public const string CronExpression = "0 * * * *";

    public static void Register(IRecurringJobManager recurringJobs)
    {
        ArgumentNullException.ThrowIfNull(recurringJobs);

        recurringJobs.AddOrUpdate<IAllowanceResetJob>(
            JobId,

            // Hangfire swaps CancellationToken.None for its own when the job runs, so a Worker
            // shutting down can still stop it cleanly.
            job => job.RunAsync(CancellationToken.None),
            CronExpression,
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });
    }
}
