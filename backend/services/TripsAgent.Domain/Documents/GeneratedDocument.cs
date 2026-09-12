using TripsAgent.Domain.Common;

namespace TripsAgent.Domain.Documents;

/// <summary>Where an issued document's file has got to.</summary>
/// <remarks>Stored by name, like every enum in the documents schema.</remarks>
public enum DocumentStatus
{
    /// <summary>Numbered, and waiting for the Worker to render it. Nothing to download yet.</summary>
    Pending = 1,

    /// <summary>Rendered and stored. From here the file is final and is never rendered again.</summary>
    Ready = 2,

    /// <summary>
    /// Rendering failed on every attempt. Still numbered — a number is never given back — and it
    /// renders again the next time it is asked for.
    /// </summary>
    Failed = 3,
}

/// <summary>Who a document is made out to, and where a copy of it is emailed.</summary>
/// <param name="Name">Printed as "Billed to" on an invoice and "Issued to" on a voucher.</param>
/// <param name="Email">Where the Worker emails the PDF once it is rendered. Null sends nothing.</param>
public sealed record DocumentRecipient(string Name, string? Email);

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
/// because either would leave a hole in a sequence the tax authority expects to be complete.
/// </para>
/// <para>
/// <b>The file is written once (#46).</b> A document is numbered first and rendered afterwards, by
/// the Worker, so a slow render never holds the numbering lock. Once the PDF is stored the same
/// trigger freezes it: <see cref="AssetId"/>, <see cref="Checksum"/> and the template that drew it
/// can never change again. A reprint is those exact bytes — every download is checked against
/// <see cref="Checksum"/>.
/// </para>
/// <para>
/// <b>A correction is a new document.</b> <see cref="Reissue"/> returns a new row with a new number,
/// <see cref="IssueNumber"/> one higher and <see cref="SupersedesDocumentId"/> pointing back here.
/// Nothing on this row changes when that happens: "superseded" is read from the new row, never
/// written onto the old one.
/// </para>
/// </remarks>
public sealed class GeneratedDocument : Entity, IAuditableEntity, ITenantScoped
{
    /// <summary>Rendering attempts in a row before a document is marked failed. The same five a notification gets.</summary>
    public const int MaxRenderAttempts = 5;

    /// <summary>Longest error kept in <see cref="LastRenderError"/>. The full exception is in the logs.</summary>
    public const int MaxErrorLength = 2000;

    /// <summary>Longest recipient name kept, matching the column.</summary>
    public const int MaxRecipientNameLength = 200;

    /// <summary>Longest email address there is (RFC 5321), matching the column.</summary>
    public const int MaxRecipientEmailLength = 320;

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

    /// <summary>The order this document belongs to. Null for a document not issued against an order.</summary>
    public Guid? OrderId { get; private set; }

    /// <summary>The order line a voucher is for. Null for an invoice, which covers the whole order.</summary>
    public Guid? OrderLineId { get; private set; }

    /// <summary>1 for a first issue, and one more for each reissue that replaced the one before it.</summary>
    public int IssueNumber { get; private set; }

    /// <summary>The document this one replaced. Null for a first issue.</summary>
    public Guid? SupersedesDocumentId { get; private set; }

    public DocumentStatus Status { get; private set; }

    /// <summary>Who the document is made out to, as printed on it.</summary>
    public string? RecipientName { get; private set; }

    /// <summary>Where a copy was emailed, lower-cased. Null when nobody was to be emailed.</summary>
    public string? RecipientEmail { get; private set; }

    /// <summary>Which template drew the file, e.g. <c>voucher.flight</c>. Null until it has rendered.</summary>
    public string? TemplateKey { get; private set; }

    /// <summary>Which version of <see cref="TemplateKey"/>, so "what did this voucher look like" has an answer.</summary>
    public int? TemplateVersion { get; private set; }

    /// <summary>The stored PDF. Null until it has rendered.</summary>
    public Guid? AssetId { get; private set; }

    /// <summary>SHA-256 of the PDF, hex-encoded. What every download is checked against.</summary>
    public string? Checksum { get; private set; }

