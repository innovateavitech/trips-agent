using System.Globalization;
using TripsAgent.Domain.Common;

namespace TripsAgent.Domain.Crm;

/// <summary>
/// Something someone at the agency has to do by a given time — "call Chiamaka about hotels" — about a
/// lead, a quote or a customer: <c>crm.tasks</c>.
/// </summary>
/// <remarks>
/// <para>
/// Named for what it is rather than <c>Task</c>, which every file in .NET already means something else by.
/// </para>
/// <para>
/// <b>Reminders.</b> A reminder job emails the task's owner as it falls due, once: it stamps
/// <see cref="ReminderSentAt"/>, and a task with that set is never reminded again.
/// </para>
/// <para>
/// The task points at what it is about by type and id, which no foreign key can check, so the
/// application checks the record exists first. It also keeps the customer, and the lead where there
/// is one, so a customer's or a lead's tasks are one indexed read.
/// </para>
/// </remarks>
public sealed class FollowUpTask : Entity, IAuditableEntity, ITenantScoped
{
    private FollowUpTask()
    {
        Title = string.Empty;
    }

    /// <summary>A new open task. Throws on a title <see cref="Check"/> refuses: the application checks first.</summary>
    /// <param name="customerId">The customer it concerns, however it is related.</param>
    /// <param name="leadId">The lead it concerns: the lead itself, or the lead a quote answers. Null for a customer's own task.</param>
    public static FollowUpTask Create(
        Guid agencyId,
        string title,
        DateTimeOffset dueAt,
        CrmRecordType relatedType,
        Guid relatedId,
        Guid customerId,
        Guid? leadId,
        Guid? ownerUserId)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(agencyId, Guid.Empty);
        ArgumentOutOfRangeException.ThrowIfEqual(relatedId, Guid.Empty);
        ArgumentOutOfRangeException.ThrowIfEqual(customerId, Guid.Empty);

        if (Check(title) is { Count: > 0 } problems)
        {
            throw new ArgumentException(problems[0].Message, nameof(title));
        }

        if (!Enum.IsDefined(relatedType))
        {
            throw new ArgumentOutOfRangeException(nameof(relatedType), relatedType, "Not a CRM record type.");
        }

        return new FollowUpTask
        {
            AgencyId = agencyId,
            Title = title.Trim(),
            DueAt = dueAt.ToUniversalTime(),
            RelatedType = relatedType,
            RelatedId = relatedId,
            CustomerId = customerId,
            LeadId = leadId,
            OwnerUserId = ownerUserId,
        };
    }

    /// <summary>Every reason <paramref name="title"/> cannot be a task. Empty when it can.</summary>
    public static IReadOnlyList<CrmProblem> Check(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return [new("title", "Say what needs doing.")];
        }

        if (title.Trim().Length > CrmLimits.MaxTaskTitleLength)
        {
            return
            [
                new("title", string.Create(
                    CultureInfo.InvariantCulture, $"Keep it to {CrmLimits.MaxTaskTitleLength} characters.")),
            ];
        }

        return [];
    }

    /// <inheritdoc />
    public Guid AgencyId { get; private set; }

    public string Title { get; private set; }

    public DateTimeOffset DueAt { get; private set; }

    /// <summary>When it was done. Null while it is open.</summary>
    public DateTimeOffset? CompletedAt { get; private set; }

    public CrmRecordType RelatedType { get; private set; }

    public Guid RelatedId { get; private set; }

    public Guid CustomerId { get; private set; }

    public Guid? LeadId { get; private set; }

    /// <summary>Who it is for: the person who added it. Their reminder goes to them.</summary>
    public Guid? OwnerUserId { get; private set; }

    /// <summary>When its reminder went out. Null until then; a task is reminded once.</summary>
    public DateTimeOffset? ReminderSentAt { get; private set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public bool IsOpen => CompletedAt is null;

    /// <summary>Marks the task done.</summary>
    /// <exception cref="InvalidOperationException">It is already done: done once is done.</exception>
    public void Complete(DateTimeOffset now)
    {
        if (CompletedAt is not null)
        {
            throw new InvalidOperationException("That task is already done.");
        }

        CompletedAt = now.ToUniversalTime();
    }

    /// <summary>Records that its reminder went out, so it is never sent twice.</summary>
    public void MarkReminded(DateTimeOffset now) => ReminderSentAt ??= now.ToUniversalTime();
}
