using Hangfire;
using TripsAgent.Application.Crm;
using TripsAgent.Domain.Auditing;
using TripsAgent.Infrastructure.Auditing;

namespace TripsAgent.Infrastructure.Crm;

/// <summary>
/// The follow-up reminder sweep, as Hangfire runs it (#62). Not retried: the next run is five
/// minutes away, and a task it missed is still due then.
/// </summary>
/// <remarks>
/// No <c>DisableConcurrentExecution</c> is needed for correctness — each reminder is queued under a
/// key of its own task, so a second run that overlaps queues nothing twice.
/// </remarks>
public sealed class CrmTaskReminderJob(TaskReminders reminders, AuditContext audit)
{
    [AutomaticRetry(Attempts = 0, OnAttemptsExceeded = AttemptsExceededAction.Delete)]
    public Task<int> RunAsync(CancellationToken cancellationToken)
    {
        audit.ActorType = AuditActorType.System;
        return reminders.RunAsync(cancellationToken);
    }
}

/// <summary>Puts the follow-up reminder sweep on Hangfire's clock: every five minutes.</summary>
/// <remarks>
/// Five minutes, not one: a reminder that arrives four minutes after a task fell due is no worse to
/// the agent, and the sweep reads every agency's tasks.
/// </remarks>
public static class CrmTaskReminderSchedule
{
    public const string JobId = "crm-task-reminders";

    public const string CronExpression = "*/5 * * * *";

    public static void Register(IRecurringJobManager recurringJobs)
    {
        ArgumentNullException.ThrowIfNull(recurringJobs);

        recurringJobs.AddOrUpdate<CrmTaskReminderJob>(
            JobId,
            job => job.RunAsync(CancellationToken.None),
            CronExpression,
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });
    }
}
