using System.Collections.Frozen;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using TripsAgent.Application.Suppliers;
using TripsAgent.Domain.Common;
using TripsAgent.Domain.Suppliers;
using TripsAgent.Integrations.TripsAfrica.Wire;

namespace TripsAgent.Integrations.TripsAfrica;

/// <summary>
/// Trips Africa's words to ours and back. Pure functions — no HTTP, no clock, no database — so every
/// rule here is tested against the documented payload shapes without a network.
/// </summary>
/// <remarks>
/// <para>
/// <b>Money is kobo before it gets here.</b> The supplier sends decimal naira (<c>844519.0</c>);
/// <see cref="NairaAsKoboConverter"/> reads each amount straight into kobo as the JSON is parsed, so
/// no decimal amount exists anywhere in this code (CLAUDE.md rule 2). An amount that is not a whole
/// number of kobo, or is negative, reads as missing rather than being rounded into something we
/// would then charge — and an offer with no readable total is dropped and counted.
/// </para>
/// <para>
/// <b>An offer we cannot read completely is not one we can sell.</b> A flight with no carrier, an
/// airport that is not three letters, an arrival before its departure — the whole offer is dropped
/// rather than shown with a hole in it, and the adapter logs how many went.
/// </para>
/// </remarks>
internal static partial class TripsAfricaMapping
{
    /// <summary>PascalCase out, as documented; case-insensitive in, because their own samples are not consistent.</summary>
    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = null,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    /// <summary>Lagos time, all year: Nigeria keeps no daylight saving.</summary>
    internal static readonly TimeSpan NigeriaOffset = TimeSpan.FromHours(1);

    /// <summary>
    /// Nigerian airports, which decide the domestic endpoint and the time zone of a wall-clock time.
    /// </summary>
    /// <remarks>
    /// The supplier has two flight search endpoints and says nothing about which serves what beyond
    /// their names, so a search is domestic when every airport in it is on this list. A new airport
    /// is one line here; until it is added, its flights are searched internationally.
    /// </remarks>
    internal static readonly FrozenSet<string> NigerianAirports = new[]
    {
        "ABB", "ABV", "AKR", "BCU", "BNI", "CBQ", "DKA", "ENU", "GMO", "IBA", "ILR", "JOS", "KAD",
        "KAN", "LOS", "MDI", "MIU", "MXJ", "PHC", "QOW", "QRW", "QUO", "SKO", "YOL",
    }.ToFrozenSet(StringComparer.Ordinal);

    // ------------------------------------------------------------------------------ flight search

    internal static bool IsDomestic(SupplierSearchQuery query) =>
        query.Legs.All(leg => NigerianAirports.Contains(Upper(leg.Origin)) && NigerianAirports.Contains(Upper(leg.Destination)));

    internal static FlightSearchRequestWire ToFlightSearchRequest(SupplierSearchQuery query, int pageSize)
    {
        ArgumentNullException.ThrowIfNull(query);

        return new FlightSearchRequestWire(
            FlightRoutes: query.Legs
                .Select(leg => new FlightRouteWire(IsoDate(leg.DepartureDate), Upper(leg.Origin), Upper(leg.Destination)))
                .ToList(),
            FlightPassengers: Passengers(query.Passengers),
            FlightClasses: [new FlightClassWire(CabinName(query.Cabin))],
            Currency: "NGN",
            // The documented return and multi-city samples set this; the one-way sample does not.
            EnsureAgentUnicityForRoutes: query.TripShape != SupplierTripShape.OneWay,
            OrderbyFlightTime: "asc",
            OrderbyPrice: "asc",
            PageSize: pageSize,
            From: (Math.Max(query.Page, 1) - 1) * pageSize);
    }

    /// <summary>
    /// Our cabin words to theirs. The documentation only ever shows "Economy"; the others follow the
    /// same capitalisation and are passed through as given when unrecognised.
    /// </summary>
    internal static string CabinName(string? cabin) => cabin?.Trim().ToUpperInvariant() switch
    {
        null or "" or "ECONOMY" => "Economy",
        "PREMIUM_ECONOMY" or "PREMIUMECONOMY" or "PREMIUM ECONOMY" => "PremiumEconomy",
        "BUSINESS" => "Business",
        "FIRST" => "First",
        _ => cabin.Trim(),
    };

