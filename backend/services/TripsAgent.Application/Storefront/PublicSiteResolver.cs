using Microsoft.EntityFrameworkCore;
using TripsAgent.Application.Persistence;
using TripsAgent.Application.Tenancy;
using TripsAgent.Domain.Storefront;

namespace TripsAgent.Application.Storefront;

/// <summary>
/// Turns the hostname a traveller typed into the agency whose site answers on it, and puts the rest
/// of the request inside that agency (issue 59, issue 60).
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the only cross-tenant read the storefront makes.</b> Finding the owner of a hostname
/// has to look at every agency's domains, because the point of the lookup is that we do not yet know
/// whose it is. It therefore runs inside <see cref="IPlatformScope"/>, with a reason, and reads
/// exactly two columns' worth of routing — never content. Everything after it runs under the
/// resolved tenant, with the ordinary query filter doing its ordinary job.
/// </para>
/// <para>
/// <b>The hostname comes from the request and nothing else.</b> No query parameter, header or path
/// segment may choose which agency is served; the API reads <c>Host</c>, which behind the ingress is
/// the traveller's own (<c>ForwardedHeadersSetup</c>).
/// </para>
/// </remarks>
public sealed class PublicSiteResolver
{
    private readonly IAppDbContext _db;
    private readonly IPlatformScope _platformScope;
    private readonly IStorefrontHostCache _cache;
    private readonly TenantContext _tenant;

    public PublicSiteResolver(
        IAppDbContext db,
        IPlatformScope platformScope,
        IStorefrontHostCache cache,
        TenantContext tenant)
    {
        _db = db;
        _platformScope = platformScope;
        _cache = cache;
        _tenant = tenant;
    }

    /// <summary>
    /// The route for <paramref name="requestHost"/>, with this request moved inside its agency. Null
    /// when no site answers on that name, or when the name is not one we could ever serve.
    /// </summary>
    /// <param name="requestHost">The <c>Host</c> header, port and all.</param>
    public async Task<StorefrontHostRoute?> ResolveAsync(string? requestHost, CancellationToken cancellationToken = default)
    {
        if (!TryNormalise(requestHost, out var hostname))
        {
            return null;
        }

        var route = await _cache.GetOrLoadAsync(hostname, token => LoadAsync(hostname, token), cancellationToken);

        if (route is null)
        {
            return null;
        }

        // From here on this request is an ordinary tenant request: the query filter scopes every
        // read to this agency, and nothing else can widen it.
        if (!_tenant.HasTenant)
        {
            _tenant.SetTenant(route.AgencyId);
        }

        return route;
    }

    /// <summary>
    /// A hostname as the domains table stores it: lower case, no port, no trailing dot. False when
    /// the caller sent something that could not be a hostname at all.
    /// </summary>
    public static bool TryNormalise(string? requestHost, out string hostname)
    {
        hostname = string.Empty;

        if (string.IsNullOrWhiteSpace(requestHost))
        {
            return false;
        }

        var candidate = requestHost.Trim();

        // A Host header may carry a port, and an IPv6 literal carries brackets. Neither is part of
        // the name an agency registered.
        var colon = candidate.LastIndexOf(':');

        if (colon > 0 && !candidate.Contains(']', StringComparison.Ordinal))
        {
            candidate = candidate[..colon];
        }

        candidate = candidate.TrimEnd('.').ToLowerInvariant();

        if (candidate.Length is 0 or > Hostnames.MaxLength)
        {
            return false;
        }

        hostname = candidate;
        return true;
    }

    /// <summary>
    /// Reads the one domain row that matches, across agencies, plus the site's primary hostname.
    /// </summary>
    private async Task<StorefrontHostRoute?> LoadAsync(string hostname, CancellationToken cancellationToken)
    {
        using (_platformScope.Enter(
                   "Storefront host resolution — finds which agency's site answers on a hostname a "
                   + "traveller asked for, before anything is read under that agency."))
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

            // The site's primary hostname, for canonical links. A site always has one — its free
            // subdomain, until the agency points a custom domain at it and promotes that.
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
