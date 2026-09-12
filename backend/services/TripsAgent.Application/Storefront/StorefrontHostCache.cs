namespace TripsAgent.Application.Storefront;

/// <summary>
/// Which agency and site answer on one hostname, and whether that hostname may be served at all.
/// </summary>
/// <remarks>
/// <para>
/// This is routing, not content: it says whose site to read, and nothing about what is on it. It is
/// the one thing every storefront request needs before it can do anything else, so it is the thing
/// worth caching (issue 59).
/// </para>
/// <para>
/// <see cref="Serveable"/> is false for a hostname that exists but is not ready — a custom domain
/// whose DNS has not been proved yet, or one held for manual review. Those are still cached, so a
/// name pointed at us early does not hammer the database while its owner waits.
/// </para>
/// </remarks>
/// <param name="AgencyId">Whose site it is. The tenant every following read runs under.</param>
/// <param name="PrimaryHostname">The hostname canonical URLs point at, which may be this one.</param>
public sealed record StorefrontHostRoute(
    Guid AgencyId,
    Guid SiteId,
    string Hostname,
    string PrimaryHostname,
    bool Serveable)
{
    /// <summary>True when this is the hostname canonical URLs and the sitemap should use.</summary>
    public bool IsPrimaryHostname =>
        string.Equals(Hostname, PrimaryHostname, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Remembers which agency answers on which hostname, so the lookup is not a database round trip on
/// every traveller's page view.
/// </summary>
/// <remarks>
/// <para>
/// <b>Misses are cached too.</b> A storefront answers on the open internet, where scanners try
/// thousands of names that belong to nobody. Without a negative entry each one is a database query,
/// which is a free denial-of-service. <see cref="GetOrLoadAsync"/> therefore caches "no such host"
/// as well as a hit.
/// </para>
/// <para>
/// <b>Failures degrade to the database, never to an error.</b> An unreachable cache makes the
/// storefront slower; it must never make it wrong or make it stop. Implementations swallow their own
/// transport failures, log them, and fall through to the loader.
/// </para>
/// <para>
/// <b>Invalidation is wholesale, not per key.</b> Hostnames change rarely — one is added, verified,
/// removed or made primary — so <see cref="InvalidateAllAsync"/> drops the lot rather than reasoning
/// about which entries a change touched. The map is small and refills in one query per hostname.
/// </para>
/// </remarks>
public interface IStorefrontHostCache
{
    /// <summary>
    /// The route for <paramref name="hostname"/>, from the cache when it is there and from
    /// <paramref name="load"/> when it is not. Null means no site answers on that name.
    /// </summary>
    public Task<StorefrontHostRoute?> GetOrLoadAsync(
        string hostname,
        Func<CancellationToken, Task<StorefrontHostRoute?>> load,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Drops every cached route, so the next request for each hostname is read afresh. Called when a
    /// site's hostnames change.
    /// </summary>
    public Task InvalidateAllAsync(CancellationToken cancellationToken = default);
}
