using System.Globalization;
using TripsAgent.Domain.Common;

namespace TripsAgent.Domain.Crm;

/// <summary>One priced line of a quote.</summary>
/// <param name="UnitPriceMinor">The price of one, in minor units. What the customer pays: net, markup and tax as the agent chose.</param>
/// <param name="ProductId">The agency's own catalog product this line sells, if it is one.</param>
public sealed record QuoteItemContent(string Description, int Quantity, Money UnitPriceMinor, Guid? ProductId);

/// <summary>One day of the itinerary a quote proposes.</summary>
/// <param name="DayNumber">1 for the first day; the n-th entry in the list is day n.</param>
public sealed record QuoteDayContent(int DayNumber, string Title, string Description);

/// <summary>Everything the agent writes on a quote, as one value: what the console sends to save a draft.</summary>
/// <param name="ValidUntil">The last day the customer can accept it, inclusive, in the agency's own time zone.</param>
public sealed record QuoteContent(
    string Title,
    DateOnly ValidUntil,
    IReadOnlyList<QuoteItemContent> Items,
    IReadOnlyList<QuoteDayContent> Itinerary,
    string Notes)
{
    /// <summary>The same content with every piece of text trimmed, so blank means blank.</summary>
    public QuoteContent Normalised() => new(
        (Title ?? string.Empty).Trim(),
        ValidUntil,
        (Items ?? []).Select(item => item with { Description = (item.Description ?? string.Empty).Trim() }).ToList(),
        (Itinerary ?? [])
            .Select(day => day with
            {
                Title = (day.Title ?? string.Empty).Trim(),
                Description = (day.Description ?? string.Empty).Trim(),
            })
            .ToList(),
        (Notes ?? string.Empty).Trim());

    /// <summary>Quantity times unit price, summed.</summary>
    /// <remarks>
    /// Whole minor units throughout, and checked: a total too large to hold throws rather than wrapping
    /// round to a negative number. <see cref="QuoteRules.Validate"/> keeps every real quote far from that.
    /// </remarks>
    public Money Total() =>
        Items.Aggregate(Money.Zero, (sum, item) => sum + (item.UnitPriceMinor * item.Quantity));
}

/// <summary>What a quote must be to be saved, and to be sent.</summary>
public static class QuoteRules
{
    /// <summary>Every reason <paramref name="content"/> cannot be saved on <paramref name="today"/>. Empty when it can.</summary>
    /// <remarks>Pass normalised content (<see cref="QuoteContent.Normalised"/>).</remarks>
    public static IReadOnlyList<CrmProblem> Validate(QuoteContent content, DateOnly today)
    {
        var problems = CheckShape(content).ToList();

        if (content.ValidUntil < today)
        {
            problems.Add(new("validUntil", "That day has passed. Choose the last day it can be accepted."));
        }

        return problems;
    }

    /// <summary>Every rule that does not depend on the date. The entity enforces these on its own.</summary>
    public static IReadOnlyList<CrmProblem> CheckShape(QuoteContent content)
    {
        ArgumentNullException.ThrowIfNull(content);

        var problems = new List<CrmProblem>();

        if (content.Title.Length == 0)
        {
            problems.Add(new("title", "Give the quote a title the customer will recognise."));
        }
        else if (content.Title.Length > CrmLimits.MaxQuoteTitleLength)
        {
            problems.Add(new("title", Limit("Keep the title to {0} characters.", CrmLimits.MaxQuoteTitleLength)));
        }

        if (content.Items.Count == 0)
        {
            problems.Add(new("items", "Add at least one item."));
        }
        else if (content.Items.Count > CrmLimits.MaxItems)
        {
            problems.Add(new("items", Limit("A quote has at most {0} items.", CrmLimits.MaxItems)));
        }

        for (var i = 0; i < content.Items.Count; i++)
        {
            CheckItem(content.Items[i], $"items[{i}]", problems);
        }

        if (content.Itinerary.Count > CrmLimits.MaxDays)
        {
            problems.Add(new("itinerary", Limit("An itinerary has at most {0} days.", CrmLimits.MaxDays)));
        }

        for (var i = 0; i < content.Itinerary.Count; i++)
        {
            CheckDay(content.Itinerary[i], i, problems);
        }

        if (content.Notes.Length > CrmLimits.MaxNotesLength)
        {
            problems.Add(new("notes", Limit("Keep the notes to {0} characters.", CrmLimits.MaxNotesLength)));
        }

        return problems;
    }

    /// <summary>
    /// Why a quote cannot go to the customer yet, or null when it can. The same reasons, in the same
    /// order, as the console's own check, so the button and the server never disagree.
    /// </summary>
    public static string? WhyNotSendable(Quote quote, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(quote);

        if (quote.Status != QuoteStatus.Draft)
        {
            return "It has already been sent.";
        }

        if (quote.Items.Count == 0)
        {
            return "Add at least one item.";
        }

        if (quote.TotalMinor.AmountMinor <= 0)
        {
            return "The total is nothing yet.";
        }

        if (quote.ValidUntil < today)
        {
            return "It expired before it was sent. Move the date.";
        }

        return null;
    }

    private static void CheckItem(QuoteItemContent item, string field, List<CrmProblem> problems)
    {
        if (item.Description.Length == 0)
        {
            problems.Add(new($"{field}.description", "Say what it is."));
        }
        else if (item.Description.Length > CrmLimits.MaxItemDescriptionLength)
        {
            problems.Add(new($"{field}.description", Limit("Keep it to {0} characters.", CrmLimits.MaxItemDescriptionLength)));
        }

        if (item.Quantity is < 1 or > CrmLimits.MaxQuantity)
        {
            problems.Add(new($"{field}.quantity", Limit("One or more, up to {0}.", CrmLimits.MaxQuantity)));
        }

        if (item.UnitPriceMinor.IsNegative || item.UnitPriceMinor.AmountMinor > CrmLimits.MaxAmountMinor)
        {
            problems.Add(new($"{field}.unitPriceMinor", "A price is zero or more, in kobo, and below ₦100 billion."));
        }

        if (item.ProductId == Guid.Empty)
        {
            problems.Add(new($"{field}.productId", "Leave it out, or choose one of your products."));
        }
    }

    private static void CheckDay(QuoteDayContent day, int index, List<CrmProblem> problems)
    {
        var field = $"itinerary[{index}]";

        if (day.DayNumber != index + 1)
        {
            problems.Add(new($"{field}.dayNumber", "Days run 1, 2, 3 in the order they are listed."));
        }

        if (day.Title.Length == 0)
        {
            problems.Add(new($"{field}.title", "Give the day a title."));
        }
        else if (day.Title.Length > CrmLimits.MaxDayTitleLength)
        {
            problems.Add(new($"{field}.title", Limit("Keep the title to {0} characters.", CrmLimits.MaxDayTitleLength)));
        }

        if (day.Description.Length > CrmLimits.MaxDayDescriptionLength)
        {
            problems.Add(new($"{field}.description", Limit("Keep the description to {0} characters.", CrmLimits.MaxDayDescriptionLength)));
        }
    }

    private static string Limit(string format, int limit) =>
        string.Format(CultureInfo.InvariantCulture, format, limit);
}
