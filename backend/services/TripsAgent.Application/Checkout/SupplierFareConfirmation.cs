using Microsoft.EntityFrameworkCore;
using TripsAgent.Application.Persistence;
using TripsAgent.Application.Pricing;
using TripsAgent.Application.Suppliers;
using TripsAgent.Domain.Common;
using TripsAgent.Domain.Pricing;
using TripsAgent.Domain.Suppliers;

namespace TripsAgent.Application.Checkout;

/// <summary>Everything about a searched fare that confirming its price needs.</summary>
/// <param name="Subject">What the pricing engine prices it as.</param>
public sealed record SupplierFare(
    SupplierOffer Offer,
    string SupplierSessionId,
    string SupplierCode,
    ISupplierAdapter Adapter,
    PricingSubject Subject);

/// <summary>A fare the supplier has confirmed and held, with the deadline it holds it until.</summary>
/// <param name="NetMinor">What the supplier will charge for it. Never shown to a traveller.</param>
/// <param name="TicketTimeLimit">Pay and issue before this, or the supplier releases the fare.</param>
public sealed record ConfirmedFare(
    SupplierPriceConfirmation Confirmation,
    Money NetMinor,
    DateTimeOffset TicketTimeLimit,
    string Title);

/// <summary>
/// Confirming a searched fare's price with the supplier, and proving the answer can be trusted.
/// </summary>
/// <remarks>
/// <para>
/// The one place a fare is confirmed, shared by the console's checkout (<see cref="CheckoutService"/>)
/// and the storefront's (<c>StorefrontCheckoutService</c>) — so the hash gate, the deadline check and
/// the words a traveller is refused with cannot drift apart between the two.
/// </para>
/// <para>
/// <b>Confirming is sent once, never retried.</b> Asking twice holds the fare twice. This is not the
/// issue call, so the no-retry rule here is about not wasting the supplier's inventory rather than
/// about double-ticketing (ADR-0003) — but the discipline is the same.
/// </para>
/// <para>
/// <b>Every element of the answer verifies on its own</b> (issue 35). A domestic or round-trip
/// confirmation comes back as an array, and one bad hash in it condemns the whole answer: nothing is
/// booked, and a security alert is raised.
/// </para>
/// </remarks>
public sealed class SupplierFareConfirmation
{
    private readonly IAppDbContext _db;
    private readonly ISupplierAdapterRegistry _adapters;
    private readonly PriceConfirmationService _priceConfirmation;
    private readonly SupplierLineTitles _titles;
    private readonly TimeProvider _clock;

    public SupplierFareConfirmation(
        IAppDbContext db,
        ISupplierAdapterRegistry adapters,
        PriceConfirmationService priceConfirmation,
        SupplierLineTitles titles,
        TimeProvider clock)
    {
        _db = db;
        _adapters = adapters;
        _priceConfirmation = priceConfirmation;
        _titles = titles;
        _clock = clock;
    }

    /// <summary>Loads the searched fare, the session it came from, and the adapter that speaks to it.</summary>
    /// <exception cref="CheckoutRefusedException">There is no such fare for this agency any more.</exception>
    public async Task<SupplierFare> LoadAsync(Guid offerId, CancellationToken cancellationToken = default)
    {
        var offer = await _db.SupplierOffers.AsNoTracking()
                        .SingleOrDefaultAsync(candidate => candidate.Id == offerId, cancellationToken)
                    ?? throw new CheckoutRefusedException(
                        CheckoutRefusal.NotFound,
                        "We could not find that fare.",
                        "Fares from a search are kept for a few minutes only. Search again and choose one.");

        var supplierSessionId = await _db.SearchSessions.AsNoTracking()
            .Where(session => session.Id == offer.SearchSessionId)
            .Select(session => session.SupplierSessionId)
            .SingleAsync(cancellationToken);

        var supplierCode = await _db.Suppliers.AsNoTracking()
            .Where(supplier => supplier.Id == offer.SupplierId)
            .Select(supplier => supplier.Code)
            .SingleAsync(cancellationToken);

        var adapter = _adapters.Resolve(supplierCode, offer.ProductType);
        var subject = new PricingSubject(PricedTypeOf(offer.ProductType), offer.Currency, supplierCode: supplierCode);

        return new SupplierFare(offer, supplierSessionId, supplierCode, adapter, subject);
    }