    public long? SizeBytes { get; private set; }

    public DateTimeOffset? RenderedAt { get; private set; }

    /// <summary>Attempts at rendering since the last time it was asked for, successful or not.</summary>
    public int RenderAttempts { get; private set; }

    /// <summary>Why the most recent attempt failed, trimmed to <see cref="MaxErrorLength"/>.</summary>
    public string? LastRenderError { get; private set; }

    /// <summary>The email that carried this document to its recipient, once one is queued.</summary>
    public Guid? EmailNotificationId { get; private set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>True once the PDF is stored and final.</summary>
    public bool IsReady => Status == DocumentStatus.Ready;

    /// <summary>
    /// The PDF's file name: the number with anything a file system might object to replaced, as in
    /// <c>INV-2026-000042.pdf</c>. A prefix may contain a slash; a file name may not.
    /// </summary>
    public string FileName =>
        string.Concat(DocumentNumber.Select(c => char.IsAsciiLetterOrDigit(c) || c == '-' ? c : '-')) + ".pdf";

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
            IssueNumber = 1,
            Status = DocumentStatus.Pending,
        };
    }

    /// <summary>
    /// Records the first issue of an order's invoice, or of one line's voucher, to be rendered by
    /// the Worker.
    /// </summary>
    /// <param name="agencyId">The agency that sold the order.</param>
    /// <param name="number">A number just allocated from the invoice or voucher sequence.</param>
    /// <param name="issuedAt">When the number was taken.</param>
    /// <param name="orderId">The order it belongs to.</param>
    /// <param name="orderLineId">The line a voucher is for; null for an invoice, which covers the order.</param>
    /// <param name="recipient">Who it is made out to, and where to email it.</param>
    /// <exception cref="ArgumentException">
    /// The number is not an invoice's or a voucher's, or the line does not fit the type.
    /// </exception>
    public static GeneratedDocument IssueForOrder(
        Guid agencyId,
        AllocatedDocumentNumber number,
        DateTimeOffset issuedAt,
        Guid orderId,
        Guid? orderLineId,
        DocumentRecipient recipient)
    {
        ArgumentNullException.ThrowIfNull(number);
        ArgumentNullException.ThrowIfNull(recipient);
        ArgumentOutOfRangeException.ThrowIfEqual(orderId, Guid.Empty);
        ArgumentException.ThrowIfNullOrWhiteSpace(recipient.Name);

        switch (number.DocumentType)
        {
            case DocumentType.Invoice when orderLineId is not null:
                throw new ArgumentException("An invoice covers the whole order, so it names no single line.", nameof(orderLineId));

            case DocumentType.Voucher when orderLineId is null:
                throw new ArgumentException("A voucher is for one order line, and has to name it.", nameof(orderLineId));

            case DocumentType.Invoice or DocumentType.Voucher:
                break;

            default:
                throw new ArgumentException(
                    $"Only invoices and vouchers are issued against an order, not {number.DocumentType}.", nameof(number));
        }

        var document = Issue(agencyId, number, issuedAt);
        document.OrderId = orderId;
        document.OrderLineId = orderLineId;
        document.RecipientName = Truncate(recipient.Name.Trim(), MaxRecipientNameLength);
        document.RecipientEmail = NormaliseEmail(recipient.Email);

        return document;
    }

    /// <summary>
    /// Issues the next version of this document under <paramref name="number"/>. This row is not
    /// touched: it keeps its number, its file and everything else it had.
    /// </summary>
    /// <remarks>
    /// Only a document whose file exists can be reissued — there is nothing to replace otherwise —
    /// and the caller must make sure it is the newest in its chain. The database enforces the same
    /// thing: a document can be superseded once.
    /// </remarks>
    /// <exception cref="InvalidOperationException">This document has not rendered yet.</exception>
    /// <exception cref="ArgumentException">The number is from another document type's sequence.</exception>
    public GeneratedDocument Reissue(AllocatedDocumentNumber number, DateTimeOffset issuedAt)
    {
        ArgumentNullException.ThrowIfNull(number);

        if (!IsReady)
        {
            throw new InvalidOperationException(
                $"Document {DocumentNumber} is {Status}. Only a document whose file exists can be reissued.");
        }

        if (number.DocumentType != DocumentType)
        {
            throw new ArgumentException(
                $"A reissued {DocumentType} takes its number from the {DocumentType} sequence, not {number.DocumentType}.",
                nameof(number));
        }

        return new GeneratedDocument
        {
            AgencyId = AgencyId,
            DocumentType = DocumentType,
            DocumentNumber = number.Value,
            SequenceYear = number.SequenceYear,
            SequenceNumber = number.SequenceNumber,
            IssuedAt = issuedAt,
            OrderId = OrderId,
            OrderLineId = OrderLineId,
            IssueNumber = IssueNumber + 1,
            SupersedesDocumentId = Id,
            RecipientName = RecipientName,
            RecipientEmail = RecipientEmail,
            Status = DocumentStatus.Pending,
        };
    }

    /// <summary>Counts one attempt at rendering. A failed document asked for again starts counting afresh.</summary>
    /// <exception cref="InvalidOperationException">The file already exists, and is final.</exception>
    public void BeginRender()
    {
        EnsureNotRendered();

        if (Status == DocumentStatus.Failed)
        {
            RenderAttempts = 0;
        }

        RenderAttempts++;
        Status = DocumentStatus.Pending;
    }

    /// <summary>The PDF is stored. From here nothing about the file can change.</summary>
    /// <exception cref="InvalidOperationException">The file already exists, and is final.</exception>
    public void MarkRendered(
        Guid assetId,
        string checksum,
        long sizeBytes,
        string templateKey,
        int templateVersion,
        DateTimeOffset renderedAt)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(assetId, Guid.Empty);
        ArgumentException.ThrowIfNullOrWhiteSpace(checksum);
        ArgumentException.ThrowIfNullOrWhiteSpace(templateKey);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sizeBytes);
        ArgumentOutOfRangeException.ThrowIfLessThan(templateVersion, 1);

        EnsureNotRendered();

        AssetId = assetId;
        Checksum = checksum;
        SizeBytes = sizeBytes;
        TemplateKey = templateKey;
        TemplateVersion = templateVersion;
        RenderedAt = renderedAt;
        LastRenderError = null;
        Status = DocumentStatus.Ready;
    }

    /// <summary>
    /// An attempt failed. It stays pending for the next attempt, until
    /// <see cref="MaxRenderAttempts"/> in a row have failed and it is marked failed for a person.
    /// </summary>
    /// <param name="error">What went wrong, trimmed to <see cref="MaxErrorLength"/>.</param>
    /// <param name="permanent">
    /// True when trying again cannot help — a document that would carry our brand to a traveller,
    /// say — so it is marked failed at once rather than after four more identical failures.
    /// </param>
    /// <returns>True when that was the last attempt.</returns>
    public bool RecordRenderFailure(string error, bool permanent = false)
    {
        ArgumentNullException.ThrowIfNull(error);

        EnsureNotRendered();

        LastRenderError = Truncate(error, MaxErrorLength);
        Status = permanent || RenderAttempts >= MaxRenderAttempts ? DocumentStatus.Failed : DocumentStatus.Pending;

        return Status == DocumentStatus.Failed;
    }

    /// <summary>Remembers the email that carried this document. The first one sticks.</summary>
    public void RecordEmail(Guid notificationId)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(notificationId, Guid.Empty);
        EmailNotificationId ??= notificationId;
    }

    private void EnsureNotRendered()
    {
        if (IsReady)
        {
            throw new InvalidOperationException(
                $"Document {DocumentNumber} is already rendered, and its file is final. Reissue it to change anything.");
        }
    }

    private static string? NormaliseEmail(string? email) =>
        string.IsNullOrWhiteSpace(email) ? null : Truncate(email.Trim().ToLowerInvariant(), MaxRecipientEmailLength);

    private static string Truncate(string value, int length) => value.Length <= length ? value : value[..length];
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
