using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TripsAgent.Application.Persistence;
using TripsAgent.Application.Pricing;
using TripsAgent.Domain.Catalog;
using TripsAgent.Domain.Common;
using TripsAgent.Domain.Orders;
using TripsAgent.Domain.Pricing;

namespace TripsAgent.Application.Commerce;

/// <summary>How many people, by type. The shape a cart line and an order line both count in.</summary>
public readonly record struct PartySize(int Adults, int Children, int Infants)
{
    /// <summary>Everyone, of every type. What a seat hold counts and what a price is worked out from.</summary>
    public int Total => Adults + Children + Infants;

    /// <summary>The order line's <c>pax_breakdown</c> jsonb.</summary>
    public string ToJson() =>
        JsonSerializer.Serialize(new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["adults"] = Adults,
            ["children"] = Children,
            ["infants"] = Infants,
        });

    /// <summary>Reads a stored <c>pax_breakdown</c> back. An unreadable one counts as one adult.</summary>
    public static PartySize FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new PartySize(1, 0, 0);
        }

        try
        {
            var counts = JsonSerializer.Deserialize<Dictionary<string, int>>(json);

            return counts is null
                ? new PartySize(1, 0, 0)
                : new PartySize(Read(counts, "adults"), Read(counts, "children"), Read(counts, "infants"));
        }
        catch (JsonException)
        {
            return new PartySize(1, 0, 0);
        }

        static int Read(Dictionary<string, int> counts, string key) =>
            counts.TryGetValue(key, out var value) && value > 0 ? value : 0;
    }
}

/// <summary>What a priced cart line came to, and what it is for.</summary>
/// <param name="Quote">The recorded price. An order line is built from exactly this (rule 5).</param>
/// <param name="Title">What the traveller sees on the line.</param>
public sealed record PricedCartItem(PriceQuote Quote, string Title, PartySize Party);

/// <summary>
/// Prices the things a traveller can put in a cart: a catalog product, a seat on a dated departure,
/// or a searched fare.
/// </summary>
/// <remarks>
/// <para>
/// <b>The net rate is whatever the thing costs the agency, and the markup rides on top.</b> For a
/// tour, a visa or a departure the agency hosts it itself, so the "net" is the price the agent
/// typed and the platform's fee comes out of the markup (decision 4). For a flight or a bus the net
/// is what the supplier charges. Either way <see cref="PricingService"/> decides the sell price, so
/// there is one markup engine and not two.
/// </para>
/// <para>
/// <b>Nothing here is a promise.</b> A quote recorded when something goes in a cart is indicative,
/// and checkout re-quotes every line before anybody pays — see the remarks on <c>Cart</c>. The quote
/// an order line is frozen from is always the one checkout made.
/// </para>
/// </remarks>
public sealed class CartPricing
{
    private readonly IAppDbContext _db;
    private readonly PricingService _pricing;
    private readonly TimeProvider _clock;

    public CartPricing(IAppDbContext db, PricingService pricing, TimeProvider clock)
    {
        _db = db;
        _pricing = pricing;
        _clock = clock;
    }

    /// <summary>
    /// Prices a published catalog product for this party, and records the quote.
    /// </summary>
    /// <returns>Null when the product is not one this agency sells to the public.</returns>
    public async Task<PricedCartItem?> ForProductAsync(
        Guid productId,
        PartySize party,
        CancellationToken cancellationToken = default)
    {
        var product = await _db.Products.AsNoTracking()
            .Where(candidate => candidate.Id == productId)
            .Select(candidate => new
            {
                candidate.Id,
                candidate.Title,
                candidate.ProductType,
                candidate.Status,
                candidate.Currency,
                candidate.BasePriceMinor,
            })
            .FirstOrDefaultAsync(cancellationToken);

        // A draft or archived product is not on sale, and looks to a traveller exactly like one that
        // does not exist.
        if (product is null || product.Status != ProductStatus.Published)
        {
            return null;
        }

        var subject = new PricingSubject(PricedTypeOf(product.ProductType), product.Currency, product.Id);

        // Priced per person, everyone counted the same. Per-type pricing for a child on a tour is an
        // agent-authored price list we do not have yet; when it lands it belongs here.
        var net = product.BasePriceMinor * party.Total;
        var quote = await _pricing.QuoteAsync(subject, net, cancellationToken);

        return new PricedCartItem(quote, product.Title, party);
    }

    /// <summary>
    /// Prices seats on a dated departure for this party, from the tier its size falls in.
    /// </summary>
    /// <returns>Null when there is no such departure on sale, or no tier covers a party this size.</returns>
    public async Task<PricedCartItem?> ForDepartureAsync(
        Guid departureId,
        PartySize party,
        CancellationToken cancellationToken = default)
    {
        var departure = await _db.Departures.AsNoTracking()
            .Include(candidate => candidate.PriceTiers)
            .FirstOrDefaultAsync(candidate => candidate.Id == departureId, cancellationToken);

        if (departure is null || !departure.IsSellable(_clock.GetUtcNow(), party.Total))
        {
            return null;
        }

        var product = await _db.Products.AsNoTracking()
            .Where(candidate => candidate.Id == departure.ProductId)
            .Select(candidate => new { candidate.Title, candidate.Status, candidate.Currency })
            .FirstOrDefaultAsync(cancellationToken);

        if (product is null || product.Status != ProductStatus.Published)
        {
            return null;
        }

        var perPax = DepartureRules.PriceForParty(departure.ToTerms().PriceTiers, party.Total);

        if (perPax is not { } pricePerPax)
        {
            return null;
        }

        var subject = new PricingSubject(PricedProductType.GroupDeparture, product.Currency, departure.ProductId);
        var quote = await _pricing.QuoteAsync(subject, pricePerPax * party.Total, cancellationToken);

        return new PricedCartItem(quote, $"{product.Title} — {departure.DepartureDate:d MMMM yyyy}", party);
    }

