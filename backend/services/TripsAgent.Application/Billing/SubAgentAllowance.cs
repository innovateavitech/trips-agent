using Microsoft.EntityFrameworkCore;
using TripsAgent.Application.Persistence;
using TripsAgent.Application.Tenancy;
using TripsAgent.Application.Tenancy.SubAgents;
using TripsAgent.Domain.Billing;

namespace TripsAgent.Application.Billing;

/// <summary>
/// Whether a principal agency may take on another agency beneath it.
/// </summary>
/// <remarks>
/// <para>
/// The <c>max_sub_agents</c> entitlement, enforced against the agency hierarchy that already
/// exists. The sub-agent network itself is F10 and is not built yet, but the hierarchy is: an
/// agency's sub-agents are the rows whose <c>parent_agency_id</c> is its id, and counting them is
/// the whole of the "how many do they have?" half of the question.
/// </para>
/// <para>
/// It is also the sub-agent network's <see cref="ISubAgentEntitlement"/>: the invitation flow and
/// the network screen ask it once, before anything is created. It exists as its own small class
/// rather than as a method on <see cref="IEntitlements"/> because knowing how to count agencies is
/// a fact about tenancy, and <see cref="IEntitlements"/> would grow a method per feature if every
/// caller's counting lived there.
/// </para>
/// </remarks>
public sealed class SubAgentAllowance : ISubAgentEntitlement
{
    private readonly IAppDbContext _db;
    private readonly IEntitlements _entitlements;
    private readonly IPlatformScope _platformScope;

    public SubAgentAllowance(IAppDbContext db, IEntitlements entitlements, IPlatformScope platformScope)
    {
        _db = db;
        _entitlements = entitlements;
        _platformScope = platformScope;
    }

    /// <summary>How many agencies <paramref name="principalId"/> already runs beneath it.</summary>
    /// <remarks>
    /// Terminated sub-agents still count. An agency that terminated one and expects the slot back
    /// will be told to ask, which is a conversation; quietly freeing slots means the ceiling on the
    /// plan is not the ceiling anyone can rely on.
    /// </remarks>
    public async Task<int> CountAsync(Guid principalId, CancellationToken cancellationToken = default)
    {
        // The agency filter already shows a principal its own sub-agents, but this is also called
        // while acting as nobody — by a back-office screen, or by a job re-evaluating a plan — so
        // the read is scoped rather than left to depend on who is asking.
        using var scope = _platformScope.Enter(
            "sub-agent allowance — counting the agencies beneath one principal to check its plan's ceiling");

        return await _db.Agencies.CountAsync(agency => agency.ParentAgencyId == principalId, cancellationToken);
    }

    /// <summary>
    /// May <paramref name="principalId"/> take on <paramref name="adding"/> more sub-agents?
    /// </summary>
    public async Task<EntitlementDecision> MayAddAsync(
        Guid principalId,
        int adding = 1,
        CancellationToken cancellationToken = default)
    {
        var current = await CountAsync(principalId, cancellationToken);

        return await _entitlements.MayAddAsync(
            principalId, EntitlementCodes.MaxSubAgents, current, adding, cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>
    /// The network service has already counted, the same way <see cref="CountAsync"/> does, so its
    /// count is used rather than asking the database a second time.
    /// </remarks>
    public async Task<SubAgentEntitlementDecision> MayAddSubAgentAsync(
        Guid agencyId,
        int currentCount,
        CancellationToken cancellationToken = default)
    {
        var decision = await _entitlements.MayAddAsync(
            agencyId, EntitlementCodes.MaxSubAgents, currentCount, 1, cancellationToken);

        return new SubAgentEntitlementDecision(
            decision.IsAllowed,
            decision.Limit,
            currentCount,
            decision.IsAllowed ? null : decision.Detail);
    }
}
