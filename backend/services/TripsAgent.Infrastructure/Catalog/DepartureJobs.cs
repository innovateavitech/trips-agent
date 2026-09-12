using Hangfire;
using TripsAgent.Application.Catalog;
using TripsAgent.Domain.Auditing;
using TripsAgent.Infrastructure.Auditing;

namespace TripsAgent.Infrastructure.Catalog;

/// <summary>
/// Plan §3 job 6, the departures half: gives back the seats of a checkout that timed out.
/// </summary>
/// <remarks>
/// Not retried: the next run is a minute away, and each hold is released idempotently, so a run that
/// stops halfway leaves nothing half done.
/// </remarks>
public sealed class DepartureHoldExpiryJob(DepartureHoldExpiry holds, AuditContext audit)
{
    [AutomaticRetry(Attempts = 0, OnAttemptsExceeded = AttemptsExceededAction.Delete)]
    public Task<DepartureHoldExpiryRun> RunAsync(CancellationToken cancellationToken)
    {
        audit.ActorType = AuditActorType.System;
        return holds.RunAsync(cancellationToken);
    }
}

/// <summary>Puts the seat hold expiry on Hangfire's clock: every minute, like the cart expiry it belongs with.</summary>
public static class DepartureHoldExpirySchedule
{
    public const string JobId = "departure-hold-expiry";

    public const string CronExpression = "* * * * *";

    public static void Register(IRecurringJobManager recurringJobs)
    {
        ArgumentNullException.ThrowIfNull(recurringJobs);

        recurringJobs.AddOrUpdate<DepartureHoldExpiryJob>(
            JobId,
            job => job.RunAsync(CancellationToken.None),
            CronExpression,
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });
    }
}

/// <summary>Plan §3 job 10: expires unanswered seat offers and rolls them on to the next person.</summary>
public sealed class WaitlistOfferExpiryJob(DepartureWaitlistService waitlist, AuditContext audit)
{
    [AutomaticRetry(Attempts = 0, OnAttemptsExceeded = AttemptsExceededAction.Delete)]
    public Task<WaitlistSweepRun> RunAsync(CancellationToken cancellationToken)
    {
        audit.ActorType = AuditActorType.System;
        return waitlist.SweepAsync(cancellationToken);
    }
}

/// <summary>Puts the waitlist sweep on Hangfire's clock: every five minutes, as plan §3 job 10 says.</summary>
public static class WaitlistOfferExpirySchedule
{
    public const string JobId = "waitlist-offer-expiry";

    public const string CronExpression = "*/5 * * * *";

    public static void Register(IRecurringJobManager recurringJobs)
    {
        ArgumentNullException.ThrowIfNull(recurringJobs);

        recurringJobs.AddOrUpdate<WaitlistOfferExpiryJob>(
            JobId,
            job => job.RunAsync(CancellationToken.None),
            CronExpression,
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });
    }
}

/// <summary>
/// Plan §3 job 9's nightly half: the backstop for a status that was never brought in line with its
/// seats. The seat moves themselves do it as it is earned.
/// </summary>
public sealed class DepartureStatusSweepJob(DepartureStatusSweep sweep, AuditContext audit)
{
    [AutomaticRetry(Attempts = 0, OnAttemptsExceeded = AttemptsExceededAction.Delete)]
    public Task<DepartureStatusSweepRun> RunAsync(CancellationToken cancellationToken)
    {
        audit.ActorType = AuditActorType.System;
        return sweep.RunAsync(cancellationToken);
    }
}

/// <summary>Puts the status sweep on Hangfire's clock: once a night, before the reminders go out.</summary>
public static class DepartureStatusSweepSchedule
{
    public const string JobId = "departure-status-sweep";

    /// <summary>02:10 UTC — 03:10 in Lagos, well away from the evening's bookings.</summary>
    public const string CronExpression = "10 2 * * *";

    public static void Register(IRecurringJobManager recurringJobs)
    {
        ArgumentNullException.ThrowIfNull(recurringJobs);

        recurringJobs.AddOrUpdate<DepartureStatusSweepJob>(
            JobId,
            job => job.RunAsync(CancellationToken.None),
            CronExpression,
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });
    }
}

/// <summary>
/// Plan §3 job 11: reminds travellers what they owe on a group departure, and tells the agency when
/// nobody has paid. It never charges anything — job 12 waits until after the MVP.
/// </summary>
public sealed class InstallmentReminderJob(InstallmentReminders reminders, AuditContext audit)
{
    [AutomaticRetry(Attempts = 0, OnAttemptsExceeded = AttemptsExceededAction.Delete)]
    public Task<InstallmentReminderRun> RunAsync(CancellationToken cancellationToken)
    {
        audit.ActorType = AuditActorType.System;
        return reminders.RunAsync(cancellationToken);
    }
}

/// <summary>Puts the installment reminders on Hangfire's clock: once a day.</summary>
public static class InstallmentReminderSchedule
{
    public const string JobId = "installment-reminders";

    /// <summary>06:00 UTC — 07:00 in Lagos, so a reminder arrives at the start of the day.</summary>
    public const string CronExpression = "0 6 * * *";

    public static void Register(IRecurringJobManager recurringJobs)
    {
        ArgumentNullException.ThrowIfNull(recurringJobs);

        recurringJobs.AddOrUpdate<InstallmentReminderJob>(
            JobId,
            job => job.RunAsync(CancellationToken.None),
            CronExpression,
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });
    }
}