    /// <summary>
    /// Prices a searched fare at what the supplier last quoted for it.
    /// </summary>
    /// <remarks>
    /// Indicative in the strongest sense: a searched fare is not a held one. Checkout confirms the
    /// price with the supplier, which is where the figure that gets paid comes from.
    /// </remarks>
    /// <returns>Null when the offer is not this agency's, or has gone.</returns>
    public async Task<PricedCartItem?> ForOfferAsync(
        Guid offerId,
        PartySize party,
        CancellationToken cancellationToken = default)
    {
        var offer = await _db.SupplierOffers.AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == offerId, cancellationToken);

        if (offer is null)
        {
            return null;
        }

        var supplierCode = await _db.Suppliers.AsNoTracking()
            .Where(supplier => supplier.Id == offer.SupplierId)
            .Select(supplier => supplier.Code)
            .FirstOrDefaultAsync(cancellationToken);

        if (supplierCode is null)
        {
            return null;
        }

        var subject = new PricingSubject(
            offer.ProductType == Domain.Suppliers.SupplierProductType.Bus ? PricedProductType.Bus : PricedProductType.Flight,
            offer.Currency,
            supplierCode: supplierCode);

        var quote = await _pricing.QuoteAsync(subject, offer.TotalFareMinor, cancellationToken);

        return new PricedCartItem(quote, await TitleAsync(offer.Id, offer.ProductType, cancellationToken), party);
    }

    /// <summary>What a searched fare reads as on a cart line: the route, and who flies or drives it.</summary>
    private async Task<string> TitleAsync(
        Guid offerId,
        Domain.Suppliers.SupplierProductType product,
        CancellationToken cancellationToken)
    {
        if (product == Domain.Suppliers.SupplierProductType.Bus)
        {
            var bus = await _db.BusSegments.AsNoTracking()
                .Where(segment => segment.SupplierOfferId == offerId)
                .OrderBy(segment => segment.DepartureAt)
                .Select(segment => new { segment.OperatorName, segment.DepartureTerminalId, segment.ArrivalTerminalId })
                .FirstOrDefaultAsync(cancellationToken);

            return bus is null
                ? "Bus trip"
                : $"{bus.OperatorName}, terminal {bus.DepartureTerminalId} → {bus.ArrivalTerminalId}";
        }

        var segments = await _db.FlightSegments.AsNoTracking()
            .Where(segment => segment.SupplierOfferId == offerId)
            .OrderBy(segment => segment.LegIndex)
            .ThenBy(segment => segment.SegmentIndex)
            .Select(segment => new { segment.LegIndex, segment.OriginIata, segment.DestinationIata, segment.FlightNumber })
            .ToListAsync(cancellationToken);

        if (segments.Count == 0)
        {
            return "Flight";
        }

        var outbound = segments.Where(segment => segment.LegIndex == segments[0].LegIndex).ToList();
        var returns = segments.Exists(segment => segment.LegIndex != segments[0].LegIndex);

        return $"{outbound[0].OriginIata} {(returns ? "⇄" : "→")} {outbound[^1].DestinationIata}, {outbound[0].FlightNumber}";
    }

    /// <summary>True for a line the agency fulfils itself, with no supplier to book with.</summary>
    /// <remarks>
    /// The distinction the whole money path turns on. An agency-hosted line owes the platform its fee
    /// and nothing else; a supplier line owes the supplier's net rate as well.
    /// </remarks>
    public static bool IsAgencyHosted(OrderLineItemType itemType) =>
        itemType is OrderLineItemType.Tour
            or OrderLineItemType.Visa
            or OrderLineItemType.Package
            or OrderLineItemType.GroupDeparture;

    /// <summary>What the agency owes for a line: the supplier's net rate, where there is a supplier, plus the fee.</summary>
    public static Money CostToAgency(OrderLine line)
    {
        ArgumentNullException.ThrowIfNull(line);

        return IsAgencyHosted(line.ItemType)
            ? line.PlatformFeeMinor
            : line.NetAmountMinor + line.PlatformFeeMinor;
    }

    private static PricedProductType PricedTypeOf(ProductType productType) => productType switch
    {
        ProductType.Tour => PricedProductType.Tour,
        ProductType.Visa => PricedProductType.Visa,
        ProductType.Package => PricedProductType.Package,
        _ => throw new ArgumentOutOfRangeException(nameof(productType), productType, "Unknown catalog product type."),
    };
}
