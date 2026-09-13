using TripsAgent.Domain.Catalog;
using TripsAgent.Domain.Common;

namespace TripsAgent.UnitTests.Catalog;

/// <summary>Complete, publishable products to start each test from, so a test changes only what it is about.</summary>
internal static class CatalogSamples
{
    public static readonly Guid Agency = Guid.CreateVersion7();

    public static readonly Guid Beach = Guid.CreateVersion7();

    public static readonly Guid StoneTown = Guid.CreateVersion7();

    /// <summary>Today, in the agency's own time zone, for every test in this folder.</summary>
    public static readonly DateOnly Today = new(2026, 9, 11);

    public static readonly DateTimeOffset Now = new(2026, 9, 11, 9, 30, 0, TimeSpan.Zero);

    /// <summary>₦150,000.00, in kobo.</summary>
    public static readonly Money Price = new(15_000_000);

    public static ProductContent Tour() => new()
    {
        ProductType = ProductType.Tour,
        Title = "Zanzibar Escape",
        Summary = "Five days on the spice island.",
        Description = "Stone Town, the north coast beaches and a spice farm.",
        DestinationCountry = "TZ",
        DestinationCity = "Zanzibar",
        DurationDays = 3,
        Currency = "NGN",
        BasePriceMinor = Price,
        AvailableFrom = Today.AddDays(10),
        AvailableTo = Today.AddDays(100),
        HeroAssetId = Beach,
        Media = [new ProductMediaContent(Beach, "Nungwi beach"), new ProductMediaContent(StoneTown, null)],
        Itinerary =
        [
            new ItineraryDayContent(1, "Arrival", "Transfer to Stone Town.", [Meal.Dinner], "Stone Town hotel"),
            new ItineraryDayContent(2, "Spice farm", "A morning among the cloves.", [Meal.Breakfast, Meal.Lunch], "Stone Town hotel"),
            new ItineraryDayContent(3, "Departure", "Transfer to the airport.", [Meal.Breakfast], null),
        ],
        Inclusions =
        [
            new InclusionContent(InclusionKind.Inclusion, "Airport transfers"),
            new InclusionContent(InclusionKind.Exclusion, "International flights"),
        ],
        PriceVariants =
        [
            new PriceVariantContent("Double occupancy", PaxType.Adult, 2, null, null, Price),
            new PriceVariantContent("Child sharing", PaxType.Child, null, null, null, new Money(7_500_000)),
        ],
    };

    public static ProductContent Visa() => new()
    {
        ProductType = ProductType.Visa,
        Title = "UK Standard Visitor Visa",
        Summary = "Six months, multiple entry.",
        DestinationCountry = "GB",
        Currency = "NGN",
        BasePriceMinor = new Money(25_000_000),
        HeroAssetId = null,
        Media = [new ProductMediaContent(Beach, null)],
        Visa = new VisaContent(
            "Tourist",
            VisaEntryType.Multiple,
            ProcessingTimeDays: 15,
            ValidityDays: 180,
            ConsularFeeMinor: new Money(17_500_000),
            ServiceFeeMinor: new Money(5_000_000),
            [
                new VisaDocumentContent("Passport valid for six months", true),
                new VisaDocumentContent("Bank statement for the last six months", true),
                new VisaDocumentContent("Letter of invitation", false),
            ]),
    };

    /// <summary>A draft with nothing in it but its type: what the console creates first.</summary>
    public static ProductContent Blank(ProductType type) => new()
    {
        ProductType = type,
        Currency = "NGN",
    };

    public static Product Draft(ProductContent? content = null, string slug = "zanzibar-escape") =>
        Product.CreateDraft(Agency, content ?? Tour(), slug);

    public static Product Published(ProductContent? content = null)
    {
        var product = Draft(content);

        if (!product.TryPublish(Now, Today, out var problems))
        {
            throw new InvalidOperationException(
                "The sample should be publishable: " + string.Join("; ", problems.Select(problem => problem.Message)));
        }

        return product;
    }

    public static IEnumerable<string> Fields(IEnumerable<ProductProblem> problems) =>
        problems.Select(problem => problem.Field);
}
