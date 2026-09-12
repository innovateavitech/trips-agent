using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore;
using TripsAgent.Application.Assets;
using TripsAgent.Application.Persistence;
using TripsAgent.Contracts.Storefront;
using TripsAgent.Domain.Assets;
using TripsAgent.Domain.Catalog;

namespace TripsAgent.Application.Storefront;

/// <summary>How a traveller has narrowed the catalog down.</summary>
/// <param name="ProductType"><c>Tour</c>, <c>Package</c> or <c>Visa</c>. Null for all of them.</param>
/// <param name="Destination">A city, or a two-letter country code. Matched whole, ignoring case.</param>
/// <param name="Category">One of the agency's own category names.</param>
/// <param name="MaxPriceMinor">The most they want to pay, in minor units.</param>
/// <param name="Search">Words to look for in the title, summary and destination.</param>
public sealed record PublicCatalogQuery(
    string? ProductType = null,
    string? Destination = null,
    string? Category = null,
    long? MinPriceMinor = null,
    long? MaxPriceMinor = null,
    string? Search = null,
    int Page = 1,
    int PageSize = PublicCatalogService.DefaultPageSize);

/// <summary>
/// The agency's published products, as travellers browse them on its site (issue 60).
/// </summary>
/// <remarks>
/// <para>
/// <b>Published only, and sell prices only.</b> A draft or archived product is invisible here, and
/// nothing on the way out carries a net rate, a markup or the agency's internal ids. The tenant
/// filter has already scoped every query to the agency the hostname resolved to.
/// </para>
/// <para>
/// <b>Descriptions are plain text.</b> They are stored as the agent typed them and returned as they
/// are stored; the storefront renders them as text. Neither end ever treats them as HTML, so an
/// agent cannot put a script — their own or a pasted one — on a page their customers visit.
/// </para>
/// </remarks>
public sealed class PublicCatalogService
{
    /// <summary>How many products a catalog page shows.</summary>
    public const int DefaultPageSize = 12;

    /// <summary>The most a caller may ask for at once, however large a page they request.</summary>
    public const int MaxPageSize = 48;

    /// <summary>A card in a grid: big enough to look good, small enough for a page of them.</summary>
    private static readonly AssetVariantKind[] CardPreference =
        [AssetVariantKind.Medium, AssetVariantKind.Large, AssetVariantKind.Thumbnail, AssetVariantKind.Original];

    /// <summary>A product's own page, where one image fills the width.</summary>
    private static readonly AssetVariantKind[] DetailPreference =
        [AssetVariantKind.Large, AssetVariantKind.Medium, AssetVariantKind.Original, AssetVariantKind.Thumbnail];

    private readonly IAppDbContext _db;
    private readonly AssetDelivery _delivery;

    public PublicCatalogService(IAppDbContext db, AssetDelivery delivery)
    {
        _db = db;
        _delivery = delivery;
    }

    /// <summary>One page of the agency's published products, newest first.</summary>
    public async Task<PublicCatalogResponse> BrowseAsync(
        PublicCatalogQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var page = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize, 1, MaxPageSize);

        var categoryNames = await CategoryNamesAsync(cancellationToken);
        var published = Published();

        var filtered = Narrow(published, query, categoryNames);

        var total = await filtered.CountAsync(cancellationToken);

        var products = await filtered
            .OrderByDescending(product => product.PublishedAt)
            .ThenBy(product => product.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        var images = await ImageUrlsAsync(
            products.Select(CoverAssetId).OfType<Guid>().Distinct().ToList(),
            CardPreference,
            cancellationToken);

        return new PublicCatalogResponse(
            products.Select(product => Summarise(product, categoryNames, images)).ToList(),
            total,
            page,
            pageSize,
            await FiltersAsync(published, categoryNames, cancellationToken));
    }

    /// <summary>One published product by its slug, or null when the agency has no such page.</summary>
    /// <remarks>
    /// A draft, an archived product and another agency's product are all the same "no such page":
    /// nothing here tells an outsider that something exists but is hidden.
    /// </remarks>
    public async Task<PublicProductResponse?> GetAsync(string slug, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(slug))
        {
            return null;
        }

        var normalised = slug.Trim().ToLowerInvariant();

