using Microsoft.EntityFrameworkCore;
using TripsAgent.Application.Assets;
using TripsAgent.Application.Persistence;
using TripsAgent.Application.Tenancy;
using TripsAgent.Domain.Assets;
using TripsAgent.Domain.Catalog;
using TripsAgent.Domain.Tenancy;

namespace TripsAgent.Application.Catalog;

/// <summary>A product, and what the console shows alongside it.</summary>
/// <param name="Product">The product as stored.</param>
/// <param name="Content">The same product as a value, every list in its stored order.</param>
/// <param name="PublishProblems">Every reason it cannot be published today. Empty when it can.</param>
/// <param name="PreviewUrls">
/// A signed preview link for each gallery image that may be shown. An image that has not been
/// scanned clean has no entry.
/// </param>
public sealed record ProductView(
    Product Product,
    ProductContent Content,
    IReadOnlyList<ProductProblem> PublishProblems,
    IReadOnlyDictionary<Guid, string> PreviewUrls);

/// <summary>A product as a row of the console's list.</summary>
/// <param name="HeroPreviewUrl">The cover image, else the first image; null when neither may be shown.</param>
public sealed record ProductSummaryView(Product Product, string? HeroPreviewUrl, int PublishProblemCount);

/// <summary>What to list. Every filter is optional.</summary>
/// <param name="Search">Matched against the title and the destination, ignoring case.</param>
public sealed record ProductListFilter(ProductType? Type, ProductStatus? Status, string? Search);

/// <summary>What came of a change to a product.</summary>
public abstract record ProductChangeOutcome
{
    private ProductChangeOutcome()
    {
    }

    /// <summary>The change was saved. <paramref name="View"/> is the product as it now stands.</summary>
    public sealed record Saved(ProductView View) : ProductChangeOutcome;

    /// <summary>The product cannot be stored like this. <paramref name="Problems"/> lists every reason.</summary>
    public sealed record Invalid(IReadOnlyList<ProductProblem> Problems) : ProductChangeOutcome;

    /// <summary>No product with that id belongs to this agency.</summary>
    public sealed record NotFound : ProductChangeOutcome;

    /// <summary>Another of the agency's products already uses <paramref name="Slug"/>. <paramref name="SuggestedSlug"/> is free.</summary>
    public sealed record SlugTaken(string Slug, string SuggestedSlug) : ProductChangeOutcome;

    /// <summary>A draft that cannot be published yet, with every reason.</summary>
    public sealed record NotPublishable(IReadOnlyList<ProductProblem> Problems) : ProductChangeOutcome;

    /// <summary>A save refused because it would leave a live product unpublishable, with every reason.</summary>
    public sealed record WouldBreakLiveProduct(IReadOnlyList<ProductProblem> Problems) : ProductChangeOutcome;

    /// <summary>The product's status does not allow this: publishing a published product, saving an archived one.</summary>
    public sealed record WrongStatus(string Reason) : ProductChangeOutcome;

    /// <summary>Someone else saved the product between this request reading it and writing it.</summary>
    public sealed record ChangedElsewhere : ProductChangeOutcome;
}

/// <summary>
/// Builds, saves, publishes and retires the calling agency's tours, packages and visas (#161).
/// </summary>
/// <remarks>
/// <para>
/// <b>A save is one transaction.</b> The product is loaded with every row it is made of, brought in
/// line with the request by <see cref="Product.TryRevise"/>, and written with one
/// <c>SaveChangesAsync</c> — which EF Core runs as a single database transaction. The basics, the
/// days, the prices and the visa checklist change together or not at all.
/// </para>
/// <para>
/// <b>Rules live in the domain.</b> <see cref="ProductRules"/> decides what can be stored and
/// <see cref="ProductPublishRules"/> what can be published. This class adds only the checks that
/// need the database: that each image is the agency's own and scanned clean, that each category
/// exists, and that the slug is free.
/// </para>
/// <para>
/// Another agency's product, image or category looks exactly like a missing one: the tenant
/// filter hides it, and row-level security hides it again below that.
/// </para>
/// </remarks>
public sealed class ProductCatalogService
{
    /// <summary>What a gallery tile shows: big enough to judge a photo, small enough to load a page of them.</summary>
    private static readonly AssetVariantKind[] GalleryPreview =
        [AssetVariantKind.Medium, AssetVariantKind.Large, AssetVariantKind.Original, AssetVariantKind.Thumbnail];

    /// <summary>What a list row shows.</summary>
    private static readonly AssetVariantKind[] ListPreview =
        [AssetVariantKind.Thumbnail, AssetVariantKind.Medium, AssetVariantKind.Large, AssetVariantKind.Original];

