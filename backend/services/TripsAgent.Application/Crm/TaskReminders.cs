using Microsoft.EntityFrameworkCore;
using TripsAgent.Application.Notifications;
using TripsAgent.Application.Persistence;
using TripsAgent.Application.Tenancy;
using TripsAgent.Domain.Crm;

namespace TripsAgent.Application.Crm;

/// <summary>
/// Reminds the agency about follow-up tasks that have fallen due (#62).
/// </summary>
/// <remarks>
/// <para>
/// A task nobody is reminded of is a customer nobody called back, which is the whole reason the CRM
/// has tasks. Once a task's time has passed, its owner gets one email — <see cref="FollowUpTask.ReminderSentAt"/>
/// records it, so a run every few minutes never sends a second.
/// </para>
/// <para>
/// It reads across agencies, because it is one job for the whole platform rather than one per
/// agency, and says so through <see cref="IPlatformScope"/>. Nothing leaves one agency: each email
/// is queued for the task's own agency and goes to that agency's own person.
/// </para>
/// </remarks>
public sealed class TaskReminders
{
    /// <summary>How many reminders one run sends. A backlog drains over the next few runs.</summary>
    public const int MaxPerRun = 200;

    private readonly IAppDbContext _db;
    private readonly IPlatformScope _platformScope;
    private readonly INotifier _notifier;
    private readonly TimeProvider _clock;

    public TaskReminders(IAppDbContext db, IPlatformScope platformScope, INotifier notifier, TimeProvider clock)
    {
        _db = db;
        _platformScope = platformScope;
        _notifier = notifier;
        _clock = clock;
    }

    /// <summary>What makes the reminder for a task the same reminder, however often the job runs.</summary>
    public static string DedupeKeyFor(Guid taskId) => $"{NotificationTemplateCatalog.CrmTaskDue}:{taskId}";

    /// <summary>Emails the owner of every task now due that has not been reminded.</summary>
    /// <param name="cancellationToken">Cancels the run; what has been saved stays saved.</param>
    /// <returns>How many reminders were queued.</returns>
    public async Task<int> RunAsync(CancellationToken cancellationToken = default)
    {
        using var scope = _platformScope.Enter(
            "CRM follow-up reminders — finds tasks now due across every agency, and emails each one's own owner");

        var now = _clock.GetUtcNow();

        var due = await _db.FollowUpTasks
            .Where(task => task.CompletedAt == null && task.ReminderSentAt == null && task.DueAt <= now)
            .OrderBy(task => task.DueAt)
            .Take(MaxPerRun)
            .ToListAsync(cancellationToken);

        if (due.Count == 0)
        {
            return 0;
        }

        var ownerIds = due.Where(task => task.OwnerUserId.HasValue).Select(task => task.OwnerUserId!.Value).Distinct().ToList();

        var owners = await _db.Users
            .Where(user => ownerIds.Contains(user.Id))
            .Select(user => new { user.Id, user.Email, user.FirstName, user.LastName })
            .ToDictionaryAsync(user => user.Id, cancellationToken);

        var customerIds = due.Select(task => task.CustomerId).Distinct().ToList();

        var customers = await _db.Customers
            .Where(customer => customerIds.Contains(customer.Id))
            .Select(customer => new { customer.Id, customer.Name })
            .ToDictionaryAsync(customer => customer.Id, customer => customer.Name, cancellationToken);

        var agencyIds = due.Select(task => task.AgencyId).Distinct().ToList();

        var zones = await _db.Agencies
            .Where(agency => agencyIds.Contains(agency.Id))
            .Select(agency => new { agency.Id, agency.Timezone })
            .ToDictionaryAsync(agency => agency.Id, agency => agency.Timezone, cancellationToken);

        var queued = 0;

        foreach (var task in due)
        {
            // A task nobody owns — one raised by an integration, or whose owner has left — has nobody
            // to remind. Marked anyway, so it is not looked at again on every run forever.
            if (task.OwnerUserId is { } ownerId
                && owners.TryGetValue(ownerId, out var owner)
                && !string.IsNullOrWhiteSpace(owner.Email))
            {
                var sent = await _notifier.QueueEmailAsync(
                    new EmailNotificationRequest(
                        task.AgencyId,
                        NotificationTemplateCatalog.CrmTaskDue,
                        owner.Email,
                        CrmContext.PersonName(owner.FirstName, owner.LastName) ?? CrmContext.TeamName,
                        new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["taskTitle"] = task.Title,
                            ["customerName"] = customers.GetValueOrDefault(task.CustomerId, "your customer"),
                            ["dueAt"] = CrmFormat.Moment(task.DueAt, zones.GetValueOrDefault(task.AgencyId, "UTC")),
                        },
                        DedupeKeyFor(task.Id),
                        ownerId),
                    cancellationToken);

                if (sent)
                {
                    queued++;
                }
            }

            task.MarkReminded(now);
        }

        await _db.SaveChangesAsync(cancellationToken);

        return queued;
    }
}
