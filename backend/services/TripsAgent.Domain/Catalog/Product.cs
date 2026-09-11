using TripsAgent.Domain.Auditing;
using TripsAgent.Domain.Common;

namespace TripsAgent.Domain.Catalog;

/// <summary>
/// A tour, package or visa an agency sells under its own brand, with everything written about it:
/// the gallery, the itinerary, what is included, the prices and the visa details.
/// </summary>
/// <remarks>
/// <para>
/// <b>One product, one save.</b> The console edits a whole product and sends the whole thing back;
/// <see cref="TryRevise"/> takes it as one <see cref="ProductContent"/> and brings every child table
/// in line with it. The application saves that with a single <c>SaveChanges</c>, which is one
/// database transaction: the basics, the days and the prices all change together, or none of them do.
/// </para>
/// <para>
/// <b>Status</b> moves Draft → Published → Archived, with unpublish taking a published or archived
/// product back to a draft. A published product is live on the storefront, so it must stay
/// publishable: an edit that would break a publish rule is refused, not saved.
/// </para>
/// <para>
/// A product is never deleted. Order lines point at it, and a sale must always be able to say what
/// was sold — archiving is how a product is retired.
/// </para>
/// </remarks>
public sealed class Product : Entity, IAuditableEntity, ITenantScoped, IAuditLogged
{
    private readonly List<ProductMedia> _media = [];
    private readonly List<ProductCategoryLink> _categories = [];
    private readonly List<TourItineraryDay> _itinerary = [];
    private readonly List<ProductInclusion> _inclusions = [];
    private readonly List<ProductPriceVariant> _priceVariants = [];

    private Product()
    {
        Title = string.Empty;
        Slug = string.Empty;
        Summary = string.Empty;
        Description = string.Empty;
        Currency = string.Empty;
    }

    /// <summary>
    /// Starts a draft. Throws when <paramref name="content"/> fails <see cref="ProductRules.Validate"/>
    /// or <paramref name="slug"/> is not a slug: the application checks both first and turns them into
    /// messages; this is the guard behind that.
    /// </summary>
    public static Product CreateDraft(Guid agencyId, ProductContent content, string slug)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(agencyId, Guid.Empty);
        ArgumentNullException.ThrowIfNull(content);

        var product = new Product
        {
            AgencyId = agencyId,
            Status = ProductStatus.Draft,
        };

        product.Apply(content.Normalised(), slug);

