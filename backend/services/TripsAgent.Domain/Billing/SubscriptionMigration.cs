using TripsAgent.Domain.Auditing;
using TripsAgent.Domain.Common;

namespace TripsAgent.Domain.Billing;

/// <summary>
/// A tier change that has been agreed but has not happened yet.
/// </summary>
/// <remarks>
/// <para>
/// Two things use this, and they are the same shape:
/// </para>
/// <list type="bullet">
///   <item>
///     an agency choosing a cheaper plan. Build-plan decision 15 says a downgrade keeps existing
///     usage, blocks new usage and gives thirty days' notice, so the change is scheduled rather
///     than applied — and the row is what tells the agency's plan screen when it lands.
///   </item>
///   <item>
///     an admin moving a tier's existing subscribers (<see cref="TierMigrationPolicy.MigrateExisting"/>).
///     One row per subscriber, all scheduled for the same date, all notified in advance. FRD RS-7.
///   </item>
/// </list>
/// <para>
/// Tenant-scoped, because the thing being changed is one agency's subscription. An admin migrating a
/// whole tier writes many of these, inside a platform scope.
/// </para>
/// </remarks>
public sealed class SubscriptionMigration : Entity, IAuditableEntity, ITenantScoped, IAuditLogged
{
    /// <summary>
    /// How much warning a subscriber gets before a change lands on them. Build-plan decision 15.
    /// </summary>
    public static readonly TimeSpan NoticePeriod = TimeSpan.FromDays(30);

    private SubscriptionMigration() => Reason = string.Empty;

    private SubscriptionMigration(
        Guid agencyId,
        Guid subscriptionId,
        Guid fromTierId,
        Guid toTierId,
        SubscriptionChangeReason changeReason,
        string reason,
        DateTimeOffset scheduledFor)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(agencyId, Guid.Empty);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        AgencyId = agencyId;
        SubscriptionId = subscriptionId;
        FromTierId = fromTierId;
        ToTierId = toTierId;
        ChangeReason = changeReason;
        Reason = reason.Trim();
        ScheduledFor = scheduledFor;
    }

    /// <summary>Schedules a change for <paramref name="scheduledFor"/>.</summary>
    public static SubscriptionMigration Schedule(
        Guid agencyId,
        Guid subscriptionId,
        Guid fromTierId,
        Guid toTierId,
        SubscriptionChangeReason changeReason,
        string reason,
        DateTimeOffset scheduledFor) =>
        new(agencyId, subscriptionId, fromTierId, toTierId, changeReason, reason, scheduledFor);

    public Guid AgencyId { get; private set; }

    public Guid SubscriptionId { get; private set; }

    public Guid FromTierId { get; private set; }

    public Guid ToTierId { get; private set; }

    public SubscriptionChangeReason ChangeReason { get; private set; }

    /// <summary>Why, in words the agency reads on its plan screen.</summary>
    public string Reason { get; private set; }

    /// <summary>When it takes effect. Never earlier than the notice the agency was given.</summary>
    public DateTimeOffset ScheduledFor { get; private set; }

    /// <summary>When the agency was told. Null until the notice goes out.</summary>
    public DateTimeOffset? NotifiedAt { get; private set; }

    /// <summary>When it actually happened. Null until the job applies it.</summary>
    public DateTimeOffset? AppliedAt { get; private set; }

    /// <summary>When it was called off, by the agency changing its mind or by an admin.</summary>
    public DateTimeOffset? CancelledAt { get; private set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>True while it is still going to happen.</summary>
    public bool IsPending => AppliedAt is null && CancelledAt is null;

    /// <summary>True when it is pending and its date has arrived.</summary>
    public bool IsDueAt(DateTimeOffset now) => IsPending && ScheduledFor <= now;

    public void MarkNotified(DateTimeOffset at) => NotifiedAt ??= at;

    public void MarkApplied(DateTimeOffset at) => AppliedAt ??= at;

    public void Cancel(DateTimeOffset at) => CancelledAt ??= at;
}

/// <summary>
/// A change an admin made to a tier, with what it looked like before and after.
/// </summary>
/// <remarks>
/// <para>
/// The general audit interceptor already records every insert and update to
/// <c>billing.subscription_tiers</c>, which is the tamper-evident trail. This table is the
/// <i>product</i> record next to it: it carries the migration policy the admin chose, and when the
/// affected subscribers were told. FRD RS-6 and RS-7 both ask for that, and neither is derivable
/// from a generic before/after diff.
/// </para>
/// <para>
/// Platform-owned: a tier belongs to Trips, so there is no <c>agency_id</c> and no tenant filter.
/// It is reachable only through <c>subscription.manage</c>, which no agency role may hold.
/// </para>
/// </remarks>
public sealed class TierChangeLogEntry : Entity, IAuditableEntity
{
    private TierChangeLogEntry()
    {
        Action = string.Empty;
        Reason = string.Empty;
    }

    private TierChangeLogEntry(
        Guid tierId,
        Guid? actorUserId,
        string action,
        string? before,
        string? after,
        TierMigrationPolicy migrationPolicy,
        string reason,
        DateTimeOffset occurredAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(action);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        TierId = tierId;
        ActorUserId = actorUserId;
        Action = action;
        Before = before;
        After = after;
        MigrationPolicy = migrationPolicy;
        Reason = reason.Trim();
        OccurredAt = occurredAt;
    }

    public static TierChangeLogEntry Record(
        Guid tierId,
        Guid? actorUserId,
        string action,
        string? before,
        string? after,
        TierMigrationPolicy migrationPolicy,
        string reason,
        DateTimeOffset occurredAt) =>
        new(tierId, actorUserId, action, before, after, migrationPolicy, reason, occurredAt);

    public Guid TierId { get; private set; }

    /// <summary>The back-office user who did it. Null for a change a job made.</summary>
    public Guid? ActorUserId { get; private set; }

    /// <summary><c>tier.created</c>, <c>tier.repriced</c>, <c>tier.entitlements_changed</c>, <c>tier.archived</c>.</summary>
    public string Action { get; private set; }

    /// <summary>The tier as it was, as JSON. Null when it did not exist yet.</summary>
    public string? Before { get; private set; }

    /// <summary>The tier as it became, as JSON.</summary>
    public string? After { get; private set; }

    /// <summary>What the admin chose to do about existing subscribers.</summary>
    public TierMigrationPolicy MigrationPolicy { get; private set; }

    /// <summary>Why. Demanded of every admin write, like every other back-office action.</summary>
    public string Reason { get; private set; }

    /// <summary>When the affected subscribers were told. Null when nobody needed telling.</summary>
    public DateTimeOffset? NoticeSentAt { get; private set; }

    /// <summary>How many subscribers were scheduled to move.</summary>
    public int SubscribersAffected { get; private set; }

    public DateTimeOffset OccurredAt { get; private set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public void RecordNotice(DateTimeOffset at, int subscribersAffected)
    {
        NoticeSentAt ??= at;
        SubscribersAffected = subscribersAffected;
    }
}
