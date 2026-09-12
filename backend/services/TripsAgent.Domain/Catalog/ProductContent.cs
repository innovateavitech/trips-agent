using TripsAgent.Domain.Common;
using TripsAgent.Domain.Pricing;

namespace TripsAgent.Domain.Catalog;

/// <summary>
/// Everything an agent writes about a product, as one value: what the console sends on every save.
/// </summary>
/// <remarks>
/// <para>
/// A save replaces the whole product with this, so there is one shape to validate and one shape
/// the publish rules read. The product as stored (<see cref="Product.ToContent"/>) and the product
/// about to be stored are both a <see cref="ProductContent"/>, which is how "would this edit break
/// a live product?" is answered before anything is written.
/// </para>
/// <para>Every list is in the order the agent arranged it, and that order is what is stored.</para>
/// </remarks>
public sealed record ProductContent
{
    public ProductType ProductType { get; init; }

    /// <summary>May be blank on a draft. Publishing needs one.</summary>
    public string Title { get; init; } = string.Empty;

    public string Summary { get; init; } = string.Empty;

    /// <summary>
    /// Stored as the agent wrote it. The storefront (#60) renders it, and must render it as text:
    /// nothing here sanitises HTML.
    /// </summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>ISO 3166-1 alpha-2, upper case: <c>KE</c>. Null until the agent chooses one.</summary>
    public string? DestinationCountry { get; init; }

    public string? DestinationCity { get; init; }

    public int? DurationDays { get; init; }

    /// <summary>ISO 4217, upper case: <c>NGN</c>.</summary>
    public string Currency { get; init; } = string.Empty;

    /// <summary>The "from" price listings show, in minor units. ₦150,000 is <c>15000000</c>.</summary>
    public Money BasePriceMinor { get; init; }

    /// <summary>The first date a tour or package can be booked for. Inclusive.</summary>
    public DateOnly? AvailableFrom { get; init; }

    /// <summary>The last date a tour or package can be booked for. Inclusive.</summary>
    public DateOnly? AvailableTo { get; init; }

    /// <summary>The cover image. Always one of <see cref="Media"/>.</summary>
    public Guid? HeroAssetId { get; init; }

    public IReadOnlyList<ProductMediaContent> Media { get; init; } = [];

    public IReadOnlyList<Guid> CategoryIds { get; init; } = [];

    public IReadOnlyList<ItineraryDayContent> Itinerary { get; init; } = [];

    public IReadOnlyList<InclusionContent> Inclusions { get; init; } = [];

    public IReadOnlyList<PriceVariantContent> PriceVariants { get; init; } = [];

    /// <summary>Only on a visa product.</summary>
    public VisaContent? Visa { get; init; }

    /// <summary>
    /// The same content tidied: whitespace trimmed, codes upper-cased, blank optional text made
    /// null, repeated meals removed. Lists keep their order and length, so a problem found in
    /// <c>itinerary[2]</c> of the tidied content is still <c>itinerary[2]</c> of the request.
    /// </summary>
    public ProductContent Normalised() => this with
    {
        Title = Trim(Title),
        Summary = Trim(Summary),
        Description = Trim(Description),
        DestinationCountry = Optional(DestinationCountry)?.ToUpperInvariant(),
        DestinationCity = Optional(DestinationCity),
        Currency = Trim(Currency).ToUpperInvariant(),
        Media = (Media ?? []).Select(item => item with { Caption = Optional(item.Caption) }).ToList(),
        CategoryIds = CategoryIds ?? [],
        Itinerary = (Itinerary ?? [])
            .Select(day => day with
            {
                Title = Trim(day.Title),
                Description = Trim(day.Description),
                Meals = (day.Meals ?? []).Distinct().Order().ToList(),
                Accommodation = Optional(day.Accommodation),
            })
            .ToList(),
        Inclusions = (Inclusions ?? []).Select(line => line with { Text = Trim(line.Text) }).ToList(),
        PriceVariants = (PriceVariants ?? []).Select(variant => variant with { Name = Trim(variant.Name) }).ToList(),
        Visa = Visa is null
            ? null
            : Visa with
            {
                VisaType = Trim(Visa.VisaType),
                Documents = (Visa.Documents ?? []).Select(document => document with { Label = Trim(document.Label) }).ToList(),
            },
    };

