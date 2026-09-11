using System.Globalization;
using System.Security.Claims;
using TripsAgent.Api.Authorization;
using TripsAgent.Api.Pricing;
using TripsAgent.Application.Search;
using TripsAgent.Application.Suppliers;
using TripsAgent.Contracts.Search;
using TripsAgent.Domain.Identity;
using TripsAgent.Domain.Suppliers;

namespace TripsAgent.Api.Search;

/// <summary>
/// Flight and bus search (#33, #34): cached at the net rate (#40), priced for the caller, and the
/// margin shown only to <c>margin.view</c>.
/// </summary>
public static class SearchEndpoints
{
    public static IEndpointRouteBuilder MapSearchEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/v1/search")
            .WithTags("Search")
            .RequireAuthorization(PermissionPolicies.For(PermissionCodes.BookingSearch));

        group.MapPost("/flights", async (
                FlightSearchRequest request,
                SupplierSearchService search,
                TimeProvider clock,
                ClaimsPrincipal user,
                CancellationToken cancellationToken) =>
            {
                ArgumentNullException.ThrowIfNull(request);

                var problems = SearchRequestRules.Flights(request, SearchRequestRules.TodayInLagos(clock));

                return problems.Count > 0
                    ? Results.ValidationProblem(problems)
                    : await RunAsync(search, SearchRequestRules.ToQuery(request), user, cancellationToken);
            })
            .WithName("SearchFlights")
            .Produces<SearchResponse>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        group.MapPost("/buses", async (
                BusSearchRequest request,
                SupplierSearchService search,
                TimeProvider clock,
                ClaimsPrincipal user,
                CancellationToken cancellationToken) =>
            {
                ArgumentNullException.ThrowIfNull(request);

                var problems = SearchRequestRules.Buses(request, SearchRequestRules.TodayInLagos(clock));

                return problems.Count > 0
                    ? Results.ValidationProblem(problems)
                    : await RunAsync(search, SearchRequestRules.ToQuery(request), user, cancellationToken);
            })
            .WithName("SearchBuses")
            .Produces<SearchResponse>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        return app;
    }

    private static async Task<IResult> RunAsync(
        SupplierSearchService search,
        SupplierSearchQuery query,
        ClaimsPrincipal user,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await search.SearchAsync(query, cancellationToken);
            return Results.Ok(ToResponse(result, PricingEndpoints.CanViewMargin(user)));
        }
        catch (SupplierUnavailableException)
        {
            // 503: asking again shortly is the right move, and safe — search is a read.
            return Results.Problem(
                title: "The supplier did not answer in time",
                detail: "Nothing has been booked or charged. Searching again usually works.",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        catch (SupplierRequestRejectedException)
        {
            return Results.Problem(
                title: "The supplier could not run that search",
                detail: "Check the places and dates, then search again. Nothing has been booked or charged.",
                statusCode: StatusCodes.Status422UnprocessableEntity);
        }
    }

    internal static SearchResponse ToResponse(PricedSearchResult result, bool canViewMargin) =>
        new(
            result.SearchRequestId,
            result.SearchedAt,
            result.ExpiresAt,
            result.FromCache,
            result.Offers.Select(offer => ToOffer(offer, canViewMargin)).ToList());

    private static SearchOfferResponse ToOffer(PricedOffer priced, bool canViewMargin)
    {
        var offer = priced.Offer;
        var price = priced.Price;

        return new SearchOfferResponse(
            offer.OfferId,
            offer.Leg,
            offer.FlightSegments
                .GroupBy(segment => segment.LegIndex)
                .OrderBy(leg => leg.Key)
                .Select(leg => Journey(leg.OrderBy(segment => segment.SegmentIndex).ToList()))
                .ToList(),
            offer.BusSegments.Select(BusTrip).ToList(),
            new OfferPriceResponse(
                price.Currency,
                price.GrossAmountMinor.AmountMinor,
                // Omitted, not zeroed, without margin.view: the net rate is what Trips charges the
                // agency, and the VAT on the markup gives the markup away just as surely.
                canViewMargin
                    ? new OfferMarginResponse(
                        price.NetAmountMinor.AmountMinor,
                        price.MarkupAmountMinor.AmountMinor,
                        price.TaxAmountMinor.AmountMinor)
                    : null));
    }

    /// <summary>
    /// Flying time from the supplier where it says; each connection from the clock — exact, because
    /// both ends of a layover are at the same airport, whatever its time zone.
    /// </summary>
    private static FlightJourneyResponse Journey(List<SupplierFlightSegmentQuote> segments)
    {
        var flying = segments.Sum(FlyingMinutes);
        var waiting = segments
            .Zip(segments.Skip(1), (arriving, leaving) => (int)(leaving.DepartureAt - arriving.ArrivalAt).TotalMinutes)
            .Sum();

        return new FlightJourneyResponse(flying + waiting, segments.Count - 1, segments.Select(Segment).ToList());
    }

    private static FlightSegmentResponse Segment(SupplierFlightSegmentQuote segment) =>
        new(
            segment.MarketingCarrier,
            segment.MarketingCarrierName,
            segment.FlightNumber,
            segment.OriginIata,
            segment.DestinationIata,
            WallClock(segment.DepartureAt),
            WallClock(segment.ArrivalAt),
            FlyingMinutes(segment),
            segment.Cabin,
            segment.BaggageAllowance);

    private static int FlyingMinutes(SupplierFlightSegmentQuote segment) =>
        segment.DurationMinutes ?? (int)(segment.ArrivalAt - segment.DepartureAt).TotalMinutes;

    private static BusTripResponse BusTrip(SupplierBusSegmentQuote segment) =>
        new(
            segment.OperatorName,
            segment.VehicleType,
            segment.DepartureTerminalId,
            segment.ArrivalTerminalId,
            WallClock(segment.DepartureAt),
            segment.ArrivalAt is { } arrives ? WallClock(arrives) : null,
            segment.ArrivalAt is { } end ? (int)(end - segment.DepartureAt).TotalMinutes : null,
            segment.AvailableSeats ?? segment.SeatNumbers.Count,
            segment.SeatNumbers);

    /// <summary>The wall-clock time a ticket prints, in the offset it was recorded with: <c>2026-10-02T06:45</c>.</summary>
    private static string WallClock(DateTimeOffset at) => at.ToString("yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture);
}

/// <summary>
/// What a search request must satisfy before a supplier is asked — the same rules the console's
/// form checks, because an API is not only called by the console.
/// </summary>
internal static class SearchRequestRules
{
    private const int MaxSeatedPassengers = 9;
    private const int MaxMultiCityLegs = 5;
    private const int MaxBusPassengers = 10;

    private static readonly HashSet<string> Cabins = new(StringComparer.OrdinalIgnoreCase)
    {
        "economy", "premium_economy", "business", "first",
    };

    /// <summary>Today in Lagos. Nigeria keeps no daylight saving, so that is always UTC+1's date.</summary>
    public static DateOnly TodayInLagos(TimeProvider clock) =>
        DateOnly.FromDateTime(clock.GetUtcNow().ToOffset(TimeSpan.FromHours(1)).DateTime);

    public static Dictionary<string, string[]> Flights(FlightSearchRequest request, DateOnly today)
    {
        var problems = new Dictionary<string, string[]>();
        var shape = ShapeOf(request.TripType);
        var legs = request.Legs ?? [];

        if (shape is null)
        {
            problems["tripType"] = ["Use one_way, round_trip or multi_city."];
        }

        var (fewest, most) = shape switch
        {
            SupplierTripShape.OneWay => (1, 1),
            SupplierTripShape.RoundTrip => (2, 2),
            _ => (2, MaxMultiCityLegs),
        };

        if (legs.Count < fewest || legs.Count > most)
        {
            problems["legs"] = [fewest == most
                ? $"A {request.TripType} search has {fewest} flight{(fewest == 1 ? string.Empty : "s")}."
                : $"A {request.TripType} search has {fewest} to {most} flights."];
        }

        for (var index = 0; index < legs.Count; index++)
        {
            var leg = legs[index];

            if (!IsIata(leg.Origin))
            {
                problems[$"legs[{index}].origin"] = ["A three-letter airport code, like LOS."];
            }

            if (!IsIata(leg.Destination))
            {
                problems[$"legs[{index}].destination"] = ["A three-letter airport code, like ABV."];
            }
            else if (string.Equals(leg.Origin?.Trim(), leg.Destination.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                problems[$"legs[{index}].destination"] = ["Pick a destination other than the departure airport."];
            }

            if (leg.Date < today)
            {
                problems[$"legs[{index}].date"] = ["That date has already passed."];
            }
            else if (index > 0 && leg.Date < legs[index - 1].Date)
            {
                problems[$"legs[{index}].date"] = ["Each flight has to be on or after the one before it."];
            }
        }

        if (request.Adults < 1 || request.Children < 0 || request.Infants < 0)
        {
            problems["passengers"] = ["At least one adult has to travel."];
        }
        else if (request.Adults + request.Children > MaxSeatedPassengers)
        {
            problems["passengers"] = [$"One booking holds up to {MaxSeatedPassengers} passengers with seats."];
        }
        else if (request.Infants > request.Adults)
        {
            problems["passengers"] = ["Each infant travels on an adult's lap, so there cannot be more infants than adults."];
        }

        if (request.Cabin is { Length: > 0 } cabin && !Cabins.Contains(cabin.Trim()))
        {
            problems["cabin"] = ["Use economy, premium_economy, business or first."];
        }

        return problems;
    }

    public static Dictionary<string, string[]> Buses(BusSearchRequest request, DateOnly today)
    {
        var problems = new Dictionary<string, string[]>();
        var shape = ShapeOf(request.TripType);

        if (shape is not (SupplierTripShape.OneWay or SupplierTripShape.RoundTrip))
        {
            problems["tripType"] = ["Use one_way or round_trip."];
        }

        if (!IsTerminal(request.DepartureTerminalId))
        {
            problems["departureTerminalId"] = ["Choose where the bus leaves from."];
        }

        if (!IsTerminal(request.ArrivalTerminalId))
        {
            problems["arrivalTerminalId"] = ["Choose where the bus is going."];
        }
        else if (request.ArrivalTerminalId.Trim() == request.DepartureTerminalId?.Trim())
        {
            problems["arrivalTerminalId"] = ["Choose a different terminal from the one the bus leaves from."];
        }

        if (request.Date < today)
        {
            problems["date"] = ["That date has already passed."];
        }

        if (shape == SupplierTripShape.RoundTrip)
        {
            if (request.ReturnDate is not { } back)
            {
                problems["returnDate"] = ["Choose a return date."];
            }
            else if (back < request.Date)
            {
                problems["returnDate"] = ["The return cannot be before the outbound trip."];
            }
        }

        if (request.Passengers is < 1 or > MaxBusPassengers)
        {
            problems["passengers"] = [$"Book between 1 and {MaxBusPassengers} passengers at a time."];
        }

        return problems;
    }

    public static SupplierSearchQuery ToQuery(FlightSearchRequest request) =>
        new(
            SupplierProductType.Flight,
            ShapeOf(request.TripType)!.Value,
            request.Legs
                .Select(leg => new SupplierSearchLeg(leg.Origin.Trim().ToUpperInvariant(), leg.Destination.Trim().ToUpperInvariant(), leg.Date))
                .ToList(),
            new SupplierPassengerCounts(request.Adults, request.Children, request.Infants),
            string.IsNullOrWhiteSpace(request.Cabin) ? null : request.Cabin.Trim().ToLowerInvariant());

    public static SupplierSearchQuery ToQuery(BusSearchRequest request)
    {
        var shape = ShapeOf(request.TripType)!.Value;
        var from = request.DepartureTerminalId.Trim();
        var to = request.ArrivalTerminalId.Trim();

        List<SupplierSearchLeg> legs = [new SupplierSearchLeg(from, to, request.Date)];

        if (shape == SupplierTripShape.RoundTrip && request.ReturnDate is { } back)
        {
            legs.Add(new SupplierSearchLeg(to, from, back));
        }

        return new SupplierSearchQuery(SupplierProductType.Bus, shape, legs, new SupplierPassengerCounts(request.Passengers));
    }

    private static SupplierTripShape? ShapeOf(string? tripType) => tripType?.Trim().ToLowerInvariant() switch
    {
        "one_way" => SupplierTripShape.OneWay,
        "round_trip" => SupplierTripShape.RoundTrip,
        "multi_city" => SupplierTripShape.MultiCity,
        _ => null,
    };

    private static bool IsIata(string? code) =>
        code?.Trim() is { Length: 3 } trimmed && trimmed.All(char.IsAsciiLetter);

    /// <summary>The supplier's terminal ids are positive whole numbers.</summary>
    private static bool IsTerminal(string? id) =>
        long.TryParse(id?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value > 0;
}
