namespace TripsAgent.Contracts.Catalog;

// The product management API from issue #161, which the console's catalog screens (#162, #163)
// are built against. Enum values travel as their PascalCase names — Tour, Draft, Breakfast — and
// money as whole minor units: ₦150,000 is 15000000. Every list is in the agent's order; that
// order is what is stored and what comes back.

/// <summary>
/// A whole product as the console writes it: to create one, or to save one. A save replaces
/// everything — basics, gallery, categories, itinerary, inclusions, prices and visa details — in
/// one transaction.
/// </summary>
/// <param name="ProductType"><c>Tour</c>, <c>Package</c> or <c>Visa</c>.</param>
/// <param name="Title">May be blank on a draft; publishing needs one.</param>
/// <param name="Slug">
/// The storefront URL name, unique within the agency. Omitted means worked out from the title —
/// except on a published product, which keeps the address it is live at. A slug another of the
/// agency's products already uses is a 409 whose <c>suggestedSlug</c> is free.
/// </param>
/// <param name="DestinationCountry">ISO 3166-1 alpha-2: <c>KE</c>. Blank until chosen.</param>
/// <param name="DurationDays">Between 1 and 365, or omitted.</param>
/// <param name="Currency">ISO 4217: <c>NGN</c>. Blank means the agency's own currency.</param>
/// <param name="BasePriceMinor">The "from" price, in kobo. Publishing needs it above zero.</param>
/// <param name="AvailableFrom">The first date a tour or package can be booked for, inclusive.</param>
/// <param name="AvailableTo">The last date a tour or package can be booked for, inclusive.</param>
/// <param name="HeroAssetId">The cover image. Must be one of <paramref name="Media"/>.</param>
/// <param name="Media">
/// The gallery. Only the agency's own uploads, and only once they have been scanned clean and
/// processed (<c>GET /api/v1/assets/{id}</c> says <c>Ready</c>), can be attached.
/// </param>
/// <param name="CategoryIds">The agency's own categories and themes.</param>
/// <param name="Itinerary">Tours and packages only. Day numbers run 1, 2, 3 in list order.</param>
/// <param name="Inclusions">Tours and packages only.</param>
/// <param name="Visa">Visa products only.</param>
public sealed record ProductRequest(
    string ProductType,
    string Title,
    string? Slug,
    string Summary,
    string Description,
    string DestinationCountry,
    string DestinationCity,
    int? DurationDays,
    string Currency,
    long BasePriceMinor,
    DateOnly? AvailableFrom,
    DateOnly? AvailableTo,
    Guid? HeroAssetId,
    IReadOnlyList<ProductMediaRequest> Media,
    IReadOnlyList<Guid> CategoryIds,
    IReadOnlyList<ItineraryDayRequest> Itinerary,
    IReadOnlyList<InclusionRequest> Inclusions,
    IReadOnlyList<PriceVariantRequest> PriceVariants,
    VisaDetailsRequest? Visa);

/// <summary>One image in the gallery.</summary>
/// <param name="AssetId">An asset from <c>POST /api/v1/assets/uploads</c>.</param>
public sealed record ProductMediaRequest(Guid AssetId, string Caption);

/// <summary>One day of a tour or package.</summary>
/// <param name="DayNumber">1 for the first day; the n-th entry in the list is day n.</param>
/// <param name="Meals">Any of <c>Breakfast</c>, <c>Lunch</c> and <c>Dinner</c>.</param>
public sealed record ItineraryDayRequest(
    int DayNumber,
    string Title,
    string Description,
    IReadOnlyList<string> Meals,
    string Accommodation);

/// <summary>A line of what is included, or not.</summary>
/// <param name="Kind"><c>Inclusion</c> or <c>Exclusion</c>.</param>
public sealed record InclusionRequest(string Kind, string Text);

/// <summary>One price: by traveller type, room occupancy and group size.</summary>
/// <param name="Name">"Double occupancy", "Child under 12".</param>
/// <param name="PaxType"><c>Adult</c>, <c>Child</c> or <c>Infant</c>.</param>
/// <param name="Occupancy">People sharing a room, 1 to 10. Omitted when rooms do not apply.</param>
/// <param name="MinGroupSize">The smallest group this price is for. Omitted means no lower bound.</param>
/// <param name="MaxGroupSize">The largest group this price is for. Omitted means no upper bound.</param>
/// <param name="PriceMinor">In kobo. Decimals and strings like "1500.00" are refused.</param>
public sealed record PriceVariantRequest(
    string Name,
    string PaxType,
    int? Occupancy,
    int? MinGroupSize,
    int? MaxGroupSize,
    long PriceMinor);

/// <summary>What a visa product says about the visa.</summary>
/// <param name="VisaType">"Tourist", "Business".</param>
/// <param name="EntryType"><c>Single</c> or <c>Multiple</c>.</param>
/// <param name="ProcessingTimeDays">Whole days. Zero while unknown; publishing needs it above zero.</param>
/// <param name="ValidityDays">Whole days. Zero while unknown; publishing needs it above zero.</param>
/// <param name="ConsularFeeMinor">The embassy's fee, in kobo. Kept apart from the service fee.</param>
/// <param name="ServiceFeeMinor">The agency's own fee, in kobo.</param>
/// <param name="Documents">The applicant's checklist. Publishing needs at least one.</param>
public sealed record VisaDetailsRequest(
    string VisaType,
    string EntryType,
    int ProcessingTimeDays,
    int ValidityDays,
    long ConsularFeeMinor,
    long ServiceFeeMinor,
    IReadOnlyList<VisaDocumentRequest> Documents);

