namespace TripsAgent.Domain.Documents;

/// <summary>
/// The kinds of numbered document an agency issues. Each type has its own, independent sequence:
/// issuing a voucher never moves the invoice counter.
/// </summary>
/// <remarks>
/// Stored as its name (<c>Invoice</c>, <c>Voucher</c>, …), so renaming a member is a data
/// migration, not a refactor.
/// </remarks>
public enum DocumentType
{
    /// <summary>The tax document. Gapless numbering matters most here.</summary>
    Invoice,

    /// <summary>What the traveller shows at check-in, the bus park or the tour desk.</summary>
    Voucher,

    /// <summary>The day-by-day plan for a trip.</summary>
    Itinerary,

    /// <summary>A priced offer sent to a traveller before they book.</summary>
    Quote,
}
