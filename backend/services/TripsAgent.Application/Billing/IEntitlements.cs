using TripsAgent.Domain.Billing;

namespace TripsAgent.Application.Billing;

/// <summary>
/// The one place that answers "may this agency do this?".
/// </summary>
/// <remarks>
/// <para>
/// Every feature with a plan limit asks here and nowhere else. That is the whole point: entitlement
/// rules copied into each feature drift apart, and the drift is invisible until one screen lets an
/// agency past a ceiling another screen enforces. There is exactly one implementation, one place
/// that decides what "unlimited" means, and one place that decides what an agency with no
/// subscription gets.
/// </para>
/// <para>
/// <b>Counted limits take the current count from the caller.</b> The catalog knows how to count
/// published products and the sub-agent network knows how to count agencies; neither fact belongs
/// here, and putting them here would make this interface grow a method per feature. The caller
/// counts, this decides.
/// </para>
/// <para>
/// <b>Who calls what.</b>
/// </para>
/// <list type="bullet">
///   <item>
///     <c>custom_domain</c> — the storefront's domain-claim handler (F4), before a custom hostname
///     is attached to a site.
///   </item>
///   <item>
///     <c>max_sub_agents</c> — the sub-agent invitation flow (F10). Enforced here today against the
///     agency hierarchy that already exists, so the ceiling is real before the screens are.
///   </item>
///   <item>
///     <c>max_catalog_listings</c> — the catalog's publish handler (F3), counting products already
///     published.
///   </item>
///   <item>
///     <c>transaction_fee_bps</c> — <see cref="Pricing.IPlatformFeePolicy"/>, on every quote.
///   </item>
///   <item><c>loyalty_program</c>, <c>api_access</c> — F13 and the public API, when they land.</item>
/// </list>
/// </remarks>
public interface IEntitlements
{
    /// <summary>
    /// Everything <paramref name="agencyId"/> is entitled to.
    /// </summary>
    /// <remarks>
    /// An agency with no live subscription gets <see cref="EntitlementSet.Fallback"/> — the
    /// catalogue's defaults, which are always the restrictive answer.
    /// </remarks>
    public Task<EntitlementSet> ForAsync(Guid agencyId, CancellationToken cancellationToken = default);

    /// <summary>May <paramref name="agencyId"/> use the feature behind the flag <paramref name="code"/>?</summary>
    public Task<EntitlementDecision> MayUseAsync(
        Guid agencyId,
        string code,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// May <paramref name="agencyId"/> add <paramref name="adding"/> more of what
    /// <paramref name="code"/> counts, given it already has <paramref name="current"/>?
    /// </summary>
    /// <param name="current">
    /// How many the agency has now. The caller counts, because the caller is what knows how.
    /// </param>
    public Task<EntitlementDecision> MayAddAsync(
        Guid agencyId,
        string code,
        int current,
        int adding = 1,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Forgets what it has resolved for <paramref name="agencyId"/>.
    /// </summary>
    /// <remarks>
    /// Called whenever a subscription changes — a renewal, a plan change, a migration landing,
    /// dunning running out. Entitlements are re-evaluated after any change; this is how.
    /// </remarks>
    public void Forget(Guid agencyId);
}