/// <summary>One document a visa applicant must provide.</summary>
public sealed record VisaDocumentRequest(string Label, bool IsMandatory);

/// <summary>A whole product: everything in <see cref="ProductRequest"/>, plus where it stands.</summary>
/// <param name="Status"><c>Draft</c>, <c>Published</c> or <c>Archived</c>.</param>
/// <param name="PublishedAt">When it went live. Set exactly while it is published.</param>
/// <param name="PublishProblems">
/// Every reason it cannot be published yet, from the server's own rules — empty when it can. The
/// console's checklist reads this rather than keeping a copy of the rules. On a published product
/// it is normally empty; it fills if a booking window has since ended.
/// </param>
public sealed record ProductResponse(
    Guid Id,
    string ProductType,
    string Title,
    string Slug,
    string Summary,
    string Description,
    string DestinationCountry,
    string DestinationCity,
    int? DurationDays,
    string Currency,
    long BasePriceMinor,
    DateOnly? AvailableFrom,
    DateOnly? AvailableTo,
    Guid? HeroAssetId,
    IReadOnlyList<ProductMediaResponse> Media,
    IReadOnlyList<Guid> CategoryIds,
    IReadOnlyList<ItineraryDayResponse> Itinerary,
    IReadOnlyList<InclusionResponse> Inclusions,
    IReadOnlyList<PriceVariantResponse> PriceVariants,
    VisaDetailsResponse? Visa,
    string Status,
    DateTimeOffset? PublishedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<PublishProblemResponse> PublishProblems);

/// <summary>One image in the gallery.</summary>
/// <param name="PreviewUrl">
/// A signed, short-lived link to show the image in the console. Null unless the asset has been
/// scanned clean and processed. Fetch the product again for a fresh one rather than storing it.
/// </param>
public sealed record ProductMediaResponse(Guid AssetId, string Caption, string? PreviewUrl);

/// <summary>One day of a tour or package.</summary>
/// <param name="Meals"><c>Breakfast</c>, <c>Lunch</c> and <c>Dinner</c>, in that order, as included.</param>
public sealed record ItineraryDayResponse(
    int DayNumber,
    string Title,
    string Description,
    IReadOnlyList<string> Meals,
    string Accommodation);

/// <summary>A line of what is included, or not.</summary>
/// <param name="Kind"><c>Inclusion</c> or <c>Exclusion</c>.</param>
public sealed record InclusionResponse(string Kind, string Text);

/// <summary>One price.</summary>
/// <param name="PaxType"><c>Adult</c>, <c>Child</c> or <c>Infant</c>.</param>
public sealed record PriceVariantResponse(
    string Name,
    string PaxType,
    int? Occupancy,
    int? MinGroupSize,
    int? MaxGroupSize,
    long PriceMinor);

/// <summary>What a visa product says about the visa.</summary>
/// <param name="EntryType"><c>Single</c> or <c>Multiple</c>.</param>
public sealed record VisaDetailsResponse(
    string VisaType,
    string EntryType,
    int ProcessingTimeDays,
    int ValidityDays,
    long ConsularFeeMinor,
    long ServiceFeeMinor,
    IReadOnlyList<VisaDocumentResponse> Documents);

/// <summary>One document on the applicant's checklist.</summary>
public sealed record VisaDocumentResponse(string Label, bool IsMandatory);

/// <summary>One reason a product cannot be published yet.</summary>
/// <param name="Field">
/// The request field it concerns — <c>title</c>, <c>media</c>, <c>availableTo</c>,
/// <c>visa.documents</c> — so the checklist can link to the place in the editor that fixes it.
/// </param>
/// <param name="Message">Written for the agent, saying what to do.</param>
public sealed record PublishProblemResponse(string Field, string Message);

/// <summary>A product as a row of the console's list.</summary>
/// <param name="HeroPreviewUrl">
/// A signed, short-lived link to the cover image, or the first image when no cover is chosen. Null
/// when there is no image that has been scanned clean.
/// </param>
/// <param name="PublishProblemCount">How many things stop it being published. Zero when nothing does.</param>
public sealed record ProductSummaryResponse(
    Guid Id,
    string ProductType,
    string Title,
    string Slug,
    string Status,
    string DestinationCity,
    string DestinationCountry,
    int? DurationDays,
    long BasePriceMinor,
    string Currency,
    string? HeroPreviewUrl,
    DateTimeOffset UpdatedAt,
    int PublishProblemCount);

/// <summary>A new category or theme for the agency's products.</summary>
/// <param name="Name">Unique, ignoring case, among the agency's categories of the same type.</param>
/// <param name="Type"><c>Category</c> or <c>Theme</c>.</param>
public sealed record CategoryRequest(string Name, string Type);

/// <summary>One of the agency's categories or themes.</summary>
/// <param name="Type"><c>Category</c> or <c>Theme</c>.</param>
public sealed record CategoryResponse(Guid Id, string Name, string Type);
