using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TripsAgent.Application.Messaging;
using TripsAgent.Application.Persistence;
using TripsAgent.Application.Storage;
using TripsAgent.Application.Tenancy;
using TripsAgent.Domain.Documents;
using TripsAgent.Domain.Orders;

namespace TripsAgent.Application.Documents;

/// <summary>The email that carried a document, and where it has got to.</summary>
/// <param name="Recipient">The address it went to.</param>
/// <param name="Status">One of <c>NotificationStatus</c>: queued, sending, sent, delivered, failed, bounced.</param>
/// <param name="SentAt">When the provider accepted it.</param>
public sealed record DocumentEmailView(string Recipient, string Status, DateTimeOffset? SentAt);

/// <summary>An issued document as the agent's console shows it.</summary>
/// <param name="Document">The document.</param>
/// <param name="ProductType">For a voucher, what it is for.</param>
/// <param name="SupersedesDocumentNumber">The number of the document this one replaced.</param>
/// <param name="SupersededByDocumentId">The document that replaced this one, if any.</param>
/// <param name="SupersededByDocumentNumber">That document's number.</param>
/// <param name="Download">A signed link to the PDF, once it is rendered.</param>
/// <param name="Email">The email that carried it to the customer, once queued.</param>
public sealed record DocumentView(
    GeneratedDocument Document,
    OrderLineItemType? ProductType,
    string? SupersedesDocumentNumber,
    Guid? SupersededByDocumentId,
    string? SupersededByDocumentNumber,
    SignedDocumentDownload? Download,
    DocumentEmailView? Email);

/// <summary>What asking to reissue a document came to.</summary>
public abstract record ReissueDocumentOutcome
{
    private ReissueDocumentOutcome()
    {
    }

    /// <summary>A new document is numbered and on its way to being rendered.</summary>
    public sealed record Reissued(DocumentView Document) : ReissueDocumentOutcome;

    /// <summary>No such document for this agency, or it was not issued for an order.</summary>
    public sealed record NotFound : ReissueDocumentOutcome;

    /// <summary>Its file does not exist yet, so there is nothing to replace.</summary>
    public sealed record NotReady : ReissueDocumentOutcome;

    /// <summary>It has been replaced already. Reissue the newest one instead.</summary>
    public sealed record AlreadySuperseded(string ByDocumentNumber) : ReissueDocumentOutcome;
}

/// <summary>What opening a document's PDF came to.</summary>
public abstract record DocumentFileOutcome
{
    private DocumentFileOutcome()
    {
    }

    /// <summary>The bytes, exactly as issued.</summary>
    public sealed record File(byte[] Content, string FileName) : DocumentFileOutcome;

    /// <summary>No such document, or no file yet.</summary>
    public sealed record NotFound : DocumentFileOutcome;

    /// <summary>A customer asked for a document that has since been replaced.</summary>
    public sealed record Superseded(string ByDocumentNumber) : DocumentFileOutcome;

    /// <summary>The stored bytes are not the bytes that were issued. Never served.</summary>
    public sealed record Corrupted : DocumentFileOutcome;
}

/// <summary>
/// The agent's side of an order's documents: list them, reissue one, and open a PDF (#46).
/// </summary>
/// <remarks>
/// <para>
/// Listing and reissuing run in the agent's own request, so the tenant filter decides what exists:
/// another agency's order or document is simply not found.
/// </para>
/// <para>
/// <b>Reissuing never touches the original.</b> It takes a new number in the same transaction as
/// the new row and the message that asks the Worker to render it, so the three commit together. The
/// original's row — its number, its file, its checksum — is left exactly as it was, and a database
/// trigger would refuse to change it anyway.
/// </para>
/// <para>
/// <b>Every download is checked.</b> The bytes are hashed on the way out and compared with the
/// checksum recorded when the document was issued; a mismatch is refused, not served. That check is
/// what "a reprint is byte-identical to the original" rests on.
/// </para>
/// </remarks>
public sealed partial class BookingDocumentsHandler
{
    private readonly IAppDbContext _db;
    private readonly IDocumentNumberAllocator _numbers;
    private readonly ITransactionRunner _transactions;
    private readonly IUniqueViolationDetector _uniqueViolations;
    private readonly IOutbox _outbox;
    private readonly ITenantContext _tenant;
    private readonly IPlatformScope _platformScope;
    private readonly IBlobStorage _storage;
    private readonly DocumentLinks _links;
    private readonly TimeProvider _clock;
    private readonly ILogger<BookingDocumentsHandler> _logger;

