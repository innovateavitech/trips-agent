using System.Text.RegularExpressions;
using TripsAgent.Domain.Common;

namespace TripsAgent.Domain.Suppliers;

/// <summary>
/// One search an agency ran, whatever suppliers it fanned out to.
/// </summary>
/// <remarks>
/// Kept for two reasons: <see cref="CriteriaHash"/> is the search cache key (#40), and the rows
/// power the FRD's search-to-book conversion report.
/// </remarks>
public sealed partial class SearchRequest : Entity, ITenantScoped
{
    private SearchRequest()
    {
        CriteriaHash = string.Empty;
        Criteria = "{}";
    }

    /// <param name="criteriaHash">SHA-256 of the normalised criteria, as 64 lower-case hex characters.</param>
    /// <param name="criteriaJson">The criteria as searched, as a JSON object.</param>
    /// <param name="tripType">The trip shape in the supplier-neutral words the search used — one-way, round-trip, multi-city.</param>
    public static SearchRequest Start(
        Guid agencyId,
        Guid? userId,
        SupplierProductType productType,
        string criteriaHash,
        string criteriaJson,
        string? tripType,
        DateTimeOffset requestedAt)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(agencyId, Guid.Empty);
        ArgumentException.ThrowIfNullOrWhiteSpace(criteriaJson);

        if (criteriaHash is null || !Sha256Hex().IsMatch(criteriaHash))
        {
            throw new ArgumentException("The criteria hash must be a SHA-256 digest in lower-case hex.", nameof(criteriaHash));
        }

        return new SearchRequest
        {
            AgencyId = agencyId,
            UserId = userId,
            ProductType = productType,
            CriteriaHash = criteriaHash,
            Criteria = criteriaJson,
            TripType = tripType,
            RequestedAt = requestedAt,
        };
    }

    public Guid AgencyId { get; private set; }

    public Guid? UserId { get; private set; }

    public SupplierProductType ProductType { get; private set; }

    public string CriteriaHash { get; private set; }

    public string Criteria { get; private set; }

    public string? TripType { get; private set; }

    public int? ResultCount { get; private set; }

    public int? LatencyMs { get; private set; }

    /// <summary>Set when the search failed, so the error-rate report can count it.</summary>
    public string? ErrorCode { get; private set; }

    public DateTimeOffset RequestedAt { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    public void RecordCompleted(int resultCount, int latencyMs, DateTimeOffset at)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(resultCount);
        ArgumentOutOfRangeException.ThrowIfNegative(latencyMs);

        ResultCount = resultCount;
        LatencyMs = latencyMs;
        ErrorCode = null;
        CompletedAt = at;
    }

    public void RecordFailed(string errorCode, int latencyMs, DateTimeOffset at)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(errorCode);
        ArgumentOutOfRangeException.ThrowIfNegative(latencyMs);

        ErrorCode = errorCode;
        LatencyMs = latencyMs;
        CompletedAt = at;
    }

    [GeneratedRegex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Hex();
}

/// <summary>
/// The supplier's session for one search — Trips Africa's <c>SessionId</c>, which every later call
/// for an offer from that search must quote back.
/// </summary>
public sealed class SearchSession : Entity, ITenantScoped
{
    private SearchSession() => SupplierSessionId = string.Empty;

    public static SearchSession Open(
        Guid agencyId,
        Guid searchRequestId,
        Guid supplierId,
        string supplierSessionId,
        string? gdsSessionId,
        DateTimeOffset createdAt,
        DateTimeOffset? expiresAt)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(agencyId, Guid.Empty);
        ArgumentOutOfRangeException.ThrowIfEqual(searchRequestId, Guid.Empty);
        ArgumentOutOfRangeException.ThrowIfEqual(supplierId, Guid.Empty);
        ArgumentException.ThrowIfNullOrWhiteSpace(supplierSessionId);

