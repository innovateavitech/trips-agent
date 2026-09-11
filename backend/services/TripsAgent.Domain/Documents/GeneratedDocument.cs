using TripsAgent.Domain.Common;

namespace TripsAgent.Domain.Documents;

/// <summary>
/// A numbered document an agency has issued — an invoice, a voucher, an itinerary, a quote.
/// </summary>
/// <remarks>
/// <para>
/// This is the record that owns the number. It is inserted in the same transaction that took the
/// number, so either both exist or neither does, and that is what keeps the numbering gapless.
/// </para>
/// <para>
/// Once issued, the number is final: a database trigger refuses to change it or to delete the row,
/// because either would leave a hole in a sequence the tax authority expects to be complete. A
/// correction is a new document with a new number.
/// </para>
/// <para>
/// The rendered PDF, its checksum and the order line it describes arrive with #46, which extends
/// this record rather than replacing it.
/// </para>
/// </remarks>
public sealed class GeneratedDocument : Entity, IAuditableEntity, ITenantScoped
{
    private GeneratedDocument()
    {
        DocumentNumber = string.Empty;
    }

    public Guid AgencyId { get; private set; }

    public DocumentType DocumentType { get; private set; }

    /// <summary>The number as printed, e.g. <c>INV-LAGOST-2026-000042</c>.</summary>
    public string DocumentNumber { get; private set; }

    /// <summary>The counter it was drawn from: the year, or 0 for a counter that never resets.</summary>
    public int SequenceYear { get; private set; }

    /// <summary>The counter value, so gaps can be checked with arithmetic rather than by parsing text.</summary>
    public long SequenceNumber { get; private set; }

    /// <summary>When the number was taken. It decides the year the document belongs to.</summary>
    public DateTimeOffset IssuedAt { get; private set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Records a document under a number just allocated for it.</summary>
    public static GeneratedDocument Issue(Guid agencyId, AllocatedDocumentNumber number, DateTimeOffset issuedAt)
    {
        ArgumentNullException.ThrowIfNull(number);

        if (agencyId == Guid.Empty)
        {
            throw new ArgumentException("A document is issued by an agency.", nameof(agencyId));
        }

        return new GeneratedDocument
        {
            AgencyId = agencyId,
            DocumentType = number.DocumentType,
            DocumentNumber = number.Value,
            SequenceYear = number.SequenceYear,
            SequenceNumber = number.SequenceNumber,
            IssuedAt = issuedAt,
        };
    }
}

/// <summary>A number taken from a sequence, and how it is written.</summary>
/// <param name="DocumentType">The sequence it came from.</param>
/// <param name="SequenceYear">The sequence's year, or 0 for one that never resets.</param>
/// <param name="SequenceNumber">The counter value — 1 for the first document.</param>
/// <param name="Value">The number as printed on the document.</param>
public sealed record AllocatedDocumentNumber(
    DocumentType DocumentType,
    int SequenceYear,
    long SequenceNumber,
    string Value);
