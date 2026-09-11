using System.Globalization;
using System.Text.Json;
using TripsAgent.Application.Suppliers;
using TripsAgent.Domain.Common;
using TripsAgent.Domain.Suppliers;
using TripsAgent.Integrations.TripsAfrica.Wire;
using static TripsAgent.Integrations.TripsAfrica.TripsAfricaMapping;

namespace TripsAgent.Integrations.TripsAfrica;

/// <summary>Trips Africa's bus payloads to ours and back. Pure, like <see cref="TripsAfricaMapping"/>.</summary>
/// <remarks>
/// <para>
/// <b>One-way only, on purpose.</b> Trips Africa documents a one-way bus search and nothing else: its
/// round-trip pages cover confirmation and issue, but no search request carries a return date. A
/// guessed field name would most likely be ignored, and the "return" results would silently be
/// one-way ones. So a multi-leg query is refused here, and the search service runs a return as two
/// one-way searches — two tickets, which is what an agent at a bus park sells anyway.
/// </para>
/// <para>
/// <b>Each bus is an offer.</b> The answer nests operator → buses; one operator runs several
/// departures on a route, each with its own time, seats and reservation, and each is chosen and
/// confirmed on its own.
/// </para>
/// </remarks>
internal static class TripsAfricaBusMapping
{
    /// <summary>The documented sample asks for 5,000; a route has nowhere near that many departures.</summary>
    private const int PageSize = 500;

    internal static BusSearchRequestWire ToBusSearchRequest(SupplierSearchQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (query.Legs.Count != 1)
        {
            throw new ArgumentException(
                "Trips Africa documents one-way bus search only. Search a return as two one-way trips.",
                nameof(query));
        }

        var leg = query.Legs[0];
        var passengers = new List<PassengerCountWire> { new("ADT", query.Passengers.Adults) };

        if (query.Passengers.Children > 0)
        {
            passengers.Add(new PassengerCountWire("CHD", query.Passengers.Children));
        }

        return new BusSearchRequestWire(
            new BusSearchParameterWire(
                new BusTravelRouteWire(TerminalId(leg.Origin), TerminalId(leg.Destination), IsoDate(leg.DepartureDate)),
                passengers,
                IsRoundTrip: false,
                Currency: "NGN",
                OrderbyDepartureTime: "ASC",
                OrderByPrice: "ASC"),
            PageSize,
            From: 0);
    }

    internal static SupplierSearchResult MapBusSearch(string responseJson, out int dropped)
    {
        using var document = JsonDocument.Parse(responseJson);
        var root = document.RootElement;
        var response = root.Deserialize<BusSearchResponseWire>(Json)
                       ?? throw new JsonException("Trips Africa answered a bus search with an empty body.");

        var offers = new List<SupplierOfferQuote>();
        dropped = 0;

        var routes = response.FirstLeg?.ResultList ?? [];
        var rawRoutes = RawArray(Property(root, "FirstLeg"), "ResultList");

        for (var r = 0; r < routes.Count; r++)
        {
            var route = routes[r];
            var buses = route.AvailableBuses ?? [];
            var rawBuses = RawArray(r < rawRoutes.Count ? rawRoutes[r] : null, "AvailableBuses");

            for (var b = 0; b < buses.Count; b++)
            {
                // The bus as the supplier sent it. Its operator and terminals live one level up, on
                // the route, and are kept on the segment instead.
                var raw = b < rawBuses.Count ? rawBuses[b].GetRawText() : "{}";

                if (MapBusOffer(route, buses[b], raw, response.Currency) is { } offer)
                {
                    offers.Add(offer);
                }
                else
                {
                    dropped++;
                }
            }
        }

        if (offers.Count > 0 && string.IsNullOrEmpty(response.TripsSessionId))
        {
            throw new JsonException("Trips Africa's bus search answer carried no TripsSessionId, so none of its offers could ever be confirmed.");
        }

        return new SupplierSearchResult(response.TripsSessionId ?? string.Empty, GdsSessionId: null, ExpiresAt: null, offers);
    }