        return product;
    }

    /// <inheritdoc />
    public Guid AgencyId { get; private set; }

    public ProductType ProductType { get; private set; }

    public string Title { get; private set; }

    /// <summary>Unique within the agency. Part of the storefront URL.</summary>
    public string Slug { get; private set; }

    public string Summary { get; private set; }

    public string Description { get; private set; }

    public string? DestinationCountry { get; private set; }

    public string? DestinationCity { get; private set; }

    public int? DurationDays { get; private set; }

    public string Currency { get; private set; }

    /// <summary>The "from" price, in minor units.</summary>
    public Money BasePriceMinor { get; private set; }

    public DateOnly? AvailableFrom { get; private set; }

    public DateOnly? AvailableTo { get; private set; }

    /// <summary>The cover image: always one of <see cref="Media"/>.</summary>
    public Guid? HeroAssetId { get; private set; }

    public ProductStatus Status { get; private set; }

    /// <summary>When the product went live. Set exactly while it is <see cref="ProductStatus.Published"/>.</summary>
    public DateTimeOffset? PublishedAt { get; private set; }

    /// <summary>
    /// Bumped by every change, and checked by the database on every save.
    /// </summary>
    /// <remarks>
    /// Two people saving the same product at once would otherwise both succeed, and the second
    /// would silently undo the first. With this, the second save fails and is told to reload.
    /// </remarks>
    public int Version { get; private set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public IReadOnlyList<ProductMedia> Media => _media;

    public IReadOnlyList<ProductCategoryLink> Categories => _categories;

    public IReadOnlyList<TourItineraryDay> Itinerary => _itinerary;

    public IReadOnlyList<ProductInclusion> Inclusions => _inclusions;

    public IReadOnlyList<ProductPriceVariant> PriceVariants => _priceVariants;

    /// <summary>Only ever set on a visa product.</summary>
    public VisaDetails? Visa { get; private set; }

    /// <summary>The product as a <see cref="ProductContent"/>, with every list in its stored order.</summary>
    public ProductContent ToContent() => new()
    {
        ProductType = ProductType,
        Title = Title,
        Summary = Summary,
        Description = Description,
        DestinationCountry = DestinationCountry,
        DestinationCity = DestinationCity,
        DurationDays = DurationDays,
        Currency = Currency,
        BasePriceMinor = BasePriceMinor,
        AvailableFrom = AvailableFrom,
        AvailableTo = AvailableTo,
        HeroAssetId = HeroAssetId,
        Media = _media.OrderBy(item => item.Position)
            .Select(item => new ProductMediaContent(item.AssetId, item.Caption))
            .ToList(),

        // Categories have no order of their own; sorted so the same product always reads the same.
        CategoryIds = _categories.Select(link => link.CategoryId).Order().ToList(),
        Itinerary = _itinerary.OrderBy(day => day.DayNumber).Select(day => day.ToContent()).ToList(),
        Inclusions = _inclusions.OrderBy(line => line.Position)
            .Select(line => new InclusionContent(line.Kind, line.Text))
            .ToList(),
        PriceVariants = _priceVariants.OrderBy(variant => variant.Position)
            .Select(variant => variant.ToContent())
            .ToList(),
        Visa = Visa?.ToContent(),
    };

    /// <summary>Every reason this product cannot be published on <paramref name="today"/>.</summary>
    public IReadOnlyList<ProductProblem> PublishProblems(DateOnly today) =>
        ProductPublishRules.Check(ToContent(), today);

    /// <summary>
    /// Replaces everything written about the product with <paramref name="content"/>.
    /// </summary>
    /// <returns>
    /// True when the change was made. False only for a published product that the change would leave
    /// unpublishable — <paramref name="problems"/> then says why, and nothing has changed.
    /// </returns>
    /// <exception cref="InvalidOperationException">The product is archived. Unpublish it back to a draft first.</exception>
    public bool TryRevise(
        ProductContent content,
        string slug,
        DateOnly today,
        out IReadOnlyList<ProductProblem> problems)
    {
        ArgumentNullException.ThrowIfNull(content);

        if (Status == ProductStatus.Archived)
        {
            throw new InvalidOperationException(
                "This product is archived. Restore it to a draft before changing it.");
        }

        var normalised = content.Normalised();

        // A published product is on the storefront right now. Checked against the new content
        // before any of it is applied, so a refused edit leaves the live product exactly as it was.
        if (Status == ProductStatus.Published)
        {
            problems = ProductPublishRules.Check(normalised, today);

            if (problems.Count > 0)
            {
                return false;
            }
        }

        Apply(normalised, slug);
        problems = [];
        return true;
    }

    /// <summary>Puts a draft on the storefront.</summary>
    /// <returns>True when it was published; false with every reason when it cannot be yet.</returns>
    /// <exception cref="InvalidOperationException">It is not a draft: already published, or archived.</exception>
    public bool TryPublish(DateTimeOffset now, DateOnly today, out IReadOnlyList<ProductProblem> problems)
    {
        if (Status != ProductStatus.Draft)
        {
            throw new InvalidOperationException(Status == ProductStatus.Published
                ? "This product is already published."
                : "This product is archived. Restore it to a draft before publishing it.");
        }

        problems = PublishProblems(today);

        if (problems.Count > 0)
        {
            return false;
        }

        Status = ProductStatus.Published;
        PublishedAt = now;
        Version++;
        return true;
    }

    /// <summary>
    /// Takes a published product off the storefront, or restores an archived one — either way it
    /// becomes a draft.
    /// </summary>
    /// <exception cref="InvalidOperationException">It is already a draft.</exception>
    public void Unpublish()
    {
        if (Status == ProductStatus.Draft)
        {
            throw new InvalidOperationException("This product is already a draft.");
        }

        Status = ProductStatus.Draft;
        PublishedAt = null;
        Version++;
    }

    /// <summary>Retires a draft or published product. It leaves the storefront and stays on record.</summary>
    /// <exception cref="InvalidOperationException">It is already archived.</exception>
    public void Archive()
    {
        if (Status == ProductStatus.Archived)
        {
            throw new InvalidOperationException("This product is already archived.");
        }

        Status = ProductStatus.Archived;
        PublishedAt = null;
        Version++;
    }

    private void Apply(ProductContent content, string slug)
    {
        var problems = ProductRules.Validate(content);

        if (problems.Count > 0)
        {
            throw new ArgumentException(
                "The product cannot be saved: "
                + string.Join("; ", problems.Select(problem => $"{problem.Field}: {problem.Message}")),
                nameof(content));
        }

        if (!ProductSlug.IsValid(slug))
        {
            throw new ArgumentException($"'{slug}' is not a slug: lower-case letters, digits and single hyphens.", nameof(slug));
        }

        ProductType = content.ProductType;
        Title = content.Title;
        Slug = slug;
        Summary = content.Summary;
        Description = content.Description;
        DestinationCountry = content.DestinationCountry;
        DestinationCity = content.DestinationCity;
        DurationDays = content.DurationDays;
        Currency = content.Currency;
        BasePriceMinor = content.BasePriceMinor;
        AvailableFrom = content.AvailableFrom;
        AvailableTo = content.AvailableTo;
        HeroAssetId = content.HeroAssetId;

        // Rows with a natural key — an image, a category, a day number — are updated in place, so
        // a unique index on that key can never see the old row and the new one at the same time.
        // Plain lists with no key are replaced outright.
        SyncMedia(content.Media);
        SyncCategories(content.CategoryIds);
        SyncItinerary(content.Itinerary);
        ReplaceInclusions(content.Inclusions);
        ReplacePriceVariants(content.PriceVariants);
        SyncVisa(content.Visa);

        Version++;
    }

    private void SyncMedia(IReadOnlyList<ProductMediaContent> media)
    {
        _media.RemoveAll(row => !media.Any(item => item.AssetId == row.AssetId));

        for (var position = 0; position < media.Count; position++)
        {
            var item = media[position];
            var existing = _media.Find(row => row.AssetId == item.AssetId);

            if (existing is null)
            {
                _media.Add(ProductMedia.Create(AgencyId, Id, item.AssetId, position, item.Caption));
            }
            else
            {
                existing.Update(position, item.Caption);
            }
        }
    }

    private void SyncCategories(IReadOnlyList<Guid> categoryIds)
    {
        var wanted = categoryIds.Distinct().ToList();

        _categories.RemoveAll(link => !wanted.Contains(link.CategoryId));

        foreach (var categoryId in wanted)
        {
            if (!_categories.Exists(link => link.CategoryId == categoryId))
            {
                _categories.Add(ProductCategoryLink.Create(AgencyId, Id, categoryId));
            }
        }
    }

    private void SyncItinerary(IReadOnlyList<ItineraryDayContent> days)
    {
        // Validation guarantees the new days are numbered 1..n, so every existing day is either
        // updated or, past the new last day, removed.
        _itinerary.RemoveAll(row => row.DayNumber > days.Count);

        foreach (var day in days)
        {
            var existing = _itinerary.Find(row => row.DayNumber == day.DayNumber);

            if (existing is null)
            {
                _itinerary.Add(TourItineraryDay.Create(AgencyId, Id, day));
            }
            else
            {
                existing.Update(day);
            }
        }
    }

    private void ReplaceInclusions(IReadOnlyList<InclusionContent> inclusions)
    {
        _inclusions.Clear();

        for (var position = 0; position < inclusions.Count; position++)
        {
            _inclusions.Add(ProductInclusion.Create(AgencyId, Id, position, inclusions[position]));
        }
    }

    /// <remarks>
    /// Replaced rather than matched up, because a price has no key of its own: matching by
    /// position would quietly turn yesterday's "Single room" row into today's "Double room".
    /// A quote or order line never reads these rows back — it froze its own copy of the price.
    /// </remarks>
    private void ReplacePriceVariants(IReadOnlyList<PriceVariantContent> variants)
    {
        _priceVariants.Clear();

        for (var position = 0; position < variants.Count; position++)
        {
            _priceVariants.Add(ProductPriceVariant.Create(AgencyId, Id, position, variants[position]));
        }
    }

    private void SyncVisa(VisaContent? visa)
    {
        if (visa is null)
        {
            Visa = null;
            return;
        }

        if (Visa is null)
        {
            Visa = VisaDetails.Create(AgencyId, Id, visa);
        }
        else
        {
            Visa.Update(visa);
        }
    }
}