    public BookingDocumentsHandler(
        IAppDbContext db,
        IDocumentNumberAllocator numbers,
        ITransactionRunner transactions,
        IUniqueViolationDetector uniqueViolations,
        IOutbox outbox,
        ITenantContext tenant,
        IPlatformScope platformScope,
        IBlobStorage storage,
        DocumentLinks links,
        TimeProvider clock,
        ILogger<BookingDocumentsHandler> logger)
    {
        _db = db;
        _numbers = numbers;
        _transactions = transactions;
        _uniqueViolations = uniqueViolations;
        _outbox = outbox;
        _tenant = tenant;
        _platformScope = platformScope;
        _storage = storage;
        _links = links;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>Every document issued for the order with this reference, or null when there is no such order.</summary>
    public async Task<IReadOnlyList<DocumentView>?> ListForOrderAsync(string orderReference, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(orderReference);

        var reference = orderReference.Trim();

        var order = await _db.Orders
            .AsNoTracking()
            .Include(o => o.Lines)
            .FirstOrDefaultAsync(o => o.OrderNumber == reference, cancellationToken);

        if (order is null)
        {
            return null;
        }

        var documents = await _db.GeneratedDocuments
            .AsNoTracking()
            .Where(d => d.OrderId == order.Id)
            .ToListAsync(cancellationToken);

        return await ViewsAsync(documents, order, cancellationToken);
    }

    /// <summary>
    /// Issues a replacement for <paramref name="documentId"/> under a new number, and asks the Worker
    /// to render it and email it to the same customer.
    /// </summary>
    /// <exception cref="InvalidOperationException">No agency is resolved for this request.</exception>
    public async Task<ReissueDocumentOutcome> ReissueAsync(Guid documentId, CancellationToken cancellationToken = default)
    {
        var agencyId = _tenant.AgencyId
            ?? throw new InvalidOperationException("Reissuing a document needs an agency; this endpoint requires one.");

        try
        {
            return await _transactions.RunAsync(
                token => ReissueInTransactionAsync(agencyId, documentId, token),
                cancellationToken);
        }
        catch (DbUpdateException ex) when (_uniqueViolations.IsUniqueViolation(ex))
        {
            // Somebody reissued it in the same moment, and a document can be replaced only once.
            // Theirs stands; ours rolled back, number and all, so the numbering has no gap.
            _db.ChangeTracker.Clear();

            var winner = await _db.GeneratedDocuments
                .AsNoTracking()
                .Where(d => d.SupersedesDocumentId == documentId)
                .Select(d => d.DocumentNumber)
                .FirstOrDefaultAsync(cancellationToken);

            if (winner is null)
            {
                throw;
            }

            return new ReissueDocumentOutcome.AlreadySuperseded(winner);
        }
    }

    /// <summary>
    /// The PDF, checked against the checksum recorded when it was issued. For a signed link, whose
    /// signature the caller has already checked.
    /// </summary>
    /// <param name="documentId">The document the link names.</param>
    /// <param name="forCustomer">
    /// True for the customer's link, which refuses a document that has been replaced: a traveller
    /// should not board with a voucher their agent has since corrected.
    /// </param>
    /// <param name="cancellationToken">Cancels the read.</param>
    public async Task<DocumentFileOutcome> OpenAsync(Guid documentId, bool forCustomer, CancellationToken cancellationToken = default)
    {
        // A signed link names one document and arrives with no session, so there is no agency to
        // scope by. The signature — checked before this is called — is the authorisation.
        using var scope = _platformScope.Enter("document download — a signed link names one document, not an agency");

        var document = await _db.GeneratedDocuments
            .AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == documentId, cancellationToken);

        if (document is not { IsReady: true, AssetId: { } assetId, Checksum: { } issuedChecksum })
        {
            return new DocumentFileOutcome.NotFound();
        }

        if (forCustomer)
        {
            var replacedBy = await _db.GeneratedDocuments
                .AsNoTracking()
                .Where(d => d.SupersedesDocumentId == documentId)
                .Select(d => d.DocumentNumber)
                .FirstOrDefaultAsync(cancellationToken);

            if (replacedBy is not null)
            {
                return new DocumentFileOutcome.Superseded(replacedBy);
            }
        }

        var asset = await _db.Assets.AsNoTracking().FirstOrDefaultAsync(a => a.Id == assetId, cancellationToken);

        if (asset is null || !asset.IsServable || !await _storage.ExistsAsync(asset.StorageKey, cancellationToken))
        {
            LogFileMissing(_logger, document.DocumentNumber, document.Id);
            return new DocumentFileOutcome.NotFound();
        }

        byte[] content;
        await using (var stream = await _storage.OpenReadAsync(asset.StorageKey, cancellationToken))
        using (var buffer = new MemoryStream())
        {
            await stream.CopyToAsync(buffer, cancellationToken);
            content = buffer.ToArray();
        }

        var servedChecksum = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

        if (!string.Equals(servedChecksum, issuedChecksum, StringComparison.Ordinal))
        {
            LogCorrupted(_logger, document.DocumentNumber, document.Id);
            return new DocumentFileOutcome.Corrupted();
        }

        return new DocumentFileOutcome.File(content, document.FileName);
    }

