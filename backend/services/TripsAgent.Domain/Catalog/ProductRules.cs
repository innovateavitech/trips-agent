using System.Globalization;
using System.Text.RegularExpressions;

namespace TripsAgent.Domain.Catalog;

/// <summary>
/// What a product must be for it to be <i>stored</i> at all — the save-time rules.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately lenient about completeness. A draft is a thing being written, so a blank title, a
/// zero price or a visa with no processing time yet are all fine here; those are publish rules
/// (<see cref="ProductPublishRules"/>). What this refuses is what can never be right: a negative
/// price, day 3 followed by day 5, an itinerary on a visa, a line of text nobody wrote.
/// </para>
/// <para>
/// Returns <b>every</b> problem, not the first, so the console can mark each field at once rather
/// than making the agent fix one thing per save. Checks that need the database — does that image
/// exist, is it scanned — are the application's, and are added to this list there.
/// </para>
/// </remarks>
public static partial class ProductRules
{
    /// <summary>Every reason <paramref name="content"/> cannot be saved. Empty when it can.</summary>
    /// <remarks>Pass normalised content (<see cref="ProductContent.Normalised"/>); blank means blank after trimming.</remarks>
    public static IReadOnlyList<ProductProblem> Validate(ProductContent content)
    {
        ArgumentNullException.ThrowIfNull(content);

        var problems = new List<ProductProblem>();

        CheckBasics(content, problems);
        CheckTypeFit(content, problems);
        CheckMedia(content, problems);
        CheckCategories(content, problems);
        CheckItinerary(content, problems);
        CheckInclusions(content, problems);
        CheckPriceVariants(content, problems);

        if (content.Visa is not null)
        {
            CheckVisa(content.Visa, problems);
        }

        return problems;
    }

    /// <summary>True for a two-letter, upper-case country code.</summary>
    public static bool IsCountryCode(string? value) => value is not null && CountryCodePattern().IsMatch(value);

    /// <summary>True for a three-letter, upper-case currency code.</summary>
    public static bool IsCurrencyCode(string? value) => value is not null && CurrencyCodePattern().IsMatch(value);

    private static void CheckBasics(ProductContent content, List<ProductProblem> problems)
    {
        if (!Enum.IsDefined(content.ProductType))
        {
            problems.Add(new("productType", "Choose Tour, Package or Visa."));
        }

        TooLong(problems, "title", content.Title, CatalogLimits.MaxTitleLength);
        TooLong(problems, "summary", content.Summary, CatalogLimits.MaxSummaryLength);
        TooLong(problems, "description", content.Description, CatalogLimits.MaxDescriptionLength);
        TooLong(problems, "destinationCity", content.DestinationCity, CatalogLimits.MaxCityLength);

        if (content.DestinationCountry is not null && !IsCountryCode(content.DestinationCountry))
        {
            problems.Add(new("destinationCountry", "Use the two-letter country code, like KE for Kenya."));
        }

        if (content.DurationDays is < 1 or > CatalogLimits.MaxDurationDays)
        {
            problems.Add(new("durationDays", $"A duration is between 1 and {CatalogLimits.MaxDurationDays} days."));
        }

        if (!IsCurrencyCode(content.Currency))
        {
            problems.Add(new("currency", "Use the three-letter currency code, like NGN."));
        }

        Price(problems, "basePriceMinor", content.BasePriceMinor.AmountMinor);

        if (content.AvailableFrom is { } from && content.AvailableTo is { } to && to < from)
        {
            problems.Add(new("availableTo", "The last date can't be before the first."));
        }
    }

    /// <summary>Tours and packages have days and inclusions; a visa has visa details. Never the other way round.</summary>
    private static void CheckTypeFit(ProductContent content, List<ProductProblem> problems)
    {
        if (content.ProductType == ProductType.Visa)
        {
            if (content.Itinerary.Count > 0)
            {
                problems.Add(new("itinerary", "A visa has no itinerary. Remove the days, or make this a tour or package."));
            }

            if (content.Inclusions.Count > 0)
            {
                problems.Add(new("inclusions", "A visa has no inclusions or exclusions. List what the applicant needs as documents instead."));
            }
        }
        else if (content.Visa is not null)
        {
            problems.Add(new("visa", "Visa details only go on a visa product."));
        }
    }

