using System.Globalization;

namespace TripsAgent.Domain.Catalog;

/// <summary>
/// What a product needs before travellers may see it — the answer to "why can't I publish?".
/// </summary>
/// <remarks>
/// <para>
/// The rules, from issue #160 and plan §2.5:
/// </para>
/// <list type="bullet">
/// <item>every product: a title, a price above zero, and at least one image;</item>
/// <item>a tour or package: a booking window — at least a first or a last date — that has not ended;</item>
/// <item>a visa: its details (type, processing time, validity) and at least one document on the checklist.</item>
/// </list>
/// <para>
/// Visas are exempt from the booking window: an application can be made on any day. Dated
/// departures with seats and capacity are a different thing and stay with group departures (#57).
/// </para>
/// <para>
/// <b>Every</b> problem is returned, never just the first. The console shows them as a checklist
/// straight from the server, so it never keeps a second copy of these rules that could drift.
/// </para>
/// </remarks>
public static class ProductPublishRules
{
    /// <summary>Every reason <paramref name="content"/> cannot be published on <paramref name="today"/>. Empty when it can.</summary>
    /// <param name="content">The product, normalised.</param>
    /// <param name="today">Today in the agency's own time zone — a window that ends today is still open today.</param>
    public static IReadOnlyList<ProductProblem> Check(ProductContent content, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(content);

        var problems = new List<ProductProblem>();

        if (string.IsNullOrWhiteSpace(content.Title))
        {
            problems.Add(new("title", "Give the product a title."));
        }

        if (content.BasePriceMinor.AmountMinor <= 0)
        {
            problems.Add(new("basePriceMinor", "Set a price above zero."));
        }

        if (content.Media.Count == 0)
        {
            problems.Add(new("media", "Add at least one image."));
        }

        if (content.ProductType is ProductType.Tour or ProductType.Package)
        {
            CheckBookingWindow(content, today, problems);
        }

        if (content.ProductType == ProductType.Visa)
        {
            CheckVisa(content.Visa, problems);
        }

        return problems;
    }

    private static void CheckBookingWindow(ProductContent content, DateOnly today, List<ProductProblem> problems)
    {
        if (content.AvailableFrom is null && content.AvailableTo is null)
        {
            problems.Add(new("availableFrom", "Set the dates this can be booked for: at least the first date or the last."));
            return;
        }

        if (content.AvailableTo is { } last && last < today)
        {
            problems.Add(new(
                "availableTo",
                $"Bookings closed on {last.ToString("d MMMM yyyy", CultureInfo.InvariantCulture)}. Move the last date, or clear it to keep selling."));
        }
    }

    private static void CheckVisa(VisaContent? visa, List<ProductProblem> problems)
    {
        if (visa is null)
        {
            problems.Add(new("visa", "Add the visa details: the type, processing time, validity and fees."));
            return;
        }

        if (string.IsNullOrWhiteSpace(visa.VisaType))
        {
            problems.Add(new("visa.visaType", "Say what kind of visa this is, like Tourist or Business."));
        }

        if (visa.ProcessingTimeDays <= 0)
        {
            problems.Add(new("visa.processingTimeDays", "Say how many days processing takes."));
        }

        if (visa.ValidityDays <= 0)
        {
            problems.Add(new("visa.validityDays", "Say how many days the visa is valid for."));
        }

        if (visa.Documents.Count == 0)
        {
            problems.Add(new("visa.documents", "List at least one document the applicant must provide."));
        }
    }
}
