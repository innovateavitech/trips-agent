using Hangfire;
using TripsAgent.Application.Billing;

namespace TripsAgent.Infrastructure.Billing;

/// <summary>
/// Puts the subscription billing run on Hangfire's clock.
/// </summary>
/// <remarks>
/// <para>
/// 04:00 UTC daily — 05:00 in Lagos. After the night's maintenance jobs, and early enough that a
/// failed charge is emailed to an agency before its working day rather than during it.
/// </para>
/// <para>
/// Daily rather than hourly on purpose. Every date this job works from — a renewal date, a dunning
/// retry, a scheduled migration — is a date rather than a time, so running it more often would only
/// mean charging cards at odd hours for no benefit. Safe to run twice and safe to trigger by hand
/// from the dashboard: see <see cref="ISubscriptionBillingRun"/>.
/// </para>
/// <para>
/// Registered by the Worker only — the API can enqueue but must never execute, or every instance
/// would run it at once and four instances would make four attempts at the same card.
/// </para>
/// </remarks>
public static class SubscriptionBillingSchedule
{
    /// <summary>The recurring job's id, as it appears in the Hangfire dashboard.</summary>
    public const string JobId = "subscription-billing";

    public const string CronExpression = "0 4 * * *";

    public static void Register(IRecurringJobManager recurringJobs)
    {
        ArgumentNullException.ThrowIfNull(recurringJobs);

        recurringJobs.AddOrUpdate<ISubscriptionBillingRun>(
            JobId,

            // Hangfire swaps CancellationToken.None for its own token when the job runs.
            run => run.RunAsync(CancellationToken.None),
            CronExpression,
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });
    }
}
