using TripsAgent.Api.Authorization;
using TripsAgent.Application.Catalog;
using TripsAgent.Contracts.Catalog;
using TripsAgent.Domain.Catalog;
using TripsAgent.Domain.Common;
using TripsAgent.Domain.Identity;
using TripsAgent.Domain.Orders;

namespace TripsAgent.Api.Catalog;

/// <summary>
/// The console's group departures API (#57): the agency's dated runs of a tour or package, the
/// manifest for one, and the queue for one that has sold out.
/// </summary>
/// <remarks>
/// <para>
/// <c>catalog.view</c> reads, <c>catalog.edit</c> creates and saves, and <c>catalog.publish</c>
/// closes, reopens and cancels — the same split as products, for the same reason: a counter agent
/// can look at a departure and sell from it without being able to take it off sale.
/// </para>
/// <para>
/// <b>Cancelling refunds everybody</b> (build plan decision 12). Every paid booking on the
/// departure goes to the agent's resolution queue as a full refund, what is still owed on it is
/// closed, and the traveller is told — in the agency's name.
/// </para>
/// <para>
/// A save carries the version it was read at. A stale one, and a capacity below the seats already
/// taken, are both a 409: the first would silently undo somebody else's work, the second would
/// leave travellers without a seat they have been sold.
/// </para>
/// </remarks>
public static class DepartureEndpoints
{
    public static IEndpointRouteBuilder MapDepartureEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/v1/catalog")
            .WithTags("Departures")
            .RequireAuthorization();

        group.MapGet("/departures", async (
                Guid? productId,
                DateOnly? from,
                DepartureService departures,
                CancellationToken cancellationToken) =>
            {
                var rows = await departures.ListAsync(new DepartureListFilter(productId, from), cancellationToken);

                return Results.Ok(rows.Select(ToResponse).ToList());
            })
            .RequireAuthorization(PermissionPolicies.For(PermissionCodes.CatalogView))
            .WithName("ListDepartures")
            .Produces<List<DepartureResponse>>();

