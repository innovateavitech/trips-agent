using TripsAgent.Application.Auditing;
using TripsAgent.Domain.Auditing;

namespace TripsAgent.UnitTests.Auditing;

/// <summary>
/// A settable actor, so a test can say who is acting without standing up authentication.
/// The production implementation is populated at the edge; this one is populated by the test.
/// </summary>
internal sealed class StubAuditContext : IAuditContext
{
    public Guid? ActorUserId { get; set; }

    public AuditActorType ActorType { get; set; } = AuditActorType.Unknown;

    public string? ActorIpAddress { get; set; }

    public Guid? AgencyId { get; set; }

    public string? CorrelationId { get; set; }

    public string? Reason { get; private set; }

    public void SetReason(string? reason) => Reason = reason;
}

/// <summary>
/// A clock that does not move, so an assertion can name the exact timestamp it expects.
/// Hand-rolled rather than pulling in a testing package for one frozen instant.
/// </summary>
internal sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}
