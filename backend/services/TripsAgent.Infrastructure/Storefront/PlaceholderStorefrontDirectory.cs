using Microsoft.EntityFrameworkCore;
using TripsAgent.Application.Persistence;
using TripsAgent.Application.Storefront;
using TripsAgent.Application.Tenancy;

namespace TripsAgent.Infrastructure.Storefront;

/// <summary>
/// Stands in for the storefront's domain lookup until custom domains (#59) land: every agency's
/// storefront is <c>https://{agency-slug}.storefront.invalid</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Deliberately fake.</b> <c>.invalid</c> is reserved (RFC 2606) and can never resolve, so a link
/// built from it cannot reach anyone else's site, and it is obvious in an email or a log that the
/// real lookup is not wired in yet. The storefront work replaces this registration with a lookup of
/// the agency's primary verified domain in <c>site_domains</c>.
/// </para>
/// <para>
/// It round-trips, which is what lets the CRM's public endpoints be built and tested now: a quote
/// sent by <c>lagos-travel</c> links to <c>lagos-travel.storefront.invalid</c>, and a request naming
/// that host resolves back to the same agency.
/// </para>
/// </remarks>
public sealed class PlaceholderStorefrontDirectory : IStorefrontDirectory
{
    /// <summary>What every placeholder storefront host ends with.</summary>
    public const string HostSuffix = ".storefront.invalid";

    private readonly IAppDbContext _db;
    private readonly IPlatformScope _platformScope;

    public PlaceholderStorefrontDirectory(IAppDbContext db, IPlatformScope platformScope)
    {
        _db = db;
        _platformScope = platformScope;
    }

    /// <summary>The placeholder host for an agency's slug.</summary>
    public static string HostFor(string agencySlug) => agencySlug + HostSuffix;

    public async Task<Guid?> FindAgencyAsync(string host, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(host);

        if (!host.EndsWith(HostSuffix, StringComparison.Ordinal))
        {
            return null;
        }

        var slug = host[..^HostSuffix.Length];

        if (slug.Length == 0 || slug.Contains('.', StringComparison.Ordinal))
        {
            return null;
        }

        // Before this read there is no tenant — the request is anonymous, and finding its agency is
        // the whole point — so it has to look across agencies, and says why.
        using (_platformScope.Enter("Storefront request: find the agency whose storefront answers on this host name"))
        {
            return await _db.Agencies.AsNoTracking()
                .Where(agency => agency.Slug == slug)
                .Select(agency => (Guid?)agency.Id)
                .FirstOrDefaultAsync(cancellationToken);
        }
    }

    public async Task<Uri> SiteUrlAsync(Guid agencyId, CancellationToken cancellationToken = default)
    {
        // The caller is acting for this agency, so its own row is visible through the tenant filter.
        var slug = await _db.Agencies.AsNoTracking()
            .Where(agency => agency.Id == agencyId)
            .Select(agency => agency.Slug)
            .FirstAsync(cancellationToken);

        return new Uri("https://" + HostFor(slug));
    }
}
