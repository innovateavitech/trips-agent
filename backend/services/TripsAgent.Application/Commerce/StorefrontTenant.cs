using Microsoft.EntityFrameworkCore;
using TripsAgent.Application.Persistence;
using TripsAgent.Application.Storefront;
using TripsAgent.Application.Tenancy;

namespace TripsAgent.Application.Commerce;

/// <summary>The agency a storefront request turned out to be for, and the facts its answers need.</summary>
/// <param name="Currency">What the agency sells in. One currency per agency (decision 17).</param>
public sealed record StorefrontAgency(Guid Id, string Name, string Currency, string TimeZone, DateTimeOffset Now);

/// <summary>
/// Turns the host name a traveller's browser used into the agency whose shop answers on it, and puts
/// the rest of the request inside that agency.
/// </summary>
/// <remarks>
/// <para>
/// <b>The one cross-tenant read the storefront makes.</b> Finding who owns a host name has to look
/// past the tenant filter, because the point of the lookup is that we do not yet know whose it is;
/// <see cref="IStorefrontDirectory"/> does it inside <c>IPlatformScope</c>, with its reason logged.
/// Everything after it is an ordinary tenant read — never <c>IgnoreQueryFilters</c> (TRIPS002).
/// </para>
/// <para>
/// The same shape the CRM's storefront side uses (<c>StorefrontCrmService</c>), lifted out so the
/// cart, the checkout and the manage-my-booking page all resolve a host exactly one way.
/// </para>
/// </remarks>
public sealed class StorefrontTenant
{
    private readonly IAppDbContext _db;
    private readonly IStorefrontDirectory _storefront;
    private readonly TenantContext _tenant;
    private readonly TimeProvider _clock;

    public StorefrontTenant(
        IAppDbContext db,
        IStorefrontDirectory storefront,
        TenantContext tenant,
        TimeProvider clock)
    {
        _db = db;
        _storefront = storefront;
        _tenant = tenant;
        _clock = clock;
    }

    public DateTimeOffset Now => _clock.GetUtcNow();

    /// <summary>
    /// The agency whose storefront answers on <paramref name="host"/>, with this request moved
    /// inside it. Null when no shop answers there.
    /// </summary>
    public async Task<StorefrontAgency?> EnterAsync(string? host, CancellationToken cancellationToken = default)
    {
        var tidy = NormaliseHost(host);

        if (tidy is null)
        {
            return null;
        }

        var agencyId = await _storefront.FindAgencyAsync(tidy, cancellationToken);

        if (agencyId is null)
        {
            return null;
        }

        EnterTenant(agencyId.Value);

        var agency = await _db.Agencies.AsNoTracking()
            .Where(candidate => candidate.Id == agencyId.Value)
            .Select(candidate => new { candidate.LegalName, candidate.TradingName, candidate.BaseCurrency, candidate.Timezone })
            .FirstOrDefaultAsync(cancellationToken);

        if (agency is null)
        {
            // The directory named an agency the tenant filter cannot see. Treated as "no shop here"
            // rather than as an error: an anonymous caller learns nothing either way.
            return null;
        }

        return new StorefrontAgency(
            agencyId.Value,
            string.IsNullOrWhiteSpace(agency.TradingName) ? agency.LegalName : agency.TradingName,
            agency.BaseCurrency,
            agency.Timezone,
            Now);
    }

    /// <summary>The root of an agency's own site — where a traveller is sent back to, and never ours.</summary>
    public Task<Uri> SiteUrlAsync(Guid agencyId, CancellationToken cancellationToken = default) =>
        _storefront.SiteUrlAsync(agencyId, cancellationToken);

    /// <summary>Lower-case, without the port a browser may add: <c>Lekki-Horizon.com:443</c> is one host.</summary>
    public static string? NormaliseHost(string? host)
    {
        var tidy = host?.Trim().TrimEnd('.').ToLowerInvariant();

        if (string.IsNullOrEmpty(tidy))
        {
            return null;
        }

        var colon = tidy.LastIndexOf(':');

        // Only a port, never the colons of a bare IPv6 literal, which no storefront is reached by.
        if (colon > 0 && tidy.IndexOf(':', StringComparison.Ordinal) == colon)
        {
            tidy = tidy[..colon];
        }

        return tidy.Length == 0 ? null : tidy;
    }

    private void EnterTenant(Guid agencyId)
    {
        if (!_tenant.HasTenant)
        {
            _tenant.SetTenant(agencyId);
        }
        else if (_tenant.AgencyId != agencyId)
        {
            throw new InvalidOperationException(
                "This request already acts for another agency. A storefront request serves one host.");
        }
    }
}
