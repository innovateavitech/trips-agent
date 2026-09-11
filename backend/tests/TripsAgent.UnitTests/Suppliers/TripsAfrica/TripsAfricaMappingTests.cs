using System.Text.Json;
using FluentAssertions;
using TripsAgent.Application.Suppliers;
using TripsAgent.Domain.Common;
using TripsAgent.Domain.Suppliers;
using TripsAgent.Integrations.TripsAfrica;
using TripsAgent.Integrations.TripsAfrica.Wire;

namespace TripsAgent.UnitTests.Suppliers.TripsAfrica;

/// <summary>
/// Trips Africa's payloads, in the shapes its documentation shows, read into ours (#33, #34, #35).
/// </summary>
/// <remarks>
/// The payloads are written here in the documented shape with our own values rather than pasted from
/// the documentation: distinctive figures make each assertion unambiguous, and the documentation's
/// hash samples cannot be checked anyway without the merchant key that produced them. The expected
/// hashes below were computed with <c>shasum -a 512</c>, independently of .NET, so these tests are not
/// the code agreeing with itself.
/// </remarks>
public class TripsAfricaMappingTests
{
    private const string TestKey = "test-merchant-key";

    // shasum -a 512 of "test-merchant-key*{code}*{whole}".
    private const string Code1 = "37656|6C6E44D1AGEJ";
    private const string Hash1 = "0a9240cd2040b3dc7b41f04c8d4d7c33f6034ec68a956a4f2287ef70498ed81ac1c16e26b1ff288714c4091acecc560e846caf43204cf8327d901c7c5a2d865f";
    private const string Code2 = "37657|7D7F55E2BHFK";
    private const string Hash2 = "95141cc5134577cd9c2f4e0cf60fc33c6e9a33f1947c1e0a1bfabce9e45aea7fcb235e8b45ee638011cdf24b4821b8bf42a406487a868db71fc380450f160943";
    private const string Code3 = "37658|8E8A66F3CIGL";
    private const string Hash3 = "277d0c623097e83c9f2382f5d30fa08b869babf97b5ffdf59504c20c69c23d469438664e030e44234cbe7daaf21df6c93a04da1ca1f48920e522e0dcd822d4c9";

    private static readonly TimeSpan Lagos = TimeSpan.FromHours(1);

    // ------------------------------------------------------------------------------ money

    [Theory]
    [InlineData("844519.0", 84_451_900L)]
    [InlineData("23937.5", 2_393_750L)]
    [InlineData("0.01", 1L)]
    [InlineData("0", 0L)]
    public void A_naira_amount_reads_as_exact_kobo(string naira, long kobo)
    {
        NairaAsKoboConverter.ToKobo(decimal.Parse(naira, System.Globalization.CultureInfo.InvariantCulture))
            .Should().Be(kobo);
    }

    [Theory]
    [InlineData("0.005")]
    [InlineData("-1.00")]
    public void An_amount_that_is_not_whole_kobo_or_is_negative_reads_as_missing_never_rounded(string naira)
    {
        NairaAsKoboConverter.ToKobo(decimal.Parse(naira, System.Globalization.CultureInfo.InvariantCulture))
            .Should().BeNull("rounding a supplier's price turns it into a price they never gave");
    }

    [Theory]
    [InlineData("4h:30m", 270)]
    [InlineData("10h:05m", 605)]
    [InlineData("0h:59m", 59)]
    [InlineData(" 1h:00m ", 60)]
    public void A_supplier_duration_reads_as_minutes(string duration, int minutes)
    {
        TripsAfricaMapping.ParseDuration(duration).Should().Be(minutes);
    }

    [Theory]
    [InlineData("1h:75m")]
    [InlineData("soon")]
    [InlineData("")]
    public void A_duration_that_does_not_read_cleanly_is_null_rather_than_a_guess(string duration)
    {
        TripsAfricaMapping.ParseDuration(duration).Should().BeNull();
    }

    // ------------------------------------------------------------------------- flight search

    [Fact]
    public void A_fare_arrives_in_kobo_with_its_base_and_total()
    {
        var offer = MapInternational().Offers.Should().ContainSingle().Subject;

        offer.TotalFare.Should().Be(new Money(84_451_950));
        offer.BaseFare.Should().Be(new Money(58_500_800));
        offer.Currency.Should().Be("NGN");
    }