    private static void CheckMedia(ProductContent content, List<ProductProblem> problems)
    {
        if (content.Media.Count > CatalogLimits.MaxMedia)
        {
            problems.Add(new("media", $"A product can have up to {CatalogLimits.MaxMedia} images."));
        }

        var seen = new HashSet<Guid>();

        for (var i = 0; i < content.Media.Count; i++)
        {
            var item = content.Media[i];

            if (item.AssetId == Guid.Empty)
            {
                problems.Add(new($"media[{i}].assetId", "Choose an uploaded image."));
            }
            else if (!seen.Add(item.AssetId))
            {
                problems.Add(new($"media[{i}].assetId", "This image is already in the gallery."));
            }

            TooLong(problems, $"media[{i}].caption", item.Caption, CatalogLimits.MaxCaptionLength);
        }

        // The cover is one of the gallery's images rather than a separate upload, so removing an
        // image from the gallery can never leave the storefront pointing at something detached.
        if (content.HeroAssetId is { } hero && !content.Media.Any(item => item.AssetId == hero))
        {
            problems.Add(new("heroAssetId", "The cover image must be one of the product's images. Add it to the gallery first."));
        }
    }

    private static void CheckCategories(ProductContent content, List<ProductProblem> problems)
    {
        if (content.CategoryIds.Count > CatalogLimits.MaxCategories)
        {
            problems.Add(new("categoryIds", $"A product can have up to {CatalogLimits.MaxCategories} categories and themes."));
        }

        for (var i = 0; i < content.CategoryIds.Count; i++)
        {
            if (content.CategoryIds[i] == Guid.Empty)
            {
                problems.Add(new($"categoryIds[{i}]", "Choose a category."));
            }
        }
    }

    private static void CheckItinerary(ProductContent content, List<ProductProblem> problems)
    {
        if (content.Itinerary.Count > CatalogLimits.MaxItineraryDays)
        {
            problems.Add(new("itinerary", $"An itinerary can have up to {CatalogLimits.MaxItineraryDays} days."));
        }

        for (var i = 0; i < content.Itinerary.Count; i++)
        {
            var day = content.Itinerary[i];
            var field = $"itinerary[{i}]";

            // The list is the order, and the day number has to agree with it. That one check is
            // "contiguous from 1": it refuses a gap, a repeat and a day out of place alike.
            if (day.DayNumber != i + 1)
            {
                problems.Add(new(
                    $"{field}.dayNumber",
                    $"Days run 1, 2, 3 and so on, in order, with no gaps or repeats. This should be day {i + 1}."));
            }

            TooLong(problems, $"{field}.title", day.Title, CatalogLimits.MaxDayTitleLength);
            TooLong(problems, $"{field}.description", day.Description, CatalogLimits.MaxDayDescriptionLength);
            TooLong(problems, $"{field}.accommodation", day.Accommodation, CatalogLimits.MaxAccommodationLength);

            if (day.Meals.Any(meal => !Enum.IsDefined(meal)))
            {
                problems.Add(new($"{field}.meals", "Meals are Breakfast, Lunch or Dinner."));
            }
        }
    }

    private static void CheckInclusions(ProductContent content, List<ProductProblem> problems)
    {
        if (content.Inclusions.Count > CatalogLimits.MaxInclusions)
        {
            problems.Add(new("inclusions", $"A product can have up to {CatalogLimits.MaxInclusions} inclusions and exclusions."));
        }

        for (var i = 0; i < content.Inclusions.Count; i++)
        {
            var line = content.Inclusions[i];

            if (!Enum.IsDefined(line.Kind))
            {
                problems.Add(new($"inclusions[{i}].kind", "Choose Inclusion or Exclusion."));
            }

            // A blank line is not a draft of anything; it would reach the storefront as an empty bullet.
            if (string.IsNullOrWhiteSpace(line.Text))
            {
                problems.Add(new($"inclusions[{i}].text", "Write what is included or left out, or remove the line."));
            }

            TooLong(problems, $"inclusions[{i}].text", line.Text, CatalogLimits.MaxInclusionTextLength);
        }
    }

