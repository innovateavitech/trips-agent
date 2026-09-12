namespace TripsAgent.Contracts.Documents;

/// <summary>The email that carried a document to the customer, and where it has got to.</summary>
/// <param name="Recipient">The address it was sent to.</param>
/// <param name="Status">queued, sending, sent, delivered, failed or bounced.</param>
/// <param name="SentAt">When the email provider accepted it.</param>
public sealed record BookingDocumentEmailResponse(string Recipient, string Status, DateTimeOffset? SentAt);

/// <summary>An invoice or voucher issued for a booking, as the console shows it.</summary>
/// <param name="Id">The document's id — what reissuing names.</param>
/// <param name="DocumentType">Invoice or Voucher.</param>
/// <param name="DocumentNumber">The number as printed, e.g. INV-LAGOST-2026-000042.</param>
/// <param name="IssueNumber">1 for a first issue; one more for each reissue that replaced the one before.</param>
/// <param name="Status">Pending while the PDF is being prepared, Ready once it can be downloaded, Failed if it could not be prepared.</param>
/// <param name="ProductType">For a voucher, what it is for: Flight, Bus, Tour, Visa or GroupDeparture.</param>
/// <param name="IssuedAt">When the number was taken.</param>
/// <param name="SupersedesDocumentNumber">The number of the document this one replaced, for a reissue.</param>
/// <param name="SupersededByDocumentId">The document that replaced this one. Null while it is current.</param>
/// <param name="SupersededByDocumentNumber">That document's number.</param>
/// <param name="FileName">What the PDF is called when downloaded. Null until it is ready.</param>
/// <param name="SizeBytes">The PDF's size. Null until it is ready.</param>
/// <param name="Checksum">SHA-256 of the PDF, hex. Every download is checked against it.</param>
/// <param name="DownloadUrl">
/// A signed, time-limited link to the PDF — relative to the API when files are stored locally.
/// Needs no Authorization header, so it works as an ordinary link. Null until the PDF is ready.
/// </param>
/// <param name="DownloadExpiresAt">When <paramref name="DownloadUrl"/> stops working.</param>
/// <param name="Email">The email that carried it to the customer, once one is queued.</param>
public sealed record BookingDocumentResponse(
    Guid Id,
    string DocumentType,
    string DocumentNumber,
    int IssueNumber,
    string Status,
    string? ProductType,
    DateTimeOffset IssuedAt,
    string? SupersedesDocumentNumber,
    Guid? SupersededByDocumentId,
    string? SupersededByDocumentNumber,
    string? FileName,
    long? SizeBytes,
    string? Checksum,
    string? DownloadUrl,
    DateTimeOffset? DownloadExpiresAt,
    BookingDocumentEmailResponse? Email);
