using TripsAgent.Domain.Common;

namespace TripsAgent.Domain.Catalog;

// The rows a product is made of. Each carries its own agency_id rather than relying on being
// reachable through product_id: that is what makes every one of them ITenantScoped, so the EF
// filter and the row-level security policy apply to each table directly (ADR-0006). They are
// created and changed only through Product, which keeps them in line with one another.

/// <summary>One image in a product's gallery. Detaching it never deletes the asset itself.</summary>
public sealed class ProductMedia : Entity, ITenantScoped
{
    private ProductMedia()
    {
    }

    internal static ProductMedia Create(Guid agencyId, Guid productId, Guid assetId, int position, string? caption) =>
        new()
        {
            AgencyId = agencyId,
            ProductId = productId,
            AssetId = assetId,
            Position = position,
            Caption = caption,
        };

    public Guid AgencyId { get; private set; }

    public Guid ProductId { get; private set; }

    public Guid AssetId { get; private set; }

    /// <summary>0 for the first image in the gallery.</summary>
    public int Position { get; private set; }

    public string? Caption { get; private set; }

    internal void Update(int position, string? caption)
    {
        Position = position;
        Caption = caption;
    }
}

/// <summary>A product tagged with one of the agency's categories or themes.</summary>
public sealed class ProductCategoryLink : Entity, ITenantScoped
{
    private ProductCategoryLink()
    {
    }

    internal static ProductCategoryLink Create(Guid agencyId, Guid productId, Guid categoryId) =>
        new()
        {
            AgencyId = agencyId,
            ProductId = productId,
            CategoryId = categoryId,
        };

    public Guid AgencyId { get; private set; }

    public Guid ProductId { get; private set; }

    public Guid CategoryId { get; private set; }
}

/// <summary>One day of a tour or package.</summary>
/// <remarks>
/// The meals are three yes/no columns rather than a list, so a psql query reads
/// <c>breakfast_included</c> rather than decoding an array or a bit mask.
/// </remarks>
public sealed class TourItineraryDay : Entity, ITenantScoped
{
    private TourItineraryDay()
    {
        Title = string.Empty;
        Description = string.Empty;
    }

    internal static TourItineraryDay Create(Guid agencyId, Guid productId, ItineraryDayContent content)
    {
        var day = new TourItineraryDay
        {
            AgencyId = agencyId,
            ProductId = productId,
        };

        day.Update(content);
        return day;
    }

    public Guid AgencyId { get; private set; }

    public Guid ProductId { get; private set; }

    /// <summary>1 for the first day. Unique within the product.</summary>
    public int DayNumber { get; private set; }

    public string Title { get; private set; }

    public string Description { get; private set; }

    public bool BreakfastIncluded { get; private set; }

    public bool LunchIncluded { get; private set; }

    public bool DinnerIncluded { get; private set; }

    public string? Accommodation { get; private set; }

    /// <summary>The meals included, in the order they are eaten.</summary>
    public IReadOnlyList<Meal> Meals =>
    [
        .. BreakfastIncluded ? [Meal.Breakfast] : Array.Empty<Meal>(),
        .. LunchIncluded ? [Meal.Lunch] : Array.Empty<Meal>(),
        .. DinnerIncluded ? [Meal.Dinner] : Array.Empty<Meal>(),
    ];

    internal ItineraryDayContent ToContent() => new(DayNumber, Title, Description, Meals, Accommodation);

    internal void Update(ItineraryDayContent content)
    {
        DayNumber = content.DayNumber;
        Title = content.Title;
        Description = content.Description;
        BreakfastIncluded = content.Meals.Contains(Meal.Breakfast);
        LunchIncluded = content.Meals.Contains(Meal.Lunch);
        DinnerIncluded = content.Meals.Contains(Meal.Dinner);
        Accommodation = content.Accommodation;
    }
}

/// <summary>A line of "what's included" or "what's not".</summary>
public sealed class ProductInclusion : Entity, ITenantScoped
{
    private ProductInclusion() => Text = string.Empty;

    internal static ProductInclusion Create(Guid agencyId, Guid productId, int position, InclusionContent content) =>
        new()
        {
            AgencyId = agencyId,
            ProductId = productId,
            Position = position,
            Kind = content.Kind,
            Text = content.Text,
        };

    public Guid AgencyId { get; private set; }

    public Guid ProductId { get; private set; }

    public InclusionKind Kind { get; private set; }

    public string Text { get; private set; }

    /// <summary>0 for the first line. One sequence across inclusions and exclusions, in the agent's order.</summary>
    public int Position { get; private set; }
}

