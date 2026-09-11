using System.Text.Json;
using System.Text.Json.Serialization;

namespace TripsAgent.Integrations.TripsAfrica.Wire;

// ============================================================================================
//  Trips Africa's bus JSON, and the price-confirmation answer both products share.
// ============================================================================================

internal sealed record BusSearchRequestWire(BusSearchParameterWire Parameter, int PageSize, int From);

internal sealed record BusSearchParameterWire(
    BusTravelRouteWire TravelRoute,
    IReadOnlyList<PassengerCountWire> PassengerTypes,
    bool IsRoundTrip,
    string Currency,
    string OrderbyDepartureTime,
    string OrderByPrice);

/// <param name="DepartureId">The supplier's numeric terminal id — our <c>SupplierSearchLeg.Origin</c>.</param>
/// <remarks>No return date: the documentation shows none. See <c>TripsAfricaBusMapping</c>.</remarks>
internal sealed record BusTravelRouteWire(long DepartureId, long ArrivalId, string DepartureDate);

internal sealed class BusSearchResponseWire
{
    /// <summary>The session every later call quotes back.</summary>
    public string? TripsSessionId { get; init; }

    public string? Currency { get; init; }

    /// <summary>The outbound departures.</summary>
    public BusLegWire? FirstLeg { get; init; }

    /// <summary>The return departures, for a round trip; empty otherwise.</summary>
    public BusLegWire? SecondLeg { get; init; }
}

internal sealed class BusLegWire
{
    public List<BusRouteResultWire>? ResultList { get; init; }
}

/// <summary>One operator on one route and day, with the buses it is running.</summary>
internal sealed class BusRouteResultWire
{
    public long? CombinationId { get; init; }

    public string? SessionId { get; init; }

    public long? GdsId { get; init; }

    public long? AgentId { get; init; }

    public string? Currency { get; init; }

    /// <summary>The operator — "LIBRA Motors". Trips Africa calls it the agent.</summary>
    public string? AgentName { get; init; }

    public string? DepartureTerminal { get; init; }

    public string? ArrivalTerminal { get; init; }

    public long? DepartureTerminalId { get; init; }

    public long? ArrivalTerminalId { get; init; }

    public int? BusRouteIndex { get; init; }

    public List<AvailableBusWire>? AvailableBuses { get; init; }
}

/// <summary>One bus: a departure time, a vehicle, and its seats. Each is its own offer.</summary>
internal sealed class AvailableBusWire
{
    public long? RecommendationId { get; init; }

    public string? BusType { get; init; }

    /// <summary>Local wall-clock time, no offset.</summary>
    public string? EstimatedDepartureDate { get; init; }

    public string? EstimatedArrivalDate { get; init; }

    public int? TotalAvailableSeats { get; init; }

    public string? ReservationId { get; init; }

    public List<BusSeatWire>? AvailableSeats { get; init; }

    public List<BusPassengerFareWire>? PassengerFares { get; init; }

    /// <summary>For the whole party, read as kobo.</summary>
    [JsonPropertyName("TotalFare")]
    [JsonConverter(typeof(NairaAsKoboConverter))]
    public long? TotalFareMinor { get; init; }
}

internal sealed class BusSeatWire
{
    public bool? IsAvailable { get; init; }

    public string? SeatNumber { get; init; }
}

internal sealed class BusPassengerFareWire
{
    /// <summary>Before the supplier's service charge, read as kobo.</summary>
    [JsonPropertyName("BaseFare")]
    [JsonConverter(typeof(NairaAsKoboConverter))]
    public long? BaseFareMinor { get; init; }
}

// ------------------------------------------------------------------------------------ confirm

/// <summary>
/// One element of a price confirmation. International flights answer with one of these; domestic
/// flights and buses answer with an array, one per route — and every element is verified alone.
/// </summary>
internal sealed class ConfirmationWire
{
    public string? ConfirmationCode { get; init; }

    public DateTimeOffset? TicketTimeLimit { get; init; }

    /// <summary>The supplier's decimal price, read as kobo. Not covered by the hash.</summary>
    [JsonPropertyName("OldPrice")]
    [JsonConverter(typeof(NairaAsKoboConverter))]
    public long? OldPriceMinor { get; init; }

    /// <summary>The supplier's decimal price, read as kobo. Not covered by the hash — see <see cref="NewPriceWhole"/>.</summary>
    [JsonPropertyName("NewPrice")]
    [JsonConverter(typeof(NairaAsKoboConverter))]
    public long? NewPriceMinor { get; init; }

    /// <summary>Whole naira. This, not <see cref="NewPriceMinor"/>, is what the hash covers.</summary>
    public long? OldPriceWhole { get; init; }

    public long? NewPriceWhole { get; init; }

    /// <summary>Shape undocumented beyond "an array"; kept raw so a message is never lost to a type mismatch.</summary>
    public List<JsonElement>? Errors { get; init; }

    public string? Hash { get; init; }
}

// -------------------------------------------------------------------------------- bus confirm

internal sealed record BusConfirmRequestWire(
    string SessionId,
    IReadOnlyList<BusTravellerWire> Travellers,
    int NumberOfSeats,
    bool IsRoundTrip,
    IReadOnlyList<SelectedBusWire> SelectedBuses,
    BusBillingWire BillingAddress);

internal sealed record BusTravellerWire(
    string PassengerTypeCode,
    string FirstName,
    string LastName,
    string? NamePrefix,
    IReadOnlyList<string> SeatNumbers);

/// <param name="BusRouteIndex">0 for the outbound bus, 1 for the return.</param>
internal sealed record SelectedBusWire(
    long? RecommendationID,
    long? CombinationID,
    long? GdsId,
    long? AgentId,
    int BusRouteIndex);

/// <remarks>
/// The documented sample also carries <c>NextOfKinDetails</c>. The port has no next of kin to give,
/// so it is left out rather than invented; if staging insists on it, the port grows a field.
/// </remarks>
internal sealed record BusBillingWire(
    string ContactName,
    IReadOnlyList<string> ContactEmail,
    string ContactPhoneNumber,
    string AddressLine1);
