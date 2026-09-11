using System.Diagnostics.Metrics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TripsAgent.Application.Persistence;
using TripsAgent.Application.Pricing;
using TripsAgent.Application.Suppliers;
using TripsAgent.Application.Tenancy;
using TripsAgent.Domain.Pricing;
using TripsAgent.Domain.Suppliers;

namespace TripsAgent.Application.Search;

/// <summary>Settings for supplier search: <c>Search:CacheLifetime</c> and <c>Search:ResultLifetime</c>.</summary>
public sealed class SearchOptions
{
    public const string SectionName = "Search";

    /// <summary>How long a net result is reused for the same agency and criteria. #40 asks for 3–5 minutes.</summary>
    public TimeSpan CacheLifetime { get; init; } = TimeSpan.FromMinutes(4);

    /// <summary>
    /// How long the console treats a result as bookable before asking the agent to search again.
    /// The supplier documents no session lifetime; price confirmation re-checks with the supplier
    /// live either way, so this bounds how stale a shown price gets, not whether it is honoured.
    /// </summary>
    public TimeSpan ResultLifetime { get; init; } = TimeSpan.FromMinutes(10);
}

/// <summary>A search, priced for the agency that ran it.</summary>
/// <param name="FromCache">True when the net result came from the cache rather than from the supplier.</param>
public sealed record PricedSearchResult(
    Guid SearchRequestId,
    DateTimeOffset SearchedAt,
    DateTimeOffset ExpiresAt,
    bool FromCache,
    IReadOnlyList<PricedOffer> Offers);

/// <summary>One offer, and what it sells for under the agency's rules right now.</summary>
public sealed record PricedOffer(NetOffer Offer, PriceBreakdown Price);

/// <summary>
/// Runs a flight or bus search: from the cache when it can (#40), from the suppliers when it must
/// (#33, #34), and priced with the agency's markup every time.
/// </summary>
/// <remarks>
/// <para>
/// <b>Net in the cache, sell on the way out.</b> What is cached and stored is the supplier's net
/// rate; markup is applied as each result is read, through <see cref="PricingService"/> — so a rule
/// changed a minute ago prices the next search, cached or not, and no margin is ever stored against
/// a search.
/// </para>
/// <para>
/// <b>Every search is recorded</b> — the request, the supplier's session and each offer with its
/// raw payload — because a later price confirmation must quote the supplier's identifiers back
/// exactly, and a dispute needs what the supplier actually said. Price confirmation never goes near
/// the cache: it always asks the supplier live.
/// </para>
/// </remarks>
public sealed class SupplierSearchService
{
    /// <summary>The meter the cache hit rate is read from (#40: "hit/miss ratio instrumented").</summary>
    public const string MeterName = "TripsAgent.Search";

    private static readonly Meter Meter = new(MeterName);

    private static readonly Counter<long> CacheLookups = Meter.CreateCounter<long>(
        "tripsagent.search.cache.lookups",
        unit: "{lookup}",
        description: "Supplier search cache lookups, tagged result=hit|miss and the product searched.");

    private readonly IAppDbContext _db;
    private readonly ITenantContext _tenant;
    private readonly ISupplierAdapterRegistry _adapters;
    private readonly ISearchResultCache _cache;
    private readonly PricingService _pricing;
    private readonly SearchOptions _options;
    private readonly TimeProvider _clock;

    public SupplierSearchService(
        IAppDbContext db,
        ITenantContext tenant,
        ISupplierAdapterRegistry adapters,
        ISearchResultCache cache,
        PricingService pricing,
        SearchOptions options,
        TimeProvider clock)
    {
        _db = db;
        _tenant = tenant;
        _adapters = adapters;
        _cache = cache;
        _pricing = pricing;
        _options = options;
        _clock = clock;
    }

