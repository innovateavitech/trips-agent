using TripsAgent.Api.Authorization;
using TripsAgent.Application.Catalog;
using TripsAgent.Contracts.Catalog;
using TripsAgent.Domain.Catalog;
using TripsAgent.Domain.Common;
using TripsAgent.Domain.Identity;

namespace TripsAgent.Api.Catalog;

/// <summary>
/// The console's product management API (#161): the agency's own tours, packages and visas, and the
/// categories they are tagged with.
/// </summary>
/// <remarks>
/// <para>
/// <c>catalog.view</c> reads, <c>catalog.edit</c> creates and saves, and <c>catalog.publish</c>
/// moves a product on and off the storefront. A counter agent can browse the catalog to sell from
/// it without being able to change what travellers see.
/// </para>
/// <para>
/// Status changes: publish only from Draft; unpublish takes Published or Archived back to Draft,
/// which is also how an archived product is restored; archive from Draft or Published. Anything
/// else is a 409. Publishing a draft with problems is a 422 listing every one of them.
/// </para>
/// </remarks>
public static class CatalogEndpoints
{
    public static IEndpointRouteBuilder MapCatalogEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/v1/catalog")
            .WithTags("Catalog")

            // Every route reads or writes the caller's own agency's catalog. Without a token there
            // is no tenant, and the filters would return nothing: an honest 401 beats a baffling 404.
            .RequireAuthorization();

        group.MapGet("/products", async (
                string? type,
                string? status,
                string? q,
                ProductCatalogService catalog,
                CancellationToken cancellationToken) =>
            {
                ProductType? productType = null;
                ProductStatus? productStatus = null;

                if (!string.IsNullOrWhiteSpace(type))
                {
                    if (!TryParseEnum<ProductType>(type, out var parsed))
                    {
                        return BadFilter("type must be Tour, Package or Visa.");
                    }

                    productType = parsed;
                }

                if (!string.IsNullOrWhiteSpace(status))
                {
                    if (!TryParseEnum<ProductStatus>(status, out var parsed))
                    {
                        return BadFilter("status must be Draft, Published or Archived.");
                    }

                    productStatus = parsed;
                }

                var rows = await catalog.ListAsync(new ProductListFilter(productType, productStatus, q), cancellationToken);

                return Results.Ok(rows.Select(ToSummary).ToList());
            })
            .RequireAuthorization(PermissionPolicies.For(PermissionCodes.CatalogView))
            .WithName("ListProducts")
            .Produces<List<ProductSummaryResponse>>()
            .ProducesProblem(StatusCodes.Status400BadRequest);

