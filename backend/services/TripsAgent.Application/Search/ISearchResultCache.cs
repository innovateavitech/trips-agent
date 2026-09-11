using TripsAgent.Application.Suppliers;
using TripsAgent.Domain.Common;

namespace TripsAgent.Application.Search;

/// <summary>
/// A search's result at the <b>net</b> rate, as cached.
/// </summary>
/// <remarks>
/// No markup, no sell price — deliberately. Markup is applied every time the result is read, so an
/// agent who changes a rule sees it on their very next search, cached or not (#40). A cached sell
/// price would go on showing the old margin until the entry expired.
/// </remarks>
public sealed record NetSearch(
    Guid SearchRequestId,
    DateTimeOffset SearchedAt,
    DateTimeOffset ExpiresAt,
    IReadOnlyList<NetOffer> Offers);

/// <summary>One offer at the net rate, with what the console needs to show it.</summary>
/// <param name="OfferId">The <c>supplier_offers</c> row — what selecting and confirming refer to.</param>
/// <param name="Leg">
/// Our leg: 0 outbound, 1 return. Needed for a bus return, which is searched as two one-way trips
/// and so comes back as two sets of offers.
/// </param>
/// <param name="Net">What the supplier charges us: the figure markup is applied to.</param>
public sealed record NetOffer(
    Guid OfferId,
    string SupplierCode,
    int Leg,
    string Currency,
    Money Net,
    IReadOnlyList<SupplierFlightSegmentQuote> FlightSegments,
    IReadOnlyList<SupplierBusSegmentQuote> BusSegments);

/// <summary>
/// Where net search results are kept for a few minutes, so a repeated search does not ask the
/// supplier again (#40). A port: Redis in production, nothing at all without it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Keyed by agency as well as criteria.</b> The offers a result points at are rows in
/// <c>supplier_offers</c>, which row-level security gives to one agency only; another agency handed
/// the same result could not even read them. So one agency's cached search is never another's —
/// by construction, not by care.
/// </para>
/// <para>
/// A cache failure is a miss, never an error: the cache makes search faster, and an outage of it
/// must not make search fail.
/// </para>
/// </remarks>
public interface ISearchResultCache
{
    public Task<NetSearch?> GetAsync(Guid agencyId, string criteriaHash, CancellationToken cancellationToken = default);

    public Task SetAsync(
        Guid agencyId,
        string criteriaHash,
        NetSearch search,
        TimeSpan lifetime,
        CancellationToken cancellationToken = default);
}

/// <summary>No cache: every search asks the supplier. What runs when Redis is not configured.</summary>
public sealed class NoSearchResultCache : ISearchResultCache
{
    public Task<NetSearch?> GetAsync(Guid agencyId, string criteriaHash, CancellationToken cancellationToken = default) =>
        Task.FromResult<NetSearch?>(null);

    public Task SetAsync(
        Guid agencyId,
        string criteriaHash,
        NetSearch search,
        TimeSpan lifetime,
        CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}