        return new SearchSession
        {
            AgencyId = agencyId,
            SearchRequestId = searchRequestId,
            SupplierId = supplierId,
            SupplierSessionId = supplierSessionId,
            GdsSessionId = gdsSessionId,
            CreatedAt = createdAt,
            ExpiresAt = expiresAt,
        };
    }

    public Guid AgencyId { get; private set; }

    public Guid SearchRequestId { get; private set; }

    public Guid SupplierId { get; private set; }

    public string SupplierSessionId { get; private set; }

    public string? GdsSessionId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? ExpiresAt { get; private set; }
}

/// <summary>
/// The supplier's identifiers for one offer, which must reach price confirmation exactly as they
/// were received.
/// </summary>
/// <remarks>
/// Every field is optional. These are the pieces of Trips Africa's identity tuple; an aggregator
/// that identifies an offer by a single token uses <see cref="SupplierOffer.OfferRef"/> and leaves
/// these empty — which is why adding one needs no schema change.
/// </remarks>
public sealed record SupplierOfferReference(
    string? AgentId = null,
    string? GdsId = null,
    string? CombinationId = null,
    string? RecommendationId = null,
    int? FlightRouteIndex = null);

/// <summary>One priced option returned by a supplier search.</summary>
public sealed class SupplierOffer : Entity, ITenantScoped
{
    private SupplierOffer()
    {
        OfferRef = string.Empty;
        Currency = string.Empty;
        RawPayload = "{}";
    }

    /// <param name="offerRef">The supplier's own handle for the offer. Opaque; never parsed.</param>
    /// <param name="rawPayload">The offer exactly as the supplier sent it, as JSON, for replay and disputes.</param>
    public static SupplierOffer Record(
        Guid agencyId,
        Guid searchSessionId,
        Guid supplierId,
        SupplierProductType productType,
        string offerRef,
        SupplierOfferReference reference,
        string currency,
        Money baseFare,
        Money totalFare,
        string rawPayload,
        DateTimeOffset createdAt,
        DateTimeOffset? expiresAt)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(agencyId, Guid.Empty);
        ArgumentOutOfRangeException.ThrowIfEqual(searchSessionId, Guid.Empty);
        ArgumentOutOfRangeException.ThrowIfEqual(supplierId, Guid.Empty);
        ArgumentException.ThrowIfNullOrWhiteSpace(offerRef);
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentException.ThrowIfNullOrWhiteSpace(currency);
        ArgumentException.ThrowIfNullOrWhiteSpace(rawPayload);

        if (baseFare.IsNegative || totalFare.IsNegative)
        {
            throw new ArgumentOutOfRangeException(nameof(totalFare), "A fare cannot be negative.");
        }

        return new SupplierOffer
        {
            AgencyId = agencyId,
            SearchSessionId = searchSessionId,
            SupplierId = supplierId,
            ProductType = productType,
            OfferRef = offerRef,
            AgentIdExt = reference.AgentId,
            GdsIdExt = reference.GdsId,
            CombinationId = reference.CombinationId,
            RecommendationId = reference.RecommendationId,
            FlightRouteIndex = reference.FlightRouteIndex,
            Currency = currency.Trim().ToUpperInvariant(),
            BaseFareMinor = baseFare,
            TotalFareMinor = totalFare,
            RawPayload = rawPayload,
            CreatedAt = createdAt,
            ExpiresAt = expiresAt,
        };
    }

    public Guid AgencyId { get; private set; }

    public Guid SearchSessionId { get; private set; }

    public Guid SupplierId { get; private set; }

    public SupplierProductType ProductType { get; private set; }

    public string OfferRef { get; private set; }

    public string? AgentIdExt { get; private set; }

    public string? GdsIdExt { get; private set; }

    public string? CombinationId { get; private set; }

    public string? RecommendationId { get; private set; }

    public int? FlightRouteIndex { get; private set; }

    public string Currency { get; private set; }

    /// <summary>The supplier's net fare before taxes. Never shown to a traveller.</summary>
    public Money BaseFareMinor { get; private set; }

    /// <summary>What the supplier charges us for this offer — the net rate markup is applied to.</summary>
    public Money TotalFareMinor { get; private set; }

    public string RawPayload { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? ExpiresAt { get; private set; }

    /// <summary>The identity tuple, reassembled for the price-confirmation call.</summary>
    public SupplierOfferReference Reference =>
        new(AgentIdExt, GdsIdExt, CombinationId, RecommendationId, FlightRouteIndex);
}