    private static void CheckPriceVariants(ProductContent content, List<ProductProblem> problems)
    {
        var variants = content.PriceVariants;

        if (variants.Count > CatalogLimits.MaxPriceVariants)
        {
            problems.Add(new("priceVariants", $"A product can have up to {CatalogLimits.MaxPriceVariants} prices."));
        }

        for (var i = 0; i < variants.Count; i++)
        {
            var variant = variants[i];
            var field = $"priceVariants[{i}]";

            if (string.IsNullOrWhiteSpace(variant.Name))
            {
                problems.Add(new($"{field}.name", "Give this price a name, like Double occupancy."));
            }

            TooLong(problems, $"{field}.name", variant.Name, CatalogLimits.MaxVariantNameLength);

            if (!Enum.IsDefined(variant.PaxType))
            {
                problems.Add(new($"{field}.paxType", "Choose Adult, Child or Infant."));
            }

            if (variant.Occupancy is < 1 or > CatalogLimits.MaxOccupancy)
            {
                problems.Add(new($"{field}.occupancy", $"Occupancy is between 1 and {CatalogLimits.MaxOccupancy} people per room."));
            }

            if (variant.MinGroupSize is < 1 or > CatalogLimits.MaxGroupSize)
            {
                problems.Add(new($"{field}.minGroupSize", $"A group size is between 1 and {CatalogLimits.MaxGroupSize}."));
            }

            if (variant.MaxGroupSize is < 1 or > CatalogLimits.MaxGroupSize)
            {
                problems.Add(new($"{field}.maxGroupSize", $"A group size is between 1 and {CatalogLimits.MaxGroupSize}."));
            }

            if (variant.MinGroupSize is { } min && variant.MaxGroupSize is { } max && min > max)
            {
                problems.Add(new($"{field}.maxGroupSize", "The largest group can't be smaller than the smallest."));
            }

            Price(problems, $"{field}.priceMinor", variant.PriceMinor.AmountMinor);

            // Two prices a traveller would match at once leave the price to chance. Only the first
            // clash is reported for each line: one message says what to fix.
            for (var earlier = 0; earlier < i; earlier++)
            {
                if (Overlap(variants[earlier], variant))
                {
                    problems.Add(new(
                        field,
                        $"This overlaps price {earlier + 1}: the same name, traveller and room, for some of the same group sizes."));
                    break;
                }
            }
        }
    }

    private static void CheckVisa(VisaContent visa, List<ProductProblem> problems)
    {
        TooLong(problems, "visa.visaType", visa.VisaType, CatalogLimits.MaxVisaTypeLength);

        if (!Enum.IsDefined(visa.EntryType))
        {
            problems.Add(new("visa.entryType", "Choose Single or Multiple."));
        }

        // Zero is allowed while the draft is being written — it means "not filled in yet", and
        // publishing asks for it. Negative is never anything.
        if (visa.ProcessingTimeDays is < 0 or > CatalogLimits.MaxProcessingTimeDays)
        {
            problems.Add(new("visa.processingTimeDays", $"Processing time is between 1 and {CatalogLimits.MaxProcessingTimeDays} days."));
        }

        if (visa.ValidityDays is < 0 or > CatalogLimits.MaxValidityDays)
        {
            problems.Add(new("visa.validityDays", $"Validity is between 1 and {CatalogLimits.MaxValidityDays} days."));
        }

        Price(problems, "visa.consularFeeMinor", visa.ConsularFeeMinor.AmountMinor);
        Price(problems, "visa.serviceFeeMinor", visa.ServiceFeeMinor.AmountMinor);

        if (visa.Documents.Count > CatalogLimits.MaxVisaDocuments)
        {
            problems.Add(new("visa.documents", $"A checklist can have up to {CatalogLimits.MaxVisaDocuments} documents."));
        }

        for (var i = 0; i < visa.Documents.Count; i++)
        {
            var label = visa.Documents[i].Label;

            if (string.IsNullOrWhiteSpace(label))
            {
                problems.Add(new($"visa.documents[{i}].label", "Name the document, like Passport photograph, or remove the line."));
            }

            TooLong(problems, $"visa.documents[{i}].label", label, CatalogLimits.MaxDocumentLabelLength);
        }
    }

    /// <summary>Same name, traveller type and room, and group-size ranges that share at least one size.</summary>
    private static bool Overlap(PriceVariantContent first, PriceVariantContent second)
    {
        if (!string.Equals(first.Name, second.Name, StringComparison.OrdinalIgnoreCase)
            || first.PaxType != second.PaxType
            || first.Occupancy != second.Occupancy)
        {
            return false;
        }

        // An open end is as wide as it can be: no minimum is 1, no maximum is unbounded.
        var firstMin = first.MinGroupSize ?? 1;
        var firstMax = first.MaxGroupSize ?? int.MaxValue;
        var secondMin = second.MinGroupSize ?? 1;
        var secondMax = second.MaxGroupSize ?? int.MaxValue;

        // A backwards range is reported on its own line; comparing it would only add noise.
        if (firstMin > firstMax || secondMin > secondMax)
        {
            return false;
        }

        return firstMin <= secondMax && secondMin <= firstMax;
    }

    private static void Price(List<ProductProblem> problems, string field, long amountMinor)
    {
        if (amountMinor is < 0 or > CatalogLimits.MaxPriceMinor)
        {
            problems.Add(new(field, "A price can't be negative, or more than 100 billion."));
        }
    }

    private static void TooLong(List<ProductProblem> problems, string field, string? value, int max)
    {
        if (value is not null && value.Length > max)
        {
            problems.Add(new(field, $"Keep this to {max.ToString(CultureInfo.InvariantCulture)} characters."));
        }
    }

    [GeneratedRegex("^[A-Z]{2}$")]
    private static partial Regex CountryCodePattern();

    [GeneratedRegex("^[A-Z]{3}$")]
    private static partial Regex CurrencyCodePattern();
}