    private static string Trim(string? value) => value?.Trim() ?? string.Empty;

    private static string? Optional(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>One image in a product's gallery.</summary>
/// <param name="AssetId">An uploaded asset of the same agency, scanned clean.</param>
public sealed record ProductMediaContent(Guid AssetId, string? Caption);

/// <summary>One day of a tour or package.</summary>
/// <param name="DayNumber">1 for the first day. Days run 1, 2, 3 with no gaps.</param>
public sealed record ItineraryDayContent(
    int DayNumber,
    string Title,
    string Description,
    IReadOnlyList<Meal> Meals,
    string? Accommodation);

/// <summary>A line saying what the price includes, or leaves out.</summary>
public sealed record InclusionContent(InclusionKind Kind, string Text);

/// <summary>
/// One price for one kind of traveller: "Adult, sharing a double room, groups of 2 to 5".
/// </summary>
/// <param name="Occupancy">People sharing a room: 1 for single, 2 for double. Null when rooms do not apply.</param>
/// <param name="MinGroupSize">The smallest group this price is for. Null means no lower bound.</param>
/// <param name="MaxGroupSize">The largest group this price is for. Null means no upper bound.</param>
public sealed record PriceVariantContent(
    string Name,
    PaxType PaxType,
    int? Occupancy,
    int? MinGroupSize,
    int? MaxGroupSize,
    Money PriceMinor);

/// <summary>
/// What a visa product says about the visa. Fulfilment is manual: nothing here calls anyone.
/// </summary>
/// <param name="ProcessingTimeDays">Zero until the agent fills it in. Publishing needs it above zero.</param>
/// <param name="ValidityDays">Zero until the agent fills it in. Publishing needs it above zero.</param>
/// <param name="ConsularFeeMinor">What the embassy charges. Kept apart from the service fee, never summed in storage.</param>
/// <param name="ServiceFeeMinor">What the agency charges for handling the application.</param>
public sealed record VisaContent(
    string VisaType,
    VisaEntryType EntryType,
    int ProcessingTimeDays,
    int ValidityDays,
    Money ConsularFeeMinor,
    Money ServiceFeeMinor,
    IReadOnlyList<VisaDocumentContent> Documents);

/// <summary>One document on a visa applicant's checklist.</summary>
public sealed record VisaDocumentContent(string Label, bool IsMandatory);

/// <summary>How much a product may hold. Each matches its column, so the database never truncates.</summary>
public static class CatalogLimits
{
    public const int MaxTitleLength = 200;
    public const int MaxSummaryLength = 500;
    public const int MaxDescriptionLength = 20_000;
    public const int MaxCityLength = 100;
    public const int MaxDurationDays = 365;
    public const int MaxMedia = 30;
    public const int MaxCaptionLength = 300;
    public const int MaxCategories = 50;
    public const int MaxItineraryDays = 90;
    public const int MaxDayTitleLength = 200;
    public const int MaxDayDescriptionLength = 5_000;
    public const int MaxAccommodationLength = 200;
    public const int MaxInclusions = 100;
    public const int MaxInclusionTextLength = 300;
    public const int MaxPriceVariants = 50;
    public const int MaxVariantNameLength = 100;
    public const int MaxOccupancy = 10;
    public const int MaxGroupSize = 1_000;
    public const int MaxVisaTypeLength = 100;
    public const int MaxProcessingTimeDays = 365;
    public const int MaxValidityDays = 3_650;
    public const int MaxVisaDocuments = 50;
    public const int MaxDocumentLabelLength = 200;

    /// <summary>
    /// The most any price on a product may be: the same ceiling as a markup rule's amounts, so the
    /// largest product price marked up by the largest rule still fits in a <see cref="long"/>.
    /// </summary>
    public const long MaxPriceMinor = MarkupRuleTerms.MaxAmountMinor;
}
