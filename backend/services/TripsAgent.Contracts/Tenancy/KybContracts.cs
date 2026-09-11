namespace TripsAgent.Contracts.Tenancy;

/// <summary>One uploaded document, as the console shows it.</summary>
public sealed record KybDocumentResponse(
    Guid Id,
    string DocumentType,
    string FileName,
    string ContentType,
    long SizeBytes,
    DateTimeOffset UploadedAt);

/// <summary>
/// Everything the onboarding screens need to render the KYB step in any of its states.
/// </summary>
/// <param name="Status">Draft, Submitted, UnderReview, Approved or Rejected.</param>
/// <param name="RejectionReason">Why it was turned down, shown verbatim. Null unless rejected.</param>
/// <param name="CanEdit">Whether documents may still be added or removed.</param>
/// <param name="MissingDocumentTypes">Required documents not yet attached.</param>
/// <param name="CanFundWallet">
/// Whether the agency may add funds yet. False until verification, and the FRD asks for the
/// option to be visibly disabled with a reason rather than to fail when pressed.
/// </param>
/// <param name="WalletFundingBlockedReason">The explanation to show. Null when funding is allowed.</param>
public sealed record KybStatusResponse(
    Guid? SubmissionId,
    string Status,
    string AgencyStatus,
    DateTimeOffset? SubmittedAt,
    DateTimeOffset? ReviewedAt,
    string? RejectionReason,
    bool CanEdit,
    IReadOnlyList<KybDocumentResponse> Documents,
    IReadOnlyList<string> MissingDocumentTypes,
    bool CanFundWallet,
    string? WalletFundingBlockedReason);

/// <summary>The limits the upload control should enforce before a byte is sent.</summary>
/// <remarks>
/// Served to the client so the browser can reject an oversized or wrong-typed file immediately,
/// rather than after a slow upload. The server enforces the same limits regardless — this is for
/// the person, not for safety.
/// </remarks>
public sealed record KybUploadLimitsResponse(
    long MaxSizeBytes,
    IReadOnlyList<string> AllowedContentTypes,
    IReadOnlyList<string> AllowedExtensions,
    IReadOnlyList<string> RequiredDocumentTypes);

/// <summary>One row in the Trips-side review queue.</summary>
public sealed record KybQueueItemResponse(
    Guid SubmissionId,
    Guid AgencyId,
    string AgencyName,
    string CountryCode,
    string Status,
    DateTimeOffset? SubmittedAt,
    int DocumentCount);

/// <summary>A document a reviewer can open, behind a link that expires.</summary>
public sealed record KybReviewDocumentResponse(
    Guid Id,
    string DocumentType,
    string FileName,
    string ContentType,
    long SizeBytes,
    string Url,
    DateTimeOffset UrlExpiresAt);

/// <summary>Everything a reviewer needs to decide one submission.</summary>
public sealed record KybReviewDetailResponse(
    Guid SubmissionId,
    Guid AgencyId,
    string LegalName,
    string? TradingName,
    string CountryCode,
    string AgencyStatus,
    string SubmissionStatus,
    DateTimeOffset? SubmittedAt,
    string? RejectionReason,
    IReadOnlyList<KybReviewDocumentResponse> Documents);

/// <summary>A rejection. The reason is mandatory and is shown to the agency verbatim.</summary>
public sealed record RejectKybRequest(string Reason);