    /// <summary>The whole answer to a flight search, mapped. <paramref name="dropped"/> counts offers that could not be read.</summary>
    internal static SupplierSearchResult MapFlightSearch(string responseJson, out int dropped)
    {
        using var document = JsonDocument.Parse(responseJson);
        var root = document.RootElement;
        var response = root.Deserialize<FlightSearchResponseWire>(Json)
                       ?? throw new JsonException("Trips Africa answered a flight search with an empty body.");

        var results = response.ResultList ?? [];

        // The raw text of each result, by position, for SupplierOffer.RawPayload — replay and disputes.
        // Taken from the document rather than re-serialised, so fields we do not model survive.
        var raw = Property(root, "ResultList") is { ValueKind: JsonValueKind.Array } list
            ? list.EnumerateArray().Select(element => element.GetRawText()).ToList()
            : [];

        var offers = new List<SupplierOfferQuote>(results.Count);
        dropped = 0;

        for (var index = 0; index < results.Count; index++)
        {
            if (MapFlightOffer(results[index], index < raw.Count ? raw[index] : "{}") is { } offer)
            {
                offers.Add(offer);
            }
            else
            {
                dropped++;
            }
        }

        var sessionId = response.SessionId
                        ?? results.Select(result => result.Properties?.TripsSessionId).FirstOrDefault(id => !string.IsNullOrEmpty(id));

        if (offers.Count > 0 && string.IsNullOrEmpty(sessionId))
        {
            throw new JsonException("Trips Africa's search answer carried no SessionId, so none of its offers could ever be confirmed.");
        }

        var gdsSessionId = results.Select(result => result.Properties?.GdsSessionId).FirstOrDefault(id => !string.IsNullOrEmpty(id));

        return new SupplierSearchResult(sessionId ?? string.Empty, gdsSessionId, ExpiresAt: null, offers);
    }

    private static SupplierOfferQuote? MapFlightOffer(FlightResultWire result, string rawPayload)
    {
        if (result.TotalFareMinor is not { } totalMinor)
        {
            return null;
        }

        var total = new Money(totalMinor);

        // Domestic results carry no base fare; the total stands in rather than an invented split.
        var baseFare = result.BaseFareMinor is { } baseMinor ? new Money(baseMinor) : total;

        var segments = new List<SupplierFlightSegmentQuote>();
        var details = result.FlightDetails ?? [];

        // Legs are told apart only by position: FlightDetails[i] is route i. A domestic result prices a
        // single route and says which with FlightRouteIndex, so its leg is that index, not zero.
        var firstLeg = result.FlightRouteIndex ?? 0;

        for (var leg = 0; leg < details.Count; leg++)
        {
            var entries = details[leg].FlightEntries ?? [];

            for (var position = 0; position < entries.Count; position++)
            {
                if (MapSegment(entries[position], firstLeg + leg, position) is not { } segment)
                {
                    return null;
                }

                segments.Add(segment);
            }
        }

        if (segments.Count == 0)
        {
            return null;
        }

        var reference = new SupplierOfferReference(
            AgentId: Invariant(result.AgentId),
            GdsId: Invariant(result.GdsId),
            CombinationId: Invariant(result.Properties?.CombinationID),
            RecommendationId: Invariant(result.Properties?.RecommendationID),
            FlightRouteIndex: result.FlightRouteIndex);

        return new SupplierOfferQuote(
            OfferRef(reference),
            reference,
            CurrencyOf(result.Currency),
            baseFare,
            total,
            segments,
            BusSegments: [],
            rawPayload,
            ExpiresAt: null);
    }