    /// <summary>
    /// Asks the supplier to confirm and hold <paramref name="fare"/> for these travellers.
    /// </summary>
    /// <exception cref="CheckoutRefusedException">
    /// The fare has gone, the supplier did not answer in time, or its answer could not be verified.
    /// Nothing is booked or charged in any of those cases.
    /// </exception>
    public async Task<ConfirmedFare> ConfirmAsync(
        Guid agencyId,
        SupplierFare fare,
        IReadOnlyList<SupplierPassenger> passengers,
        string? correlationId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fare);
        ArgumentNullException.ThrowIfNull(passengers);

        SupplierPriceConfirmation confirmation;

        try
        {
            // Sent once: asking twice would hold the fare twice.
            confirmation = await fare.Adapter.ConfirmPriceAsync(
                new SupplierCallContext(agencyId, SupplierBookingId: null, correlationId),
                new SupplierPriceConfirmationRequest(
                    fare.Offer.ProductType,
                    fare.SupplierSessionId,
                    fare.Offer.OfferRef,
                    fare.Offer.Reference,
                    passengers),
                cancellationToken);
        }
        catch (SupplierRequestRejectedException)
        {
            throw new CheckoutRefusedException(
                CheckoutRefusal.Conflict,
                "The supplier no longer offers this fare.",
                "Nothing was booked or charged. Search again for a fresh fare.");
        }
        catch (SupplierCallOutcomeUnknownException)
        {
            throw new CheckoutRefusedException(
                CheckoutRefusal.SupplierFailed,
                "The supplier did not confirm the price in time.",
                "Nothing was booked or charged. Try again in a moment.");
        }

        var ticketTimeLimit = await VerifyAsync(agencyId, fare.SupplierCode, confirmation, cancellationToken);
        var net = new Money(confirmation.Lines.Sum(line => line.NewPrice.AmountMinor));
        var title = await _titles.ForAsync(fare.Offer.Id, fare.Offer.ProductType, cancellationToken);

        return new ConfirmedFare(confirmation, net, ticketTimeLimit, title);
    }

    /// <summary>
    /// Every element of the confirmation must verify on its own (issue 35), and there must be a deadline.
    /// </summary>
    private async Task<DateTimeOffset> VerifyAsync(
        Guid agencyId,
        string supplierCode,
        SupplierPriceConfirmation confirmation,
        CancellationToken cancellationToken)
    {
        if (confirmation.Lines.Count == 0)
        {
            throw new CheckoutRefusedException(
                CheckoutRefusal.SupplierFailed,
                "The supplier's confirmation was empty.",
                "Nothing was booked or charged. Search again.");
        }

        if (confirmation.Lines.Any(line => !SupplierBookingConfirmation.HashesMatch(line.HashExpected, line.HashReceived)))
        {
            await _priceConfirmation.RaiseIntegrityAlertAsync(
                agencyId, supplierBookingId: null, supplierCode, confirmation.Lines, cancellationToken);

            throw new CheckoutRefusedException(
                CheckoutRefusal.SupplierFailed,
                "The supplier's price could not be verified.",
                "Nothing was booked or charged, and the fare has been reported. Search again for another.");
        }

        var limit = confirmation.Lines.Min(line => line.TicketTimeLimit)
                    ?? throw new CheckoutRefusedException(
                        CheckoutRefusal.SupplierFailed,
                        "The supplier did not say how long it will hold the fare.",
                        "Nothing was booked or charged. Search again.");

        if (limit <= _clock.GetUtcNow())
        {
            throw new CheckoutRefusedException(
                CheckoutRefusal.Gone,
                "The supplier's hold on this fare has already ended.",
                "Nothing was booked or charged. Search again.");
        }

        return limit;
    }

    private static PricedProductType PricedTypeOf(SupplierProductType product) =>
        product == SupplierProductType.Bus ? PricedProductType.Bus : PricedProductType.Flight;
}

/// <summary>What a booked fare reads as on an order line: the route, and who flies or drives it.</summary>
/// <remarks>
/// Snapshotted onto the line at purchase and never read from the supplier again — an airline that
/// renames a route must not rewrite what somebody's booking says they bought.
/// </remarks>
public sealed class SupplierLineTitles
{
    private readonly IAppDbContext _db;

    public SupplierLineTitles(IAppDbContext db) => _db = db;

    public async Task<string> ForAsync(Guid offerId, SupplierProductType product, CancellationToken cancellationToken = default)
    {
        if (product == SupplierProductType.Bus)
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
}
