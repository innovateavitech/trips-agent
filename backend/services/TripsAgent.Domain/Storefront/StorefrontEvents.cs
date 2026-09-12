using TripsAgent.Domain.Common;

namespace TripsAgent.Domain.Storefront;

/// <summary>An agency created its website. Written through the outbox with the site itself.</summary>
public sealed record SiteCreated(Guid SiteId, Guid AgencyId) : IDomainEvent;

/// <summary>
/// A version of a site went live — by publishing a staged version, or by rolling back to an old one.
/// </summary>
/// <remarks>
/// The storefront cache invalidator (plan §3, job 15) consumes this and purges the site's cached
/// pages for every hostname it has, so travellers see the change without waiting out a TTL.
/// </remarks>
/// <param name="PreviousVersionId">The version that was live before, if any.</param>
/// <param name="IsRollback">True when an old version was put back rather than a new one published.</param>
public sealed record SitePublished(
    Guid SiteId,
    Guid AgencyId,
    Guid VersionId,
    int VersionNumber,
    Guid? PreviousVersionId,
    bool IsRollback) : IDomainEvent;

/// <summary>
/// The hostnames a site answers on changed: one was added, verified, removed, or made primary.
/// </summary>
/// <param name="Hostnames">
/// Every hostname whose cached pages are now wrong, including one that was just removed — the site
/// no longer lists it, so the invalidator could not find it any other way.
/// </param>
public sealed record SiteDomainsChanged(Guid SiteId, Guid AgencyId, IReadOnlyList<string> Hostnames) : IDomainEvent;