    private static SupplierFlightSegmentQuote? MapSegment(FlightEntryWire entry, int legIndex, int segmentIndex)
    {
        var carrier = entry.MarketingAirlineCode?.Trim().ToUpperInvariant();
        var number = entry.FlightNumber?.Trim();

        if (string.IsNullOrEmpty(carrier) || carrier.Length > 3 || string.IsNullOrEmpty(number)
            || !IsIata(entry.DepartureAirportCode) || !IsIata(entry.ArrivalAirportCode))
        {
            return null;
        }

        var origin = Upper(entry.DepartureAirportCode!);
        var destination = Upper(entry.ArrivalAirportCode!);

        if (!TryAirportTime(entry.DepartureDate, origin, out var departs)
            || !TryAirportTime(entry.ArrivalDate, destination, out var arrives)
            || arrives < departs)
        {
            return null;
        }

        // "AT 554", as a ticket prints it. flight_segments.flight_number is ten wide.
        var flightNumber = $"{carrier} {number}";
        if (flightNumber.Length > 10)
        {
            return null;
        }

        var operating = entry.OperatingAirlineCode?.Trim().ToUpperInvariant();

        return new SupplierFlightSegmentQuote(
            legIndex,
            segmentIndex,
            carrier,
            operating is { Length: > 0 and <= 3 } && operating != carrier ? operating : null,
            flightNumber,
            origin,
            destination,
            departs,
            arrives,
            Cabin: Clip(entry.FlightClass, 30),
            BaggageAllowance: Baggage(entry),
            FareBasis: Clip(
                entry.AvailablePassengerSeats?.FirstOrDefault(seat => seat.PassengerType == "ADT")?.FareBasis,
                30),
            MarketingCarrierName: Clip(entry.MarketingAirlineName, 100),
            DurationMinutes: ParseDuration(entry.FlightDuration));
    }

    /// <summary>"2" + "PC" → "2 pieces". Domestic results send "KGS" with no amount, which says nothing.</summary>
    private static string? Baggage(FlightEntryWire entry)
    {
        var amount = entry.Baggages?.Trim();

        if (string.IsNullOrEmpty(amount) || !amount.All(char.IsAsciiDigit))
        {
            return null;
        }

        var unit = entry.BaggageUnit?.Trim().ToUpperInvariant() switch
        {
            null or "" => null,
            "PC" => amount == "1" ? "piece" : "pieces",
            "KG" or "KGS" => "kg",
            var other => other.ToLowerInvariant(),
        };

        return Clip(unit is null ? amount : $"{amount} {unit}", 100);
    }

    // ------------------------------------------------------------------------- price confirmation

    /// <summary>The domestic endpoint, and a list of selected flights, when the offer names its route.</summary>
    internal static bool IsDomesticOffer(SupplierOfferReference reference) => reference.FlightRouteIndex is not null;

    internal static string ToFlightConfirmBody(SupplierPriceConfirmationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var travellers = request.Passengers.Select(ToTraveller).ToList();
        var billing = Billing(request.Passengers);
        var reference = request.Reference;

        if (!IsDomesticOffer(reference))
        {
            return JsonSerializer.Serialize(
                new InternationalConfirmRequestWire(
                    ParseId(reference.CombinationId),
                    ParseId(reference.RecommendationId),
                    FlightRouteIndex: 0,
                    ParseId(reference.AgentId),
                    ParseId(reference.GdsId),
                    request.SupplierSessionId,
                    billing,
                    travellers),
                Json);
        }

        // A domestic return is two offers, one per route, confirmed in one call — which is why the
        // answer is an array. The other route's offer arrives as AdditionalRoutes.
        var selected = new[] { reference }
            .Concat(request.AdditionalRoutes ?? [])
            .OrderBy(route => route.FlightRouteIndex)
            .Select(route => new SelectedFlightWire(
                ParseId(route.RecommendationId),
                ParseId(route.CombinationId),
                ParseId(route.GdsId),
                ParseId(route.AgentId),
                route.FlightRouteIndex ?? 0))
            .ToList();

        return JsonSerializer.Serialize(
            new DomesticConfirmRequestWire(selected, request.SupplierSessionId, billing, travellers),
            Json);
    }

    /// <summary>
    /// Every element of a confirmation, each with the hash we expect beside the hash they sent.
    /// </summary>
    /// <remarks>
    /// Comparing them is <c>SupplierBooking.RecordPriceConfirmation</c>'s job, one element at a time.
    /// Here, an element that cannot be verified at all — no confirmation code, no whole price, or a
    /// decimal price that disagrees with the whole one — gets an expected hash nobody can produce, so
    /// it fails that comparison rather than being quietly waved through.
    /// </remarks>
    internal static IReadOnlyList<PriceConfirmationLine> MapConfirmations(string responseJson, string merchantKey)
    {
        ArgumentException.ThrowIfNullOrEmpty(merchantKey);

        using var document = JsonDocument.Parse(responseJson);
        var root = document.RootElement;

        var elements = root.ValueKind switch
        {
            JsonValueKind.Array => root.EnumerateArray().ToList(),
            JsonValueKind.Object => [root],
            _ => throw new JsonException($"Trips Africa answered a price confirmation with {root.ValueKind}, not an object or array."),
        };

        return elements
            .Select(element => MapConfirmation(
                element.Deserialize<ConfirmationWire>(Json) ?? new ConfirmationWire(),
                merchantKey))
            .ToList();
    }

