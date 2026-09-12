using TripsAgent.Domain.Common;

namespace TripsAgent.Domain.Assets;

/// <summary>The target size of one rendition, and what to call it.</summary>
/// <param name="Kind">Which rendition this is.</param>
/// <param name="MaxEdge">
/// The longest edge the rendition may have, in pixels. Zero means "full resolution" and is used
/// only by <see cref="AssetVariantKind.Original"/>.
/// </param>
public sealed record AssetVariantSpec(AssetVariantKind Kind, int MaxEdge);

/// <summary>
/// What the asset pipeline will accept, and what it renders.
/// </summary>
/// <remarks>
/// In Domain rather than in configuration because these are product rules, not deployment
/// settings: a 40MB logo is not something an operator should be able to switch on, and the
/// database repeats the same caps as CHECK constraints so a hand-written INSERT cannot dodge them.
/// </remarks>
public static class AssetRules
{
    /// <summary>The images we accept as input. Output is always WebP.</summary>
    /// <remarks>
    /// SVG is absent deliberately: it is XML that can carry script, and "resize an SVG" is not a
    /// thing. GIF is absent because animation makes every step of this pipeline ambiguous.
    /// </remarks>
    public static IReadOnlyList<string> ImageContentTypes { get; } =
        [MediaTypes.Jpeg, MediaTypes.Png, MediaTypes.Webp];

    /// <summary>The extensions matching <see cref="ImageContentTypes"/>, for the file picker.</summary>
    public static IReadOnlyList<string> ImageExtensions { get; } = [".jpg", ".jpeg", ".png", ".webp"];

    /// <summary>Every rendition is WebP: roughly 30% smaller than JPEG at the same quality.</summary>
    public const string VariantContentType = MediaTypes.Webp;

    /// <summary>
    /// WebP quality for renditions.
    /// </summary>
    /// <remarks>
    /// 82 rather than 100. Above about 85 the file grows fast for differences nobody can see on a
    /// phone, and these images are served to travellers on Nigerian mobile data.
    /// </remarks>
    public const int VariantQuality = 82;

    /// <summary>How long a filename we keep. Matches the column.</summary>
    public const int MaxFileNameLength = 200;

    /// <summary>How much of a scanner's finding we keep. Matches the column.</summary>
    public const int MaxScanSignatureLength = 200;

    /// <summary>
    /// How long an upload window stays open.
    /// </summary>
    /// <remarks>
    /// Long enough for a 10MB photo over a slow mobile connection, short enough that a leaked
    /// presigned URL is worthless by the time it is found.
    /// </remarks>
    public static TimeSpan UploadWindow { get; } = TimeSpan.FromMinutes(15);

