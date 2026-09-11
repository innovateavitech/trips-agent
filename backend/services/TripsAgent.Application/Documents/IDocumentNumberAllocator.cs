using TripsAgent.Domain.Documents;

namespace TripsAgent.Application.Documents;

/// <summary>
/// Hands out the next gapless document number for the current agency.
/// </summary>
/// <remarks>
/// <para>
/// <b>Must be called inside an open transaction</b>, and the document that uses the number must be
/// inserted in that same transaction. The counter is incremented under a row lock that is held
/// until the transaction ends; if the transaction rolls back, the increment rolls back with it and
/// the next caller gets the same number. That is the whole gapless guarantee. Called outside a
/// transaction, the increment would commit on its own, and any failure before the document is saved
/// would burn a number — so the allocator refuses.
/// </para>
/// <para>
/// Keep the transaction short. Every other document of the same type for the same agency waits on
/// that lock, so render PDFs and send emails after the commit, not inside it.
/// </para>
/// <para>
/// Use <see cref="DocumentIssuer"/> rather than calling this directly unless you are composing it
/// into a larger transaction of your own.
/// </para>
/// </remarks>
public interface IDocumentNumberAllocator
{
    /// <summary>Takes the next number for <paramref name="documentType"/>.</summary>
    /// <param name="documentType">Which sequence to draw from.</param>
    /// <param name="issuedAt">When the document is issued. Decides its year, in the agency's time zone.</param>
    /// <param name="cancellationToken">Cancels the database call.</param>
    /// <exception cref="InvalidOperationException">
    /// No transaction is open, or no agency is resolved for this request.
    /// </exception>
    public Task<AllocatedDocumentNumber> NextAsync(
        DocumentType documentType,
        DateTimeOffset issuedAt,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Holds the current agency's numbering of <paramref name="documentType"/> until the open
    /// transaction ends. <see cref="NextAsync"/> takes the same lock before it reads the format.
    /// </summary>
    /// <remarks>
    /// For work that must decide something from "has a number been issued yet?" and then act on
    /// it — changing the format is the case. Without the lock, a first invoice still being issued in
    /// another transaction is invisible to the check, and commits under the old format anyway.
    /// With it, the check waits for that invoice to commit and then sees it.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// No transaction is open, or no agency is resolved for this request.
    /// </exception>
    public Task LockNumberingAsync(DocumentType documentType, CancellationToken cancellationToken = default);
}
