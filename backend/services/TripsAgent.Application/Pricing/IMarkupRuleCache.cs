using TripsAgent.Domain.Pricing;

namespace TripsAgent.Application.Pricing;

/// <summary>
/// One agency's markup rules — every rule that is in force now or will be later — and its
/// principal, if it has one.
/// </summary>
/// <remarks>
/// This is the unit that is cached. Resolution itself is a pure calculation over it
/// (<see cref="MarkupEngine"/>) and costs microseconds; what costs a round trip is reading the rules
/// from PostgreSQL, so that is what the cache saves.
/// </remarks>
/// <param name="ParentAgencyId">The principal whose rules a sub-agent inherits. Null for a principal.</param>
public sealed record MarkupRuleSet(
    Guid AgencyId,
    Guid? ParentAgencyId,
    IReadOnlyList<MarkupRuleDefinition> Rules);

/// <summary>
/// Where each agency's <see cref="MarkupRuleSet"/> is kept between requests. Redis in production.
/// </summary>
/// <remarks>
/// <para>
/// A port, so Application never references a Redis client — the layering tests forbid it — and
/// so the pricing rules can be tested without one.
/// </para>
/// <para>
/// <b>A cache failure never fails a price.</b> If the cache is unreachable, implementations load
/// from the database and carry on. A slow quote is an inconvenience; a checkout that fails
/// because a cache is down is lost revenue for the agent.
/// </para>
/// </remarks>
public interface IMarkupRuleCache
{
    /// <summary>
    /// The cached set for <paramref name="agencyId"/>, or the result of <paramref name="load"/>,
    /// which is then cached.
    /// </summary>
    public Task<MarkupRuleSet> GetOrLoadAsync(
        Guid agencyId,
        Func<CancellationToken, Task<MarkupRuleSet>> load,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Makes the next read for <paramref name="agencyId"/> go to the database. Call it after every
    /// committed change to that agency's rules.
    /// </summary>
    /// <remarks>
    /// Only the agency whose rules changed needs invalidating. A sub-agent reads its principal's
    /// rules from the principal's own entry, so it sees the change on its next quote too.
    /// </remarks>
    public Task InvalidateAsync(Guid agencyId, CancellationToken cancellationToken = default);
}
