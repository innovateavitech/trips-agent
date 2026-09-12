namespace TripsAgent.Application.Tenancy.SubAgents;

/// <summary>What an entitlement check decided, and what to tell the agent if it said no.</summary>
/// <param name="IsAllowed">True when the agency may add another sub-agent.</param>
/// <param name="Limit">
/// The most sub-agents this agency's plan allows, or null when the plan does not cap it. Shown on
/// the sub-agent list so an agency can see how much room it has left.
/// </param>
/// <param name="Used">How many it has already, counted by the caller.</param>
/// <param name="Refusal">
/// One sentence for the agent, or null when nothing is being refused. Written for somebody who
/// has just pressed "Invite" and needs to know whether to upgrade or to remove a sub-agent.
/// </param>
public sealed record SubAgentEntitlementDecision(
    bool IsAllowed,
    int? Limit,
    int Used,
    string? Refusal)
{
    /// <summary>Allowed, with no plan limit known. What the unlimited implementation returns.</summary>
    public static SubAgentEntitlementDecision Unlimited(int used) => new(true, null, used, null);
}

/// <summary>
/// May this agency add another sub-agent?
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the seam where subscriptions and billing plugs in.</b> The number of sub-agents is
/// the <c>max_sub_agents</c> entitlement of an agency's subscription tier (feature F9, issue 64),
/// which is being built separately. This interface is the whole of what the sub-agent network
/// needs from it: one question, asked in one place — <c>SubAgentNetworkService.InviteAsync</c> —
/// before an agency is created.
/// </para>
/// <para>
/// Until F9 lands, <see cref="UnlimitedSubAgentEntitlement"/> is registered and answers yes to
/// everything. Replacing it is a one-line change in <c>DependencyInjection.AddApplication</c>: register an
/// implementation that reads the agency's subscription and its tier's entitlement value, and
/// nothing in this feature changes. The count of existing sub-agents is passed in, so the
/// implementation does not have to repeat the query.
/// </para>
/// <para>
/// Build-plan decision 15 says what happens when an agency is already over its limit after a
/// downgrade: existing sub-agents are kept and new ones are blocked. That is the shape of this
/// interface — it is asked before adding, and never asked about a sub-agent that already exists.
/// </para>
/// </remarks>
public interface ISubAgentEntitlement
{
    /// <summary>
    /// Whether <paramref name="agencyId"/> may add one more sub-agent, given it already has
    /// <paramref name="currentCount"/>.
    /// </summary>
    public Task<SubAgentEntitlementDecision> MayAddSubAgentAsync(
        Guid agencyId,
        int currentCount,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The stand-in until subscription entitlements exist: everything is allowed.
/// </summary>
/// <remarks>
/// Deliberately a real implementation rather than a null check at the call site. A null check
/// spreads — every new caller has to remember it — whereas this makes "no plan limits yet" a
/// single registered decision that one line in <c>DependencyInjection.AddApplication</c> replaces.
/// </remarks>
public sealed class UnlimitedSubAgentEntitlement : ISubAgentEntitlement
{
    public Task<SubAgentEntitlementDecision> MayAddSubAgentAsync(
        Guid agencyId,
        int currentCount,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(SubAgentEntitlementDecision.Unlimited(currentCount));
}