/// <summary>One flight within an offer. Legs are the journey's directions; segments are the flights in a leg.</summary>
public sealed class FlightSegment : Entity, ITenantScoped
{
    private FlightSegment()
    {
        MarketingCarrier = string.Empty;
        FlightNumber = string.Empty;
        OriginIata = string.Empty;
        DestinationIata = string.Empty;
    }

    public static FlightSegment Create(
        Guid agencyId,
        Guid supplierOfferId,
        int legIndex,
        int segmentIndex,
        string marketingCarrier,
        string? operatingCarrier,
        string flightNumber,
        string originIata,
        string destinationIata,
        DateTimeOffset departureAt,
        DateTimeOffset arrivalAt,
        string? cabin = null,
        string? baggageAllowance = null,
        string? fareBasis = null)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(agencyId, Guid.Empty);
        ArgumentOutOfRangeException.ThrowIfEqual(supplierOfferId, Guid.Empty);
        ArgumentOutOfRangeException.ThrowIfNegative(legIndex);
        ArgumentOutOfRangeException.ThrowIfNegative(segmentIndex);
        ArgumentException.ThrowIfNullOrWhiteSpace(marketingCarrier);
        ArgumentException.ThrowIfNullOrWhiteSpace(flightNumber);

        if (arrivalAt < departureAt)
        {
            throw new ArgumentException("A flight cannot arrive before it departs.", nameof(arrivalAt));
        }

        return new FlightSegment
        {
            AgencyId = agencyId,
            SupplierOfferId = supplierOfferId,
            LegIndex = legIndex,
            SegmentIndex = segmentIndex,
            MarketingCarrier = marketingCarrier.Trim().ToUpperInvariant(),
            OperatingCarrier = operatingCarrier?.Trim().ToUpperInvariant(),
            FlightNumber = flightNumber.Trim(),
            OriginIata = Iata(originIata, nameof(originIata)),
            DestinationIata = Iata(destinationIata, nameof(destinationIata)),
            DepartureAt = departureAt,
            ArrivalAt = arrivalAt,
            Cabin = cabin,
            BaggageAllowance = baggageAllowance,
            FareBasis = fareBasis,
        };
    }

    public Guid AgencyId { get; private set; }

    public Guid SupplierOfferId { get; private set; }

    public int LegIndex { get; private set; }

    public int SegmentIndex { get; private set; }

    public string MarketingCarrier { get; private set; }

    public string? OperatingCarrier { get; private set; }

    public string FlightNumber { get; private set; }

    public string OriginIata { get; private set; }

    public string DestinationIata { get; private set; }

    public DateTimeOffset DepartureAt { get; private set; }

    public DateTimeOffset ArrivalAt { get; private set; }

    public string? Cabin { get; private set; }

    public string? BaggageAllowance { get; private set; }

    public string? FareBasis { get; private set; }

    private static string Iata(string code, string parameter)
    {
        var trimmed = code?.Trim().ToUpperInvariant() ?? string.Empty;

        if (trimmed.Length != 3 || !trimmed.All(char.IsAsciiLetterUpper))
        {
            throw new ArgumentException($"'{code}' is not a three-letter IATA airport code.", parameter);
        }

        return trimmed;
    }
}

/// <summary>One bus trip within an offer.</summary>
public sealed class BusSegment : Entity, ITenantScoped
{
    private BusSegment()
    {
        OperatorName = string.Empty;
        DepartureTerminalId = string.Empty;
        ArrivalTerminalId = string.Empty;
    }

