using TripsAgent.Domain.Common;

namespace TripsAgent.Domain.Auditing;

/// <summary>
/// One immutable record of who did what, when, and what changed.
///
/// FRD §2.15 RS-5 requires actor, timestamp and before/after state for admin actions, and the
/// same record answers every later "how did this happen" about money and identity. Rows are
/// never updated and never deleted individually — the database refuses both. They age out only
/// when a whole monthly partition is dropped past its retention window.
///
/// Nothing here is nullable by accident:
/// <list type="bullet">
///   <item><see cref="ActorUserId"/> is null when a background job or an anonymous visitor acted.</item>
///   <item><see cref="AgencyId"/> is null for platform-wide actions that belong to no one agency.</item>
///   <item><see cref="BeforeState"/> is null on an insert, <see cref="AfterState"/> null on a delete.</item>
/// </list>
///
/// The inherited <see cref="Entity.Id"/> is not unique on its own: the primary key is
/// (<c>id</c>, <c>occurred_at</c>), because PostgreSQL requires the partition key in every unique
/// constraint. Ids are UUIDv7 and already time-ordered, so this costs nothing at insert.
/// </summary>
public sealed class AuditLogEntry : Entity
{
    /// <summary>
    /// When the action happened. Also the partition key, so it is set once and never updated.
    /// Required rather than defaulted: the time comes from the injected clock, never from
    /// <c>DateTimeOffset.UtcNow</c>, so a test can pin it.
    /// </summary>
    public required DateTimeOffset OccurredAt { get; init; }

    /// <summary>The agency the action belongs to, or null for a platform-wide action.</summary>
    public Guid? AgencyId { get; init; }

    /// <summary>The user who acted, or null when <see cref="ActorType"/> is System or Anonymous.</summary>
    public Guid? ActorUserId { get; init; }

    /// <summary>Whether a person acted, and in what capacity.</summary>
    public AuditActorType ActorType { get; init; }

    /// <summary>
    /// Where the request came from. Kept for the security questions — "was this really them?" —
    /// and stored as text so IPv6 and a proxied chain both fit.
    /// </summary>
    public string? ActorIpAddress { get; init; }

    /// <summary>What happened: <c>created</c>, <c>updated</c>, or a business action name.</summary>
    public required string Action { get; init; }

    /// <summary>The CLR type name of the thing acted on, e.g. <c>Agency</c>.</summary>
    public required string EntityType { get; init; }

    /// <summary>The key of the thing acted on, as text so any key type fits.</summary>
    public required string EntityId { get; init; }

    /// <summary>The entity's recorded columns before the change. Null on an insert.</summary>
    public string? BeforeState { get; init; }

    /// <summary>The entity's recorded columns after the change. Null on a delete.</summary>
    public string? AfterState { get; init; }

    /// <summary>
    /// Why, in the actor's words. Mandatory for the admin actions that demand a justification —
    /// suspending an agency, adjusting a wallet — and null for routine changes.
    /// </summary>
    public string? Reason { get; init; }

    /// <summary>
    /// Ties this row to the request or job that caused it, so one action spanning several
    /// entities can be reassembled from the logs.
    /// </summary>
    public string? CorrelationId { get; init; }
}