/// <summary>
/// One price on a product: by traveller type, by room occupancy and by group size.
/// </summary>
/// <remarks>
/// Whether this is the price the traveller pays or a rate the markup engine adds to is open
/// questions 4 and 25 in the plan. It is stored as the agent typed it either way; what it means
/// is decided where it is priced, never by rewriting it here.
/// </remarks>
public sealed class ProductPriceVariant : Entity, ITenantScoped
{
    private ProductPriceVariant() => Name = string.Empty;

    internal static ProductPriceVariant Create(Guid agencyId, Guid productId, int position, PriceVariantContent content) =>
        new()
        {
            AgencyId = agencyId,
            ProductId = productId,
            Position = position,
            Name = content.Name,
            PaxType = content.PaxType,
            Occupancy = content.Occupancy,
            MinGroupSize = content.MinGroupSize,
            MaxGroupSize = content.MaxGroupSize,
            PriceMinor = content.PriceMinor,
        };

    public Guid AgencyId { get; private set; }

    public Guid ProductId { get; private set; }

    /// <summary>"Double occupancy", "Child under 12".</summary>
    public string Name { get; private set; }

    public PaxType PaxType { get; private set; }

    public int? Occupancy { get; private set; }

    public int? MinGroupSize { get; private set; }

    public int? MaxGroupSize { get; private set; }

    public Money PriceMinor { get; private set; }

    public int Position { get; private set; }

    internal PriceVariantContent ToContent() =>
        new(Name, PaxType, Occupancy, MinGroupSize, MaxGroupSize, PriceMinor);
}

/// <summary>What a visa product says about the visa itself. One per visa product.</summary>
public sealed class VisaDetails : Entity, ITenantScoped
{
    private readonly List<VisaDocumentRequirement> _documents = [];

    private VisaDetails() => VisaType = string.Empty;

    internal static VisaDetails Create(Guid agencyId, Guid productId, VisaContent content)
    {
        var details = new VisaDetails
        {
            AgencyId = agencyId,
            ProductId = productId,
        };

        details.Update(content);
        return details;
    }

    public Guid AgencyId { get; private set; }

    /// <summary>The visa product. Unique: a product has at most one set of visa details.</summary>
    public Guid ProductId { get; private set; }

    /// <summary>"Tourist", "Business", "Transit".</summary>
    public string VisaType { get; private set; }

    public VisaEntryType EntryType { get; private set; }

    public int ProcessingTimeDays { get; private set; }

    public int ValidityDays { get; private set; }

    /// <summary>The embassy's fee. Stored apart from the service fee and never summed with it in storage.</summary>
    public Money ConsularFeeMinor { get; private set; }

    /// <summary>The agency's own fee for handling the application.</summary>
    public Money ServiceFeeMinor { get; private set; }

    public IReadOnlyList<VisaDocumentRequirement> Documents => _documents;

    internal VisaContent ToContent() => new(
        VisaType,
        EntryType,
        ProcessingTimeDays,
        ValidityDays,
        ConsularFeeMinor,
        ServiceFeeMinor,
        _documents.OrderBy(document => document.Position)
            .Select(document => new VisaDocumentContent(document.Label, document.IsMandatory))
            .ToList());

    internal void Update(VisaContent content)
    {
        VisaType = content.VisaType;
        EntryType = content.EntryType;
        ProcessingTimeDays = content.ProcessingTimeDays;
        ValidityDays = content.ValidityDays;
        ConsularFeeMinor = content.ConsularFeeMinor;
        ServiceFeeMinor = content.ServiceFeeMinor;

        // The checklist is replaced as a whole, in the order the agent arranged it.
        _documents.Clear();

        for (var position = 0; position < content.Documents.Count; position++)
        {
            _documents.Add(VisaDocumentRequirement.Create(AgencyId, Id, position, content.Documents[position]));
        }
    }
}

/// <summary>One document a visa applicant has to provide.</summary>
public sealed class VisaDocumentRequirement : Entity, ITenantScoped
{
    private VisaDocumentRequirement() => Label = string.Empty;

    internal static VisaDocumentRequirement Create(Guid agencyId, Guid visaDetailsId, int position, VisaDocumentContent content) =>
        new()
        {
            AgencyId = agencyId,
            VisaDetailsId = visaDetailsId,
            Position = position,
            Label = content.Label,
            IsMandatory = content.IsMandatory,
        };

    public Guid AgencyId { get; private set; }

    public Guid VisaDetailsId { get; private set; }

    /// <summary>"Passport valid for six months", "Bank statement".</summary>
    public string Label { get; private set; }

    public bool IsMandatory { get; private set; }

    public int Position { get; private set; }
}
