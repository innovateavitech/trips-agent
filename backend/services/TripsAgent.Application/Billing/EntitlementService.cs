using Microsoft.EntityFrameworkCore;
using TripsAgent.Application.Persistence;
using TripsAgent.Application.Tenancy;
using TripsAgent.Domain.Billing;

namespace TripsAgent.Application.Billing;

/// <summary>
/// Resolves an agency's entitlements from its subscription and its tier.
/// </summary>
/// <remarks>
/// <para>
/// Scoped, and it remembers what it has already resolved for the length of one request or one job
/// step. A quote asks for the transaction fee, the same request then asks whether a catalog listing
/// may be published, and both should see the same plan and cost one query between them.
/// </para>
/// <para>
/// Deliberately not a Redis cache, though the seam is here if it ever needs one
/// (<see cref="IEntitlements.Forget"/> is already the invalidation point). A cross-request cache on
/// something that decides whether an agency may transact is a stale answer waiting to happen, and
/// the query is one indexed read of a row an agency has exactly one of.
/// </para>
/// <para>
/// <b>It reads across tenants on purpose.</b> The subscription belongs to the agency being asked
/// about, which is not always the agency making the request — a billing job resolves entitlements
/// for every agency in turn, and the platform fee is looked up while pricing. So the read goes
/// through <see cref="IPlatformScope"/>, which is audited and logged, rather than through
/// <c>IgnoreQueryFilters</c>, which is neither.
/// </para>
/// </remarks>
public sealed class EntitlementService : IEntitlements
{
    private readonly Dictionary<Guid, EntitlementSet> _resolved = [];
    private readonly IAppDbContext _db;
    private readonly IPlatformScope _platformScope;
    private readonly TimeProvider _clock;

    public EntitlementService(IAppDbContext db, IPlatformScope platformScope, TimeProvider clock)
    {
        _db = db;
        _platformScope = platformScope;
        _clock = clock;
    }

    public async Task<EntitlementSet> ForAsync(Guid agencyId, CancellationToken cancellationToken = default)
    {
        if (_resolved.TryGetValue(agencyId, out var remembered))
        {
            return remembered;
        }

        var resolved = await ResolveAsync(agencyId, cancellationToken);
        _resolved[agencyId] = resolved;

        return resolved;
    }

    public async Task<EntitlementDecision> MayUseAsync(
        Guid agencyId,
        string code,
        CancellationToken cancellationToken = default)
    {
        var entitlements = await ForAsync(agencyId, cancellationToken);
        return entitlements.MayUse(code);
    }

    public async Task<EntitlementDecision> MayAddAsync(
        Guid agencyId,
        string code,
        int current,
        int adding = 1,
        CancellationToken cancellationToken = default)
    {
        var entitlements = await ForAsync(agencyId, cancellationToken);
        return entitlements.MayAdd(code, current, adding);
    }

    public void Forget(Guid agencyId) => _resolved.Remove(agencyId);

    private async Task<EntitlementSet> ResolveAsync(Guid agencyId, CancellationToken cancellationToken)
    {
        using var scope = _platformScope.Enter(
            "entitlements — resolving one agency's plan, which the platform owns and a job may ask about on any agency's behalf");

        var now = _clock.GetUtcNow();

        var subscription = await _db.Subscriptions
            .Where(candidate => candidate.AgencyId == agencyId)
            .Where(candidate => candidate.Status == SubscriptionStatus.Trialing
                             || candidate.Status == SubscriptionStatus.Active
                             || candidate.Status == SubscriptionStatus.PastDue)
            .OrderByDescending(candidate => candidate.CurrentPeriodStart)
            .FirstOrDefaultAsync(cancellationToken);

        if (subscription is null)
        {
            // No plan means the catalogue's defaults, which grant nothing chargeable. Falling back
            // to the restrictive answer is the only safe direction: the generous one hands an
            // agency that has never paid us everything the top tier offers.
            return EntitlementSet.Fallback;
        }

        // A trial that ran out but whose row the job has not reached yet grants nothing either. The
        // job is the thing that ends it properly; this stops a late job from extending a trial.
        if (subscription.Status == SubscriptionStatus.Trialing
            && subscription.TrialEndsAt is { } trialEnds
            && trialEnds <= now)
        {
            return EntitlementSet.Fallback;
        }

        var tier = await _db.SubscriptionTiers
            .FirstOrDefaultAsync(candidate => candidate.Id == subscription.TierId, cancellationToken);

        if (tier is null)
        {
            return EntitlementSet.Fallback;
        }

        var grants = await _db.TierEntitlements
            .Where(grant => grant.TierId == tier.Id)
            .Join(
                _db.Entitlements,
                grant => grant.EntitlementId,
                entitlement => entitlement.Id,
                (grant, entitlement) => new { entitlement.Code, grant.ValueType, grant.Value })
            .ToListAsync(cancellationToken);

        return EntitlementSet.From(
            tier.Id,
            tier.Name,
            grants.Select(grant => KeyValuePair.Create(
                grant.Code,
                EntitlementValue.FromJson(grant.ValueType, grant.Value))));
    }
}

/// <summary>
/// The platform's share of each sale, taken from the agency's subscription tier.
/// </summary>
/// <remarks>
/// <para>
/// This is the seam <c>NoPlatformFeePolicy</c> was standing in for. The rate is the
/// <c>transaction_fee_bps</c> entitlement, so changing a tier's fee changes what its subscribers
/// are charged on their next quote and nothing else — a stored quote keeps the rate it was priced
/// at, because CLAUDE.md rule 5 freezes it there.
/// </para>
/// <para>
/// Build-plan decision 4: the fee comes out of the agency's margin and is never added to the
/// traveller's price. That rule lives in <c>MarkupEngine.Price</c>, not here; all this does is say
/// what the rate is.
/// </para>
/// </remarks>
public sealed class EntitlementPlatformFeePolicy : Pricing.IPlatformFeePolicy
{
    private readonly IEntitlements _entitlements;

    public EntitlementPlatformFeePolicy(IEntitlements entitlements) => _entitlements = entitlements;

    public async Task<int> FeeBasisPointsAsync(Guid agencyId, CancellationToken cancellationToken = default)
    {
        var entitlements = await _entitlements.ForAsync(agencyId, cancellationToken);

        return entitlements.BasisPoints(EntitlementCodes.TransactionFeeBasisPoints);
    }
}