    /// <summary>How long one pipeline run owns an asset before another may take it over.</summary>
    /// <remarks>
    /// Comfortably longer than the slowest real run — a 20MB photo rendered four times takes
    /// seconds — so a live run is never overtaken, and short enough that a dead Worker's asset is
    /// picked up by the next sweep.
    /// </remarks>
    public static TimeSpan ProcessingLease { get; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// How long an uploaded asset may sit untouched before the sweep assumes its job was lost.
    /// </summary>
    public static TimeSpan StalledAfter { get; } = TimeSpan.FromMinutes(15);

    /// <summary>How long a link to a processed asset stays valid.</summary>
    /// <remarks>An hour, so a storefront page's images survive the page being open.</remarks>
    public static TimeSpan DownloadWindow { get; } = TimeSpan.FromHours(1);

    /// <summary>
    /// The renditions rendered for every image, largest bound first.
    /// </summary>
    /// <remarks>
    /// <see cref="AssetVariantKind.Original"/> comes first and is unbounded: it is the
    /// metadata-stripped replacement for the raw upload, and the asset points at it once
    /// processing finishes. The rest are skipped when the source is already smaller than the
    /// bound — upscaling invents detail and costs bytes.
    /// </remarks>
    public static IReadOnlyList<AssetVariantSpec> VariantSpecs { get; } =
    [
        new(AssetVariantKind.Original, 0),
        new(AssetVariantKind.Large, 2048),
        new(AssetVariantKind.Medium, 1024),
        new(AssetVariantKind.Thumbnail, 320),
    ];

    /// <summary>
    /// The hard ceiling on any upload, whatever its purpose.
    /// </summary>
    /// <remarks>
    /// The pipeline reads a whole file into memory to scan and re-encode it, so this number is
    /// also the worst-case memory cost of one job. Raising it means raising the Worker's memory.
    /// </remarks>
    public const long AbsoluteMaxSizeBytes = 20L * 1024 * 1024;

    /// <summary>
    /// The most pixels an image may decode to.
    /// </summary>
    /// <remarks>
    /// A size cap on the file is not a cap on memory: a 2MB PNG of one flat colour can declare
    /// 50,000 × 50,000 pixels and decode to ten gigabytes. Forty megapixels is comfortably above
    /// any phone camera and bounds one decode to roughly 160MB.
    /// </remarks>
    public const long MaxPixels = 40_000_000;

    /// <summary>The size cap for one purpose.</summary>
    public static long MaxSizeBytes(AssetPurpose purpose) => purpose switch
    {
        // A logo is a logo. Anything bigger is a photograph somebody put in the wrong field, and
        // it is about to be rendered 40 pixels tall on an invoice.
        AssetPurpose.AgencyLogo => 2L * 1024 * 1024,

        AssetPurpose.ProductMedia or AssetPurpose.SiteMedia => 10L * 1024 * 1024,

        AssetPurpose.Attachment => AbsoluteMaxSizeBytes,

        // Rendered by us, but still bounded: a runaway template should fail, not fill the bucket.
        AssetPurpose.GeneratedDocument => AbsoluteMaxSizeBytes,

        _ => throw new ArgumentOutOfRangeException(nameof(purpose), purpose, "Unknown asset purpose."),
    };

    /// <summary>The content types one purpose accepts.</summary>
    public static IReadOnlyList<string> AllowedContentTypes(AssetPurpose purpose) => purpose switch
    {
        // Branding and media are rendered as images, so a PDF logo is not a logo.
        AssetPurpose.AgencyLogo or AssetPurpose.ProductMedia or AssetPurpose.SiteMedia => ImageContentTypes,

        AssetPurpose.Attachment => [MediaTypes.Pdf, .. ImageContentTypes],

        AssetPurpose.GeneratedDocument => [MediaTypes.Pdf],

        _ => throw new ArgumentOutOfRangeException(nameof(purpose), purpose, "Unknown asset purpose."),
    };

    /// <summary>The extensions one purpose accepts, for the file picker.</summary>
    public static IReadOnlyList<string> AllowedExtensions(AssetPurpose purpose) => purpose switch
    {
        AssetPurpose.Attachment => [".pdf", .. ImageExtensions],
        AssetPurpose.GeneratedDocument => [".pdf"],
        _ => ImageExtensions,
    };

    /// <summary>True for a purpose somebody may upload a file under. Generated documents are ours alone.</summary>
    public static bool IsUploadable(AssetPurpose purpose) =>
        Enum.IsDefined(purpose) && purpose != AssetPurpose.GeneratedDocument;

    public static bool IsAllowedSize(AssetPurpose purpose, long sizeBytes) =>
        sizeBytes > 0 && sizeBytes <= MaxSizeBytes(purpose);

    public static bool IsAllowedContentType(AssetPurpose purpose, string? contentType) =>
        contentType is not null
        && AllowedContentTypes(purpose).Contains(contentType, StringComparer.OrdinalIgnoreCase);

    public static bool IsImage(string? contentType) =>
        contentType is not null
        && ImageContentTypes.Contains(contentType, StringComparer.OrdinalIgnoreCase);

    /// <summary>"2MB" — for a message someone actually reads.</summary>
    public static string SizeDescription(AssetPurpose purpose) =>
        $"{MaxSizeBytes(purpose) / (1024 * 1024)}MB";

    /// <summary>
    /// The storage key for an asset's raw upload.
    /// </summary>
    /// <remarks>
    /// Generated from ids, never from the filename. A key built from user input is how one
    /// agency's upload overwrites another's, and how <c>../</c> escapes the storage root. The
    /// agency id leads so that a bucket listing is grouped by tenant, which makes a per-tenant
    /// lifecycle rule or a deletion request possible later.
    /// </remarks>
    public static string UploadKey(Guid agencyId, Guid assetId) =>
        $"assets/{agencyId:N}/{assetId:N}/upload";

    /// <summary>
    /// The storage key for a byte-for-byte copy of a scanned file that is not an image.
    /// </summary>
    /// <remarks>
    /// A new key rather than the upload key, because the upload key is still writable through its
    /// presigned URL until the window closes — see <see cref="Asset.ReplaceOriginal"/>.
    /// </remarks>
    public static string VerbatimKey(Guid agencyId, Guid assetId, string contentType) =>
        $"assets/{agencyId:N}/{assetId:N}/original{ExtensionFor(contentType)}";

    /// <summary>The storage key for one rendition.</summary>
    public static string VariantKey(Guid agencyId, Guid assetId, AssetVariantKind kind) =>
        $"assets/{agencyId:N}/{assetId:N}/{kind.ToString().ToLowerInvariant()}.webp";

    /// <summary>
    /// The storage key for one render of an issued document's PDF.
    /// </summary>
    /// <remarks>
    /// From ids, never the printed number: a number prefix may contain a slash, and a key is no place
    /// for text an agency configured. A key per render rather than per document, so two renders
    /// racing for one document can never write over each other — the file that was issued is the
    /// only one its row can ever point at.
    /// </remarks>
    public static string GeneratedDocumentKey(Guid agencyId, Guid documentId, Guid renderId) =>
        $"documents/{agencyId:N}/{documentId:N}/{renderId:N}.pdf";

    /// <summary>The extension a stored copy is given, so a bucket listing is readable.</summary>
    public static string ExtensionFor(string contentType) =>
        Extensions.TryGetValue(contentType, out var extension) ? extension : string.Empty;

    private static readonly Dictionary<string, string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        [MediaTypes.Pdf] = ".pdf",
        [MediaTypes.Jpeg] = ".jpg",
        [MediaTypes.Png] = ".png",
        [MediaTypes.Webp] = ".webp",
    };
}
