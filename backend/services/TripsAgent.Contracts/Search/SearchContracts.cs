namespace TripsAgent.Contracts.Search;

/// <summary>A flight search, as the console sends it.</summary>
/// <param name="TripType"><c>one_way</c>, <c>round_trip</c> or <c>multi_city</c>.</param>
/// <param name="Legs">One for one-way; two for a return, the second reversed; two to five for multi-city.</param>
/// <param name="Adults">12 and over. At least one.</param>
/// <param name="Children">2 to 11. With the adults, nine seats at most.</param>
/// <param name="Infants">Under 2, on an adult's lap — so never more than the adults.</param>
/// <param name="Cabin"><c>economy</c> (the default), <c>premium_economy</c>, <c>business</c> or <c>first</c>.</param>
public sealed record FlightSearchRequest(
    string TripType,
    IReadOnlyList<SearchLegRequest> Legs,
    int Adults,
    int Children,
    int Infants,
    string? Cabin);

/// <param name="Origin">A three-letter IATA airport code: <c>LOS</c>.</param>
/// <param name="Date">The local date of departure.</param>
public sealed record SearchLegRequest(string Origin, string Destination, DateOnly Date);

/// <summary>A bus search, as the console sends it. A return is searched as two one-way trips.</summary>
/// <param name="TripType"><c>one_way</c> or <c>round_trip</c>.</param>
/// <param name="DepartureTerminalId">The supplier's terminal id.</param>
/// <param name="ReturnDate">Required for <c>round_trip</c>; ignored otherwise.</param>
/// <param name="Passengers">1 to 10.</param>
public sealed record BusSearchRequest(
    string TripType,
    string DepartureTerminalId,
    string ArrivalTerminalId,
    DateOnly Date,
    DateOnly? ReturnDate,
    int Passengers);

/// <summary>A search's offers, priced for the caller.</summary>
/// <param name="SearchId">The <c>search_requests</c> row.</param>
/// <param name="ExpiresAt">After this the console asks for a fresh search before anything is chosen.</param>
/// <param name="FromCache">True when the net rates came from the cache (#40). Prices are the caller's current ones either way.</param>
public sealed record SearchResponse(
    Guid SearchId,
    DateTimeOffset SearchedAt,
    DateTimeOffset ExpiresAt,
    bool FromCache,
    IReadOnlyList<SearchOfferResponse> Offers);

/// <summary>One offer: a set of flights, or one bus departure.</summary>
/// <param name="Id">What selecting and confirming the offer refer to.</param>
/// <param name="Leg">0 outbound, 1 return — for a bus return, which arrives as two sets of one-way offers.</param>
/// <param name="Journeys">Flights: one journey per direction or multi-city leg. Empty for a bus.</param>
/// <param name="BusTrips">Buses: the one departure. Empty for flights.</param>
public sealed record SearchOfferResponse(
    Guid Id,
    int Leg,
    IReadOnlyList<FlightJourneyResponse> Journeys,
    IReadOnlyList<BusTripResponse> BusTrips,
    OfferPriceResponse Price);

/// <param name="DurationMinutes">Door to door, connections included.</param>
public sealed record FlightJourneyResponse(int DurationMinutes, int Stops, IReadOnlyList<FlightSegmentResponse> Segments);

/// <param name="DepartsAt">The airport's wall-clock time, <c>yyyy-MM-ddTHH:mm</c> — what the ticket prints.</param>
public sealed record FlightSegmentResponse(
    string CarrierCode,
    string? CarrierName,
    string FlightNumber,
    string Origin,
    string Destination,
    string DepartsAt,
    string ArrivesAt,
    int DurationMinutes,
    string? Cabin,
    string? BaggageAllowance);

/// <param name="DepartsAt">Lagos wall-clock time, <c>yyyy-MM-ddTHH:mm</c>.</param>
/// <param name="ArrivesAt">When the operator says; null when it does not.</param>
public sealed record BusTripResponse(
    string Operator,
    string? Vehicle,
    string DepartureTerminalId,
    string ArrivalTerminalId,
    string DepartsAt,
    string? ArrivesAt,
    int? DurationMinutes,
    int AvailableSeats,
    IReadOnlyList<string> SeatNumbers);

/// <summary>What the offer sells for. The margin only for callers holding <c>margin.view</c>.</summary>
/// <param name="SellMinor">What the traveller pays, in kobo: net + markup + VAT on the markup.</param>
/// <param name="Margin">
/// Null — present but empty — for anyone without <c>margin.view</c>. The net rate is what Trips charges
/// the agency; a counter agent has no reason to see their employer's cost price.
/// </param>
public sealed record OfferPriceResponse(string Currency, long SellMinor, OfferMarginResponse? Margin);

/// <param name="TaxMinor">VAT on the markup. Margin too: at a known rate, it gives the markup away.</param>
public sealed record OfferMarginResponse(long NetMinor, long MarkupMinor, long TaxMinor);
