using Microsoft.EntityFrameworkCore;
using TripsAgent.Application.Persistence;
using TripsAgent.Application.Storefront;
using TripsAgent.Application.Tenancy;
using TripsAgent.Domain.Tenancy;

namespace TripsAgent.Application.Commerce;

/// <summary>The agency a storefront request turned out to be for, and the facts its answers need.</summary>
/// <param name="Currency">What the agency sells in. One currency per agency (decision 17).</param>
/// <param name="CanSell">
/// Whether the shop is open. False for a suspended agency, which still serves the travellers it
/// already has but takes no new business (decision 14), so an answer built for one of them must not
/// offer something the next request would refuse.
/// </param>
public sealed record StorefrontAgency(
    Guid Id,
    string Name,
    string Currency,
    string TimeZone,
    DateTimeOffset Now,
    bool CanSell);

/// <summary>What a storefront request is here to do, which decides what a suspended agency may serve.</summary>
/// <remarks>
/// Decision 14: a suspended agency's shop goes offline, but the people who already bought from it
/// keep their bookings and their documents. A request has to say which of the two it is.
/// </remarks>
public enum StorefrontVisit
{
    /// <summary>Browsing, filling a cart, checking out. Refused unless the agency may sell.</summary>
    Shopping = 0,

    /// <summary>A traveller returning to a booking they already hold. Refused only once terminated.</summary>
    ExistingBooking = 1,

    /// <summary>
    /// A customer reading a quote they were already sent — like a booking they hold, refused only
    /// once terminated. Answering one is new business, and so is <see cref="Shopping"/> (issue 171).
    /// </summary>
    ExistingQuote = 2,
}

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
/// Every public storefront route resolves its host here — the cart, the checkout, the public
/// departures API, the manage-my-booking page and the CRM's trip-request and quote routes — so
/// there is one place that decides who answers, and one place that applies decision 14 (issue 171).
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
    /// <param name="host">The host name the traveller's browser used.</param>
    /// <param name="visit">
    /// Shopping, or a traveller coming back to something they already hold. A suspended agency
    /// serves the second and not the first (decision 14).
    /// </param>
    /// <param name="cancellationToken">Cancels the work.</param>
    public async Task<StorefrontAgency?> EnterAsync(
        string? host,
        StorefrontVisit visit = StorefrontVisit.Shopping,
        CancellationToken cancellationToken = default)
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
            .Select(candidate => new
            {
                candidate.LegalName,
                candidate.TradingName,
                candidate.BaseCurrency,
                candidate.Timezone,
                candidate.Status,
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (agency is null)
        {
            // The directory named an agency the tenant filter cannot see. Treated as "no shop here"
            // rather than as an error: an anonymous caller learns nothing either way.
            return null;
        }

        // Decision 14, applied where every storefront request passes rather than on the page alone.
        // The site said "temporarily unavailable" while the cart, the checkout and the departures
        // API underneath went on selling to anyone who called them directly — a suspended agency
        // could still take a traveller's money. Found in the internal adversarial pass before the
        // penetration test (issue 110). The same "no shop here" answer, so nothing is disclosed.
        //
        // Anything that is not one of the two returning-traveller visits is held to the stricter
        // rule, so a visit added later is refused by a suspended agency until somebody decides.
        var mayServe = visit is StorefrontVisit.ExistingBooking or StorefrontVisit.ExistingQuote
            ? AgencyAccess.CanServeExistingTravellers(agency.Status)
            : AgencyAccess.CanServeStorefront(agency.Status);

        if (!mayServe)
        {
            return null;
        }

        return new StorefrontAgency(
            agencyId.Value,
            string.IsNullOrWhiteSpace(agency.TradingName) ? agency.LegalName : agency.TradingName,
            agency.BaseCurrency,
            agency.Timezone,
            Now,
            AgencyAccess.CanServeStorefront(agency.Status));
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
