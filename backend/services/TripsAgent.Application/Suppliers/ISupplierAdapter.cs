using TripsAgent.Domain.Suppliers;

namespace TripsAgent.Application.Suppliers;

/// <summary>
/// A travel aggregator we buy flights or bus seats from, as the application sees it.
/// </summary>
/// <remarks>
/// <para>
/// A port, the same shape as <c>IPaymentGateway</c>: Trips Africa is one implementation rather than
/// something the domain knows about. Everything crossing this interface is in <b>our</b> vocabulary
/// — <see cref="SupplierProductType"/>, <see cref="SupplierBookingStatus"/>, <c>Money</c> in minor
/// units — and each adapter translates to and from its supplier's own words. That is what lets a
/// second aggregator arrive as a new adapter and a new <c>suppliers</c> row, with no schema change.
/// </para>
/// <para>
/// An adapter is registered per supplier <b>and</b> product: the plan has a Trips Africa flight
/// adapter and a Trips Africa bus adapter, because the two use different endpoints and even
/// different authentication. <see cref="ISupplierAdapterRegistry"/> picks the right one.
/// </para>
/// <para>
/// <b>Money never crosses as a decimal.</b> An adapter whose supplier returns <c>1500.00</c> converts
/// it to <c>150000</c> kobo at the boundary, once, and nothing above this line converts again.
/// </para>
/// </remarks>
public interface ISupplierAdapter
{
    /// <summary>The <c>suppliers.code</c> this adapter serves — <c>trips_africa</c>.</summary>
    public string SupplierCode { get; }

    /// <summary>The products this adapter sells. Usually one.</summary>
    public IReadOnlyCollection<SupplierProductType> Products { get; }

