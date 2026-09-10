namespace TripsAgent.UnitTests.Persistence;

// Throwaway entities used only to exercise the persistence conventions.
//
// They are internal and top-level on purpose. Public nested types trip analyser rule CA1034,
// and TreatWarningsAsErrors is on for every project in this solution — so a tidy-looking
// nested class would fail the build rather than warn.
//
// Each one exists to prove exactly one mapping decision. None of them is a real domain entity;
// real entities arrive with their own schema issues (agencies #10, identity #13, ledger #22)
// and live in TripsAgent.Domain.

// ----------------------------------------------------------------- money (CLAUDE.md rule 2)

/// <summary>Money done correctly: <c>long</c> minor units, so ₦1,500.00 is 150000.</summary>
internal sealed class OrderLine
{
    public Guid Id { get; set; }
    public long SellPriceMinor { get; set; }

    /// <summary>Optional money is still money.</summary>
    public long? DiscountMinor { get; set; }
}

/// <summary>The mistake the money convention exists to catch.</summary>
internal sealed class DecimalMoneyOrderLine
{
    public Guid Id { get; set; }
    public decimal SellPriceMinor { get; set; }
}

/// <summary>The same mistake, floating point instead.</summary>
internal sealed class DoubleMoneyOrderLine
{
    public Guid Id { get; set; }
    public double TaxMinor { get; set; }
}

// ------------------------------------------------------------------------------ time

/// <summary>Instants done correctly.</summary>
internal sealed class Booking
{
    public Guid Id { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Not every instant has happened yet.</summary>
    public DateTimeOffset? CancelledAt { get; set; }
}

/// <summary>The mistake the timestamp convention exists to catch.</summary>
internal sealed class DateTimeBooking
{
    public Guid Id { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>Making it optional does not make it unambiguous.</summary>
internal sealed class NullableDateTimeBooking
{
    public Guid Id { get; set; }
    public DateTime? CancelledAt { get; set; }
}

// ---------------------------------------------------------- values the conventions must ignore

/// <summary>
/// Everything here is deliberately close to something a convention reacts to, without being it:
/// a decimal that is not money, a date that is not an instant, a count that is not an amount.
/// </summary>
internal sealed class Departure
{
    public Guid Id { get; set; }

    /// <summary>A calendar date. It has no time zone to get wrong.</summary>
    public DateOnly DepartureDate { get; set; }

    /// <summary>A wall-clock time — "check-in opens at 14:00", wherever you are.</summary>
    public TimeOnly CheckInOpensAt { get; set; }

    /// <summary>A decimal that is not money, and is not named like money.</summary>
    public decimal AverageRating { get; set; }

    /// <summary>A count, not an amount.</summary>
    public int SeatsRemaining { get; set; }
}

// ------------------------------------------------------------------------------ naming

/// <summary>Exists to show PascalCase turning into snake_case, table and columns alike.</summary>
internal sealed class WalletLedgerEntry
{
    public Guid Id { get; set; }
    public Guid AgencyId { get; set; }
    public long AmountMinor { get; set; }
}

/// <summary>
/// Money whose property names do <em>not</em> end in <c>Minor</c>, so the convention leaves them
/// alone and an entity configuration has to say so explicitly with <c>IsMoneyMinor()</c>.
/// </summary>
internal sealed class LegacyInvoice
{
    public Guid Id { get; set; }
    public long Total { get; set; }
    public long? Settled { get; set; }
}
