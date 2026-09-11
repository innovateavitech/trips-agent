using TripsAgent.Domain.Common;

namespace TripsAgent.Domain.Documents;

/// <summary>
/// The counter behind one agency's numbers for one document type in one year — the last value
/// handed out.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately has no methods. The only thing that ever changes a counter is the allocator's
/// single <c>INSERT … ON CONFLICT DO UPDATE</c> statement, which increments it under a row lock
/// inside the transaction that inserts the document. Loading this entity, adding one and saving
/// it would be the classic lost-update race — two invoices, one number.
/// </para>
/// <para>
/// Why not a PostgreSQL <c>SEQUENCE</c>? A sequence is never rolled back: a transaction that takes
/// value 42 and then fails leaves 42 unused forever, which is a gap in the invoice numbering. This
/// row is an ordinary row, so a failed transaction takes its increment with it.
/// </para>
/// <para>
/// <see cref="Year"/> is <see cref="DocumentNumberFormat.ContinuousSequenceYear"/> (0) for
/// numbering that never resets.
/// </para>
/// </remarks>
public sealed class DocumentNumberSequence : Entity, IAuditableEntity, ITenantScoped
{
    private DocumentNumberSequence()
    {
    }

    public Guid AgencyId { get; private set; }

    public DocumentType DocumentType { get; private set; }

    /// <summary>The calendar year this counter covers, or 0 for one that never resets.</summary>
    public int Year { get; private set; }

    /// <summary>The last number handed out. The first document of a sequence is 1.</summary>
    public long LastValue { get; private set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
