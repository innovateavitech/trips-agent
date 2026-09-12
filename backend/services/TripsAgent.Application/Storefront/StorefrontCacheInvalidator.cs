namespace TripsAgent.Application.Storefront;

/// <summary>
/// Tells the storefront that a site's pages are out of date and should be built again.
/// </summary>
/// <remarks>
/// <para>
/// The storefront serves pre-rendered pages and keeps serving them until something says otherwise
/// (Next.js calls this ISR). Without this port a traveller would see last week's prices until a
/// timer ran out; with it, publishing a site is visible within a page load.
/// </para>
/// <para>
/// A port, because how the storefront is asked depends on where it is hosted, which is not decided.
/// Implementations never throw for a storefront that cannot be reached: the publish has already been
/// committed, and its pages will refresh on their own timer. Being late is not a reason to fail a
/// message and have it redelivered forever.
/// </para>
/// </remarks>
public interface IStorefrontRevalidator
{
    /// <summary>
    /// Asks the storefront to rebuild everything it holds for one site.
    /// </summary>
    /// <param name="siteId">The site, which the storefront tags its cached pages with.</param>
    /// <param name="hostnames">Its hostnames, for the log — one site can answer on several.</param>
    public Task RevalidateAsync(Guid siteId, IReadOnlyList<string> hostnames, CancellationToken cancellationToken = default);
}