    private static PriceConfirmationLine MapConfirmation(ConfirmationWire wire, string merchantKey)
    {
        var code = wire.ConfirmationCode?.Trim() ?? string.Empty;
        var newPrice = PriceOf(wire.NewPriceMinor, wire.NewPriceWhole) ?? Money.Zero;
        var oldPrice = PriceOf(wire.OldPriceMinor, wire.OldPriceWhole) ?? newPrice;

        return new PriceConfirmationLine(
            code,
            oldPrice,
            newPrice,
            // Sent as Lagos time (+01:00). A deadline is an instant, and the database refuses any
            // instant that is not UTC — so it becomes UTC here, before the booking ever holds it.
            wire.TicketTimeLimit?.ToUniversalTime(),
            HashExpected: ExpectedHash(wire, code, merchantKey),
            HashReceived: wire.Hash?.Trim() ?? string.Empty);
    }

    private static string ExpectedHash(ConfirmationWire wire, string code, string merchantKey)
    {
        if (code.Length == 0 || wire.NewPriceWhole is not { } whole || whole < 0)
        {
            return Unforgeable();
        }

        // The hash covers NewPriceWhole only. The decimal NewPrice is the figure we record, so it has
        // to agree with the whole one — or a tampered decimal would ride on an honest hash.
        if (wire.NewPriceMinor is { } newPriceMinor && newPriceMinor / 100 != whole)
        {
            return Unforgeable();
        }

        return ConfirmationHash.Compute(merchantKey, code, whole);
    }

    /// <summary>
    /// An expected hash that can never be matched: random, so a response cannot echo it back. A fixed
    /// sentinel would be exactly the value a hostile response would learn to send.
    /// </summary>
    private static string Unforgeable() => $"unverifiable-{Guid.NewGuid():N}";

    private static AirTravellerWire ToTraveller(SupplierPassenger passenger) =>
        new(
            PassengerTypeCode: PassengerCode(passenger.Type),
            passenger.FirstName,
            passenger.LastName,
            passenger.MiddleName,
            NamePrefix: passenger.Title,
            passenger.Gender,
            BirthDate: passenger.BirthDate is { } born ? IsoDate(born) : null,
            Email: passenger.Email is null ? null : [passenger.Email],
            Documents: passenger.Document is null ? [] : [ToDocument(passenger.Document)]);

    private static TravelDocumentWire ToDocument(SupplierTravelDocument document) =>
        new(
            DocType: document.Kind == TravelDocumentKind.Visa ? "DOCO" : "DOCS",
            DocID: document.Number,
            InnerDocType: document.Kind switch
            {
                TravelDocumentKind.Passport => "PASSPORT",
                TravelDocumentKind.Visa => "VISA",
                _ => "NATIONALID",
            },
            IssueCountryCode: document.IssuingCountry,
            IssueLocation: document.IssuingCountry,
            BirthCountryCode: document.NationalityCountry,
            EffectiveDate: document.IssuedOn is { } issued ? IsoDate(issued) : null,
            ExpiryDate: document.ExpiresOn is { } expires ? IsoDate(expires) : null);

    /// <summary>
    /// The supplier wants a billing contact; the port has none. The lead adult is who the supplier
    /// would call about the booking, so they stand in.
    /// </summary>
    private static BillingAddressWire Billing(IReadOnlyList<SupplierPassenger> passengers)
    {
        var lead = passengers.FirstOrDefault(passenger => passenger.Type == PassengerType.Adult)
                   ?? (passengers.Count > 0 ? passengers[0] : null)
                   ?? throw new ArgumentException("A price confirmation needs at least one passenger.", nameof(passengers));

        return new BillingAddressWire(
            $"{lead.FirstName} {lead.LastName}",
            lead.Email is null ? [] : [lead.Email],
            lead.PhoneNumber ?? string.Empty,
            AddressLine1: string.Empty,
            City: string.Empty,
            CountryCode: "NG");
    }

