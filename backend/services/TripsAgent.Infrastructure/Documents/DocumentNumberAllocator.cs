using Microsoft.EntityFrameworkCore;
using TripsAgent.Application.Documents;
using TripsAgent.Application.Tenancy;
using TripsAgent.Domain.Documents;
using TripsAgent.Infrastructure.Persistence;

namespace TripsAgent.Infrastructure.Documents;

/// <summary>
/// Takes the next gapless document number with one locking statement against
/// <c>documents.document_number_sequences</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why not <c>MAX(number) + 1</c>?</b> Two requests read the same maximum at the same moment and
/// both write the next number. <b>Why not a PostgreSQL sequence?</b> A sequence value is never
/// given back: a transaction that takes 42 and then fails leaves 42 missing forever.
/// </para>
/// <para>
/// <b>What this does instead.</b> One <c>INSERT … ON CONFLICT DO UPDATE … RETURNING</c>. The first
/// document of a sequence inserts its counter row at 1. Every later one hits the conflict and
/// increments the existing row, and PostgreSQL <i>locks that row</i> to do it — exactly as
/// <c>SELECT … FOR UPDATE</c> would. The lock is held until the surrounding transaction commits or
/// rolls back, so a concurrent request for the same sequence waits, then sees the committed value
/// and takes the one after it. If the transaction rolls back, the increment is undone and the next
/// request gets the same number. No duplicates, and no gaps.
/// </para>
/// <para>
/// Only the counter row for <i>this</i> agency, type and year is locked. Other agencies, other
/// document types and other years never wait on each other.
/// </para>
/// </remarks>
public sealed class DocumentNumberAllocator : IDocumentNumberAllocator
{
    private readonly AppDbContext _db;
    private readonly ITenantContext _tenant;
    private readonly TimeProvider _clock;

    public DocumentNumberAllocator(AppDbContext db, ITenantContext tenant, TimeProvider clock)
    {
        _db = db;
        _tenant = tenant;
        _clock = clock;
    }

    public async Task<AllocatedDocumentNumber> NextAsync(
        DocumentType documentType,
        DateTimeOffset issuedAt,
        CancellationToken cancellationToken = default)
    {
        // The agency comes from the request, never from a parameter: numbering another agency's
        // documents is not something any caller should be able to ask for.
        var agencyId = _tenant.AgencyId
            ?? throw new InvalidOperationException(
                "Cannot allocate a document number with no agency resolved for this request.");

        if (_db.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException(
                """
                A document number must be allocated inside the transaction that saves the document.

                Outside one, the increment commits by itself, and if saving the document then fails
                the number is gone for good — a gap in the agency's invoice numbering. Use
                DocumentIssuer, or open a transaction with ITransactionRunner first.
                """);
        }

        // Before reading the format, so a format change waits for this document to commit and a
        // format change already in progress is finished before this reads it.
        await TakeNumberingLockAsync(agencyId, documentType, cancellationToken);

        // Both reads go through the tenant filter, so they can only see this agency's rows.
        var format = await _db.DocumentNumberFormats
                .AsNoTracking()
                .FirstOrDefaultAsync(f => f.DocumentType == documentType, cancellationToken)
            ?? DocumentNumberFormat.DefaultFor(
                await _db.AgencySettings.AsNoTracking().SingleAsync(cancellationToken),
                documentType);

        var timeZone = await _db.Agencies
            .AsNoTracking()
            .Where(agency => agency.Id == agencyId)
            .Select(agency => agency.Timezone)
            .SingleAsync(cancellationToken);

        var localYear = DocumentNumberFormat.LocalYear(issuedAt, timeZone);
        var sequenceYear = format.SequenceYearFor(localYear);
        var now = _clock.GetUtcNow();
        var type = documentType.ToString();

        // ToListAsync, not SingleAsync: EF only wraps raw SQL in a subquery when LINQ is composed on
        // top of it, and PostgreSQL does not accept an INSERT inside a subquery.
        var returned = await _db.Database
            .SqlQuery<long>($"""
                INSERT INTO documents.document_number_sequences AS s
                    (id, agency_id, document_type, year, last_value, created_at, updated_at)
                VALUES
                    ({Guid.CreateVersion7()}, {agencyId}, {type}, {sequenceYear}, 1, {now}, {now})
                ON CONFLICT (agency_id, document_type, year)
                DO UPDATE SET last_value = s.last_value + 1,
                              updated_at = EXCLUDED.updated_at
                RETURNING s.last_value AS "Value"
                """)
            .ToListAsync(cancellationToken);

        var sequenceNumber = returned.Single();

        return new AllocatedDocumentNumber(
            documentType,
            sequenceYear,
            sequenceNumber,
            format.Render(sequenceNumber, localYear));
    }

    public async Task LockNumberingAsync(DocumentType documentType, CancellationToken cancellationToken = default)
    {
        var agencyId = _tenant.AgencyId
            ?? throw new InvalidOperationException(
                "Cannot lock document numbering with no agency resolved for this request.");

        if (_db.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException(
                "The document numbering lock lasts until the transaction ends, so it needs one open. "
                + "Use ITransactionRunner first.");
        }

        await TakeNumberingLockAsync(agencyId, documentType, cancellationToken);
    }

    /// <summary>
    /// A PostgreSQL advisory lock on (agency, document type), released when the transaction ends.
    /// </summary>
    /// <remarks>
    /// Why not lock a row? Before an agency's first document and first format change there is no
    /// row to lock — and that first document is exactly the one a format change must not miss. An
    /// advisory lock needs no row. It is keyed by a 64-bit hash of the name; if two names ever hash
    /// alike, the only cost is that they wait on each other.
    /// </remarks>
    private async Task TakeNumberingLockAsync(
        Guid agencyId,
        DocumentType documentType,
        CancellationToken cancellationToken)
    {
        var key = $"document-numbering:{agencyId}:{documentType}";

        await _db.Database.ExecuteSqlAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({key}, 0))",
            cancellationToken);
    }
}