    /// <param name="seatNumbersJson">Seats on offer, as a JSON array, exactly as the supplier listed them.</param>
    /// <param name="reservationIdExt">The supplier's reservation handle, when the search already carries one.</param>
    public static BusSegment Create(
        Guid agencyId,
        Guid supplierOfferId,
        string operatorName,
        string departureTerminalId,
        string arrivalTerminalId,
        DateTimeOffset departureAt,
        DateTimeOffset? arrivalAt = null,
        int? availableSeats = null,
        string? seatNumbersJson = null,
        string? reservationIdExt = null)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(agencyId, Guid.Empty);
        ArgumentOutOfRangeException.ThrowIfEqual(supplierOfferId, Guid.Empty);
        ArgumentException.ThrowIfNullOrWhiteSpace(operatorName);
        ArgumentException.ThrowIfNullOrWhiteSpace(departureTerminalId);
        ArgumentException.ThrowIfNullOrWhiteSpace(arrivalTerminalId);

        if (availableSeats is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(availableSeats), availableSeats, "Seats cannot be negative.");
        }

        return new BusSegment
        {
            AgencyId = agencyId,
            SupplierOfferId = supplierOfferId,
            OperatorName = operatorName.Trim(),
            DepartureTerminalId = departureTerminalId,
            ArrivalTerminalId = arrivalTerminalId,
            DepartureAt = departureAt,
            ArrivalAt = arrivalAt,
            AvailableSeats = availableSeats,
            SeatNumbers = seatNumbersJson,
            ReservationIdExt = reservationIdExt,
        };
    }

    public Guid AgencyId { get; private set; }

    public Guid SupplierOfferId { get; private set; }

    public string OperatorName { get; private set; }

    public string DepartureTerminalId { get; private set; }

    public string ArrivalTerminalId { get; private set; }

    public DateTimeOffset DepartureAt { get; private set; }

    public DateTimeOffset? ArrivalAt { get; private set; }

    public int? AvailableSeats { get; private set; }

    /// <summary>JSON array.</summary>
    public string? SeatNumbers { get; private set; }

    public string? ReservationIdExt { get; private set; }
}

/// <summary>
/// The fare rules and penalties for an offer — which the FRD requires the agent to see before
/// choosing it.
/// </summary>
public sealed class SupplierFareRule : Entity, ITenantScoped
{
    private SupplierFareRule()
    {
    }

    /// <param name="rulesHtml">The rules text as the supplier formats it. Rendered sanitised, never raw.</param>
    /// <param name="penaltiesJson">Change and cancellation penalties, as JSON.</param>
    public static SupplierFareRule Record(
        Guid agencyId,
        Guid supplierOfferId,
        string? rulesHtml,
        string? penaltiesJson,
        DateTimeOffset fetchedAt)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(agencyId, Guid.Empty);
        ArgumentOutOfRangeException.ThrowIfEqual(supplierOfferId, Guid.Empty);

        return new SupplierFareRule
        {
            AgencyId = agencyId,
            SupplierOfferId = supplierOfferId,
            RulesHtml = rulesHtml,
            Penalties = penaltiesJson,
            FetchedAt = fetchedAt,
        };
    }

    public Guid AgencyId { get; private set; }

    public Guid SupplierOfferId { get; private set; }

    /// <summary>Set once a booking is made from the offer, so the rules it was sold under stay attached to it.</summary>
    public Guid? SupplierBookingId { get; private set; }

    public string? RulesHtml { get; private set; }

    /// <summary>JSON.</summary>
    public string? Penalties { get; private set; }

    public DateTimeOffset FetchedAt { get; private set; }

    public void AttachToBooking(Guid supplierBookingId)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(supplierBookingId, Guid.Empty);
        SupplierBookingId = supplierBookingId;
    }
}