    /// <exception cref="SupplierUnavailableException">No supplier answered. Nothing was bought; trying again is safe.</exception>
    /// <exception cref="SupplierRequestRejectedException">A supplier refused the search as asked.</exception>
    public async Task<PricedSearchResult> SearchAsync(SupplierSearchQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var agencyId = _tenant.AgencyId
                       ?? throw new InvalidOperationException("A search is always for an agency, and this request has none.");
        var criteria = SearchCriteria.Normalise(query);

        var net = await _cache.GetAsync(agencyId, criteria.Hash, cancellationToken);
        var fromCache = net is not null;

        CacheLookups.Add(
            1,
            new KeyValuePair<string, object?>("result", fromCache ? "hit" : "miss"),
            new KeyValuePair<string, object?>("product", query.ProductType.ToString()));

        if (net is null)
        {
            net = await SearchSuppliersAsync(agencyId, query, criteria, cancellationToken);

            // An empty answer is not worth keeping: the next search should ask again, not be told
            // "nothing flies" for four minutes because nothing did a minute ago.
            if (net.Offers.Count > 0)
            {
                await _cache.SetAsync(agencyId, criteria.Hash, net, _options.CacheLifetime, cancellationToken);
            }
        }

        var priced = new List<PricedOffer>(net.Offers.Count);

        foreach (var offer in net.Offers)
        {
            var subject = new PricingSubject(ProductTypeOf(query.ProductType), offer.Currency, supplierCode: offer.SupplierCode);
            priced.Add(new PricedOffer(offer, await _pricing.PriceAsync(subject, offer.Net, cancellationToken)));
        }

        return new PricedSearchResult(net.SearchRequestId, net.SearchedAt, net.ExpiresAt, fromCache, priced);
    }

    private async Task<NetSearch> SearchSuppliersAsync(
        Guid agencyId,
        SupplierSearchQuery query,
        SearchCriteria criteria,
        CancellationToken cancellationToken)
    {
        var searchedAt = _clock.GetUtcNow();
        var started = _clock.GetTimestamp();

        var request = SearchRequest.Start(
            agencyId, _tenant.UserId, query.ProductType, criteria.Hash, criteria.Json, TripTypeOf(query.TripShape), searchedAt);
        _db.SearchRequests.Add(request);

        // Suppliers are platform reference data: read directly, with no tenant filter to switch off.
        var active = await _db.Suppliers
            .Where(supplier => supplier.IsActive)
            .Select(supplier => new { supplier.Code, supplier.Id })
            .ToListAsync(cancellationToken);

        var selling = _adapters.All
            .Where(adapter => adapter.Products.Contains(query.ProductType))
            .Select(adapter => (Adapter: adapter, SupplierId: active.Find(supplier => supplier.Code == adapter.SupplierCode)?.Id))
            .Where(pair => pair.SupplierId is not null)
            .ToList();

        if (selling.Count == 0)
        {
            throw new InvalidOperationException(
                $"No active supplier sells {query.ProductType}. Check that an adapter is registered and its suppliers row is active.");
        }

        var offers = new List<NetOffer>();

        try
        {
            // One supplier at a time: they share this request's DbContext, which is not safe to use
            // from two calls at once. Today there is one supplier, so nothing is lost.
            foreach (var (adapter, supplierId) in selling)
            {
                foreach (var (leg, legQuery) in PerSupplierQueries(query))
                {
                    var result = await adapter.SearchAsync(
                        new SupplierCallContext(agencyId, CorrelationId: request.Id.ToString()), legQuery, cancellationToken);

                    Record(agencyId, request.Id, supplierId!.Value, adapter.SupplierCode, leg, query.ProductType, result, searchedAt, offers);
                }
            }
        }
        catch (Exception ex) when (ex is SupplierUnavailableException or SupplierRequestRejectedException)
        {
            // Recorded for the error-rate report, then passed on for the caller to explain. Any offers
            // an earlier leg already found are saved with it: they are real, just not a whole answer.
            request.RecordFailed(
                ex is SupplierUnavailableException ? "supplier_unavailable" : "supplier_rejected",
                LatencyMs(started),
                _clock.GetUtcNow());
            await _db.SaveChangesAsync(cancellationToken);
            throw;
        }

        request.RecordCompleted(offers.Count, LatencyMs(started), _clock.GetUtcNow());
        await _db.SaveChangesAsync(cancellationToken);

        return new NetSearch(request.Id, searchedAt, searchedAt + _options.ResultLifetime, offers);
    }

