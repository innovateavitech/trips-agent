namespace TripsAgent.Contracts.Assets;

/// <summary>Asks for somewhere to upload one file.</summary>
/// <param name="Purpose">What the file is for: AgencyLogo, ProductMedia, SiteMedia or Attachment.</param>
/// <param name="FileName">What the person called it. Shown back to them; never used as a path.</param>
/// <param name="SizeBytes">The size the browser reports. Checked now, and measured again once the file arrives.</param>
/// <param name="ContentType">The type the browser reports. Checked now; the file's own bytes decide later.</param>
public sealed record RequestAssetUploadRequest(
    string Purpose,
    string? FileName,
    long SizeBytes,
    string? ContentType);

/// <summary>
/// Where to send the file. The browser sends it there directly — the bytes never pass through the
/// API — and then calls <c>POST /api/v1/assets/{id}/complete</c>.
/// </summary>
/// <param name="AssetId">The asset this upload becomes.</param>
/// <param name="UploadUrl">Signed and short-lived. Send the file as the raw request body, not a form.</param>
/// <param name="Method">The HTTP method to use. PUT.</param>
/// <param name="Headers">Headers to send exactly as given; the signature covers them.</param>
/// <param name="MaxSizeBytes">The most the upload URL will accept.</param>
/// <param name="ExpiresAt">When the upload URL stops working.</param>
public sealed record AssetUploadResponse(
    Guid AssetId,
    string UploadUrl,
    string Method,
    IReadOnlyDictionary<string, string> Headers,
    long MaxSizeBytes,
    DateTimeOffset ExpiresAt);

/// <summary>A link to one rendition of an asset.</summary>
/// <param name="Kind">Original, Large, Medium or Thumbnail.</param>
/// <param name="Url">Signed and time-limited. Fetch a fresh one rather than storing it.</param>
/// <param name="ContentType">What the link returns — image/webp for every image rendition.</param>
/// <param name="Width">Pixel width, for an image.</param>
/// <param name="Height">Pixel height, for an image.</param>
/// <param name="ExpiresAt">When the link stops working.</param>
public sealed record AssetLinkResponse(
    string Kind,
    string Url,
    string ContentType,
    int? Width,
    int? Height,
    DateTimeOffset ExpiresAt);

/// <summary>
/// One asset and where it is in the pipeline.
/// </summary>
/// <param name="Id">The asset's id.</param>
/// <param name="Purpose">What it is for.</param>
/// <param name="FileName">What the person called it.</param>
/// <param name="Status">AwaitingUpload, Uploaded, Processing, Ready, Quarantined or Failed.</param>
/// <param name="ScanStatus">Pending, Clean, Infected or Unscannable.</param>
/// <param name="ContentType">The type established from the file's bytes. Null until it arrives.</param>
/// <param name="SizeBytes">The stored size.</param>
/// <param name="Width">Pixel width, for a processed image.</param>
/// <param name="Height">Pixel height, for a processed image.</param>
/// <param name="FailureReason">Why the file cannot be used, written for the person who uploaded it.</param>
/// <param name="CreatedAt">When the upload was started.</param>
/// <param name="Links">
/// Empty until the asset is Ready — which it only becomes once it has been scanned clean. There is
/// no way to fetch the bytes of an asset that is not.
/// </param>
public sealed record AssetResponse(
    Guid Id,
    string Purpose,
    string FileName,
    string Status,
    string ScanStatus,
    string? ContentType,
    long SizeBytes,
    int? Width,
    int? Height,
    string? FailureReason,
    DateTimeOffset CreatedAt,
    IReadOnlyList<AssetLinkResponse> Links);

/// <summary>What one purpose accepts.</summary>
public sealed record AssetPurposeLimitsResponse(
    string Purpose,
    long MaxSizeBytes,
    IReadOnlyList<string> AllowedContentTypes,
    IReadOnlyList<string> AllowedExtensions);

/// <summary>
/// What every purpose accepts, so the browser can refuse a bad file before a slow upload. The
/// server enforces the same limits regardless.
/// </summary>
public sealed record AssetUploadLimitsResponse(IReadOnlyList<AssetPurposeLimitsResponse> Purposes);