    private readonly IAppDbContext _db;
    private readonly ITenantContext _tenant;
    private readonly IUniqueViolationDetector _uniqueViolations;
    private readonly AssetDelivery _delivery;
    private readonly TimeProvider _clock;

    public ProductCatalogService(
        IAppDbContext db,
        ITenantContext tenant,
        IUniqueViolationDetector uniqueViolations,
        AssetDelivery delivery,
        TimeProvider clock)
    {
        _db = db;
        _tenant = tenant;
        _uniqueViolations = uniqueViolations;
        _delivery = delivery;
        _clock = clock;
    }

    /// <summary>The agency's products, most recently changed first.</summary>
    public async Task<IReadOnlyList<ProductSummaryView>> ListAsync(
        ProductListFilter filter,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);

        var today = (await CurrentAgencyAsync(cancellationToken)).Today;

        var query = WithEveryPart(_db.Products.AsNoTracking());

        if (filter.Type is { } type)
        {
            query = query.Where(product => product.ProductType == type);
        }

        if (filter.Status is { } status)
        {
            query = query.Where(product => product.Status == status);
        }

        // Every part is loaded because the publish-problem count reads the whole product. Worked
        // out from the same rules as the editor's checklist, so the list and the editor cannot
        // disagree about whether a product is ready.
        var products = await query
            .OrderByDescending(product => product.UpdatedAt)
            .ThenByDescending(product => product.Id)
            .ToListAsync(cancellationToken);

