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

/// <summary>Where a rule's window stands at a given instant.</summary>
/// <remarks>
/// Worked out by the server, with the server's clock, and sent with every rule. The console must
/// not work it out from the timestamps: the server stamps a replacement's start and a retired
/// rule's end with its own "now", and a browser whose clock runs a second or two behind would
/// read a rule it has just replaced as still in force.
/// </remarks>
public enum MarkupRuleStatus
{
    /// <summary>Starts later.</summary>
    Scheduled = 1,

    /// <summary>Applies now.</summary>
    InForce = 2,

    /// <summary>Retired, replaced, or past its end. Kept so past prices can be explained.</summary>
    Ended = 3,
}

/// <summary>How a rule turns a net rate into a markup.</summary>
public enum MarkupCalculationType
{
    /// <summary>A share of the net rate, in basis points: 1000 is 10%.</summary>
    Percentage = 1,

    /// <summary>The same amount whatever the net rate, in minor units.</summary>
    Fixed = 2,
}
