using System.Text.Json;

namespace TripsAgent.Integrations.TripsAfrica.Wire;

// ============================================================================================
//  Trips Africa's ticket-issue call, and the two ways of asking where a booking has got to.
//  Field names as the documentation's samples spell them; read case-insensitively.
// ============================================================================================

/// <param name="TripType">International or Domestic.</param>
/// <param name="TripMode">Flight or Road.</param>
internal sealed record IssueRequestWire(string SessionId, string TripType, string TripMode);

internal sealed class IssueResponseWire
{
    /// <summary>The booking reference. A failed answer writes the string "null" here.</summary>
    public string? Pnr { get; init; }

    public bool? IsSuccessful { get; init; }

    public string? Message { get; init; }

    /// <summary>A name — "TicketPending", "TicketIssued" — or the string "null", or JSON null. Kept raw.</summary>
    public JsonElement? BookingStatus { get; init; }
}

internal sealed record BookingStatusRequestWire(string ConfirmationCode, string Surname);

internal sealed class BookingStatusResponseWire
{
    /// <summary>0, 1, 2, 3, 11 or 100. Kept raw, so a code sent as a string is read too.</summary>
    public JsonElement? StatusCode { get; init; }

    public string? StatusDescription { get; init; }

    /// <summary>Strings in every documented sample; kept raw so an object is never lost to a type mismatch.</summary>
    public List<JsonElement>? ErrorList { get; init; }
}

/// <param name="BookingReferenceId">The bus booking's PNR.</param>
/// <param name="BookingReferenceType">10 in the only documented sample. What the other values mean is not documented.</param>
internal sealed record BusReservationRequestWire(
    string BookingReferenceId,
    int BookingReferenceType,
    string Surname,
    string ETicketNumber);

internal sealed class BusReservationResponseWire
{
    public string? BookingReferenceId { get; init; }

    /// <summary>The same codes as the flight status query, as a number.</summary>
    public JsonElement? BookingStatus { get; init; }

    public string? BookingStatusName { get; init; }
}