    // ------------------------------------------------------------------------------------- shared

    /// <summary>The supplier's price in kobo, or its whole-naira figure when the decimal one would not read.</summary>
    private static Money? PriceOf(long? minor, long? whole)
    {
        if (minor is { } exact)
        {
            return new Money(exact);
        }

        return whole is { } naira && naira >= 0 ? Money.FromMajor(naira) : null;
    }

    /// <summary>
    /// A supplier wall-clock time as an instant. A time that carries an offset keeps it; one that does
    /// not — which is all of Trips Africa's — gets Lagos time at a Nigerian airport and UTC elsewhere.
    /// </summary>
    /// <remarks>
    /// The supplier sends no offset and no time zone, and we hold no airport-to-zone table. The
    /// console shows these times as the wall clock they arrived as, which is what a ticket prints;
    /// only the instant of a foreign wall-clock time is approximate.
    /// </remarks>
    internal static bool TryAirportTime(string? value, string airport, out DateTimeOffset at)
    {
        at = default;

        if (string.IsNullOrWhiteSpace(value)
            || !DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
        {
            return false;
        }

        at = parsed.Kind == DateTimeKind.Unspecified
            ? new DateTimeOffset(parsed, NigerianAirports.Contains(airport) ? NigeriaOffset : TimeSpan.Zero)
            : new DateTimeOffset(parsed.ToUniversalTime());

        return true;
    }

    /// <summary>"4h:30m" → 270. Null for anything else, rather than a guess.</summary>
    internal static int? ParseDuration(string? value)
    {
        var match = DurationPattern().Match(value ?? string.Empty);

        if (!match.Success)
        {
            return null;
        }

        var hours = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        var minutes = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);

        return minutes < 60 ? (hours * 60) + minutes : null;
    }

    [GeneratedRegex(@"^\s*(\d{1,3})h\s*:?\s*(\d{1,2})m\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex DurationPattern();

    /// <summary>A bus time: always Lagos, because every terminal is in Nigeria.</summary>
    internal static bool TryLagosTime(string? value, out DateTimeOffset at) => TryAirportTime(value, "LOS", out at);

    internal static IReadOnlyList<PassengerCountWire> Passengers(SupplierPassengerCounts counts) =>
    [
        new("ADT", counts.Adults),
        new("CHD", counts.Children),
        new("INF", counts.Infants),
    ];

    internal static string PassengerCode(PassengerType type) => type switch
    {
        PassengerType.Child => "CHD",
        PassengerType.Infant => "INF",
        _ => "ADT",
    };

    /// <summary>The identity tuple as one opaque string, because Trips Africa has no single handle for an offer.</summary>
    internal static string OfferRef(SupplierOfferReference reference) =>
        string.Join(
            ':',
            reference.AgentId ?? "-",
            reference.GdsId ?? "-",
            reference.CombinationId ?? "-",
            reference.RecommendationId ?? "-",
            reference.FlightRouteIndex?.ToString(CultureInfo.InvariantCulture) ?? "-");

    internal static long? ParseId(string? value) =>
        long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ? id : null;

    internal static string? Invariant(long? value) => value?.ToString(CultureInfo.InvariantCulture);

    internal static string IsoDate(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    internal static string CurrencyOf(string? currency) =>
        string.IsNullOrWhiteSpace(currency) ? "NGN" : currency.Trim().ToUpperInvariant();

    internal static string? Clip(string? value, int length)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed.Length <= length ? trimmed : trimmed[..length];
    }

    private static string Upper(string value) => value.Trim().ToUpperInvariant();

    private static bool IsIata(string? code) =>
        code is not null && code.Trim() is { Length: 3 } trimmed && trimmed.All(char.IsAsciiLetter);

    /// <summary>A property by name, ignoring case — the raw-document twin of <see cref="Json"/>'s case-insensitivity.</summary>
    internal static JsonElement? Property(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return property.Value;
            }
        }

        return null;
    }
}