    private static SupplierOfferQuote? MapBusOffer(
        BusRouteResultWire route,
        AvailableBusWire bus,
        string rawPayload,
        string? responseCurrency)
    {
        if (bus.TotalFareMinor is not { } totalMinor
            || string.IsNullOrWhiteSpace(route.AgentName)
            || route.DepartureTerminalId is null
            || route.ArrivalTerminalId is null
            || !TryLagosTime(bus.EstimatedDepartureDate, out var departs))
        {
            return null;
        }

        var total = new Money(totalMinor);

        DateTimeOffset? arrives = TryLagosTime(bus.EstimatedArrivalDate, out var arrival) && arrival >= departs
            ? arrival
            : null;

        var seats = (bus.AvailableSeats ?? [])
            .Where(seat => seat.IsAvailable == true && !string.IsNullOrWhiteSpace(seat.SeatNumber))
            .Select(seat => seat.SeatNumber!.Trim())
            .ToList();

        var reference = new SupplierOfferReference(
            AgentId: Invariant(route.AgentId),
            GdsId: Invariant(route.GdsId),
            CombinationId: Invariant(route.CombinationId),
            RecommendationId: Invariant(bus.RecommendationId),
            // The bus's route index rides in the tuple's route-index slot: outbound 0, return 1.
            FlightRouteIndex: route.BusRouteIndex ?? 0);

        var segment = new SupplierBusSegmentQuote(
            OperatorName: Clip(route.AgentName, 200)!,
            DepartureTerminalId: Invariant(route.DepartureTerminalId)!,
            ArrivalTerminalId: Invariant(route.ArrivalTerminalId)!,
            departs,
            arrives,
            AvailableSeats: Math.Max(0, bus.TotalAvailableSeats ?? seats.Count),
            SeatNumbers: seats,
            ReservationIdExt: Clip(bus.ReservationId, 200));

        return new SupplierOfferQuote(
            OfferRef(reference),
            reference,
            CurrencyOf(route.Currency ?? responseCurrency),
            BaseFareOf(bus) ?? total,
            total,
            FlightSegments: [],
            BusSegments: [segment],
            rawPayload,
            ExpiresAt: null);
    }

    /// <summary>The operator's fares before the supplier's service charge, when every one of them is readable.</summary>
    private static Money? BaseFareOf(AvailableBusWire bus)
    {
        var fares = bus.PassengerFares;

        if (fares is not { Count: > 0 })
        {
            return null;
        }

        var sum = Money.Zero;

        foreach (var fare in fares)
        {
            if (fare.BaseFareMinor is not { } baseMinor)
            {
                return null;
            }

            sum += new Money(baseMinor);
        }

        return sum;
    }

    /// <summary>
    /// The bus confirmation: every route's bus in <c>SelectedBuses</c>, so a return is one call whose
    /// answer is an array, verified element by element like a domestic flight's.
    /// </summary>
    internal static string ToBusConfirmBody(SupplierPriceConfirmationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var lead = request.Passengers.FirstOrDefault(passenger => passenger.Type == PassengerType.Adult)
                   ?? (request.Passengers.Count > 0 ? request.Passengers[0] : null)
                   ?? throw new ArgumentException("A price confirmation needs at least one passenger.", nameof(request));

        var selected = new[] { request.Reference }
            .Concat(request.AdditionalRoutes ?? [])
            .OrderBy(route => route.FlightRouteIndex)
            .Select(route => new SelectedBusWire(
                ParseId(route.RecommendationId),
                ParseId(route.CombinationId),
                ParseId(route.GdsId),
                ParseId(route.AgentId),
                route.FlightRouteIndex ?? 0))
            .ToList();

        var body = new BusConfirmRequestWire(
            request.SupplierSessionId,
            request.Passengers
                .Select(passenger => new BusTravellerWire(
                    PassengerCode(passenger.Type),
                    passenger.FirstName,
                    passenger.LastName,
                    passenger.Title,
                    passenger.SeatNumbers ?? []))
                .ToList(),
            // Infants ride on a lap, as on a plane: no seat of their own.
            NumberOfSeats: request.Passengers.Count(passenger => passenger.Type != PassengerType.Infant),
            IsRoundTrip: selected.Count > 1,
            selected,
            new BusBillingWire(
                $"{lead.FirstName} {lead.LastName}",
                lead.Email is null ? [] : [lead.Email],
                lead.PhoneNumber ?? string.Empty,
                AddressLine1: string.Empty));

        return JsonSerializer.Serialize(body, Json);
    }

    /// <summary>Our leg's origin is the supplier's numeric terminal id, as text.</summary>
    private static long TerminalId(string value) =>
        long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) && id > 0
            ? id
            : throw new ArgumentException($"'{value}' is not a Trips Africa terminal id — a positive whole number.", nameof(value));

    private static List<JsonElement> RawArray(JsonElement? parent, string name) =>
        parent is { } element && Property(element, name) is { ValueKind: JsonValueKind.Array } array
            ? array.EnumerateArray().ToList()
            : [];
}
