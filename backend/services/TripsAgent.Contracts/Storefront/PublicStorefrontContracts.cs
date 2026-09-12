namespace TripsAgent.Contracts.Storefront;

/// <summary>What a site on a hostname is doing right now.</summary>
/// <remarks>
/// The storefront renders every one of these — a site that is not live still answers, in the
/// agency's own branding, rather than showing a stranger's error page.
/// </remarks>
public static class PublicSiteStatuses
{
    /// <summary>A published version is being served.</summary>
    public const string Live = "Live";

    /// <summary>The agency has a site but has never published it.</summary>
    public const string OpeningSoon = "OpeningSoon";

    /// <summary>The agency is suspended, so its shop is shut (MVP decision 14).</summary>
    public const string Offline = "Offline";

    /// <summary>An unpublished version, reached through a signed preview link.</summary>
    public const string Preview = "Preview";
}

/// <summary>
/// One of the agency's products as the storefront lists it. Only ever a published product, and only
/// ever the sell price the traveller pays.
/// </summary>
/// <remarks>
/// Deliberately not <c>ProductSummaryResponse</c>: that one carries the draft status and the count of
/// reasons a product cannot be published, which are the agency's business and nobody else's.
/// </remarks>
/// <param name="Slug">Its address on the site: <c>/tours/{slug}</c>.</param>
/// <param name="ProductType"><c>Tour</c>, <c>Package</c> or <c>Visa</c>.</param>
/// <param name="FromPriceMinor">The lowest price anyone pays, in minor units. Shown as "from".</param>
/// <param name="ImageUrl">A signed link to the cover image, or null when there is none to show.</param>
public sealed record PublicProductSummary(
    string Slug,
    string ProductType,
    string Title,
    string Summary,
    string DestinationCity,
    string DestinationCountry,
    int? DurationDays,
    string Currency,
    long FromPriceMinor,
    IReadOnlyList<string> Categories,
    string? ImageUrl);

/// <summary>
/// The products one product-grid block shows, resolved from the published snapshot.
/// </summary>
/// <remarks>
/// The caller names the block by where it sits, not by what it should hold: which page, and which
/// block on that page. What that grid means — the newest six tours, or these four in this order — is
/// read from the version the agency published, so nobody can ask a site to show products its owner
/// did not put on it.
/// </remarks>
public sealed record PublicProductGridResponse(IReadOnlyList<PublicProductSummary> Products);

/// <summary>A page of the catalog, with the choices a traveller can narrow it by.</summary>
/// <param name="Total">How many products match the filters, across every page.</param>
public sealed record PublicCatalogResponse(
    IReadOnlyList<PublicProductSummary> Products,
    int Total,
    int Page,
    int PageSize,
    PublicCatalogFilters Filters);

/// <summary>
/// The values a traveller can filter by, from what this agency actually sells — never a fixed list.
/// </summary>
/// <remarks>An empty list means the agency has nothing published that would fill it.</remarks>
/// <param name="Destinations">
/// The cities these products go to. Not countries: those are stored as two-letter codes, which is
/// not what a traveller would pick from a list.
/// </param>
public sealed record PublicCatalogFilters(
    IReadOnlyList<string> ProductTypes,
    IReadOnlyList<string> Destinations,
    IReadOnlyList<string> Categories,
    long? MinPriceMinor,
    long? MaxPriceMinor);

/// <summary>One product's own page.</summary>
/// <param name="Description">
/// Plain text, exactly as the agent typed it. The storefront renders it as text and never as HTML.
/// </param>
/// <param name="Images">The gallery, cover first. Each is a signed link to a scanned, processed image.</param>
public sealed record PublicProductResponse(
    Guid Id,
    string Slug,
    string ProductType,
    string Title,
    string Summary,
    string Description,
    string DestinationCity,
    string DestinationCountry,
    int? DurationDays,
    string Currency,
    long FromPriceMinor,
    DateOnly? AvailableFrom,
    DateOnly? AvailableTo,
    IReadOnlyList<string> Categories,
    IReadOnlyList<PublicProductImage> Images,
    IReadOnlyList<PublicItineraryDay> Itinerary,
    IReadOnlyList<PublicInclusion> Inclusions,
    IReadOnlyList<PublicPriceOption> Prices,
    PublicVisaDetails? Visa,
    DateTimeOffset UpdatedAt);

/// <summary>One image in a product's gallery.</summary>
public sealed record PublicProductImage(string Url, string Caption);

/// <summary>One day of a tour or package.</summary>
public sealed record PublicItineraryDay(
    int DayNumber,
    string Title,
    string Description,
    IReadOnlyList<string> Meals,
    string Accommodation);

/// <summary>A line of what is included, or not.</summary>
/// <param name="Kind"><c>Inclusion</c> or <c>Exclusion</c>.</param>
public sealed record PublicInclusion(string Kind, string Text);

/// <summary>One price a traveller can choose.</summary>
/// <param name="PaxType"><c>Adult</c>, <c>Child</c> or <c>Infant</c>.</param>
public sealed record PublicPriceOption(string Name, string PaxType, int? Occupancy, long PriceMinor);

/// <summary>What a visa product tells the applicant.</summary>
/// <param name="TotalFeeMinor">Consular fee plus service fee: the one figure the traveller pays.</param>
public sealed record PublicVisaDetails(
    string VisaType,
    string EntryType,
    int ProcessingTimeDays,
    int ValidityDays,
    long TotalFeeMinor,
    IReadOnlyList<PublicVisaDocument> Documents);

/// <summary>One document on the applicant's checklist.</summary>
public sealed record PublicVisaDocument(string Label, bool IsMandatory);

/// <summary>Every address on a site, for its <c>sitemap.xml</c>.</summary>
/// <param name="BaseUrl">The site's canonical address, with no trailing slash.</param>
public sealed record PublicSitemapResponse(string BaseUrl, IReadOnlyList<PublicSitemapEntry> Entries);

/// <summary>One address in the sitemap.</summary>
/// <param name="Path">A path on the site, starting with <c>/</c>.</param>
/// <param name="LastModified">When what is at that address last changed.</param>
public sealed record PublicSitemapEntry(string Path, DateTimeOffset LastModified, string ChangeFrequency);