        group.MapGet("/products/{productId:guid}", async (
                Guid productId,
                ProductCatalogService catalog,
                CancellationToken cancellationToken) =>
            {
                var view = await catalog.GetAsync(productId, cancellationToken);

                return view is null ? NoSuchProduct() : Results.Ok(ToResponse(view));
            })
            .RequireAuthorization(PermissionPolicies.For(PermissionCodes.CatalogView))
            .WithName("GetProduct")
            .Produces<ProductResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/products", async (
                ProductRequest request,
                ProductCatalogService catalog,
                CancellationToken cancellationToken) =>
            {
                ArgumentNullException.ThrowIfNull(request);

                var outcome = await catalog.CreateAsync(ReadContent(request), request.Slug, cancellationToken);

                return ToResult(outcome, created: true);
            })
            .RequireAuthorization(PermissionPolicies.For(PermissionCodes.CatalogEdit))
            .WithName("CreateProduct")
            .Produces<ProductResponse>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status409Conflict);

        // Saves the whole product in one transaction. What is not in the request is not kept: an
        // image left out of media is detached, a day left out of the itinerary is removed.
        group.MapPut("/products/{productId:guid}", async (
                Guid productId,
                ProductRequest request,
                ProductCatalogService catalog,
                CancellationToken cancellationToken) =>
            {
                ArgumentNullException.ThrowIfNull(request);

                var outcome = await catalog.SaveAsync(productId, ReadContent(request), request.Slug, cancellationToken);

                return ToResult(outcome, created: false);
            })
            .RequireAuthorization(PermissionPolicies.For(PermissionCodes.CatalogEdit))
            .WithName("SaveProduct")
            .Produces<ProductResponse>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapPost("/products/{productId:guid}/publish", async (
                Guid productId,
                ProductCatalogService catalog,
                CancellationToken cancellationToken) =>
                ToResult(await catalog.PublishAsync(productId, cancellationToken), created: false))
            .RequireAuthorization(PermissionPolicies.For(PermissionCodes.CatalogPublish))
            .WithName("PublishProduct")
            .Produces<ProductResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapPost("/products/{productId:guid}/unpublish", async (
                Guid productId,
                ProductCatalogService catalog,
                CancellationToken cancellationToken) =>
                ToResult(await catalog.UnpublishAsync(productId, cancellationToken), created: false))
            .RequireAuthorization(PermissionPolicies.For(PermissionCodes.CatalogPublish))
            .WithName("UnpublishProduct")
            .Produces<ProductResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapPost("/products/{productId:guid}/archive", async (
                Guid productId,
                ProductCatalogService catalog,
                CancellationToken cancellationToken) =>
                ToResult(await catalog.ArchiveAsync(productId, cancellationToken), created: false))
            .RequireAuthorization(PermissionPolicies.For(PermissionCodes.CatalogPublish))
            .WithName("ArchiveProduct")
            .Produces<ProductResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapGet("/categories", async (ProductCategoryService categories, CancellationToken cancellationToken) =>
                Results.Ok((await categories.ListAsync(cancellationToken)).Select(ToCategoryResponse).ToList()))
            .RequireAuthorization(PermissionPolicies.For(PermissionCodes.CatalogView))
            .WithName("ListProductCategories")
            .Produces<List<CategoryResponse>>();

        group.MapPost("/categories", async (
                CategoryRequest request,
                ProductCategoryService categories,
                CancellationToken cancellationToken) =>
            {
                ArgumentNullException.ThrowIfNull(request);

                var outcome = await categories.CreateAsync(
                    request.Name, ParseOrUnknown<CategoryType>(request.Type), cancellationToken);

                return outcome switch
                {
                    // No Location header: there is no route that reads one category on its own.
                    CategoryCreateOutcome.Created created =>
                        Results.Created((string?)null, ToCategoryResponse(created.Category)),

                    CategoryCreateOutcome.Invalid invalid => Results.ValidationProblem(
                        ByField(invalid.Problems),
                        title: "That category needs fixing before it can be saved."),

                    CategoryCreateOutcome.Duplicate duplicate => Results.Problem(
                        statusCode: StatusCodes.Status409Conflict,
                        title: duplicate.Type == CategoryType.Theme
                            ? "You already have a theme called that."
                            : "You already have a category called that.",
                        detail: $"'{duplicate.Name}' already exists. Use it, or choose another name."),

                    _ => throw new InvalidOperationException($"Unhandled outcome {outcome.GetType().Name}."),
                };
            })
            .RequireAuthorization(PermissionPolicies.For(PermissionCodes.CatalogEdit))
            .WithName("CreateProductCategory")
            .Produces<CategoryResponse>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status409Conflict);

        return app;
    }

    private static IResult ToResult(ProductChangeOutcome outcome, bool created) => outcome switch
    {
        ProductChangeOutcome.Saved saved when created =>
            Results.Created($"/api/v1/catalog/products/{saved.View.Product.Id}", ToResponse(saved.View)),

        ProductChangeOutcome.Saved saved => Results.Ok(ToResponse(saved.View)),

        // 400, with every problem keyed by the field it belongs to, so the console can put each
        // message next to its input in one pass.
        ProductChangeOutcome.Invalid invalid => Results.ValidationProblem(
            ByField(invalid.Problems),
            title: "Some of this product needs fixing before it can be saved."),

        ProductChangeOutcome.NotFound => NoSuchProduct(),

        ProductChangeOutcome.SlugTaken taken => Results.Problem(
            statusCode: StatusCodes.Status409Conflict,
            title: "Another of your products already uses that web address.",
            detail: $"'{taken.Slug}' is taken. '{taken.SuggestedSlug}' is free.",
            extensions: new Dictionary<string, object?>
            {
                ["slug"] = taken.Slug,
                ["suggestedSlug"] = taken.SuggestedSlug,
            }),

        ProductChangeOutcome.NotPublishable refused => PublishProblems(
            "This product can't be published yet.",
            refused.Problems),

        ProductChangeOutcome.WouldBreakLiveProduct refused => PublishProblems(
            "This change would take the live product below what publishing needs. Fix these, or unpublish it first.",
            refused.Problems),

        // 409: the request was well-formed, but the product is not in a state to take it.
        ProductChangeOutcome.WrongStatus wrong => Results.Problem(
            statusCode: StatusCodes.Status409Conflict,
            title: "That can't be done to this product as it stands.",
            detail: wrong.Reason),

        ProductChangeOutcome.ChangedElsewhere => Results.Problem(
            statusCode: StatusCodes.Status409Conflict,
            title: "Someone else saved this product while you were working on it.",
            detail: "Reload it to see their changes, then make yours again."),

        _ => throw new InvalidOperationException($"Unhandled outcome {outcome.GetType().Name}."),
    };

    /// <summary>
    /// A 422 listing every problem under <c>publishProblems</c> — the same field name, and the same
    /// <c>{field, message}</c> items, as <see cref="ProductResponse.PublishProblems"/>.
    /// </summary>
    private static IResult PublishProblems(string title, IReadOnlyList<ProductProblem> problems) =>
        Results.Problem(
            statusCode: StatusCodes.Status422UnprocessableEntity,
            title: title,
            detail: problems.Count == 1 ? "1 thing needs fixing first." : $"{problems.Count} things need fixing first.",
            extensions: new Dictionary<string, object?>
            {
                ["publishProblems"] = problems.Select(ToProblemResponse).ToList(),
            });

    private static IResult NoSuchProduct() =>
        Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "No product with that id.");

    private static IResult BadFilter(string detail) =>
        Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "That filter doesn't make sense.", detail: detail);

    private static Dictionary<string, string[]> ByField(IReadOnlyList<ProductProblem> problems) =>
        problems
            .GroupBy(problem => problem.Field)
            .ToDictionary(group => group.Key, group => group.Select(problem => problem.Message).ToArray());

    /// <summary>
    /// The request as the domain's <see cref="ProductContent"/>. Pure mapping: an enum name that is
    /// not recognised becomes the undefined zero value, and <see cref="ProductRules"/> reports it
    /// with every other problem, so all the messages come from one place.
    /// </summary>
    private static ProductContent ReadContent(ProductRequest request) => new()
    {
        ProductType = ParseOrUnknown<ProductType>(request.ProductType),
        Title = request.Title ?? string.Empty,
        Summary = request.Summary ?? string.Empty,
        Description = request.Description ?? string.Empty,
        DestinationCountry = request.DestinationCountry,
        DestinationCity = request.DestinationCity,
        DurationDays = request.DurationDays,
        Currency = request.Currency ?? string.Empty,
        BasePriceMinor = new Money(request.BasePriceMinor),
        AvailableFrom = request.AvailableFrom,
        AvailableTo = request.AvailableTo,
        HeroAssetId = request.HeroAssetId,

        // A null entry in a list — [null] in the JSON — becomes an empty one rather than an error
        // here, so it is reported at its own index along with everything else.
        Media = (request.Media ?? [])
            .Select(item => item is null
                ? new ProductMediaContent(Guid.Empty, null)
                : new ProductMediaContent(item.AssetId, item.Caption))
            .ToList(),
        CategoryIds = request.CategoryIds ?? [],
        Itinerary = (request.Itinerary ?? [])
            .Select(day => day is null
                ? new ItineraryDayContent(0, string.Empty, string.Empty, [], null)
                : new ItineraryDayContent(
                    day.DayNumber,
                    day.Title ?? string.Empty,
                    day.Description ?? string.Empty,
                    (day.Meals ?? []).Select(ParseOrUnknown<Meal>).ToList(),
                    day.Accommodation))
            .ToList(),
        Inclusions = (request.Inclusions ?? [])
            .Select(line => line is null
                ? new InclusionContent(default, string.Empty)
                : new InclusionContent(ParseOrUnknown<InclusionKind>(line.Kind), line.Text ?? string.Empty))
            .ToList(),
        PriceVariants = (request.PriceVariants ?? [])
            .Select(variant => variant is null
                ? new PriceVariantContent(string.Empty, default, null, null, null, Money.Zero)
                : new PriceVariantContent(
                    variant.Name ?? string.Empty,
                    ParseOrUnknown<PaxType>(variant.PaxType),
                    variant.Occupancy,
                    variant.MinGroupSize,
                    variant.MaxGroupSize,
                    new Money(variant.PriceMinor)))
            .ToList(),
        Visa = request.Visa is { } visa
            ? new VisaContent(
                visa.VisaType ?? string.Empty,
                ParseOrUnknown<VisaEntryType>(visa.EntryType),
                visa.ProcessingTimeDays,
                visa.ValidityDays,
                new Money(visa.ConsularFeeMinor),
                new Money(visa.ServiceFeeMinor),
                (visa.Documents ?? [])
                    .Select(document => document is null
                        ? new VisaDocumentContent(string.Empty, false)
                        : new VisaDocumentContent(document.Label ?? string.Empty, document.IsMandatory))
                    .ToList())
            : null,
    };

    private static ProductResponse ToResponse(ProductView view)
    {
        var product = view.Product;
        var content = view.Content;

        return new ProductResponse(
            product.Id,
            content.ProductType.ToString(),
            content.Title,
            product.Slug,
            content.Summary,
            content.Description,
            content.DestinationCountry ?? string.Empty,
            content.DestinationCity ?? string.Empty,
            content.DurationDays,
            content.Currency,
            content.BasePriceMinor.AmountMinor,
            content.AvailableFrom,
            content.AvailableTo,
            content.HeroAssetId,
            content.Media
                .Select(item => new ProductMediaResponse(
                    item.AssetId,
                    item.Caption ?? string.Empty,
                    view.PreviewUrls.GetValueOrDefault(item.AssetId)))
                .ToList(),
            content.CategoryIds,
            content.Itinerary
                .Select(day => new ItineraryDayResponse(
                    day.DayNumber,
                    day.Title,
                    day.Description,
                    day.Meals.Select(meal => meal.ToString()).ToList(),
                    day.Accommodation ?? string.Empty))
                .ToList(),
            content.Inclusions.Select(line => new InclusionResponse(line.Kind.ToString(), line.Text)).ToList(),
            content.PriceVariants
                .Select(variant => new PriceVariantResponse(
                    variant.Name,
                    variant.PaxType.ToString(),
                    variant.Occupancy,
                    variant.MinGroupSize,
                    variant.MaxGroupSize,
                    variant.PriceMinor.AmountMinor))
                .ToList(),
            content.Visa is { } visa
                ? new VisaDetailsResponse(
                    visa.VisaType,
                    visa.EntryType.ToString(),
                    visa.ProcessingTimeDays,
                    visa.ValidityDays,
                    visa.ConsularFeeMinor.AmountMinor,
                    visa.ServiceFeeMinor.AmountMinor,
                    visa.Documents.Select(document => new VisaDocumentResponse(document.Label, document.IsMandatory)).ToList())
                : null,
            product.Status.ToString(),
            product.PublishedAt,
            product.UpdatedAt,
            view.PublishProblems.Select(ToProblemResponse).ToList());
    }

    private static ProductSummaryResponse ToSummary(ProductSummaryView row) =>
        new(
            row.Product.Id,
            row.Product.ProductType.ToString(),
            row.Product.Title,
            row.Product.Slug,
            row.Product.Status.ToString(),
            row.Product.DestinationCity ?? string.Empty,
            row.Product.DestinationCountry ?? string.Empty,
            row.Product.DurationDays,
            row.Product.BasePriceMinor.AmountMinor,
            row.Product.Currency,
            row.HeroPreviewUrl,
            row.Product.UpdatedAt,
            row.PublishProblemCount);

    private static CategoryResponse ToCategoryResponse(ProductCategory category) =>
        new(category.Id, category.Name, category.Type.ToString());

    private static PublishProblemResponse ToProblemResponse(ProductProblem problem) =>
        new(problem.Field, problem.Message);

    /// <summary>The named value, or the undefined zero for anything else — which validation then reports.</summary>
    private static TEnum ParseOrUnknown<TEnum>(string? value)
        where TEnum : struct, Enum =>
        TryParseEnum<TEnum>(value, out var result) ? result : default;

    /// <summary>
    /// Parses an enum by one name only. <see cref="Enum.TryParse{TEnum}(string, bool, out TEnum)"/>
    /// also accepts "3", and "Tour,Package" — which it ORs together into 3, a perfectly defined
    /// <c>Visa</c>. Neither is a name anybody meant.
    /// </summary>
    private static bool TryParseEnum<TEnum>(string? value, out TEnum result)
        where TEnum : struct, Enum
    {
        result = default;

        return !string.IsNullOrWhiteSpace(value)
               && !value.Contains(',', StringComparison.Ordinal)
               && !value.Trim().All(character => char.IsDigit(character) || character == '-' || character == '+')
               && Enum.TryParse(value.Trim(), ignoreCase: true, out result)
               && Enum.IsDefined(result);
    }
}