        // Matched here rather than in SQL so "lagos" finds "Lagos" without a culture-sensitive
        // lower-casing in the query. An agency's catalog is hundreds of rows, not millions.
        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var term = filter.Search.Trim();
            products = products.Where(product => Matches(product, term)).ToList();
        }

        var rows = products
            .Select(product => (Product: product, Content: product.ToContent()))
            .Select(row => (row.Product, row.Content, Cover: row.Content.HeroAssetId ?? FirstImage(row.Content)))
            .ToList();

        var previews = await PreviewUrlsAsync(
            rows.Where(row => row.Cover is not null).Select(row => row.Cover!.Value).Distinct().ToList(),
            ListPreview,
            cancellationToken);

        return rows
            .Select(row => new ProductSummaryView(
                row.Product,
                row.Cover is { } cover ? previews.GetValueOrDefault(cover) : null,
                ProductPublishRules.Check(row.Content, today).Count))
            .ToList();
    }

    /// <summary>One product, or null when this agency has none with that id.</summary>
    public async Task<ProductView?> GetAsync(Guid productId, CancellationToken cancellationToken = default)
    {
        var product = await WithEveryPart(_db.Products.AsNoTracking())
            .FirstOrDefaultAsync(candidate => candidate.Id == productId, cancellationToken);

        if (product is null)
        {
            return null;
        }

        var today = (await CurrentAgencyAsync(cancellationToken)).Today;

        return await ViewAsync(product, today, cancellationToken);
    }

    /// <summary>Creates a draft.</summary>
    /// <param name="requestedSlug">Null or blank to work one out from the title.</param>
    public async Task<ProductChangeOutcome> CreateAsync(
        ProductContent content,
        string? requestedSlug,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);

        var agency = await CurrentAgencyAsync(cancellationToken);
        var tidy = InAgencyCurrency(content.Normalised(), agency.Agency);

        var problems = await ValidateAsync(tidy, cancellationToken);
        var slug = await ChooseSlugAsync(tidy.Title, requestedSlug, current: null, problems, cancellationToken);

        if (problems.Count > 0)
        {
            return new ProductChangeOutcome.Invalid(problems);
        }

        if (slug is SlugChoice.Taken taken)
        {
            return new ProductChangeOutcome.SlugTaken(taken.Slug, taken.Suggestion);
        }

        var product = Product.CreateDraft(agency.Agency.Id, tidy, ((SlugChoice.Use)slug).Slug);
        _db.Products.Add(product);

        return await SaveAsync(product, agency.Today, cancellationToken);
    }

    /// <summary>Saves the whole product: basics, gallery, categories, itinerary, inclusions, prices and visa details.</summary>
    /// <param name="requestedSlug">
    /// Null or blank to work one out from the title — except on a published product, which keeps
    /// the address it is live at.
    /// </param>
    public async Task<ProductChangeOutcome> SaveAsync(
        Guid productId,
        ProductContent content,
        string? requestedSlug,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);

        var product = await WithEveryPart(_db.Products)
            .FirstOrDefaultAsync(candidate => candidate.Id == productId, cancellationToken);

        if (product is null)
        {
            return new ProductChangeOutcome.NotFound();
        }

        // Before any validation: there is no point asking the agent to fix fields on a product
        // that cannot be saved whatever they send.
        if (product.Status == ProductStatus.Archived)
        {
            return new ProductChangeOutcome.WrongStatus(
                "This product is archived. Restore it to a draft before changing it.");
        }

        var agency = await CurrentAgencyAsync(cancellationToken);
        var tidy = InAgencyCurrency(content.Normalised(), agency.Agency);

        var problems = await ValidateAsync(tidy, cancellationToken);
        var slug = await ChooseSlugAsync(tidy.Title, requestedSlug, product, problems, cancellationToken);

        if (problems.Count > 0)
        {
            return new ProductChangeOutcome.Invalid(problems);
        }

        if (slug is SlugChoice.Taken taken)
        {
            return new ProductChangeOutcome.SlugTaken(taken.Slug, taken.Suggestion);
        }

        if (!product.TryRevise(tidy, ((SlugChoice.Use)slug).Slug, agency.Today, out var publishProblems))
        {
            return new ProductChangeOutcome.WouldBreakLiveProduct(publishProblems);
        }

        return await SaveAsync(product, agency.Today, cancellationToken);
    }

    /// <summary>Puts a draft on the storefront, or says every reason it cannot go yet.</summary>
    public Task<ProductChangeOutcome> PublishAsync(Guid productId, CancellationToken cancellationToken = default) =>
        ChangeStatusAsync(
            productId,
            (product, today) => product.TryPublish(_clock.GetUtcNow(), today, out var problems)
                ? null
                : new ProductChangeOutcome.NotPublishable(problems),
            cancellationToken);

    /// <summary>Takes a published product off the storefront, or restores an archived one, as a draft.</summary>
    public Task<ProductChangeOutcome> UnpublishAsync(Guid productId, CancellationToken cancellationToken = default) =>
        ChangeStatusAsync(
            productId,
            (product, _) =>
            {
                product.Unpublish();
                return null;
            },
            cancellationToken);

    /// <summary>Retires a draft or published product. It stays on record; it is never deleted.</summary>
    public Task<ProductChangeOutcome> ArchiveAsync(Guid productId, CancellationToken cancellationToken = default) =>
        ChangeStatusAsync(
            productId,
            (product, _) =>
            {
                product.Archive();
                return null;
            },
            cancellationToken);

    /// <summary>
    /// Loads a product, applies a status change, and saves it.
    /// </summary>
    /// <param name="change">
    /// Returns null when the change was made, or the outcome that stopped it. A transition the
    /// product's status does not allow throws <see cref="InvalidOperationException"/>, which becomes
    /// <see cref="ProductChangeOutcome.WrongStatus"/>.
    /// </param>
    private async Task<ProductChangeOutcome> ChangeStatusAsync(
        Guid productId,
        Func<Product, DateOnly, ProductChangeOutcome?> change,
        CancellationToken cancellationToken)
    {
        var product = await WithEveryPart(_db.Products)
            .FirstOrDefaultAsync(candidate => candidate.Id == productId, cancellationToken);

        if (product is null)
        {
            return new ProductChangeOutcome.NotFound();
        }

        var today = (await CurrentAgencyAsync(cancellationToken)).Today;

        try
        {
            if (change(product, today) is { } refused)
            {
                return refused;
            }
        }
        catch (InvalidOperationException ex)
        {
            return new ProductChangeOutcome.WrongStatus(ex.Message);
        }

        return await SaveAsync(product, today, cancellationToken);
    }

    /// <summary>Writes the tracked product and everything it is made of, in one transaction.</summary>
    private async Task<ProductChangeOutcome> SaveAsync(Product product, DateOnly today, CancellationToken cancellationToken)
    {
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            _db.ChangeTracker.Clear();
            return new ProductChangeOutcome.ChangedElsewhere();
        }
        catch (DbUpdateException ex) when (_uniqueViolations.IsUniqueViolation(ex))
        {
            // The slug was free when it was checked, and another save took it in the moments
            // since. The rejected rows stay tracked until cleared, and would fail the next save too.
            _db.ChangeTracker.Clear();

            var taken = await TakenSlugsAsync(product.Slug, excludingProductId: null, cancellationToken);
            return new ProductChangeOutcome.SlugTaken(product.Slug, ProductSlug.FirstFree(product.Slug, taken));
        }

        return new ProductChangeOutcome.Saved(await ViewAsync(product, today, cancellationToken));
    }

    /// <summary>
    /// Every reason <paramref name="content"/> cannot be stored: the domain's rules, then the
    /// checks that need the database.
    /// </summary>
    private async Task<List<ProductProblem>> ValidateAsync(ProductContent content, CancellationToken cancellationToken)
    {
        var problems = ProductRules.Validate(content).ToList();

        problems.AddRange(await CheckImagesAsync(content.Media, cancellationToken));
        problems.AddRange(await CheckCategoriesAsync(content.CategoryIds, cancellationToken));

        return problems;
    }

    /// <summary>
    /// Only the agency's own images, and only once they have been scanned clean and processed.
    /// </summary>
    /// <remarks>
    /// An image still being scanned is refused, not queued: attaching it now would put a file
    /// nobody has checked one publish away from a traveller's screen. The database repeats the
    /// "own agency" half with a composite foreign key.
    /// </remarks>
    private async Task<IReadOnlyList<ProductProblem>> CheckImagesAsync(
        IReadOnlyList<ProductMediaContent> media,
        CancellationToken cancellationToken)
    {
        var ids = media.Select(item => item.AssetId).Where(id => id != Guid.Empty).Distinct().ToList();

        if (ids.Count == 0)
        {
            return [];
        }

        var assets = await _db.Assets.AsNoTracking()
            .Where(asset => ids.Contains(asset.Id))
            .ToDictionaryAsync(asset => asset.Id, cancellationToken);

        var problems = new List<ProductProblem>();

        for (var i = 0; i < media.Count; i++)
        {
            if (media[i].AssetId == Guid.Empty)
            {
                continue;
            }

            var field = $"media[{i}].assetId";

            if (!assets.TryGetValue(media[i].AssetId, out var asset))
            {
                problems.Add(new(field, "There is no image with that id in your uploads."));
            }
            else if (asset.Status is AssetStatus.Quarantined or AssetStatus.Failed)
            {
                problems.Add(new(field, $"This file can't be used: {asset.FailureReason ?? "it did not pass its checks."}"));
            }
            else if (!asset.IsServable)
            {
                problems.Add(new(field, "This image is still being checked. Attach it once it is ready."));
            }
            else if (!asset.IsImage)
            {
                problems.Add(new(field, "Only images can go in the gallery."));
            }
        }

        return problems;
    }

    private async Task<IReadOnlyList<ProductProblem>> CheckCategoriesAsync(
        IReadOnlyList<Guid> categoryIds,
        CancellationToken cancellationToken)
    {
        var ids = categoryIds.Where(id => id != Guid.Empty).Distinct().ToList();

        if (ids.Count == 0)
        {
            return [];
        }

        var known = await _db.ProductCategories.AsNoTracking()
            .Where(category => ids.Contains(category.Id))
            .Select(category => category.Id)
            .ToListAsync(cancellationToken);

        var problems = new List<ProductProblem>();

        for (var i = 0; i < categoryIds.Count; i++)
        {
            if (categoryIds[i] != Guid.Empty && !known.Contains(categoryIds[i]))
            {
                problems.Add(new($"categoryIds[{i}]", "There is no category or theme with that id."));
            }
        }

        return problems;
    }

    /// <summary>
    /// Decides the slug a save will use. A slug that cannot be made into one is added to
    /// <paramref name="problems"/>, so it is reported with everything else.
    /// </summary>
    private async Task<SlugChoice> ChooseSlugAsync(
        string title,
        string? requested,
        Product? current,
        List<ProductProblem> problems,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(requested))
        {
            // Tidied rather than refused: "Zanzibar Escape" is plainly asking for zanzibar-escape.
            var slug = ProductSlug.Normalise(requested);

            if (slug.Length == 0)
            {
                problems.Add(new("slug", "Use letters, numbers and hyphens, like zanzibar-escape."));
                return new SlugChoice.Use(ProductSlug.Untitled);
            }

            var taken = await TakenSlugsAsync(slug, current?.Id, cancellationToken);

            return taken.Contains(slug)
                ? new SlugChoice.Taken(slug, ProductSlug.FirstFree(slug, taken))
                : new SlugChoice.Use(slug);
        }

        // A published product is live at its address. Changing the title does not move it;
        // only asking for a new slug does.
        if (current is { Status: not ProductStatus.Draft })
        {
            return new SlugChoice.Use(current.Slug);
        }

        var fromTitle = ProductSlug.FromTitle(title);

        // Saved again under the same title: keep the slug it has, even if that is a numbered one.
        if (current is not null && ProductSlug.BelongsTo(current.Slug, fromTitle))
        {
            return new SlugChoice.Use(current.Slug);
        }

        var takenByOthers = await TakenSlugsAsync(fromTitle, current?.Id, cancellationToken);
        return new SlugChoice.Use(ProductSlug.FirstFree(fromTitle, takenByOthers));
    }

    /// <summary>
    /// The agency's slugs in the family of <paramref name="baseSlug"/>: itself and anything that
    /// starts with it and a hyphen. Archived products count — they keep their slug.
    /// </summary>
    private async Task<IReadOnlySet<string>> TakenSlugsAsync(
        string baseSlug,
        Guid? excludingProductId,
        CancellationToken cancellationToken)
    {
        var prefix = baseSlug + "-";

        var slugs = await _db.Products.AsNoTracking()
            .Where(product => product.Id != excludingProductId)
            .Where(product => product.Slug == baseSlug || product.Slug.StartsWith(prefix))
            .Select(product => product.Slug)
            .ToListAsync(cancellationToken);

        return slugs.ToHashSet(StringComparer.Ordinal);
    }

    private async Task<ProductView> ViewAsync(Product product, DateOnly today, CancellationToken cancellationToken)
    {
        var content = product.ToContent();

        return new ProductView(
            product,
            content,
            ProductPublishRules.Check(content, today),
            await PreviewUrlsAsync(content.Media.Select(item => item.AssetId).ToList(), GalleryPreview, cancellationToken));
    }

    /// <summary>
    /// A signed link per asset, to the first rendition in <paramref name="preference"/> that exists.
    /// </summary>
    /// <remarks>
    /// Links come only from <see cref="AssetDelivery"/>, the one place that refuses to sign for an
    /// asset that has not been scanned clean. An asset without a link is simply absent.
    /// </remarks>
    private async Task<IReadOnlyDictionary<Guid, string>> PreviewUrlsAsync(
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

    /// <summary>The calling agency, and today's date where it is.</summary>
    /// <remarks>
    /// "Today" is the agency's own, from its time zone: a booking window that ends on the 3rd is
    /// open until midnight in Lagos, not until midnight UTC an hour earlier.
    /// </remarks>
    private async Task<(Agency Agency, DateOnly Today)> CurrentAgencyAsync(CancellationToken cancellationToken)
    {
        var agencyId = _tenant.AgencyId ?? throw new InvalidOperationException(
            "Products belong to an agency, and none is resolved for this request.");

        var agency = await _db.Agencies.AsNoTracking()
            .FirstAsync(candidate => candidate.Id == agencyId, cancellationToken);

        var now = _clock.GetUtcNow();
        var local = TimeZoneInfo.TryFindSystemTimeZoneById(agency.Timezone, out var zone)
            ? TimeZoneInfo.ConvertTime(now, zone)
            : now;

        return (agency, DateOnly.FromDateTime(local.DateTime));
    }

    /// <summary>A blank currency means the agency's own — the only one it sells in for now (open question 17).</summary>
    private static ProductContent InAgencyCurrency(ProductContent content, Agency agency) =>
        content.Currency.Length == 0 ? content with { Currency = agency.BaseCurrency } : content;

    /// <summary>The first image in the gallery, if there is one.</summary>
    private static Guid? FirstImage(ProductContent content) =>
        content.Media.Count > 0 ? content.Media[0].AssetId : null;

    private static bool Matches(Product product, string term) =>
        product.Title.Contains(term, StringComparison.OrdinalIgnoreCase)
        || (product.DestinationCity?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false)
        || string.Equals(product.DestinationCountry, term, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A product with every row it is made of. Split into one query per table, because joining six
    /// child tables in one query multiplies them together: ten images by ten days by ten lines is a
    /// thousand rows for one product.
    /// </summary>
    private static IQueryable<Product> WithEveryPart(IQueryable<Product> products) =>
        products
            .Include(product => product.Media)
            .Include(product => product.Categories)
            .Include(product => product.Itinerary)
            .Include(product => product.Inclusions)
            .Include(product => product.PriceVariants)
            .Include(product => product.Visa)
            .ThenInclude(visa => visa!.Documents)
            .AsSplitQuery();

    /// <summary>The slug a save will use, or the one it wanted and could not have.</summary>
    private abstract record SlugChoice
    {
        private SlugChoice()
        {
        }

        public sealed record Use(string Slug) : SlugChoice;

        public sealed record Taken(string Slug, string Suggestion) : SlugChoice;
    }
}
