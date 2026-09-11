namespace TripsAgent.Domain.Pricing;

/// <summary>The kinds of thing an agency sells, as far as pricing is concerned.</summary>
/// <remarks>
/// Flights and buses come from the supplier; tours, visas and group departures are the agency's
/// own catalog. A markup rule can target any of them by type.
/// </remarks>
public enum PricedProductType
{
    Flight = 1,
    Bus = 2,
    Tour = 3,
    Visa = 4,
    GroupDeparture = 5,
}

/// <summary>
/// How narrowly a markup rule is aimed. A narrower rule always beats a broader one.
/// </summary>
/// <remarks>
/// The precedence is <see cref="Product"/> → <see cref="ProductType"/> → <see cref="Supplier"/> →
/// <see cref="Global"/>. It is written out in <see cref="MarkupEngine"/> rather than read from
/// these numbers, so reordering the enum can never silently reorder pricing.
/// </remarks>
public enum MarkupScope
{
    /// <summary>Everything the agency sells.</summary>
    Global = 1,

    /// <summary>Everything bought from one supplier, e.g. <c>trips_africa</c>.</summary>
    Supplier = 2,

    /// <summary>Every product of one type — all flights, all tours.</summary>
    ProductType = 3,

    /// <summary>One specific catalog product.</summary>
    Product = 4,
}

/// <summary>How a rule turns a net rate into a markup.</summary>
public enum MarkupCalculationType
{
    /// <summary>A share of the net rate, in basis points: 1000 is 10%.</summary>
    Percentage = 1,

    /// <summary>The same amount whatever the net rate, in minor units.</summary>
    Fixed = 2,
}
