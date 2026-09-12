namespace TripsAgent.Contracts.Bookings;

// ============================================================================================
//  The console's bookings (#42, #44). Every *Minor field is an integer number of kobo.
// ============================================================================================

/// <summary>A traveller, as the agent entered them.</summary>
/// <param name="Type"><c>ADT</c>, <c>CHD</c> or <c>INF</c>.</param>
/// <param name="Gender"><c>female</c>, <c>male</c>, or empty.</param>
/// <param name="Email">The lead traveller's: where the supplier sends schedule changes.</param>
/// <param name="PassportNumber">Only when the route leaves Nigeria. Stored as ciphertext.</param>
/// <param name="Nationality">ISO 3166-1 alpha-2: <c>NG</c>.</param>
public sealed record BookingTravellerRequest(
    string Type,
    string? Title,
    string FirstName,
    string LastName,
    DateOnly? DateOfBirth,
    string? Gender,
    string? Email,
    string? Phone,
    string? PassportNumber,
    DateOnly? PassportExpiry,
    string? Nationality);

/// <summary>Asks the supplier to confirm a searched fare's price for these travellers.</summary>
/// <param name="OfferId">The <c>id</c> of an offer from a search.</param>
public sealed record ConfirmPriceRequest(Guid OfferId, IReadOnlyList<BookingTravellerRequest> Travellers);

/// <summary>What the supplier confirmed, priced for the agency.</summary>
/// <param name="Reference">The booking this confirmation created. Paying for it names this.</param>
/// <param name="SellMinor">What the customer pays now.</param>
/// <param name="SearchedSellMinor">What the search showed. Differs only when the supplier moved the price.</param>
/// <param name="TicketTimeLimit">Pay before this, or the supplier releases the fare. UTC.</param>
public sealed record PriceConfirmationResponse(
    string Reference,
    long SellMinor,
    long SearchedSellMinor,
    string Currency,
    DateTimeOffset TicketTimeLimit);

/// <summary>Pays for a confirmed booking, and starts its ticket.</summary>
/// <param name="Reference">From the price confirmation.</param>
/// <param name="Payment"><c>wallet</c>. (<c>card</c> is refused until the storefront checkout.)</param>
/// <param name="AcceptedSellMinor">The price the agent accepted. It must be the confirmed one.</param>
/// <param name="IdempotencyKey">One per payment attempt, reused on a retry: the same key is the same booking.</param>
public sealed record PlaceBookingRequest(string Reference, string Payment, long AcceptedSellMinor, string IdempotencyKey);

public sealed record PlacedBookingResponse(string Reference);

/// <summary>Where a placed booking has got to. Polled: there are no webhooks.</summary>
/// <param name="Status"><c>awaiting_ticket</c>, <c>ticketed</c> or <c>failed</c>.</param>
public sealed record BookingProgressResponse(string Status, string? Pnr);

/// <summary>A booking in the list.</summary>
/// <param name="Product"><c>flight</c> or <c>bus</c>.</param>
/// <param name="DepartsAt">The first departure, as an instant.</param>
/// <param name="Status"><c>awaiting_ticket</c>, <c>ticketed</c>, <c>failed</c> or <c>cancelled</c>.</param>
/// <param name="TicketTimeLimit">Only while it waits on the supplier.</param>
/// <param name="BookedAt">When it was paid for.</param>
public sealed record BookingListItemResponse(
    string Reference,
    string LeadTraveller,
    int TravellerCount,
    string Product,
    string Origin,
    string Destination,
    string Carrier,
    DateTimeOffset DepartsAt,
    string Status,
    long SellMinor,
    string Currency,
    DateTimeOffset? TicketTimeLimit,
    string? Pnr,
    DateTimeOffset BookedAt);

/// <param name="Type"><c>ADT</c>, <c>CHD</c> or <c>INF</c>.</param>
/// <param name="TicketNumber">Once issued. Buses have none.</param>
public sealed record BookingTravellerResponse(string Type, string Name, string? TicketNumber);

/// <param name="DepartsAt">Local wall-clock time, <c>YYYY-MM-DDTHH:mm</c>, as the ticket prints it.</param>
public sealed record BookingSegmentResponse(string Carrier, string Origin, string Destination, string DepartsAt, string? ArrivesAt);

/// <param name="Status">The booking's status after this entry.</param>
public sealed record BookingTimelineEntryResponse(DateTimeOffset At, string Status, string Note);

/// <summary>What Trips charged and what the agency added. Only for <c>margin.view</c>.</summary>
public sealed record BookingMarginResponse(long NetMinor, long MarkupMinor);

/// <param name="Margin">Null unless the caller holds <c>margin.view</c>.</param>
public sealed record BookingPriceResponse(long SellMinor, BookingMarginResponse? Margin);

/// <summary>Why a booking is in the resolution queue, and what is at stake.</summary>
/// <param name="AtRiskMinor">What the customer paid and has not yet got anything for.</param>
/// <param name="PaidFrom"><c>wallet</c> or <c>card</c> — which decides where a refund goes.</param>
public sealed record BookingFailureResponse(string Reason, long AtRiskMinor, string PaidFrom);

/// <summary>One booking, in full.</summary>
public sealed record BookingDetailResponse(
    string Reference,
    string LeadTraveller,
    int TravellerCount,
    string Product,
    string Origin,
    string Destination,
    string Carrier,
    DateTimeOffset DepartsAt,
    string Status,
    long SellMinor,
    string Currency,
    DateTimeOffset? TicketTimeLimit,
    string? Pnr,
    DateTimeOffset BookedAt,
    string PaidFrom,
    IReadOnlyList<BookingTravellerResponse> Travellers,
    IReadOnlyList<BookingSegmentResponse> Segments,
    BookingPriceResponse Price,
    IReadOnlyList<BookingTimelineEntryResponse> Timeline,
    BookingFailureResponse? Failure);

/// <summary>What the agent decided for a failed booking.</summary>
/// <param name="Action"><c>refund</c>, or <c>retry</c> (refused: it needs a fresh fare). Substituting is a new search.</param>
public sealed record ResolveBookingRequest(string Action);
