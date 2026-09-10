using TripsAgent.Domain.Auditing;

namespace TripsAgent.Application.Auditing;

/// <summary>
/// Who is acting right now, for the audit log to attribute a change to.
///
/// Scoped to one request or one job run. The API populates it from the authenticated principal;
/// a background worker leaves the actor null and reports <see cref="AuditActorType.System"/>.
///
/// This is deliberately the smallest thing the audit log needs and nothing more. Tenancy proper
/// — the ambient <c>agency_id</c> that every business query filters on — arrives with #11, and
/// <see cref="AgencyId"/> here is expected to be served from it once it does.
/// </summary>
public interface IAuditContext
{
    /// <summary>The acting user, or null for a background job or an unauthenticated caller.</summary>
    public Guid? ActorUserId { get; }

    /// <summary>In what capacity the actor is acting.</summary>
    public AuditActorType ActorType { get; }

    /// <summary>The caller's IP address, when there is a request behind this.</summary>
    public string? ActorIpAddress { get; }

    /// <summary>The agency this action belongs to, or null for a platform-wide action.</summary>
    public Guid? AgencyId { get; }

    /// <summary>Ties every row written by one request or job together.</summary>
    public string? CorrelationId { get; }

    /// <summary>
    /// The justification for what is about to happen, or null if none was given.
    /// Set it immediately before saving, and only for actions that require a reason.
    /// </summary>
    public string? Reason { get; }

    /// <summary>
    /// Records why the change about to be saved is being made.
    ///
    /// Applies to everything saved in the same <c>SaveChanges</c> call, so set it as close to
    /// that call as you can — a reason left over from earlier in the request would be attached
    /// to a change it does not describe, which is worse than no reason at all.
    /// </summary>
    /// <param name="reason">The actor's justification, or null to clear it.</param>
    public void SetReason(string? reason);
}