    /// <summary>Searches the supplier's inventory. Safe to retry: it is a read.</summary>
    public Task<SupplierSearchResult> SearchAsync(
        SupplierCallContext context,
        SupplierSearchQuery query,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Locks the price of an offer and, for Trips Africa, holds a PNR.
    /// </summary>
    /// <remarks>
    /// Returns one <see cref="PriceConfirmationLine"/> per element the supplier sent back. Domestic
    /// and round-trip confirmations are <b>arrays</b>, and each element carries its own hash, which
    /// the adapter computes the expected value for and <c>SupplierBooking.RecordPriceConfirmation</c>
    /// verifies one by one. Never collapse the array into one line.
    /// </remarks>
    public Task<SupplierPriceConfirmation> ConfirmPriceAsync(
        SupplierCallContext context,
        SupplierPriceConfirmationRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Issues the ticket. <b>Called at most once per booking, and never retried.</b>
    /// </summary>
    /// <remarks>
    /// <para>
    /// Trips Africa's issue endpoint is not idempotent. If a request succeeded and its response was
    /// lost, a retry issues a second real ticket for a real person. So an implementation must attach
    /// no retry or resilience handler to this call, and must report a timeout or a dropped connection
    /// as <see cref="SupplierIssueOutcome.Unknown"/> rather than throw — an unknown outcome is
    /// resolved by <see cref="GetStatusAsync"/>, never by calling this again.
    /// </para>
    /// <para>See docs/adr/0003-never-retry-ticket-issuance.md.</para>
    /// </remarks>
    public Task<SupplierIssueResult> IssueAsync(
        SupplierCallContext context,
        SupplierIssueRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Asks the supplier where a booking has got to. The only way to learn a final outcome: there
    /// are no webhooks. Safe to retry: it is a read.
    /// </summary>
    public Task<SupplierStatusResult> GetStatusAsync(
        SupplierCallContext context,
        SupplierStatusQuery query,
        CancellationToken cancellationToken = default);

    /// <summary>The fare rules and penalties for an offer, which the agent must see before choosing it.</summary>
    public Task<SupplierFareRules> GetRulesAsync(
        SupplierCallContext context,
        SupplierRulesQuery query,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Cancels a booking with the supplier.
    /// </summary>
    /// <remarks>
    /// Trips Africa documents cancellation for bus bookings only. An adapter for a product its
    /// supplier cannot cancel throws <see cref="SupplierOperationNotSupportedException"/> rather than
    /// pretending — a cancellation that silently did nothing would refund a ticket that still flies.
    /// </remarks>
    public Task<SupplierCancellationResult> CancelAsync(
        SupplierCallContext context,
        SupplierCancellationRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Who a supplier call is being made for, so it can be audited and credentialed correctly.
/// </summary>
/// <param name="AgencyId">
/// The agency the call is for, or null for platform work such as a smoke test. Selects the
/// credential (open question 1) and stamps the <c>supplier_api_calls</c> row.
/// </param>
/// <param name="SupplierBookingId">The booking the call concerns, when there is one.</param>
/// <param name="CorrelationId">Ties the audited call back to the request or saga that made it.</param>
public sealed record SupplierCallContext(
    Guid? AgencyId,
    Guid? SupplierBookingId = null,
    string? CorrelationId = null);

/// <summary>The shape of a journey, in our words.</summary>
public enum SupplierTripShape
{
    OneWay = 1,
    RoundTrip = 2,
    MultiCity = 3,
}

/// <summary>One direction of a journey being searched.</summary>
/// <param name="Origin">A three-letter IATA code for a flight; the supplier's terminal id for a bus.</param>
/// <param name="Destination">As <paramref name="Origin"/>.</param>
public sealed record SupplierSearchLeg(string Origin, string Destination, DateOnly DepartureDate);

/// <summary>How many travellers of each fare category.</summary>
public sealed record SupplierPassengerCounts(int Adults, int Children = 0, int Infants = 0);

/// <summary>A search, in supplier-neutral terms.</summary>
/// <param name="Cabin">A cabin preference for flights — economy, business. Ignored for bus.</param>
/// <param name="Page">One-based. Trips Africa pages search results fifty at a time.</param>
public sealed record SupplierSearchQuery(
    SupplierProductType ProductType,
    SupplierTripShape TripShape,
    IReadOnlyList<SupplierSearchLeg> Legs,
    SupplierPassengerCounts Passengers,
    string? Cabin = null,
    int Page = 1);

/// <summary>What a search returned.</summary>
/// <param name="SupplierSessionId">
/// The supplier's session for this search. Every later call about an offer from it quotes this
/// back — Trips Africa's <c>SessionId</c>.
/// </param>
public sealed record SupplierSearchResult(
    string SupplierSessionId,
    string? GdsSessionId,
    DateTimeOffset? ExpiresAt,
    IReadOnlyList<SupplierOfferQuote> Offers);

/// <summary>One priced option from a search.</summary>
/// <param name="Reference">
/// The supplier's identity tuple for the offer. Must reach price confirmation exactly as received.
/// </param>
/// <param name="TotalFare">What the supplier will charge us — the net rate markup is applied to.</param>
/// <param name="RawPayload">The offer as the supplier sent it, as JSON, kept for replay and disputes.</param>
public sealed record SupplierOfferQuote(
    string OfferRef,
    SupplierOfferReference Reference,
    string Currency,
    TripsAgent.Domain.Common.Money BaseFare,
    TripsAgent.Domain.Common.Money TotalFare,
    IReadOnlyList<SupplierFlightSegmentQuote> FlightSegments,
    IReadOnlyList<SupplierBusSegmentQuote> BusSegments,
    string RawPayload,
    DateTimeOffset? ExpiresAt);

/// <summary>One flight within an offer.</summary>
public sealed record SupplierFlightSegmentQuote(
    int LegIndex,
    int SegmentIndex,
    string MarketingCarrier,
    string? OperatingCarrier,
    string FlightNumber,
    string OriginIata,
    string DestinationIata,
    DateTimeOffset DepartureAt,
    DateTimeOffset ArrivalAt,
    string? Cabin = null,
    string? BaggageAllowance = null,
    string? FareBasis = null,
    string? MarketingCarrierName = null,
    int? DurationMinutes = null);

/// <summary>One bus trip within an offer.</summary>
public sealed record SupplierBusSegmentQuote(
    string OperatorName,
    string DepartureTerminalId,
    string ArrivalTerminalId,
    DateTimeOffset DepartureAt,
    DateTimeOffset? ArrivalAt,
    int? AvailableSeats,
    IReadOnlyList<string> SeatNumbers,
    string? ReservationIdExt = null,
    string? VehicleType = null);

/// <summary>A traveller, as a supplier needs them for confirmation and ticketing.</summary>
public sealed record SupplierPassenger(
    PassengerType Type,
    string FirstName,
    string LastName,
    string? MiddleName = null,
    string? Title = null,
    DateOnly? BirthDate = null,
    string? Gender = null,
    string? Email = null,
    string? PhoneNumber = null,
    SupplierTravelDocument? Document = null,
    IReadOnlyList<string>? SeatNumbers = null);

/// <summary>
/// A travel document, in clear, on its way to a supplier.
/// </summary>
/// <remarks>
/// It exists only in memory, between the encrypted <c>passenger_documents</c> row and the request
/// body. <see cref="ToString"/> is overridden because a record's generated one prints every
/// property — and one log line of this would put a passport number in the logs.
/// </remarks>
public sealed record SupplierTravelDocument(
    TravelDocumentKind Kind,
    string Number,
    string IssuingCountry,
    string? NationalityCountry = null,
    DateOnly? IssuedOn = null,
    DateOnly? ExpiresOn = null)
{
    public override string ToString() => $"SupplierTravelDocument {{ Kind = {Kind}, IssuingCountry = {IssuingCountry}, Number = [redacted] }}";
}

/// <summary>A request to lock the price of one offer.</summary>
/// <param name="AdditionalRoutes">
/// The other routes' offers when a supplier prices each route separately but confirms them together —
/// a Trips Africa domestic return, or a bus return. Their answer is then an array, one element per
/// route, and every element is verified on its own. Null for everything else.
/// </param>
public sealed record SupplierPriceConfirmationRequest(
    SupplierProductType ProductType,
    string SupplierSessionId,
    string OfferRef,
    SupplierOfferReference Reference,
    IReadOnlyList<SupplierPassenger> Passengers,
    IReadOnlyList<SupplierOfferReference>? AdditionalRoutes = null);

/// <summary>What the supplier confirmed.</summary>
/// <param name="TripType">The supplier's own trip-type word, which the issue call must send back.</param>
/// <param name="TripMode">The supplier's own mode word, which the issue call must send back.</param>
/// <param name="Lines">One per element of the supplier's answer — more than one for domestic and round-trip.</param>
public sealed record SupplierPriceConfirmation(
    string SupplierSessionId,
    string? TripType,
    string? TripMode,
    IReadOnlyList<PriceConfirmationLine> Lines);

/// <summary>A request to issue the ticket for a confirmed booking.</summary>
/// <param name="IdempotencyKey">
/// <c>supplier_bookings.idempotency_key</c>. Trips Africa ignores it; a supplier that honours one gets
/// it. Either way it does not make retrying safe — see <see cref="ISupplierAdapter.IssueAsync"/>.
/// </param>
public sealed record SupplierIssueRequest(
    SupplierProductType ProductType,
    string SupplierSessionId,
    string? TripType,
    string? TripMode,
    IReadOnlyList<string> ConfirmationCodes,
    string IdempotencyKey);

/// <summary>What is known about an issue call once it returns.</summary>
public enum SupplierIssueOutcome
{
    /// <summary>The supplier answered and accepted it. The ticket may still be pending.</summary>
    Accepted = 1,

    /// <summary>The supplier answered and refused it. Nothing was issued.</summary>
    Rejected = 2,

    /// <summary>
    /// No usable answer — a timeout, a dropped connection. The ticket <b>may have been issued</b>.
    /// Resolved by polling status, never by issuing again (ADR-0003).
    /// </summary>
    Unknown = 3,
}

/// <summary>The result of the one issue call a booking gets.</summary>
/// <param name="SupplierStatusCode">The supplier's own status code, verbatim — Trips Africa's 0, 1, 2, 3, 11 or 100.</param>
/// <param name="Status">The adapter's reading of that code in our terms, when it could make one.</param>
public sealed record SupplierIssueResult(
    SupplierIssueOutcome Outcome,
    int? HttpStatusCode,
    int? SupplierStatusCode,
    SupplierBookingStatus? Status,
    string? Pnr,
    string? Message = null);

/// <summary>Identifies a booking to the supplier for a status query.</summary>
/// <param name="PassengerSurname">Trips Africa identifies a booking by confirmation code and surname.</param>
public sealed record SupplierStatusQuery(
    SupplierProductType ProductType,
    string ConfirmationCode,
    string? Pnr = null,
    string? PassengerSurname = null);

/// <summary>A ticket number the supplier issued to one traveller.</summary>
public sealed record SupplierTicket(string PassengerLastName, string? PassengerFirstName, string TicketNumber);

/// <summary>What a status query learned. Stored as a <c>supplier_status_polls</c> row by the poller.</summary>
public sealed record SupplierStatusResult(
    SupplierPollOutcome Outcome,
    int? HttpStatusCode,
    int? SupplierStatusCode,
    SupplierBookingStatus? Status,
    string? Pnr,
    IReadOnlyList<SupplierTicket> Tickets,
    string? Message = null);

/// <summary>Identifies an offer whose rules are wanted.</summary>
public sealed record SupplierRulesQuery(
    SupplierProductType ProductType,
    string SupplierSessionId,
    string OfferRef,
    SupplierOfferReference Reference);

/// <summary>Fare rules as the supplier formats them, and the penalties as JSON.</summary>
public sealed record SupplierFareRules(string? RulesHtml, string? PenaltiesJson);

/// <summary>A request to cancel a booking with the supplier.</summary>
public sealed record SupplierCancellationRequest(
    SupplierProductType ProductType,
    string ConfirmationCode,
    string? Pnr = null,
    string? PassengerSurname = null,
    string? Reason = null);

/// <summary>What the supplier said about a cancellation.</summary>
/// <param name="Penalty">What the supplier keeps, when it says.</param>
/// <param name="Refund">What the supplier returns, when it says.</param>
public sealed record SupplierCancellationResult(
    bool Cancelled,
    int? HttpStatusCode,
    int? SupplierStatusCode,
    string? SupplierCancelRef,
    TripsAgent.Domain.Common.Money? Penalty,
    TripsAgent.Domain.Common.Money? Refund,
    string? Message = null);

/// <summary>
/// Thrown by an adapter asked for something its supplier cannot do — cancelling a Trips Africa
/// flight, for instance, for which there is no documented endpoint.
/// </summary>
public sealed class SupplierOperationNotSupportedException : Exception
{
    public SupplierOperationNotSupportedException()
    {
    }

    public SupplierOperationNotSupportedException(string message)
        : base(message)
    {
    }

    public SupplierOperationNotSupportedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public SupplierOperationNotSupportedException(string supplierCode, SupplierOperation operation, SupplierProductType productType)
        : base($"Supplier '{supplierCode}' does not support {operation} for {productType}.")
    {
    }
}