        var product = await Published()
            .Include(candidate => candidate.Media)
            .Include(candidate => candidate.Categories)
            .Include(candidate => candidate.Itinerary)
            .Include(candidate => candidate.Inclusions)
            .Include(candidate => candidate.PriceVariants)
            .Include(candidate => candidate.Visa)
            .FirstOrDefaultAsync(candidate => candidate.Slug == normalised, cancellationToken);

        if (product is null)
        {
            return null;
        }

        var categoryNames = await CategoryNamesAsync(cancellationToken);

        var ordered = OrderedMedia(product).ToList();

        var images = await ImageUrlsAsync(
            ordered.Select(media => media.AssetId).Distinct().ToList(),
            DetailPreference,
            cancellationToken);

        return new PublicProductResponse(
            product.Slug,
            product.ProductType.ToString(),
            product.Title,
            product.Summary,
            product.Description,
            product.DestinationCity ?? string.Empty,
            product.DestinationCountry ?? string.Empty,
            product.DurationDays,
            product.Currency,
            FromPriceMinor(product),
            product.AvailableFrom,
            product.AvailableTo,
            CategoriesOf(product, categoryNames),
            ordered
                .Where(media => images.ContainsKey(media.AssetId))
                .Select(media => new PublicProductImage(images[media.AssetId], media.Caption ?? string.Empty))
                .ToList(),
            product.Itinerary
                .OrderBy(day => day.DayNumber)
                .Select(day => new PublicItineraryDay(
                    day.DayNumber,
                    day.Title,
                    day.Description,
                    day.Meals.Select(meal => meal.ToString()).ToList(),
                    day.Accommodation ?? string.Empty))
                .ToList(),
            product.Inclusions
                .Select(inclusion => new PublicInclusion(inclusion.Kind.ToString(), inclusion.Text))
                .ToList(),
            product.PriceVariants
                .OrderBy(variant => variant.PriceMinor.AmountMinor)
                .Select(variant => new PublicPriceOption(
                    variant.Name,
                    variant.PaxType.ToString(),
                    variant.Occupancy,
                    variant.PriceMinor.AmountMinor))
                .ToList(),
            product.Visa is { } visa
                ? new PublicVisaDetails(
                    visa.VisaType,
                    visa.EntryType.ToString(),
                    visa.ProcessingTimeDays,
                    visa.ValidityDays,
                    // One figure, because one figure is what the traveller pays. The split between
                    // the consulate's fee and the agency's service fee is the agency's business.
                    visa.ConsularFeeMinor.AmountMinor + visa.ServiceFeeMinor.AmountMinor,
                    visa.Documents.Select(document => new PublicVisaDocument(document.Label, document.IsMandatory)).ToList())
                : null,
            product.UpdatedAt);
    }

    /// <summary>
    /// Every published product's slug and when it last changed, for the site's <c>sitemap.xml</c>.
    /// </summary>
    public Task<List<(string Slug, DateTimeOffset UpdatedAt)>> SitemapEntriesAsync(CancellationToken cancellationToken = default) =>
        Published()
            .OrderBy(product => product.Slug)
            .Select(product => new ValueTuple<string, DateTimeOffset>(product.Slug, product.UpdatedAt))
            .ToListAsync(cancellationToken);

    /// <summary>
    /// The products a product-grid block should show: the ones it names, in the order it names them,
    /// or the newest published, and always only what is published today.
    /// </summary>
    public async Task<IReadOnlyList<PublicProductSummary>> ForGridAsync(
        ProductGridBlockConfig grid,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(grid);

        var limit = Math.Clamp(grid.Limit, 1, MaxPageSize);
        var query = Published();

        if (Enum.TryParse<ProductType>(grid.ProductType, ignoreCase: true, out var productType))
        {
            query = query.Where(product => product.ProductType == productType);
        }

        var selected = string.Equals(grid.Mode, "Selected", StringComparison.OrdinalIgnoreCase)
                       && grid.ProductIds.Count > 0;

        List<Product> products;

        if (selected)
        {
            var ids = grid.ProductIds.Take(MaxPageSize).ToList();

            var found = await query.Where(product => ids.Contains(product.Id)).ToListAsync(cancellationToken);

            // In the order the agent arranged them. One that has since been unpublished simply
            // drops out rather than leaving a hole or an error.
            products = ids
                .Select(id => found.FirstOrDefault(product => product.Id == id))
                .OfType<Product>()
                .Take(limit)
                .ToList();
        }
        else
        {
            products = await query
                .OrderByDescending(product => product.PublishedAt)
                .ThenBy(product => product.Id)
                .Take(limit)
                .ToListAsync(cancellationToken);
        }

        var categoryNames = await CategoryNamesAsync(cancellationToken);

        var images = await ImageUrlsAsync(
            products.Select(CoverAssetId).OfType<Guid>().Distinct().ToList(),
            CardPreference,
            cancellationToken);

        return products.Select(product => Summarise(product, categoryNames, images)).ToList();
    }

    /// <summary>The agency's published products. Everything public starts here.</summary>
    private IQueryable<Product> Published() =>
        _db.Products.AsNoTracking().Where(product => product.Status == ProductStatus.Published);

    /// <remarks>
    /// <c>ToLower()</c> and <c>Contains</c> rather than <c>string.Equals(…, StringComparison)</c>:
    /// this is an expression tree the database runs, and EF Core cannot translate the comparison
    /// overloads CA1862 asks for. Lower-casing both sides is what it does translate.
    /// </remarks>
    [SuppressMessage(
        "Globalization",
        "CA1862:Use the StringComparison method overloads to perform case-insensitive string comparisons",
        Justification = "Translated to SQL by EF Core, which does not support the StringComparison overloads.")]
    private static IQueryable<Product> Narrow(
        IQueryable<Product> products,
        PublicCatalogQuery query,
        IReadOnlyDictionary<Guid, string> categoryNames)
    {
        if (Enum.TryParse<ProductType>(query.ProductType, ignoreCase: true, out var productType))
        {
            products = products.Where(product => product.ProductType == productType);
        }

        if (!string.IsNullOrWhiteSpace(query.Destination))
        {
            // Matched on lower case rather than with ILIKE, so this stays provider-agnostic: the
            // Application layer must not know it is talking to PostgreSQL.
            var destination = query.Destination.Trim().ToLowerInvariant();

            products = products.Where(product =>
                product.DestinationCity != null && product.DestinationCity.ToLower() == destination
                || product.DestinationCountry != null && product.DestinationCountry.ToLower() == destination);
        }

        if (!string.IsNullOrWhiteSpace(query.Category))
        {
            var categoryIds = categoryNames
                .Where(pair => string.Equals(pair.Value, query.Category.Trim(), StringComparison.OrdinalIgnoreCase))
                .Select(pair => pair.Key)
                .ToList();

            products = products.Where(product =>
                product.Categories.Any(link => categoryIds.Contains(link.CategoryId)));
        }

        if (query.MinPriceMinor is { } min)
        {
            products = products.Where(product => product.BasePriceMinor >= new Domain.Common.Money(min));
        }

        if (query.MaxPriceMinor is { } max)
        {
            products = products.Where(product => product.BasePriceMinor <= new Domain.Common.Money(max));
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            // Contains, not a hand-built LIKE pattern: the provider escapes % and _ for us, so a
            // traveller typing one searches for that character rather than matching everything.
            var term = query.Search.Trim().ToLowerInvariant();

            products = products.Where(product =>
                product.Title.ToLower().Contains(term)
                || product.Summary.ToLower().Contains(term)
                || product.DestinationCity != null && product.DestinationCity.ToLower().Contains(term)
                || product.DestinationCountry != null && product.DestinationCountry.ToLower().Contains(term));
        }

        return products;
    }

    /// <summary>
    /// The filter values worth offering: the ones this agency's published products actually have.
    /// </summary>
    private static async Task<PublicCatalogFilters> FiltersAsync(
        IQueryable<Product> published,
        IReadOnlyDictionary<Guid, string> categoryNames,
        CancellationToken cancellationToken)
    {
        var facts = await published
            .Select(product => new
            {
                product.ProductType,
                product.DestinationCity,
                product.DestinationCountry,
                Price = product.BasePriceMinor,
            })
            .ToListAsync(cancellationToken);

        if (facts.Count == 0)
        {
            return new PublicCatalogFilters([], [], [], null, null);
        }

        var usedCategoryIds = await published
            .SelectMany(product => product.Categories.Select(link => link.CategoryId))
            .Distinct()
            .ToListAsync(cancellationToken);

        // Cities, not countries: a country is stored as its two-letter code, and "NG" is not a
        // choice anyone browsing a holiday would recognise. The destination filter still matches
        // either, so a link built from a country code keeps working.
        var destinations = facts
            .Select(fact => fact.DestinationCity)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new PublicCatalogFilters(
            facts.Select(fact => fact.ProductType.ToString()).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList(),
            destinations,
            usedCategoryIds
                .Select(id => categoryNames.GetValueOrDefault(id))
                .OfType<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            facts.Min(fact => fact.Price.AmountMinor),
            facts.Max(fact => fact.Price.AmountMinor));
    }

    private async Task<IReadOnlyDictionary<Guid, string>> CategoryNamesAsync(CancellationToken cancellationToken) =>
        await _db.ProductCategories.AsNoTracking()
            .ToDictionaryAsync(category => category.Id, category => category.Name, cancellationToken);

    private static PublicProductSummary Summarise(
        Product product,
        IReadOnlyDictionary<Guid, string> categoryNames,
        IReadOnlyDictionary<Guid, string> images)
    {
        var cover = CoverAssetId(product);

        return new PublicProductSummary(
            product.Slug,
            product.ProductType.ToString(),
            product.Title,
            product.Summary,
            product.DestinationCity ?? string.Empty,
            product.DestinationCountry ?? string.Empty,
            product.DurationDays,
            product.Currency,
            FromPriceMinor(product),
            CategoriesOf(product, categoryNames),
            cover is { } assetId ? images.GetValueOrDefault(assetId) : null);
    }

    private static List<string> CategoriesOf(Product product, IReadOnlyDictionary<Guid, string> categoryNames) =>
        product.Categories
            .Select(link => categoryNames.GetValueOrDefault(link.CategoryId))
            .OfType<string>()
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// The lowest price anyone pays: the cheapest variant, or the base price when there are none.
    /// Shown as "from", so it must never be higher than something the traveller can actually buy.
    /// </summary>
    private static long FromPriceMinor(Product product) =>
        product.PriceVariants.Count == 0
            ? product.BasePriceMinor.AmountMinor
            : Math.Min(
                product.BasePriceMinor.AmountMinor,
                product.PriceVariants.Min(variant => variant.PriceMinor.AmountMinor));

    /// <summary>The cover image, else the first one in the gallery.</summary>
    private static Guid? CoverAssetId(Product product) =>
        product.HeroAssetId ?? OrderedMedia(product).FirstOrDefault()?.AssetId;

    private static IEnumerable<ProductMedia> OrderedMedia(Product product) =>
        product.Media
            .OrderByDescending(media => media.AssetId == product.HeroAssetId)
            .ThenBy(media => media.Position);

    private async Task<IReadOnlyDictionary<Guid, string>> ImageUrlsAsync(
        List<Guid> assetIds,
        AssetVariantKind[] preference,
        CancellationToken cancellationToken)
    {
        if (assetIds.Count == 0)
        {
            return new Dictionary<Guid, string>();
        }

        var assets = await _db.Assets.AsNoTracking()
            .Where(asset => assetIds.Contains(asset.Id))
            .ToListAsync(cancellationToken);

        var variants = await _db.AssetVariants.AsNoTracking()
            .Where(variant => assetIds.Contains(variant.AssetId))
            .ToListAsync(cancellationToken);

        var urls = new Dictionary<Guid, string>();

        foreach (var asset in assets)
        {
            // AssetDelivery is the only place that signs a link, and it refuses for anything that
            // has not been scanned clean. An image without a link is simply absent from the page.
            var links = await _delivery.LinksForAsync(asset, variants, cancellationToken);

            var link = preference
                .Select(kind => links.FirstOrDefault(candidate => candidate.Kind == kind))
                .FirstOrDefault(candidate => candidate is not null);

            if (link is not null)
            {
                urls[asset.Id] = link.Url;
            }
        }

        return urls;
    }
}