    [Fact]
    public void The_identity_tuple_is_kept_exactly_for_price_confirmation()
    {
        var result = MapInternational();
        var offer = result.Offers.Should().ContainSingle().Subject;

        offer.Reference.Should().Be(new SupplierOfferReference(
            AgentId: "5", GdsId: "2", CombinationId: "0", RecommendationId: "7", FlightRouteIndex: null));
        offer.OfferRef.Should().Be("5:2:0:7:-");
        result.SupplierSessionId.Should().Be("sess-intl-0001");
        result.GdsSessionId.Should().Be("gds-0001");
    }

    [Fact]
    public void Each_flight_in_a_connection_is_a_segment_on_the_same_leg()
    {
        var offer = MapInternational().Offers.Should().ContainSingle().Subject;

        offer.FlightSegments.Should().HaveCount(2);

        var first = offer.FlightSegments[0];
        first.LegIndex.Should().Be(0);
        first.SegmentIndex.Should().Be(0);
        first.FlightNumber.Should().Be("AT 554");
        first.MarketingCarrier.Should().Be("AT");
        first.OperatingCarrier.Should().BeNull("it is the marketing carrier, so saying it twice adds nothing");
        first.OriginIata.Should().Be("LOS");
        first.DestinationIata.Should().Be("CMN");
        first.BaggageAllowance.Should().Be("2 pieces");
        first.FareBasis.Should().Be("KA0WAAFA");
        first.MarketingCarrierName.Should().Be("Royal Air Maroc");
        first.DurationMinutes.Should().Be(270, "the supplier's own flying time, not a difference of two local times");

        offer.FlightSegments[1].SegmentIndex.Should().Be(1);
        offer.FlightSegments[1].DestinationIata.Should().Be("LHR");
    }

    [Fact]
    public void An_offer_that_cannot_be_read_in_full_is_dropped_and_counted_not_shown_with_a_hole()
    {
        var result = TripsAfricaMapping.MapFlightSearch(InternationalSearch, out var dropped);

        result.Offers.Should().ContainSingle();
        dropped.Should().Be(2, "one fare was not whole kobo and one flight had no carrier");
    }

    [Fact]
    public void The_raw_payload_is_the_supplier_s_own_text_for_disputes()
    {
        var offer = MapInternational().Offers.Should().ContainSingle().Subject;

        using var raw = JsonDocument.Parse(offer.RawPayload);
        raw.RootElement.GetProperty("Properties").GetProperty("RecommendationID").GetInt32().Should().Be(7);
        raw.RootElement.GetProperty("IsLocal").GetBoolean().Should().BeFalse("fields we do not model survive too");
    }

    [Fact]
    public void A_return_is_two_legs_told_apart_only_by_position()
    {
        var offer = TripsAfricaMapping.MapFlightSearch(ReturnSearch, out _).Offers.Should().ContainSingle().Subject;

        offer.FlightSegments.Select(segment => (segment.LegIndex, segment.OriginIata))
            .Should().Equal((0, "LOS"), (1, "LHR"));
    }

    [Fact]
    public void A_domestic_result_is_on_the_leg_its_route_index_names_and_carries_it_for_confirmation()
    {
        var offer = TripsAfricaMapping.MapFlightSearch(DomesticSearch, out _).Offers.Should().ContainSingle().Subject;

        offer.FlightSegments.Should().ContainSingle().Which.LegIndex.Should().Be(1);
        offer.Reference.FlightRouteIndex.Should().Be(1);
        offer.TotalFare.Should().Be(new Money(4_621_750));
        offer.BaseFare.Should().Be(offer.TotalFare, "a domestic result has no base fare, and the total stands in");
    }

    [Fact]
    public void A_nigerian_wall_clock_time_is_lagos_time()
    {
        var segment = TripsAfricaMapping.MapFlightSearch(DomesticSearch, out _).Offers[0].FlightSegments[0];

        segment.DepartureAt.Should().Be(new DateTimeOffset(2026, 10, 5, 7, 20, 0, Lagos));
        segment.ArrivalAt.Should().Be(new DateTimeOffset(2026, 10, 5, 8, 40, 0, Lagos));
        segment.BaggageAllowance.Should().BeNull("\"KGS\" with no amount says nothing about the allowance");
    }

