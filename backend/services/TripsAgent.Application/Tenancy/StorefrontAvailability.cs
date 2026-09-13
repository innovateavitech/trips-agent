using Microsoft.EntityFrameworkCore;
using TripsAgent.Application.Persistence;
using TripsAgent.Domain.Tenancy;

namespace TripsAgent.Application.Tenancy;

/// <summary>Whether a storefront should answer, and what to say when it should not.</summary>
/// <param name="IsLive">True when the public site may serve.</param>
/// <param name="Status">The agency's status, for logging. Never shown to a traveller.</param>
public sealed record StorefrontServingDecision(bool IsLive, AgencyStatus Status)
{
    /// <summary>
    /// What a traveller sees when the site is offline.
    /// </summary>
    /// <remarks>
    /// Deliberately says nothing about Trips, and nothing about why. CLAUDE.md rule 4: no
    /// traveller-facing surface mentions us, and the internal reason for a suspension is nobody's
    /// business but the agency's and ours.
    /// </remarks>
    public const string TravellerMessage =
        "This site is temporarily unavailable. Please try again later.";
}

/// <summary>
/// The one question the public storefront asks about an agency's standing.
/// </summary>
/// <remarks>
/// <para>
/// Build-plan decision 14 takes a suspended agency's storefront offline (FRD §2.15 RS-3). The
/// rule itself is <see cref="AgencyAccess.CanServeStorefront"/>; this is the part that reads the
/// status, and it exists as its own class so the storefront's host resolution has one call to
/// make rather than a copy of the rule to keep in step.
/// </para>
/// <para>
/// <b>Where this belongs.</b> The public storefront resolves a traveller's <c>Host</c> header to
/// an agency in <c>PublicSiteResolver</c> (F4, on the storefront branch). That resolution is the
/// right place to call this: it is the one point every public request passes through, it already
/// has the agency id, and answering there means a suspended agency's pages, feeds and forms all
/// go dark together rather than one at a time. It is called from the checkout's own guard today,
/// which covers new bookings; the site-serving half is wired up when the storefront lands.
/// </para>
/// <para>
/// Cheap on purpose — one indexed column on one row — because it runs on every public request.
/// It is not cached: a suspension has to take effect now, not when a cache entry happens to
/// expire, and that is the entire point of being able to suspend an agency.
/// </para>
/// </remarks>
public sealed class StorefrontAvailability
{
    private readonly IAppDbContext _db;
    private readonly IPlatformScope _platformScope;

    public StorefrontAvailability(IAppDbContext db, IPlatformScope platformScope)
    {
        _db = db;
        _platformScope = platformScope;
    }

    /// <summary>
    /// May this agency's public site serve? Null when there is no such agency.
    /// </summary>
    /// <remarks>
    /// Inside a platform scope, because host resolution runs before any tenant is established —
    /// working out whose site this is, is the thing that establishes it.
    /// </remarks>
    public async Task<StorefrontServingDecision?> ForAsync(
        Guid agencyId,
        CancellationToken cancellationToken = default)
    {
        using var scope = _platformScope.Enter(
            "Storefront serving check — reads one agency's status before its public site answers");

        var status = await _db.Agencies.AsNoTracking()
            .Where(agency => agency.Id == agencyId)
            .Select(agency => (AgencyStatus?)agency.Status)
            .FirstOrDefaultAsync(cancellationToken);

        return status is { } found
            ? new StorefrontServingDecision(AgencyAccess.CanServeStorefront(found), found)
            : null;
    }
}
