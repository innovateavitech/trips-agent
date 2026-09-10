using TripsAgent.Domain.Common;

namespace TripsAgent.Domain.Tenancy.Kyb;

/// <summary>Where a KYB submission is in its review.</summary>
public enum KybSubmissionStatus
{
    /// <summary>Being assembled. The agency can still add and remove documents.</summary>
    Draft = 1,

    /// <summary>Handed to Trips. Documents are frozen from here.</summary>
    Submitted = 2,

    /// <summary>An admin has picked it up.</summary>
    UnderReview = 3,

    Approved = 4,

    /// <summary>Turned down with a reason. The agency may correct it and submit again.</summary>
    Rejected = 5,
}

/// <summary>The kinds of document Trips asks a travel business for.</summary>
public enum KybDocumentType
{
    CertificateOfIncorporation = 1,
    TaxIdentification = 2,
    ProofOfAddress = 3,
    DirectorIdentification = 4,
    Other = 99,
}

/// <summary>
/// One agency's attempt at proving it is a real business.
/// </summary>
/// <remarks>
/// A row per attempt rather than a status column on the agency. A rejected submission and the
/// corrected one that follows are different things, and keeping both is what lets an admin see
/// what changed — and what lets us answer "why was this agency approved?" a year later.
/// </remarks>
public sealed class KybSubmission : Entity, IAuditableEntity, ITenantScoped
{
    /// <summary>Documents required before a submission can be handed over.</summary>
    public static IReadOnlyList<KybDocumentType> RequiredDocuments { get; } =
    [
        KybDocumentType.CertificateOfIncorporation,
        KybDocumentType.TaxIdentification,
    ];

    private KybSubmission()
    {
    }

    /// <summary>Opens a new, empty submission for an agency to fill in.</summary>
    public static KybSubmission StartFor(Guid agencyId)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(agencyId, Guid.Empty);

        return new KybSubmission { AgencyId = agencyId, Status = KybSubmissionStatus.Draft };
    }

    public Guid AgencyId { get; private set; }

    public KybSubmissionStatus Status { get; private set; }

    public DateTimeOffset? SubmittedAt { get; private set; }

    public Guid? ReviewedByUserId { get; private set; }

    public DateTimeOffset? ReviewedAt { get; private set; }

    /// <summary>Why it was turned down. Mandatory on rejection, and shown to the agency verbatim.</summary>
    public string? RejectionReason { get; private set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>True while the agency can still add or remove documents.</summary>
    public bool IsEditable => Status is KybSubmissionStatus.Draft or KybSubmissionStatus.Rejected;

    /// <summary>True once it is with Trips and not yet decided.</summary>
    public bool IsAwaitingDecision => Status is KybSubmissionStatus.Submitted or KybSubmissionStatus.UnderReview;

    /// <summary>
    /// Hands the submission to Trips.
    /// </summary>
    /// <param name="providedDocuments">The document types attached, so the required set can be checked.</param>
    public void Submit(IReadOnlyCollection<KybDocumentType> providedDocuments, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(providedDocuments);

        if (!IsEditable)
        {
            throw new InvalidOperationException(
                $"This submission is {Status} and cannot be submitted again. "
                + "A submission that is already with Trips is frozen until it is decided.");
        }

        var missing = RequiredDocuments.Where(required => !providedDocuments.Contains(required)).ToArray();

        if (missing.Length > 0)
        {
            throw new InvalidOperationException(
                $"Missing required document(s): {string.Join(", ", missing)}.");
        }

        Status = KybSubmissionStatus.Submitted;
        SubmittedAt = at;

        // Cleared so a corrected resubmission does not still show the previous refusal.
        RejectionReason = null;
        ReviewedByUserId = null;
        ReviewedAt = null;
    }

    /// <summary>An admin has picked the submission up.</summary>
    public void BeginReview(Guid reviewerUserId)
    {
        if (Status != KybSubmissionStatus.Submitted)
        {
            throw new InvalidOperationException($"Only a submitted application can be taken up; this one is {Status}.");
        }

        Status = KybSubmissionStatus.UnderReview;
        ReviewedByUserId = reviewerUserId;
    }

    /// <summary>Approves the submission.</summary>
    public void Approve(Guid reviewerUserId, DateTimeOffset at)
    {
        if (!IsAwaitingDecision)
        {
            throw new InvalidOperationException($"Only a submission awaiting a decision can be approved; this one is {Status}.");
        }

        Status = KybSubmissionStatus.Approved;
        ReviewedByUserId = reviewerUserId;
        ReviewedAt = at;
        RejectionReason = null;
    }

    /// <summary>
    /// Rejects the submission. The reason is mandatory — it is what the agency is shown, and
    /// "rejected" with no explanation is an unanswerable support ticket.
    /// </summary>
    public void Reject(Guid reviewerUserId, string reason, DateTimeOffset at)
    {
        if (!IsAwaitingDecision)
        {
            throw new InvalidOperationException($"Only a submission awaiting a decision can be rejected; this one is {Status}.");
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException(
                "A rejection must say why. The agency sees this text and has to know what to fix.",
                nameof(reason));
        }

        Status = KybSubmissionStatus.Rejected;
        ReviewedByUserId = reviewerUserId;
        ReviewedAt = at;
        RejectionReason = reason.Trim();
    }
}

