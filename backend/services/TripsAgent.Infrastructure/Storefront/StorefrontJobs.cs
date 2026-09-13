using Hangfire;
using TripsAgent.Application.Storefront;

namespace TripsAgent.Infrastructure.Storefront;

/// <summary>
/// The domain verification sweep, as Hangfire runs it: one at a time, never retried.
/// </summary>
/// <remarks>
/// A run that cannot take the lock is dropped rather than queued: the next one is a minute away, and a pile
/// of queued sweeps helps nobody. A wrapper, so the policy can be attributes Application cannot reference.
/// </remarks>
public sealed class DomainVerificationJob
{
    private readonly DomainVerificationSweep _sweep;

    public DomainVerificationJob(DomainVerificationSweep sweep) => _sweep = sweep;

    [DisableConcurrentExecution(timeoutInSeconds: 10)]
    [AutomaticRetry(Attempts = 0, OnAttemptsExceeded = AttemptsExceededAction.Delete)]
    public Task<int> RunAsync(CancellationToken cancellationToken) => _sweep.RunAsync(cancellationToken);
}

/// <summary>Looks for pending custom hostnames' DNS records every minute (plan §3, job 13).</summary>
/// <remarks>
/// Every minute so a hostname is found soon after it becomes due; each hostname's own schedule — five
/// minutes, then fifteen, then hourly — decides how often it is actually looked at.
/// </remarks>
public static class DomainVerificationSchedule
{
    /// <summary>The recurring job's id, as it appears in the Hangfire dashboard.</summary>
    public const string JobId = "storefront-domain-verification";

    public const string CronExpression = "* * * * *";

    public static void Register(IRecurringJobManager recurringJobs)
    {
        ArgumentNullException.ThrowIfNull(recurringJobs);

        recurringJobs.AddOrUpdate<DomainVerificationJob>(
            JobId,

            // Hangfire swaps CancellationToken.None for its own token when the job runs.
            job => job.RunAsync(CancellationToken.None),
            CronExpression,
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });
    }
}

/// <summary>The certificate sweep, as Hangfire runs it: one at a time, never retried.</summary>
public sealed class CertificateJob
{
    private readonly CertificateSweep _sweep;

    public CertificateJob(CertificateSweep sweep) => _sweep = sweep;

    [DisableConcurrentExecution(timeoutInSeconds: 10)]
    [AutomaticRetry(Attempts = 0, OnAttemptsExceeded = AttemptsExceededAction.Delete)]
    public Task<int> RunAsync(CancellationToken cancellationToken) => _sweep.RunAsync(cancellationToken);
}

/// <summary>
/// Issues certificates for newly verified hostnames and renews them 30 days before they expire, every five
/// minutes (plan §3, job 14).
/// </summary>
public static class CertificateSchedule
{
    /// <summary>The recurring job's id, as it appears in the Hangfire dashboard.</summary>
    public const string JobId = "storefront-certificates";

    public const string CronExpression = "*/5 * * * *";

    public static void Register(IRecurringJobManager recurringJobs)
    {
        ArgumentNullException.ThrowIfNull(recurringJobs);

        recurringJobs.AddOrUpdate<CertificateJob>(
            JobId,
            job => job.RunAsync(CancellationToken.None),
            CronExpression,
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });
    }
}