    private async Task<ReissueDocumentOutcome> ReissueInTransactionAsync(Guid agencyId, Guid documentId, CancellationToken token)
    {
        var original = await _db.GeneratedDocuments.FirstOrDefaultAsync(d => d.Id == documentId, token);

        if (original?.OrderId is not { } orderId)
        {
            return new ReissueDocumentOutcome.NotFound();
        }

        var replacedBy = await _db.GeneratedDocuments
            .AsNoTracking()
            .Where(d => d.SupersedesDocumentId == documentId)
            .Select(d => d.DocumentNumber)
            .FirstOrDefaultAsync(token);

        if (replacedBy is not null)
        {
            return new ReissueDocumentOutcome.AlreadySuperseded(replacedBy);
        }

        if (!original.IsReady)
        {
            return new ReissueDocumentOutcome.NotReady();
        }

        var issuedAt = _clock.GetUtcNow();
        var number = await _numbers.NextAsync(original.DocumentType, issuedAt, token);
        var reissued = original.Reissue(number, issuedAt);

        _db.GeneratedDocuments.Add(reissued);
        _outbox.Enqueue(new DocumentRenderRequested(agencyId, reissued.Id), agencyId);

        await _db.SaveChangesAsync(token);

        LogReissued(_logger, original.DocumentNumber, reissued.DocumentNumber, reissued.IssueNumber);

        var order = await _db.Orders.AsNoTracking().Include(o => o.Lines).FirstAsync(o => o.Id == orderId, token);
        var views = await ViewsAsync([original, reissued], order, token);

        return new ReissueDocumentOutcome.Reissued(views.Single(view => view.Document.Id == reissued.Id));
    }

    private async Task<List<DocumentView>> ViewsAsync(
        IReadOnlyList<GeneratedDocument> documents,
        Order order,
        CancellationToken cancellationToken)
    {
        var byId = documents.ToDictionary(d => d.Id);
        var successors = documents
            .Where(d => d.SupersedesDocumentId is not null)
            .ToDictionary(d => d.SupersedesDocumentId!.Value);

        var emailIds = documents
            .Where(d => d.EmailNotificationId is not null)
            .Select(d => d.EmailNotificationId!.Value)
            .Distinct()
            .ToList();

        var emails = await _db.Notifications
            .AsNoTracking()
            .Where(n => emailIds.Contains(n.Id))
            .ToDictionaryAsync(n => n.Id, n => new DocumentEmailView(n.RecipientAddress, n.Status, n.SentAt), cancellationToken);

        return documents
            .OrderBy(d => d.DocumentType)
            .ThenBy(d => d.OrderLineId)
            .ThenBy(d => d.IssueNumber)
            .Select(d => new DocumentView(
                d,
                d.OrderLineId is { } lineId ? order.Lines.FirstOrDefault(line => line.Id == lineId)?.ItemType : null,
                d.SupersedesDocumentId is { } replaced && byId.TryGetValue(replaced, out var previous) ? previous.DocumentNumber : null,
                successors.TryGetValue(d.Id, out var next) ? next.Id : null,
                next?.DocumentNumber,
                d.IsReady ? _links.DownloadFor(d.Id) : null,
                d.EmailNotificationId is { } emailId && emails.TryGetValue(emailId, out var email) ? email : null))
            .ToList();
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "{Original} reissued as {Replacement} (issue {IssueNumber}). The original is unchanged.")]
    private static partial void LogReissued(ILogger logger, string original, string replacement, int issueNumber);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "{DocumentNumber} ({DocumentId}) is ready but its file is missing from storage.")]
    private static partial void LogFileMissing(ILogger logger, string documentNumber, Guid documentId);

    [LoggerMessage(Level = LogLevel.Critical,
        Message = "{DocumentNumber} ({DocumentId}): the stored file does not match the checksum recorded at issue. "
                  + "It has not been served. The file has changed since it was issued — investigate before anything else.")]
    private static partial void LogCorrupted(ILogger logger, string documentNumber, Guid documentId);
}