/// <summary>One uploaded file belonging to a submission.</summary>
/// <remarks>
/// The file itself lives in blob storage; this row records where, what it is, and how big — plus a
/// checksum, so a corrupted or swapped object is detectable.
///
/// It carries its own storage key rather than an <c>asset_id</c>. The shared <c>assets</c> table,
/// with virus scanning and generated variants, belongs to the upload pipeline (#18); pointing at
/// it is a one-line migration once that lands, and inventing the table here would collide with it.
/// </remarks>
public sealed class KybDocument : Entity, IAuditableEntity, ITenantScoped
{
    private KybDocument()
    {
        FileName = string.Empty;
        StorageKey = string.Empty;
        ContentType = string.Empty;
        Checksum = string.Empty;
    }

    public static KybDocument Create(
        Guid agencyId,
        Guid submissionId,
        KybDocumentType documentType,
        string fileName,
        string storageKey,
        string contentType,
        long sizeBytes,
        string checksum)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(agencyId, Guid.Empty);

        if (!KybDocumentRules.IsAllowedContentType(contentType))
        {
            throw new ArgumentException($"'{contentType}' is not an accepted document type.", nameof(contentType));
        }

        if (!KybDocumentRules.IsAllowedSize(sizeBytes))
        {
            throw new ArgumentOutOfRangeException(
                nameof(sizeBytes),
                sizeBytes,
                $"A document must be between 1 byte and {KybDocumentRules.MaxSizeDescription}.");
        }

        return new KybDocument
        {
            AgencyId = agencyId,
            SubmissionId = submissionId,
            DocumentType = documentType,
            FileName = SanitiseFileName(fileName),
            StorageKey = storageKey,
            ContentType = contentType,
            SizeBytes = sizeBytes,
            Checksum = checksum,
        };
    }

    public Guid AgencyId { get; private set; }

    public Guid SubmissionId { get; private set; }

    public KybDocumentType DocumentType { get; private set; }

    /// <summary>The name as uploaded, stripped of any path. Shown to the reviewer.</summary>
    public string FileName { get; private set; }

    /// <summary>Where the bytes live in blob storage.</summary>
    public string StorageKey { get; private set; }

    /// <summary>The type established by sniffing the file's bytes, not the one the browser claimed.</summary>
    public string ContentType { get; private set; }

    public long SizeBytes { get; private set; }

    /// <summary>SHA-256 of the stored bytes, so corruption or substitution is detectable.</summary>
    public string Checksum { get; private set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>
    /// Keeps only the file's own name.
    /// </summary>
    /// <remarks>
    /// A browser can send <c>../../etc/passwd</c> as a filename. Nothing here builds a path from
    /// it — the storage key is generated — but it is displayed to a reviewer and may end up in a
    /// download header, so it is stripped at the point it enters the system rather than at each
    /// point it leaves.
    /// </remarks>
    private static string SanitiseFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return "document";
        }

        var name = fileName.Replace('\\', '/').Split('/')[^1].Trim();

        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(invalid.ToString(), string.Empty, StringComparison.Ordinal);
        }

        return name.Length switch
        {
            0 => "document",
            > 200 => name[^200..],
            _ => name,
        };
    }
}
