using TripsAgent.Application.Persistence;
using TripsAgent.Application.Tenancy;
using TripsAgent.Domain.Documents;

namespace TripsAgent.Application.Documents;

/// <summary>
/// Issues a numbered document for the current agency: takes the next number and records the
/// document in one transaction.
/// </summary>
/// <remarks>
/// <para>
/// This is the one place a document gets its number. The PDF renderer (#46) calls it first, commits,
/// and only then renders and stores the file — so a slow render never holds the numbering lock, and
/// a failed render leaves a numbered document to render again rather than a hole in the sequence.
/// </para>
/// <para>
/// Called while the caller already has a transaction open, the document joins it and stands or
/// falls with the caller's commit.
/// </para>
/// </remarks>
public sealed class DocumentIssuer
{
    private readonly IAppDbContext _db;
    private readonly IDocumentNumberAllocator _allocator;
    private readonly ITransactionRunner _transactions;
    private readonly ITenantContext _tenant;
    private readonly TimeProvider _clock;

    public DocumentIssuer(
        IAppDbContext db,
        IDocumentNumberAllocator allocator,
        ITransactionRunner transactions,
        ITenantContext tenant,
        TimeProvider clock)
    {
        _db = db;
        _allocator = allocator;
        _transactions = transactions;
        _tenant = tenant;
        _clock = clock;
    }

    /// <summary>Issues the next <paramref name="documentType"/> for the current agency.</summary>
    /// <exception cref="InvalidOperationException">No agency is resolved for this request.</exception>
    public Task<GeneratedDocument> IssueAsync(DocumentType documentType, CancellationToken cancellationToken = default)
    {
        var agencyId = _tenant.AgencyId
            ?? throw new InvalidOperationException(
                "Cannot issue a document with no agency resolved. A document number belongs to one agency's sequence.");

        return _transactions.RunAsync(
            async token =>
            {
                // Read inside the work, so a replay after a transient failure takes a fresh time and
                // a fresh number rather than reusing the failed attempt's.
                var issuedAt = _clock.GetUtcNow();

                var number = await _allocator.NextAsync(documentType, issuedAt, token);

                var document = GeneratedDocument.Issue(agencyId, number, issuedAt);
                _db.GeneratedDocuments.Add(document);

                await _db.SaveChangesAsync(token);

                return document;
            },
            cancellationToken);
    }
}
