using System.Text.Json.Serialization;

namespace TripsAgent.Integrations.TripsAfrica.Wire;

// ============================================================================================
//  Trips Africa's flight JSON, exactly as the documentation shows it — their words, their casing.
// ============================================================================================
//
//  Nothing here leaves this project. TripsAfricaMapping turns it into the port's supplier-neutral
//  records, and money into kobo, once, at that boundary (ISupplierAdapter's rule).
//
//  Every response property is nullable: this is someone else's API, documented by example, and a
//  missing field should reach the mapper as a null it can reason about rather than a crash here.
//  [JsonPropertyName] appears only where their casing is inconsistent ("GDSId" in search results,
//  "GdsId" in confirmations); reading is case-insensitive throughout, because the documentation's
//  own round-trip sample sends the same request in camelCase.

internal sealed record FlightSearchRequestWire(
    IReadOnlyList<FlightRouteWire> FlightRoutes,
    IReadOnlyList<PassengerCountWire> FlightPassengers,
    IReadOnlyList<FlightClassWire> FlightClasses,
    string Currency,
    bool EnsureAgentUnicityForRoutes,
    string OrderbyFlightTime,
    string OrderbyPrice,
    int PageSize,
    int From);

/// <param name="DepartureDate"><c>yyyy-MM-dd</c>.</param>
internal sealed record FlightRouteWire(string DepartureDate, string OriginLocationCode, string DestinationLocationCode);

/// <param name="Code"><c>ADT</c>, <c>CHD</c> or <c>INF</c>.</param>
internal sealed record PassengerCountWire(string Code, int Quantity);

/// <param name="Name"><c>Economy</c>, <c>Business</c>, …</param>
internal sealed record FlightClassWire(string Name);

internal sealed class FlightSearchResponseWire
{
    public string? SessionId { get; init; }

    public int? TotalCount { get; init; }

    public List<FlightResultWire>? ResultList { get; init; }
}

/// <summary>One priced option. International and domestic share this shape.</summary>
internal sealed class FlightResultWire
{
    /// <summary>One element per route searched, in the order the routes were sent — the only way to tell outbound from return.</summary>
    public List<FlightDetailWire>? FlightDetails { get; init; }

    public long? AgentId { get; init; }

    [JsonPropertyName("GDSId")]
    public long? GdsId { get; init; }

    public string? Currency { get; init; }

    /// <summary>The supplier's base fare, read as kobo. Absent on domestic results.</summary>
    [JsonPropertyName("BaseFare")]
    [JsonConverter(typeof(NairaAsKoboConverter))]
    public long? BaseFareMinor { get; init; }

    /// <summary>What the supplier charges us, as kobo — the net rate markup is applied to.</summary>
    [JsonPropertyName("TotalFare")]
    [JsonConverter(typeof(NairaAsKoboConverter))]
    public long? TotalFareMinor { get; init; }

    public FlightPropertiesWire? Properties { get; init; }

    /// <summary>True for a domestic result.</summary>
    public bool? IsLocal { get; init; }

    /// <summary>Domestic only: which route this result prices. Sent back on confirmation.</summary>
    public int? FlightRouteIndex { get; init; }
}

internal sealed class FlightPropertiesWire
{
    public long? CombinationID { get; init; }

    public long? RecommendationID { get; init; }

    public string? TripsSessionId { get; init; }

    public string? GdsSessionId { get; init; }
}

/// <summary>One route of the journey — a leg — which may be several flights.</summary>
internal sealed class FlightDetailWire
{
    public int? StopOvers { get; init; }

    public string? DepartureDate { get; init; }

    public string? DepartureAirportCode { get; init; }

    public string? ArrivalDate { get; init; }

    public string? ArrivalAirportCode { get; init; }

    public List<FlightEntryWire>? FlightEntries { get; init; }
}

/// <summary>One flight within a leg.</summary>
internal sealed class FlightEntryWire
{
    /// <summary>The number alone — "554" — without the airline code.</summary>
    public string? FlightNumber { get; init; }

    public string? MarketingAirlineCode { get; init; }

    public string? MarketingAirlineName { get; init; }

    public string? OperatingAirlineCode { get; init; }

    /// <summary>Local wall-clock time at the airport, no offset: <c>2023-07-28T06:45:00</c>.</summary>
    public string? DepartureDate { get; init; }

    public string? DepartureAirportCode { get; init; }

    public string? ArrivalDate { get; init; }

    public string? ArrivalAirportCode { get; init; }

    public string? FlightClass { get; init; }

    /// <summary>A count ("2") with a unit ("PC"), or a unit alone ("KGS") on domestic results.</summary>
    public string? Baggages { get; init; }

    public string? BaggageUnit { get; init; }

    public List<SeatAvailabilityWire>? AvailablePassengerSeats { get; init; }
}

internal sealed class SeatAvailabilityWire
{
    public string? PassengerType { get; init; }

    public string? FareBasis { get; init; }
}

// ------------------------------------------------------------------------------------ confirm

/// <summary>International confirmation: one offer, identified inline.</summary>
internal sealed record InternationalConfirmRequestWire(
    long? CombinationID,
    long? RecommendationID,
    int FlightRouteIndex,
    long? AgentID,
    long? GdsID,
    string SessionID,
    BillingAddressWire BillingAddress,
    IReadOnlyList<AirTravellerWire> AirTravellers);

/// <summary>
/// Domestic confirmation: the offers as a list, one per route — which is why its answer is an array.
/// </summary>
internal sealed record DomesticConfirmRequestWire(
    IReadOnlyList<SelectedFlightWire> SelectedFlights,
    string SessionId,
    BillingAddressWire BillingAddress,
    IReadOnlyList<AirTravellerWire> AirTravellers);

internal sealed record SelectedFlightWire(
    long? RecommendationID,
    long? CombinationID,
    long? GdsId,
    long? AgentId,
    int FlightRouteIndex);

internal sealed record BillingAddressWire(
    string ContactName,
    IReadOnlyList<string> ContactEmail,
    string ContactMobileNo,
    string AddressLine1,
    string City,
    string CountryCode);

internal sealed record AirTravellerWire(
    string PassengerTypeCode,
    string FirstName,
    string LastName,
    string? MiddleName,
    string? NamePrefix,
    string? Gender,
    string? BirthDate,
    IReadOnlyList<string>? Email,
    IReadOnlyList<TravelDocumentWire> Documents);

/// <param name="DocType"><c>DOCS</c> for the travel document, <c>DOCO</c> for a visa.</param>
/// <param name="InnerDocType"><c>PASSPORT</c>, <c>VISA</c>, …</param>
internal sealed record TravelDocumentWire(
    string DocType,
    string DocID,
    string InnerDocType,
    string IssueCountryCode,
    string? IssueLocation,
    string? BirthCountryCode,
    string? EffectiveDate,
    string? ExpiryDate);