        group.MapGet("/departures/{departureId:guid}", async (
                Guid departureId,
                DepartureService departures,
                CancellationToken cancellationToken) =>
            {
                var view = await departures.GetAsync(departureId, cancellationToken);

                return view is null ? NoSuchDeparture() : Results.Ok(ToResponse(view));
            })
            .RequireAuthorization(PermissionPolicies.For(PermissionCodes.CatalogView))
            .WithName("GetDeparture")
            .Produces<DepartureResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/products/{productId:guid}/departures", async (
                Guid productId,
                DepartureRequest request,
                DepartureService departures,
                CancellationToken cancellationToken) =>
            {
                ArgumentNullException.ThrowIfNull(request);

                return ToResult(await departures.CreateAsync(productId, ReadTerms(request), cancellationToken), created: true);
            })
            .RequireAuthorization(PermissionPolicies.For(PermissionCodes.CatalogEdit))
            .WithName("CreateDeparture")
            .Produces<DepartureResponse>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapPut("/departures/{departureId:guid}", async (
                Guid departureId,
                DepartureRequest request,
                DepartureService departures,
                CancellationToken cancellationToken) =>
            {
                ArgumentNullException.ThrowIfNull(request);

                return ToResult(
                    await departures.SaveAsync(departureId, ReadTerms(request), request.Version, cancellationToken),
                    created: false);
            })
            .RequireAuthorization(PermissionPolicies.For(PermissionCodes.CatalogEdit))
            .WithName("SaveDeparture")
            .Produces<DepartureResponse>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapPost("/departures/{departureId:guid}/close", async (
                Guid departureId,
                DepartureService departures,
                CancellationToken cancellationToken) =>
                ToResult(await departures.CloseAsync(departureId, cancellationToken), created: false))
            .RequireAuthorization(PermissionPolicies.For(PermissionCodes.CatalogPublish))
            .WithName("CloseDeparture")
            .Produces<DepartureResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapPost("/departures/{departureId:guid}/reopen", async (
                Guid departureId,
                DepartureService departures,
                CancellationToken cancellationToken) =>
                ToResult(await departures.ReopenAsync(departureId, cancellationToken), created: false))
            .RequireAuthorization(PermissionPolicies.For(PermissionCodes.CatalogPublish))
            .WithName("ReopenDeparture")
            .Produces<DepartureResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapPost("/departures/{departureId:guid}/cancel", async (
                Guid departureId,
                DepartureService departures,
                CancellationToken cancellationToken) =>
                ToResult(await departures.CancelAsync(departureId, cancellationToken), created: false))
            .RequireAuthorization(PermissionPolicies.For(PermissionCodes.CatalogPublish))
            .WithName("CancelDeparture")
            .Produces<DepartureResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapGet("/departures/{departureId:guid}/manifest", async (
                Guid departureId,
                DepartureService departures,
                CancellationToken cancellationToken) =>
            {
                var rows = await departures.ManifestAsync(departureId, cancellationToken);

                return rows is null
                    ? NoSuchDeparture()
                    : Results.Ok(rows.Select(ToManifestResponse).ToList());
            })
            .RequireAuthorization(PermissionPolicies.For(PermissionCodes.CatalogView))
            .WithName("GetDepartureManifest")
            .Produces<List<ManifestEntryResponse>>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/departures/{departureId:guid}/waitlist", async (
                Guid departureId,
                DepartureService departures,
                CancellationToken cancellationToken) =>
            {
                var rows = await departures.WaitlistAsync(departureId, cancellationToken);

                return rows is null
                    ? NoSuchDeparture()
                    : Results.Ok(rows.Select(ToWaitlistResponse).ToList());
            })
            .RequireAuthorization(PermissionPolicies.For(PermissionCodes.CatalogView))
            .WithName("GetDepartureWaitlist")
            .Produces<List<WaitlistEntryResponse>>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        // Not in the console's own contract: the storefront needs it (F5), and an agent takes calls
        // from people who want to be told when a seat comes up. Joining twice returns the place in
        // the queue somebody already has rather than a second one.
        group.MapPost("/departures/{departureId:guid}/waitlist", async (
                Guid departureId,
                JoinWaitlistRequest request,
                DepartureService departures,
                CancellationToken cancellationToken) =>
            {
                ArgumentNullException.ThrowIfNull(request);

                if (string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.Email))
                {
                    return Results.ValidationProblem(
                        new Dictionary<string, string[]>(StringComparer.Ordinal)
                        {
                            ["name"] = string.IsNullOrWhiteSpace(request.Name) ? ["A name."] : [],
                            ["email"] = string.IsNullOrWhiteSpace(request.Email) ? ["An email address."] : [],
                        },
                        title: "We need a name and an email to tell somebody about a seat.");
                }

                if (request.PaxCount < 1)
                {
                    return Results.ValidationProblem(
                        new Dictionary<string, string[]>(StringComparer.Ordinal) { ["paxCount"] = ["At least one traveller."] },
                        title: "That waitlist entry needs fixing.");
                }

                var entry = await departures.JoinWaitlistAsync(
                    departureId, request.Name, request.Email, request.PaxCount, cancellationToken);

                return entry is null
                    ? NoSuchDeparture()
                    : Results.Created(
                        $"/api/v1/catalog/departures/{departureId}/waitlist",
                        ToWaitlistResponse(entry));
            })
            .RequireAuthorization(PermissionPolicies.For(PermissionCodes.CatalogEdit))
            .WithName("JoinDepartureWaitlist")
            .Produces<WaitlistEntryResponse>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound);

        return app;
    }

    /// <summary>
    /// The request as the domain's <see cref="DepartureTerms"/>. Pure mapping: an enum name that is
    /// not recognised becomes the undefined zero value, and <see cref="DepartureRules"/> reports it
    /// with every other problem, so all the messages come from one place.
    /// </summary>
    private static DepartureTerms ReadTerms(DepartureRequest request) => new()
    {
        DepartureDate = request.DepartureDate,
        IsGroupDeparture = request.IsGroupDeparture,
        MinPax = request.MinPax,
        CapacityTotal = request.CapacityTotal,
        CutoffDaysBefore = request.CutoffDaysBefore,
        DepositType = ParseOrUnknown<DepositType>(request.DepositType),
        DepositPercentBasisPoints = request.DepositPercentBasisPoints,
        DepositAmountMinor = request.DepositAmountMinor is { } amount ? new Money(amount) : null,

        // A null entry in a list — [null] in the JSON — becomes an empty one rather than an error
        // here, so it is reported at its own index along with everything else.
        PriceTiers = (request.PriceTiers ?? [])
            .Select(tier => tier is null
                ? new PriceTierTerms(0, null, Money.Zero)
                : new PriceTierTerms(tier.MinPax, tier.MaxPax, new Money(tier.PricePerPaxMinor)))
            .ToList(),
        Installments = (request.Installments ?? [])
            .Select(item => item is null
                ? new InstallmentTerms(0, default, 0, 0)
                : new InstallmentTerms(
                    item.Sequence,
                    ParseOrUnknown<InstallmentDueBasis>(item.DueBasis),
                    item.DueOffsetDays,
                    item.PercentOfBalanceBasisPoints))
            .ToList(),
    };

    private static IResult ToResult(DepartureChangeOutcome outcome, bool created) => outcome switch
    {
        DepartureChangeOutcome.Saved saved when created =>
            Results.Created($"/api/v1/catalog/departures/{saved.View.Departure.Id}", ToResponse(saved.View)),

        DepartureChangeOutcome.Saved saved => Results.Ok(ToResponse(saved.View)),

        // 400, with every problem keyed by the field it belongs to, so the console can put each
        // message next to its input in one pass.
        DepartureChangeOutcome.Invalid invalid => Results.ValidationProblem(
            ByField(invalid.Problems),
            title: "Some of this departure needs fixing before it can be saved."),

        DepartureChangeOutcome.NotFound => NoSuchDeparture(),

        DepartureChangeOutcome.ProductHasNoDepartures => Results.Problem(
            statusCode: StatusCodes.Status422UnprocessableEntity,
            title: "A visa has no departures.",
            detail: "Departures belong to a tour or a package. A visa is sold with its own checklist instead."),

        // 409: the request was well-formed, but the departure is not in a state to take it.
        DepartureChangeOutcome.WrongStatus wrong => Results.Problem(
            statusCode: StatusCodes.Status409Conflict,
            title: "That can't be done to this departure as it stands.",
            detail: wrong.Reason),

        DepartureChangeOutcome.ChangedElsewhere => Results.Problem(
            statusCode: StatusCodes.Status409Conflict,
            title: "Someone else saved this departure while you were working on it.",
            detail: "Reload it to see their changes, then make yours again."),

        _ => throw new InvalidOperationException($"Unhandled outcome {outcome.GetType().Name}."),
    };

    private static DepartureResponse ToResponse(DepartureView view)
    {
        var departure = view.Departure;
        var terms = departure.ToTerms();

        return new DepartureResponse(
            departure.Id,
            departure.ProductId,
            view.ProductTitle,
            view.Currency,
            terms.DepartureDate,
            terms.IsGroupDeparture,
            terms.MinPax,
            terms.CapacityTotal,
            terms.CutoffDaysBefore,
            terms.DepositType.ToString(),
            terms.DepositPercentBasisPoints,
            terms.DepositAmountMinor?.AmountMinor,
            terms.PriceTiers
                .Select(tier => new PriceTierResponse(tier.MinPax, tier.MaxPax, tier.PricePerPaxMinor.AmountMinor))
                .ToList(),
            terms.Installments
                .Select(item => new InstallmentResponse(
                    item.Sequence,
                    item.DueBasis.ToString(),
                    item.DueOffsetDays,
                    item.PercentOfBalanceBasisPoints))
                .ToList(),
            departure.Status.ToString(),
            departure.CapacityReserved,
            departure.CapacityConfirmed,
            departure.Seats.SeatsLeft,
            view.WaitlistCount,
            departure.CutoffAt,
            departure.Version);
    }

    private static ManifestEntryResponse ToManifestResponse(ManifestRow row) =>
        new(
            row.OrderReference,
            row.TravellerName,
            row.PaxType.ToString(),
            row.Room,
            row.IsConfirmed ? nameof(FulfilmentStatus.Confirmed) : nameof(FulfilmentStatus.Reserved));

    private static WaitlistEntryResponse ToWaitlistResponse(DepartureWaitlistEntry entry) =>
        new(
            entry.Id,
            entry.Name,
            entry.PaxCount,
            entry.Status.ToString(),
            entry.JoinedAt,
            entry.OfferedAt,
            entry.ExpiresAt);

    private static IResult NoSuchDeparture() =>
        Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "No departure with that id.");

    private static Dictionary<string, string[]> ByField(IReadOnlyList<ProductProblem> problems) =>
        problems
            .GroupBy(problem => problem.Field)
            .ToDictionary(group => group.Key, group => group.Select(problem => problem.Message).ToArray());

    /// <summary>The named value, or the undefined zero for anything else — which validation then reports.</summary>
    private static TEnum ParseOrUnknown<TEnum>(string? value)
        where TEnum : struct, Enum =>
        !string.IsNullOrWhiteSpace(value)
        && !value.Contains(',', StringComparison.Ordinal)
        && !value.Trim().All(character => char.IsDigit(character) || character == '-' || character == '+')
        && Enum.TryParse<TEnum>(value.Trim(), ignoreCase: true, out var result)
        && Enum.IsDefined(result)
            ? result
            : default;
}
