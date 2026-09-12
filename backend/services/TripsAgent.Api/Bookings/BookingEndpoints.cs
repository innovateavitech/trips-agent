using System.Globalization;
using System.Security.Claims;
using TripsAgent.Api.Authorization;
using TripsAgent.Api.Pricing;
using TripsAgent.Application.Auditing;
using TripsAgent.Application.Checkout;
using TripsAgent.Application.Tenancy;
using TripsAgent.Contracts.Bookings;
using TripsAgent.Domain.Common;
using TripsAgent.Domain.Identity;
using TripsAgent.Domain.Orders;
using TripsAgent.Domain.Suppliers;

namespace TripsAgent.Api.Bookings;

/// <summary>
/// The console's bookings (#42, #44): confirm a fare's price, pay for it, follow it to its ticket, list
/// and open bookings, and decide what happens to one that failed.
/// </summary>
/// <remarks>
/// Every refusal is a problem response whose title and detail are written for the agent, and every one
/// says what happened to the money — which, for every refusal here, is nothing.
/// </remarks>
public static class BookingEndpoints
{
    public static IEndpointRouteBuilder MapBookingEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/v1/bookings")
            .WithTags("Bookings")
            .RequireAuthorization()
            .AddEndpointFilter(RequireAgencyAsync);

        group.MapPost("/price-confirmations", ConfirmPriceAsync)
            .RequireAuthorization(PermissionPolicies.For(PermissionCodes.BookingCreate))
            .WithName("ConfirmBookingPrice")
            .Produces<PriceConfirmationResponse>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status410Gone)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status502BadGateway);

        group.MapPost("/", PlaceAsync)
            .RequireAuthorization(PermissionPolicies.For(PermissionCodes.BookingCreate))
            .WithName("PlaceBooking")
            .Produces<PlacedBookingResponse>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status410Gone)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapGet("/", ListAsync)
            .RequireAuthorization(PermissionPolicies.For(PermissionCodes.BookingSearch))
            .WithName("ListBookings")
            .Produces<List<BookingListItemResponse>>();

        group.MapGet("/{reference}", GetAsync)
            .RequireAuthorization(PermissionPolicies.For(PermissionCodes.BookingSearch))
            .WithName("GetBooking")
            .Produces<BookingDetailResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/{reference}/progress", ProgressAsync)
            .RequireAuthorization(PermissionPolicies.For(PermissionCodes.BookingSearch))
            .WithName("GetBookingProgress")
            .Produces<BookingProgressResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/{reference}/resolution", ResolveAsync)
            .RequireAuthorization(PermissionPolicies.For(PermissionCodes.BookingRefund))
            .WithName("ResolveBooking")
            .Produces<BookingDetailResponse>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        return app;
    }

    // ------------------------------------------------------------------------------ handlers

    private static async Task<IResult> ConfirmPriceAsync(
        ConfirmPriceRequest request,
        CheckoutService checkout,
        IAuditContext audit,
        CancellationToken cancellationToken)
    {
        var problems = Validate(request);

        if (problems.Count > 0)
        {
            return Results.ValidationProblem(problems);
        }

        try
        {
            var confirmation = await checkout.ConfirmPriceAsync(
                request.OfferId, request.Travellers.Select(ToTraveller).ToList(), audit.CorrelationId, cancellationToken);

            return Results.Ok(new PriceConfirmationResponse(
                confirmation.Reference,
                confirmation.Sell.AmountMinor,
                confirmation.SearchedSell.AmountMinor,
                confirmation.Currency,
                confirmation.TicketTimeLimit));
        }
        catch (CheckoutRefusedException refused)
        {
            return Refused(refused);
        }
    }

    private static async Task<IResult> PlaceAsync(
        PlaceBookingRequest request,
        CheckoutService checkout,
        ITenantContext tenant,
        IAuditContext audit,
        CancellationToken cancellationToken)
    {
        var payment = PaymentOf(request.Payment);
        var problems = new Dictionary<string, string[]>(StringComparer.Ordinal);

        if (string.IsNullOrWhiteSpace(request.Reference))
        {
            problems["reference"] = ["Confirm the price first: its reference names the booking to pay for."];
        }

        if (payment is null)
        {
            problems["payment"] = ["Choose wallet or card."];
        }

        if (request.AcceptedSellMinor < 0)
        {
            problems["acceptedSellMinor"] = ["A price cannot be negative."];
        }

        if (string.IsNullOrWhiteSpace(request.IdempotencyKey) || request.IdempotencyKey.Length > CheckoutService.MaxIdempotencyKeyLength)
        {
            problems["idempotencyKey"] = [$"Send one key per payment attempt, at most {CheckoutService.MaxIdempotencyKeyLength} characters."];
        }

        if (problems.Count > 0)
        {
            return Results.ValidationProblem(problems);
        }

        try
        {
            var reference = await checkout.PlaceAsync(
                request.Reference, payment!.Value, new Money(request.AcceptedSellMinor), request.IdempotencyKey, tenant.UserId, audit.CorrelationId, cancellationToken);

            return Results.Ok(new PlacedBookingResponse(reference));
        }
        catch (CheckoutRefusedException refused)
        {
            return Refused(refused);
        }
    }

    private static async Task<IResult> ListAsync(BookingQueries queries, CancellationToken cancellationToken) =>
        Results.Ok((await queries.ListAsync(cancellationToken)).Select(ToListItem).ToList());

    private static async Task<IResult> GetAsync(
        string reference,
        BookingQueries queries,
        ClaimsPrincipal user,
        CancellationToken cancellationToken)
    {
        var detail = await queries.FindAsync(reference, cancellationToken);

        return detail is null ? NotFound() : Results.Ok(ToDetail(detail, PricingEndpoints.CanViewMargin(user)));
    }

    private static async Task<IResult> ProgressAsync(string reference, BookingQueries queries, CancellationToken cancellationToken)
    {
        var progress = await queries.ProgressAsync(reference, cancellationToken);

        return progress is null
            ? NotFound()
            : Results.Ok(new BookingProgressResponse(
                progress.State switch
                {
                    BookingState.Ticketed => "ticketed",
                    BookingState.Failed or BookingState.Cancelled => "failed",
                    _ => "awaiting_ticket",
                },
                progress.Pnr));
    }

    private static async Task<IResult> ResolveAsync(
        string reference,
        ResolveBookingRequest request,
        ResolutionService resolutions,
        BookingQueries queries,
        ITenantContext tenant,
        ClaimsPrincipal user,
        CancellationToken cancellationToken)
    {
        ResolutionChoice? choice = request.Action?.Trim().ToLowerInvariant() switch
        {
            "refund" => ResolutionChoice.Refund,
            "retry" => ResolutionChoice.Retry,
            _ => null,
        };

        if (choice is null)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["action"] = ["Choose refund or retry. Substituting a fare starts a new search."],
            });
        }

        if (tenant.UserId is not { } userId)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status403Forbidden,
                title: "A person has to make this decision.",
                detail: "Sign in as a member of the agency to resolve a booking.");
        }

        try
        {
            await resolutions.ResolveAsync(reference, choice.Value, userId, cancellationToken);
        }
        catch (CheckoutRefusedException refused)
        {
            return Refused(refused);
        }

        var detail = await queries.FindAsync(reference, cancellationToken);

        return detail is null ? NotFound() : Results.Ok(ToDetail(detail, PricingEndpoints.CanViewMargin(user)));
    }

    // ------------------------------------------------------------------------------ mapping

    /// <summary>The console's word for a booking's state.</summary>
    public static string StatusOf(BookingState state) => state switch
    {
        BookingState.Ticketed => "ticketed",
        BookingState.Failed => "failed",
        BookingState.Cancelled => "cancelled",
        _ => "awaiting_ticket",
    };

    internal static Dictionary<string, string[]> Validate(ConfirmPriceRequest request)
    {
        var problems = new Dictionary<string, string[]>(StringComparer.Ordinal);

        if (request.OfferId == Guid.Empty)
        {
            problems["offerId"] = ["Choose a fare from a search."];
        }

        if (request.Travellers is not { Count: > 0 })
        {
            problems["travellers"] = ["Add at least one traveller."];
            return problems;
        }

        for (var index = 0; index < request.Travellers.Count; index++)
        {
            var traveller = request.Travellers[index];
            var at = string.Create(CultureInfo.InvariantCulture, $"travellers[{index}]");

            if (TypeOf(traveller.Type) is null)
            {
                problems[$"{at}.type"] = ["ADT, CHD or INF."];
            }

            if (string.IsNullOrWhiteSpace(traveller.FirstName))
            {
                problems[$"{at}.firstName"] = ["Enter the first name as it is on the travel document."];
            }

            if (string.IsNullOrWhiteSpace(traveller.LastName))
            {
                problems[$"{at}.lastName"] = ["Enter the last name as it is on the travel document."];
            }
        }

        return problems;
    }

    private static CheckoutTraveller ToTraveller(BookingTravellerRequest traveller) =>
        new(
            TypeOf(traveller.Type) ?? PassengerType.Adult,
            Blank(traveller.Title),
            traveller.FirstName.Trim(),
            traveller.LastName.Trim(),
            traveller.DateOfBirth,
            Blank(traveller.Gender),
            Blank(traveller.Email),
            Blank(traveller.Phone),
            Blank(traveller.PassportNumber),
            traveller.PassportExpiry,
            Blank(traveller.Nationality));

    private static PassengerType? TypeOf(string? code) => code?.Trim().ToUpperInvariant() switch
    {
        "ADT" => PassengerType.Adult,
        "CHD" => PassengerType.Child,
        "INF" => PassengerType.Infant,
        _ => null,
    };

    private static OrderPaymentMethod? PaymentOf(string? payment) => payment?.Trim().ToLowerInvariant() switch
    {
        "wallet" => OrderPaymentMethod.Wallet,
        "card" => OrderPaymentMethod.Card,
        _ => null,
    };

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static BookingListItemResponse ToListItem(BookingSummaryView summary) =>
        new(
            summary.Reference,
            summary.LeadTraveller,
            summary.TravellerCount,
            ProductOf(summary.Product),
            summary.Origin,
            summary.Destination,
            summary.Carrier,
            summary.DepartsAt,
            StatusOf(summary.State),
            summary.SellMinor,
            summary.Currency,
            summary.TicketTimeLimit,
            summary.Pnr,
            summary.BookedAt);

    private static BookingDetailResponse ToDetail(BookingDetailView detail, bool canViewMargin)
    {
        var summary = detail.Summary;

        return new BookingDetailResponse(
            summary.Reference,
            summary.LeadTraveller,
            summary.TravellerCount,
            ProductOf(summary.Product),
            summary.Origin,
            summary.Destination,
            summary.Carrier,
            summary.DepartsAt,
            StatusOf(summary.State),
            summary.SellMinor,
            summary.Currency,
            summary.TicketTimeLimit,
            summary.Pnr,
            summary.BookedAt,
            PaidFromOf(detail.PaidFrom),
            detail.Travellers.Select(traveller => new BookingTravellerResponse(CodeOf(traveller.Type), traveller.Name, traveller.TicketNumber)).ToList(),
            detail.Segments.Select(segment => new BookingSegmentResponse(segment.Carrier, segment.Origin, segment.Destination, segment.DepartsAt, segment.ArrivesAt)).ToList(),
            new BookingPriceResponse(summary.SellMinor, canViewMargin ? new BookingMarginResponse(detail.NetMinor, detail.MarkupMinor) : null),
            detail.Timeline.Select(entry => new BookingTimelineEntryResponse(entry.At, StatusOf(entry.State), entry.Note)).ToList(),
            detail.Failure is { } failure
                ? new BookingFailureResponse(failure.Reason, failure.AtRiskMinor, PaidFromOf(failure.PaidFrom))
                : null);
    }

    private static string ProductOf(SupplierProductType product) => product == SupplierProductType.Bus ? "bus" : "flight";

    private static string PaidFromOf(OrderPaymentMethod method) => method == OrderPaymentMethod.Card ? "card" : "wallet";

    private static string CodeOf(TravellerType type) => type switch
    {
        TravellerType.Child => "CHD",
        TravellerType.Infant => "INF",
        _ => "ADT",
    };

    private static IResult Refused(CheckoutRefusedException refused) =>
        Results.Problem(
            title: refused.Title,
            detail: refused.Detail,
            statusCode: refused.Refusal switch
            {
                CheckoutRefusal.NotFound => StatusCodes.Status404NotFound,
                CheckoutRefusal.Gone => StatusCodes.Status410Gone,
                CheckoutRefusal.Unprocessable => StatusCodes.Status422UnprocessableEntity,
                CheckoutRefusal.SupplierFailed => StatusCodes.Status502BadGateway,
                _ => StatusCodes.Status409Conflict,
            });

    private static IResult NotFound() =>
        Results.Problem(
            statusCode: StatusCodes.Status404NotFound,
            title: "We could not find that booking.",
            detail: "It may belong to another agency, or the reference may be mistyped.");

    /// <summary>Bookings belong to an agency: a caller with none — a platform admin — is turned away.</summary>
    private static async ValueTask<object?> RequireAgencyAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var tenant = context.HttpContext.RequestServices.GetRequiredService<ITenantContext>();

        if (!tenant.HasTenant)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status403Forbidden,
                title: "Bookings belong to an agency.",
                detail: "Sign in as a member of a travel agency to book.");
        }

        return await next(context);
    }
}