    /// <summary>
    /// A bus return is two one-way searches. Trips Africa documents no round-trip bus search, and
    /// two one-way tickets is what an agent at a bus park sells anyway. Everything else is one search.
    /// </summary>
    private static IEnumerable<(int Leg, SupplierSearchQuery Query)> PerSupplierQueries(SupplierSearchQuery query) =>
        query.ProductType == SupplierProductType.Bus && query.Legs.Count > 1
            ? query.Legs.Select((leg, index) => (index, query with { TripShape = SupplierTripShape.OneWay, Legs = [leg] }))
            : [(0, query)];

    private void Record(
        Guid agencyId,
        Guid requestId,
        Guid supplierId,
        string supplierCode,
        int leg,
        SupplierProductType product,
        SupplierSearchResult result,
        DateTimeOffset at,
        List<NetOffer> offers)
    {
        if (result.Offers.Count == 0)
        {
            return;
        }

        // Supplier times carry their airport's offset — Lagos is +01:00 — and the database refuses any
        // instant that is not UTC (UtcTimestampConvention, and the MigrationTests case that pins it).
        // So they are converted here, as they are saved. The cached quote keeps its offset: that is
        // what the console shows, as the wall clock a ticket prints.
        var session = SearchSession.Open(
            agencyId, requestId, supplierId, result.SupplierSessionId, result.GdsSessionId, at, result.ExpiresAt?.ToUniversalTime());
        _db.SearchSessions.Add(session);

        foreach (var quote in result.Offers)
        {
            var offer = SupplierOffer.Record(
                agencyId, session.Id, supplierId, product, quote.OfferRef, quote.Reference, quote.Currency,
                quote.BaseFare, quote.TotalFare, quote.RawPayload, at, quote.ExpiresAt?.ToUniversalTime());
            _db.SupplierOffers.Add(offer);

            foreach (var segment in quote.FlightSegments)
            {
                _db.FlightSegments.Add(FlightSegment.Create(
                    agencyId, offer.Id, segment.LegIndex, segment.SegmentIndex, segment.MarketingCarrier,
                    segment.OperatingCarrier, segment.FlightNumber, segment.OriginIata, segment.DestinationIata,
                    segment.DepartureAt.ToUniversalTime(), segment.ArrivalAt.ToUniversalTime(),
                    segment.Cabin, segment.BaggageAllowance, segment.FareBasis));
            }

            foreach (var segment in quote.BusSegments)
            {
                _db.BusSegments.Add(BusSegment.Create(
                    agencyId, offer.Id, segment.OperatorName, segment.DepartureTerminalId, segment.ArrivalTerminalId,
                    segment.DepartureAt.ToUniversalTime(), segment.ArrivalAt?.ToUniversalTime(), segment.AvailableSeats,
                    JsonSerializer.Serialize(segment.SeatNumbers), segment.ReservationIdExt));
            }

            offers.Add(new NetOffer(
                offer.Id, supplierCode, leg, quote.Currency, quote.TotalFare, quote.FlightSegments, quote.BusSegments));
        }
    }

    private int LatencyMs(long started) =>
        (int)Math.Min(int.MaxValue, _clock.GetElapsedTime(started).TotalMilliseconds);

    private static PricedProductType ProductTypeOf(SupplierProductType product) => product switch
    {
        SupplierProductType.Bus => PricedProductType.Bus,
        _ => PricedProductType.Flight,
    };

    private static string TripTypeOf(SupplierTripShape shape) => shape switch
    {
        SupplierTripShape.RoundTrip => "round_trip",
        SupplierTripShape.MultiCity => "multi_city",
        _ => "one_way",
    };
}
