using TripsAgent.Application.Auditing;
using TripsAgent.Domain.Auditing;

namespace TripsAgent.Infrastructure.Auditing;

/// <summary>
/// The ambient actor for one request or one job run. Registered scoped, so two requests never
/// see each other's actor.
///
/// Populated at the edge — by authentication middleware in the API, by the job host in the
/// Worker. Until #16 wires JWTs up there is nothing to populate it from, so an unpopulated
/// instance reports <see cref="AuditActorType.Unknown"/> rather than inventing an actor.
/// </summary>
public sealed class AuditContext : IAuditContext
{
    /// <inheritdoc />
    public Guid? ActorUserId { get; set; }

    /// <inheritdoc />
    public AuditActorType ActorType { get; set; } = AuditActorType.Unknown;

    /// <inheritdoc />
    public string? ActorIpAddress { get; set; }

    /// <inheritdoc />
    public Guid? AgencyId { get; set; }

    /// <inheritdoc />
    public string? CorrelationId { get; set; }

    /// <inheritdoc />
    public string? Reason { get; private set; }

    /// <inheritdoc />
    public void SetReason(string? reason) =>
        Reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
}