    [Fact]
    public void A_one_way_search_is_sent_in_the_documented_shape()
    {
        var query = new SupplierSearchQuery(
            SupplierProductType.Flight,
            SupplierTripShape.OneWay,
            [new SupplierSearchLeg("los", "lhr", new DateOnly(2026, 10, 2))],
            new SupplierPassengerCounts(Adults: 2, Children: 1, Infants: 1));

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(
            TripsAfricaMapping.ToFlightSearchRequest(query, pageSize: 50), TripsAfricaMapping.Json));
        var root = json.RootElement;

        var route = root.GetProperty("FlightRoutes")[0];
        route.GetProperty("DepartureDate").GetString().Should().Be("2026-10-02");
        route.GetProperty("OriginLocationCode").GetString().Should().Be("LOS");
        route.GetProperty("DestinationLocationCode").GetString().Should().Be("LHR");

        root.GetProperty("FlightPassengers").EnumerateArray()
            .Select(passenger => (passenger.GetProperty("Code").GetString(), passenger.GetProperty("Quantity").GetInt32()))
            .Should().Equal(("ADT", 2), ("CHD", 1), ("INF", 1));

        root.GetProperty("FlightClasses")[0].GetProperty("Name").GetString().Should().Be("Economy");
        root.GetProperty("PageSize").GetInt32().Should().Be(50);
        root.GetProperty("From").GetInt32().Should().Be(0);
        root.GetProperty("EnsureAgentUnicityForRoutes").GetBoolean().Should().BeFalse();
    }

    [Theory]
    [InlineData("LOS", "ABV", true)]
    [InlineData("PHC", "KAN", true)]
    [InlineData("LOS", "LHR", false)]
    [InlineData("ACC", "LOS", false)]
    public void A_search_is_domestic_when_every_airport_is_nigerian(string origin, string destination, bool domestic)
    {
        var query = new SupplierSearchQuery(
            SupplierProductType.Flight,
            SupplierTripShape.OneWay,
            [new SupplierSearchLeg(origin, destination, new DateOnly(2026, 10, 2))],
            new SupplierPassengerCounts(1));

        TripsAfricaMapping.IsDomestic(query).Should().Be(domestic);
    }

    // ---------------------------------------------------------------------------- bus search

    [Fact]
    public void A_bus_search_sends_the_supplier_s_numeric_terminal_ids()
    {
        var query = new SupplierSearchQuery(
            SupplierProductType.Bus,
            SupplierTripShape.OneWay,
            [new SupplierSearchLeg("60", "51", new DateOnly(2026, 10, 3))],
            new SupplierPassengerCounts(2));

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(
            TripsAfricaBusMapping.ToBusSearchRequest(query), TripsAfricaMapping.Json));
        var route = json.RootElement.GetProperty("Parameter").GetProperty("TravelRoute");

        route.GetProperty("DepartureId").GetInt64().Should().Be(60);
        route.GetProperty("ArrivalId").GetInt64().Should().Be(51);
        route.GetProperty("DepartureDate").GetString().Should().Be("2026-10-03");
        json.RootElement.GetProperty("Parameter").GetProperty("IsRoundTrip").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public void A_bus_return_is_refused_rather_than_sent_with_a_guessed_field()
    {
        var query = new SupplierSearchQuery(
            SupplierProductType.Bus,
            SupplierTripShape.RoundTrip,
            [
                new SupplierSearchLeg("60", "51", new DateOnly(2026, 10, 3)),
                new SupplierSearchLeg("51", "60", new DateOnly(2026, 10, 6)),
            ],
            new SupplierPassengerCounts(1));

        var act = () => TripsAfricaBusMapping.ToBusSearchRequest(query);

        act.Should().Throw<ArgumentException>().WithMessage("*one-way bus search only*");
    }

    [Fact]
    public void Each_bus_is_its_own_offer_with_its_seats_and_reservation()
    {
        var result = TripsAfricaBusMapping.MapBusSearch(BusSearch, out var dropped);

        dropped.Should().Be(1, "the second bus has no fare");
        result.SupplierSessionId.Should().Be("sess-bus-0001", "the Trips session is what confirmation quotes, not the inner one");

        var offer = result.Offers.Should().ContainSingle().Subject;
        offer.TotalFare.Should().Be(new Money(2_167_800));
        offer.BaseFare.Should().Be(new Money(2_100_000));
        offer.Reference.Should().Be(new SupplierOfferReference("169", "103", "1", "0", 0));

        var segment = offer.BusSegments.Should().ContainSingle().Subject;
        segment.OperatorName.Should().Be("LIBRA Motors");
        segment.DepartureTerminalId.Should().Be("60");
        segment.ArrivalTerminalId.Should().Be("51");
        segment.DepartureAt.Should().Be(new DateTimeOffset(2026, 10, 3, 6, 0, 0, Lagos));
        segment.ArrivalAt.Should().Be(new DateTimeOffset(2026, 10, 3, 13, 0, 0, Lagos));
        segment.AvailableSeats.Should().Be(2);
        segment.SeatNumbers.Should().Equal("1", "11");
        segment.ReservationIdExt.Should().Be("res-0001");
        segment.VehicleType.Should().Be("Hiace");
    }

    // -------------------------------------------------------------------- price confirmation

    [Fact]
    public void The_confirmation_hash_matches_an_independently_computed_vector()
    {
        ConfirmationHash.Compute(TestKey, Code1, 21678).Should().Be(Hash1);
    }

    [Fact]
    public void The_bus_bearer_token_matches_an_independently_computed_vector()
    {
        ConfirmationHash.BusBearerToken(TestKey, "TESTCODE")
            .Should().Be("1e436d8e309266df76b7f24775600197d75f52ad8d74b3c519e05736d7c740b33885e0acb550873cf4bb184dbbcf690a88adeb0a67b04032c22ae3d36c1c9e29");
    }

    [Fact]
    public void Every_element_of_an_array_answer_gets_its_own_expected_hash()
    {
        var lines = TripsAfricaMapping.MapConfirmations(
            Array(Element(Code1, 21678, Hash1), Element(Code2, 21678, Hash2), Element(Code3, 43356, Hash3)),
            TestKey);

        lines.Select(line => line.HashExpected).Should().Equal(Hash1, Hash2, Hash3);
        lines.Should().OnlyContain(line => SupplierBookingConfirmation.HashesMatch(line.HashExpected, line.HashReceived));
        lines[2].NewPrice.Should().Be(new Money(4_335_600));
        lines[0].TicketTimeLimit.Should().Be(new DateTimeOffset(2026, 10, 1, 6, 9, 46, Lagos));
        lines[0].TicketTimeLimit!.Value.Offset.Should().Be(TimeSpan.Zero, "the database refuses any instant that is not UTC");
    }

    [Fact]
    public void A_tampered_second_element_fails_its_own_check_while_the_others_pass()
    {
        // Element 2 claims ₦11,678 but carries the hash the supplier signed for ₦21,678.
        var lines = TripsAfricaMapping.MapConfirmations(
            Array(Element(Code1, 21678, Hash1), Element(Code2, 11678, Hash2), Element(Code3, 43356, Hash3)),
            TestKey);

        lines.Select(line => SupplierBookingConfirmation.HashesMatch(line.HashExpected, line.HashReceived))
            .Should().Equal(true, false, true);
    }

    [Fact]
    public void A_decimal_price_that_disagrees_with_the_hashed_whole_price_cannot_be_verified()
    {
        // The hash covers NewPriceWhole only. A decimal NewPrice of ₦31,678 riding on an honest hash
        // for ₦21,678 is exactly the tamper the hash alone would miss.
        var line = TripsAfricaMapping.MapConfirmations(Element(Code1, 21678, Hash1, decimalWhole: 31678), TestKey)
            .Should().ContainSingle().Subject;

        SupplierBookingConfirmation.HashesMatch(line.HashExpected, line.HashReceived).Should().BeFalse();
    }

    [Fact]
    public void An_element_with_no_whole_price_cannot_be_verified_and_cannot_be_forged_either()
    {
        const string noWholePrice = """
            [{ "ConfirmationCode": "1|A", "NewPrice": 100.0, "Hash": "x" },
             { "ConfirmationCode": "2|B", "NewPrice": 100.0, "Hash": "x" }]
            """;

        var lines = TripsAfricaMapping.MapConfirmations(noWholePrice, TestKey);

        lines.Should().OnlyContain(line => !SupplierBookingConfirmation.HashesMatch(line.HashExpected, line.HashReceived));
        lines[0].HashExpected.Should().NotBe(lines[1].HashExpected, "a fixed sentinel is a value a hostile answer could learn to send");
    }

    [Fact]
    public void An_international_answer_is_a_single_object_and_one_line()
    {
        TripsAfricaMapping.MapConfirmations(Element(Code1, 21678, Hash1), TestKey).Should().ContainSingle();
    }

    [Fact]
    public void An_international_confirmation_sends_the_tuple_inline_and_the_passport_as_docs()
    {
        var request = new SupplierPriceConfirmationRequest(
            SupplierProductType.Flight,
            "sess-intl-0001",
            "5:2:0:7:-",
            new SupplierOfferReference("5", "2", "0", "7"),
            [Adult()]);

        using var json = JsonDocument.Parse(TripsAfricaMapping.ToFlightConfirmBody(request));
        var root = json.RootElement;

        root.GetProperty("RecommendationID").GetInt64().Should().Be(7);
        root.GetProperty("AgentID").GetInt64().Should().Be(5);
        root.GetProperty("GdsID").GetInt64().Should().Be(2);
        root.GetProperty("SessionID").GetString().Should().Be("sess-intl-0001");

        var document = root.GetProperty("AirTravellers")[0].GetProperty("Documents")[0];
        document.GetProperty("DocType").GetString().Should().Be("DOCS");
        document.GetProperty("InnerDocType").GetString().Should().Be("PASSPORT");
        document.GetProperty("DocID").GetString().Should().Be("A01234567");
    }

    [Fact]
    public void A_domestic_return_confirms_both_routes_in_one_call_in_route_order()
    {
        var request = new SupplierPriceConfirmationRequest(
            SupplierProductType.Flight,
            "sess-dom-0001",
            "165:101:0:3:1",
            new SupplierOfferReference("165", "101", "0", "3", FlightRouteIndex: 1),
            [Adult()],
            AdditionalRoutes: [new SupplierOfferReference("165", "101", "0", "2", FlightRouteIndex: 0)]);

        using var json = JsonDocument.Parse(TripsAfricaMapping.ToFlightConfirmBody(request));

        json.RootElement.GetProperty("SelectedFlights").EnumerateArray()
            .Select(flight => (flight.GetProperty("FlightRouteIndex").GetInt32(), flight.GetProperty("RecommendationID").GetInt64()))
            .Should().Equal((0, 2L), (1, 3L));
    }

    [Fact]
    public void A_bus_confirmation_books_a_seat_per_passenger_but_none_for_an_infant()
    {
        var request = new SupplierPriceConfirmationRequest(
            SupplierProductType.Bus,
            "sess-bus-0001",
            "169:103:1:0:0",
            new SupplierOfferReference("169", "103", "1", "0", 0),
            [
                Adult() with { SeatNumbers = ["1"] },
                new SupplierPassenger(PassengerType.Infant, "Tobi", "Adeyemi"),
            ]);

        using var json = JsonDocument.Parse(TripsAfricaBusMapping.ToBusConfirmBody(request));
        var root = json.RootElement;

        root.GetProperty("NumberOfSeats").GetInt32().Should().Be(1);
        root.GetProperty("IsRoundTrip").GetBoolean().Should().BeFalse();
        root.GetProperty("SelectedBuses")[0].GetProperty("BusRouteIndex").GetInt32().Should().Be(0);
        root.GetProperty("Travellers")[0].GetProperty("SeatNumbers")[0].GetString().Should().Be("1");
    }

    [Fact]
    public void Three_confirmations_with_the_second_tampered_reject_the_whole_booking_not_just_one_line()
    {
        // #35's acceptance test end to end: the adapter's expected hashes, the supplier's received ones,
        // and SupplierBooking deciding. One bad element must block issuing for all of them.
        var booking = NewBooking();

        booking.RecordPriceConfirmation(
            TripsAfricaMapping.MapConfirmations(
                Array(Element(Code1, 21678, Hash1), Element(Code2, 11678, Hash2), Element(Code3, 43356, Hash3)),
                TestKey),
            DateTimeOffset.UtcNow);

        booking.Status.Should().Be(SupplierBookingStatus.PriceRejected);
        booking.HashVerified.Should().BeFalse();
    }

    [Fact]
    public void Three_honest_confirmations_leave_the_booking_ready_to_issue()
    {
        // The control: the same booking and array, untampered — so the rejection above is the tamper.
        var booking = NewBooking();

        booking.RecordPriceConfirmation(
            TripsAfricaMapping.MapConfirmations(
                Array(Element(Code1, 21678, Hash1), Element(Code2, 21678, Hash2), Element(Code3, 43356, Hash3)),
                TestKey),
            DateTimeOffset.UtcNow);

        booking.Status.Should().Be(SupplierBookingStatus.PriceConfirmed);
        booking.HashVerified.Should().BeTrue();
    }

    private static SupplierBooking NewBooking() =>
        SupplierBooking.Create(
            agencyId: Guid.CreateVersion7(),
            supplierId: Guid.CreateVersion7(),
            orderLineId: Guid.CreateVersion7(),
            supplierOfferId: null,
            productType: SupplierProductType.Bus,
            tripType: "Domestic",
            tripMode: "Road",
            supplierSessionId: "sess-bus-0001",
            currency: "NGN",
            idempotencyKey: "key-0001");

    // ---------------------------------------------------------------------------- fixtures

    private static SupplierSearchResult MapInternational() => TripsAfricaMapping.MapFlightSearch(InternationalSearch, out _);

    private static SupplierPassenger Adult() =>
        new(
            PassengerType.Adult,
            "Ngozi",
            "Adeyemi",
            Title: "MRS",
            BirthDate: new DateOnly(1990, 1, 1),
            Email: "ngozi@example.test",
            PhoneNumber: "08030000000",
            Document: new SupplierTravelDocument(TravelDocumentKind.Passport, "A01234567", "NG", "NG"));

    private static string Array(params string[] elements) => "[" + string.Join(",", elements) + "]";

    private static string Element(string code, long whole, string hash, long? decimalWhole = null) =>
        $$"""
        { "ConfirmationCode": "{{code}}", "TicketTimeLimit": "2026-10-01T06:09:46+01:00",
          "OldPrice": {{whole}}.0, "NewPrice": {{decimalWhole ?? whole}}.0,
          "OldPriceWhole": {{whole}}, "NewPriceWhole": {{whole}},
          "Errors": [], "Warnings": [], "PassengerDetails": {}, "Hash": "{{hash}}" }
        """;

    private const string InternationalSearch = """
        {
          "SessionId": "sess-intl-0001",
          "TotalCount": 3,
          "ResultList": [
            {
              "FlightDetails": [{
                "StopOvers": 1,
                "FlightEntries": [
                  { "FlightNumber": "554", "MarketingAirlineCode": "AT", "MarketingAirlineName": "Royal Air Maroc",
                    "OperatingAirlineCode": "AT", "DepartureDate": "2026-10-02T06:45:00", "DepartureAirportCode": "LOS",
                    "ArrivalDate": "2026-10-02T11:15:00", "ArrivalAirportCode": "CMN", "FlightClass": "Economy", "FlightDuration": "4h:30m",
                    "Baggages": "2", "BaggageUnit": "PC",
                    "AvailablePassengerSeats": [{ "PassengerType": "ADT", "FareBasis": "KA0WAAFA" }] },
                  { "FlightNumber": "800", "MarketingAirlineCode": "AT", "MarketingAirlineName": "Royal Air Maroc",
                    "OperatingAirlineCode": "AT", "DepartureDate": "2026-10-02T13:30:00", "DepartureAirportCode": "CMN",
                    "ArrivalDate": "2026-10-02T16:50:00", "ArrivalAirportCode": "LHR", "FlightClass": "Economy",
                    "Baggages": "2", "BaggageUnit": "PC" }
                ]
              }],
              "AgentId": 5, "GDSId": 2, "Currency": "NGN",
              "BaseFare": 585008.0, "TotalFare": 844519.5,
              "Properties": { "CombinationID": 0, "RecommendationID": 7, "TripsSessionId": "sess-intl-0001", "GdsSessionId": "gds-0001" },
              "IsLocal": false
            },
            {
              "FlightDetails": [{ "FlightEntries": [
                { "FlightNumber": "101", "MarketingAirlineCode": "BA", "DepartureDate": "2026-10-02T22:00:00",
                  "DepartureAirportCode": "LOS", "ArrivalDate": "2026-10-03T05:30:00", "ArrivalAirportCode": "LHR" } ] }],
              "AgentId": 5, "GDSId": 2, "TotalFare": 100.005,
              "Properties": { "CombinationID": 0, "RecommendationID": 8 }
            },
            {
              "FlightDetails": [{ "FlightEntries": [
                { "FlightNumber": "102", "DepartureDate": "2026-10-02T22:00:00",
                  "DepartureAirportCode": "LOS", "ArrivalDate": "2026-10-03T05:30:00", "ArrivalAirportCode": "LHR" } ] }],
              "AgentId": 5, "GDSId": 2, "TotalFare": 900000.0,
              "Properties": { "CombinationID": 0, "RecommendationID": 9 }
            }
          ]
        }
        """;

    private const string ReturnSearch = """
        {
          "SessionId": "sess-ret-0001",
          "ResultList": [{
            "FlightDetails": [
              { "FlightEntries": [{ "FlightNumber": "75", "MarketingAirlineCode": "BA", "DepartureDate": "2026-10-02T23:20:00",
                "DepartureAirportCode": "LOS", "ArrivalDate": "2026-10-03T05:35:00", "ArrivalAirportCode": "LHR" }] },
              { "FlightEntries": [{ "FlightNumber": "74", "MarketingAirlineCode": "BA", "DepartureDate": "2026-10-09T13:40:00",
                "DepartureAirportCode": "LHR", "ArrivalDate": "2026-10-09T20:40:00", "ArrivalAirportCode": "LOS" }] }
            ],
            "AgentId": 5, "GDSId": 2, "TotalFare": 1500000.0,
            "Properties": { "CombinationID": 1, "RecommendationID": 0, "TripsSessionId": "sess-ret-0001" }
          }]
        }
        """;

    private const string DomesticSearch = """
        {
          "SessionId": "sess-dom-0001",
          "ResultList": [{
            "FlightDetails": [{ "FlightEntries": [{
              "FlightNumber": "0321", "MarketingAirlineCode": "QI", "MarketingAirlineName": "Ibom Air", "OperatingAirlineCode": "QI",
              "DepartureDate": "2026-10-05T07:20:00", "DepartureAirportCode": "ABV",
              "ArrivalDate": "2026-10-05T08:40:00", "ArrivalAirportCode": "LOS",
              "FlightClass": "Economy", "Baggages": "KGS", "BaggageUnit": null }] }],
            "AgentId": 165, "GDSId": 101, "Currency": "NGN", "TotalFare": 46217.5,
            "Properties": { "CombinationID": 0, "RecommendationID": 3, "TripsSessionId": "sess-dom-0001" },
            "IsLocal": true, "FlightRouteIndex": 1
          }]
        }
        """;

    private const string BusSearch = """
        {
          "TripsSessionId": "sess-bus-0001",
          "Currency": "NGN",
          "FirstLeg": {
            "TotalCount": 1,
            "ResultList": [{
              "CombinationId": 1, "SessionId": "inner-session", "GdsId": 103, "AgentId": 169, "Currency": "NGN",
              "AgentName": "LIBRA Motors", "DepartureTerminal": "Lagos (Ejigbo)", "ArrivalTerminal": "Imo (Owerri)",
              "DepartureTerminalId": 60, "ArrivalTerminalId": 51, "BusRouteIndex": 0,
              "AvailableBuses": [
                { "RecommendationId": 0, "BusType": "Hiace",
                  "EstimatedDepartureDate": "2026-10-03T06:00:00", "EstimatedArrivalDate": "2026-10-03T13:00:00",
                  "TotalAvailableSeats": 2, "ReservationId": "res-0001",
                  "AvailableSeats": [
                    { "IsAvailable": true, "SeatNumber": "1" },
                    { "IsAvailable": false, "SeatNumber": "2" },
                    { "IsAvailable": true, "SeatNumber": "11" }
                  ],
                  "PassengerFares": [{ "BaseFare": 21000.00 }],
                  "TotalFare": 21678.0 },
                { "RecommendationId": 1, "EstimatedDepartureDate": "2026-10-03T09:00:00", "TotalFare": null }
              ]
            }]
          },
          "SecondLeg": { "TotalCount": 0, "ResultList": [] }
        }
        """;
}
