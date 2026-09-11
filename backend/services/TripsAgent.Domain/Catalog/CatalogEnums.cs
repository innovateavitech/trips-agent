namespace TripsAgent.Domain.Catalog;

/// <summary>What kind of product an agency has built for its own catalog.</summary>
/// <remarks>
/// None of these come from the supplier: the Trips Africa API sells flights and buses only, so
/// every tour, package and visa is written by the agent and hosted by us. Stored by name, so
/// reordering this enum can never change what a stored product is.
/// </remarks>
public enum ProductType
{
    /// <summary>A trip with a day-by-day itinerary.</summary>
    Tour = 1,

    /// <summary>A bundle sold as one product — stay, transfers, activities. Built like a tour.</summary>
    Package = 2,

    /// <summary>Help applying for a visa: the fees, the timings and the documents the applicant needs.</summary>
    Visa = 3,
}

/// <summary>Where a product is in its life. Only a published product appears on the storefront.</summary>
public enum ProductStatus
{
    /// <summary>Being written. Saved freely; invisible to travellers.</summary>
    Draft = 1,

    /// <summary>Live on the agent's storefront. Every save must keep it publishable.</summary>
    Published = 2,

    /// <summary>
    /// Retired. Kept rather than deleted, because order lines point at products and a sale must
    /// always be able to say what was sold. Unpublishing brings it back as a draft.
    /// </summary>
    Archived = 3,
}

/// <summary>The two kinds of label an agency tags its products with. The storefront filters on both.</summary>
public enum CategoryType
{
    /// <summary>What the product is: "Beach holidays", "City breaks".</summary>
    Category = 1,

    /// <summary>What the trip is like: "Honeymoon", "Family friendly".</summary>
    Theme = 2,
}

/// <summary>Whether a line on a product says what is included, or what is not.</summary>
public enum InclusionKind
{
    Inclusion = 1,
    Exclusion = 2,
}

/// <summary>Who a price is for.</summary>
public enum PaxType
{
    Adult = 1,
    Child = 2,
    Infant = 3,
}

/// <summary>A meal an itinerary day includes.</summary>
public enum Meal
{
    Breakfast = 1,
    Lunch = 2,
    Dinner = 3,
}

/// <summary>How many times a visa lets its holder enter the country.</summary>
public enum VisaEntryType
{
    /// <summary>One entry. The name is the API's spelling, shared with the console (#161).</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Naming",
        "CA1720:Identifier contains type name",
        Justification = "\"Single\" is the visa term and the API's agreed spelling; it has nothing to do with System.Single.")]
    Single = 1,

    Multiple = 2,
}

/// <summary>
/// One thing wrong with a product, and where: a save that cannot be stored, or a reason it
/// cannot be published yet.
/// </summary>
/// <param name="Field">
/// The request field it belongs to, as the console names it: <c>title</c>,
/// <c>itinerary[2].dayNumber</c>, <c>visa.documents</c>. The console uses it to put the message
/// next to the right input.
/// </param>
/// <param name="Message">Written for the agent, saying what to do about it.</param>
public sealed record ProductProblem(string Field, string Message);
