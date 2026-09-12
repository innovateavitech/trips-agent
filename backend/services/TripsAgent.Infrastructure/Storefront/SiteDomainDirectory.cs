using Microsoft.EntityFrameworkCore;
using TripsAgent.Application.Persistence;
using TripsAgent.Application.Storefront;
using TripsAgent.Application.Tenancy;
using TripsAgent.Domain.Storefront;

namespace TripsAgent.Infrastructure.Storefront;

/// <summary>
/// The real answer to "whose storefront answers on this host name", from <c>site_domains</c>
/// (issue 59). Replaces <see cref="PlaceholderStorefrontDirectory"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Only a hostname that is ready counts.</b> A custom domain whose DNS has not been proved, and
/// one held for a brand review, both exist as rows — and neither may serve anyone. Treating them as
/// live would let somebody point a name they do not own at an agency's shop and collect its
/// enquiries.
/// </para>
/// <para>
/// <b>It shares the storefront's host cache</b>, so the CRM's anonymous endpoints and the traveller's
/// own page views answer from the same map and a domain change clears both at once.
/// </para>
/// <para>
/// <b>It does not move the caller into the agency.</b> <see cref="PublicSiteResolver"/> does that for
/// a page render, which needs it; this only answers the question, and the caller decides what to do
/// with the answer.
/// </para>
/// </remarks>
public sealed class SiteDomainDirectory : IStorefrontDirectory
{
    private readonly IAppDbContext _db;
    private readonly IPlatformScope _platformScope;
    private readonly IStorefrontHostCache _cache;
    private readonly StorefrontOptions _options;

    public SiteDomainDirectory(
        IAppDbContext db,
        IPlatformScope platformScope,
        IStorefrontHostCache cache,
        StorefrontOptions options)
    {
        _db = db;
        _platformScope = platformScope;
        _cache = cache;
        _options = options;
    }

    public async Task<Guid?> FindAgencyAsync(string host, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(host);

        if (!PublicSiteResolver.TryNormalise(host, out var hostname))
        {
            return null;
        }

        var route = await _cache.GetOrLoadAsync(hostname, token => LoadAsync(hostname, token), cancellationToken);

        return route is { Serveable: true } ? route.AgencyId : null;
    }

    public async Task<Uri> SiteUrlAsync(Guid agencyId, CancellationToken cancellationToken = default)
    {
        // The site's main address, which is what a link in an email should point at: an agency can
        // answer on several names, and only one of them is the one they tell people.
        var hostname = await _db.Sites.AsNoTracking()
            .Where(site => site.AgencyId == agencyId)
            .Join(
                _db.SiteDomains.AsNoTracking(),
                site => site.PrimaryDomainId,
                domain => (Guid?)domain.Id,
                (_, domain) => domain.Hostname)
            .FirstOrDefaultAsync(cancellationToken);

        if (hostname is null)
        {
            // An agency with no site yet, or one whose only address is still being proved. There is
            // nowhere honest to send someone, and inventing a host would send them to a stranger's.
            throw new InvalidOperationException(
                $"Agency {agencyId} has no published web address, so no link to its website can be built.");
        }

        return new Uri(_options.SiteUrlFor(hostname));
    }

    /// <summary>
    /// Reads the one domain row that matches, across agencies — the only cross-tenant read here, and
    /// only of routing columns.
    /// </summary>
    private async Task<StorefrontHostRoute?> LoadAsync(string hostname, CancellationToken cancellationToken)
    {
        using (_platformScope.Enter(
                   "Storefront host resolution — finds which agency's site answers on a hostname an "
                   + "anonymous caller used, before anything is read under that agency."))
        {
            var match = await _db.SiteDomains.AsNoTracking()
                .Where(domain => domain.Hostname == hostname)
                .Select(domain => new
                {
                    domain.AgencyId,
                    domain.SiteId,
                    domain.Hostname,
                    Serveable = domain.VerificationStatus == DomainVerificationStatus.Verified && !domain.NeedsReview,
                })
                .FirstOrDefaultAsync(cancellationToken);

            if (match is null)
            {
                return null;
            }

            var primary = await _db.Sites.AsNoTracking()
                .Where(site => site.Id == match.SiteId)
                .Join(
                    _db.SiteDomains.AsNoTracking(),
                    site => site.PrimaryDomainId,
                    domain => (Guid?)domain.Id,
                    (_, domain) => domain.Hostname)
                .FirstOrDefaultAsync(cancellationToken);

            return new StorefrontHostRoute(
                match.AgencyId,
                match.SiteId,
                match.Hostname,
                primary ?? match.Hostname,
                match.Serveable);
        }
    }
}
