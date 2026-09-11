using TripsAgent.Domain.Auditing;
using TripsAgent.Domain.Common;

namespace TripsAgent.Domain.Assets;

/// <summary>What an asset is for. Decides the size cap and which formats are accepted.</summary>
public enum AssetPurpose
{
    /// <summary>The agent's own logo, on their storefront, their invoices and their emails.</summary>
    AgencyLogo = 1,

    /// <summary>A photo on a tour, package or visa product.</summary>
    ProductMedia = 2,

    /// <summary>An image placed in a site-builder block.</summary>
    SiteMedia = 3,

    /// <summary>A file attached to a record — a supplier invoice, a signed form.</summary>
    Attachment = 4,
}

/// <summary>Where an asset is in its life.</summary>
public enum AssetStatus
{
    /// <summary>
    /// The row exists and the storage key is reserved, but the bytes have not arrived. Uploads go
    /// straight to the object store, so this is the only state the API can create.
    /// </summary>
    AwaitingUpload = 1,

    /// <summary>Bytes are in storage and have been checked for type and size. Not yet scanned.</summary>
    Uploaded = 2,

    /// <summary>The background pipeline has it.</summary>
    Processing = 3,

    /// <summary>Scanned clean, metadata stripped, variants rendered. The only servable state.</summary>
    Ready = 4,

    /// <summary>The scanner found something. The bytes are deleted and the row is kept as a record.</summary>
    Quarantined = 5,

    /// <summary>Something about this file could not be processed. Never served.</summary>
    Failed = 6,
}

/// <summary>The virus scanner's answer. Nothing is served until this is <see cref="Clean"/>.</summary>
public enum AssetScanStatus
{
    /// <summary>Not scanned yet. The default, so a row that skips the pipeline is never servable.</summary>
    Pending = 1,

    Clean = 2,

    Infected = 3,

    /// <summary>
    /// The scanner could not give an answer — it was down, or it timed out. Deliberately distinct
    /// from <see cref="Clean"/>: "we did not manage to check" is not "we checked and it was fine".
    /// </summary>
    Unscannable = 4,
}

/// <summary>A rendition of an image asset.</summary>
public enum AssetVariantKind
{
    /// <summary>Full resolution, re-encoded. The canonical servable copy of an image.</summary>
    Original = 1,

    /// <summary>A list row or a picker tile.</summary>
    Thumbnail = 2,

    /// <summary>A card or a gallery tile.</summary>
    Medium = 3,

    /// <summary>A storefront hero.</summary>
    Large = 4,
}

/// <summary>
/// One uploaded file, and everything we know about it.
/// </summary>
/// <remarks>
/// <para>
/// The state machine exists because the bytes and the row are created at different times. An
/// upload goes straight from the browser to the object store (nothing proxies through the API),
/// so the row is written first, in <see cref="AssetStatus.AwaitingUpload"/>, and the checks that
/// would normally happen while streaming the request instead happen afterwards against what
/// actually landed.
/// </para>
/// <para>
/// <see cref="ScanStatus"/> is what gates serving, and it starts at
/// <see cref="AssetScanStatus.Pending"/>. Every path to "servable" has to walk through the
/// pipeline, so a row inserted by a seed script, a half-finished migration or a future feature
/// that forgets to scan is invisible rather than dangerous.
/// </para>
/// </remarks>
public sealed class Asset : Entity, IAuditableEntity, ITenantScoped, IAuditLogged
{
    private Asset()
    {
    }

    /// <summary>
    /// Reserves a row and a storage key for bytes that have not arrived yet.
    /// </summary>
    /// <param name="agencyId">The agency that will own the file.</param>
    /// <param name="purpose">What it is for — decides the size cap and the accepted formats.</param>
    /// <param name="fileName">What the person called it. Stripped to its own name; nothing builds a path from it.</param>
    /// <param name="uploadExpiresAt">When the upload window closes, matching the presigned URL's own expiry.</param>
    /// <remarks>
    /// The storage key is derived here from the new row's own id, never from anything the caller
    /// sends — see <see cref="AssetRules.UploadKey"/>.
    /// </remarks>
    public static Asset Reserve(
        Guid agencyId,
        AssetPurpose purpose,
        string fileName,
        DateTimeOffset uploadExpiresAt)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(agencyId, Guid.Empty);

        var asset = new Asset
        {
            AgencyId = agencyId,
            Purpose = purpose,
            FileName = SafeFileName(fileName),
            Status = AssetStatus.AwaitingUpload,
            ScanStatus = AssetScanStatus.Pending,
            UploadExpiresAt = uploadExpiresAt,
        };

        asset.StorageKey = AssetRules.UploadKey(agencyId, asset.Id);

        return asset;
    }

    public Guid AgencyId { get; private set; }

    public AssetPurpose Purpose { get; private set; }

    /// <summary>The name the person chose, for download headers and for the media library.</summary>
    public string FileName { get; private set; } = string.Empty;

    /// <summary>
    /// Where the canonical bytes live.
    /// </summary>
    /// <remarks>
    /// Always the object a caller should serve. For an image it moves once, from the raw upload to
    /// the metadata-stripped rendition, when processing finishes — see
    /// <see cref="ReplaceOriginal"/>.
    /// </remarks>
    public string StorageKey { get; private set; } = string.Empty;

    /// <summary>The type established by sniffing the stored bytes. Null until they arrive.</summary>
    public string? ContentType { get; private set; }

    public long SizeBytes { get; private set; }

    /// <summary>
    /// SHA-256 of the canonical bytes, hex-encoded. Null until the pipeline has read them.
    /// </summary>
    /// <remarks>
    /// Computed by the Worker rather than when the upload completes, because hashing means reading
    /// every byte, and the point of a direct-to-storage upload is that the API never does.
    /// </remarks>
    public string? Checksum { get; private set; }

    public AssetStatus Status { get; private set; }

    public AssetScanStatus ScanStatus { get; private set; }

    public DateTimeOffset? ScannedAt { get; private set; }

    /// <summary>What the scanner named, when it found something. Kept for the incident, not shown.</summary>
    public string? ScanSignature { get; private set; }

    public int? Width { get; private set; }

    public int? Height { get; private set; }

    public DateTimeOffset? ProcessedAt { get; private set; }

    /// <summary>Why this asset will never be served. Null unless <see cref="Status"/> is Failed.</summary>
    public string? FailureReason { get; private set; }

    /// <summary>
    /// When the upload window closes.
    /// </summary>
    /// <remarks>
    /// A reserved row whose bytes never arrived is litter, and the window is what lets a sweeper
    /// tell litter from an upload still in flight over a slow Nigerian mobile connection.
    /// </remarks>
    public DateTimeOffset UploadExpiresAt { get; private set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>
    /// True only when the file has been scanned clean and fully processed.
    /// </summary>
    /// <remarks>
    /// The acceptance criterion in issue #18, as one expression. Both halves matter: Ready without
    /// Clean would serve a file the scanner rejected, and Clean without Ready would serve an image
    /// whose variants do not exist yet.
    /// </remarks>
    public bool IsServable => Status == AssetStatus.Ready && ScanStatus == AssetScanStatus.Clean;

    /// <summary>Whether the pipeline should render variants for this file.</summary>
    public bool IsImage => ContentType is not null && AssetRules.IsImage(ContentType);

    /// <summary>
    /// Records what actually landed in storage.
    /// </summary>
    /// <param name="contentType">Sniffed from the stored bytes, never taken from the client.</param>
    /// <param name="sizeBytes">Measured in storage, not the length the client declared.</param>
    public void RecordUpload(string contentType, long sizeBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sizeBytes);

        if (Status != AssetStatus.AwaitingUpload)
        {
            throw new InvalidOperationException(
                $"This asset is {Status}, so its upload is already recorded. Completing an upload "
                + "twice must not overwrite what the pipeline has since checked.");
        }

        ContentType = contentType;
        SizeBytes = sizeBytes;
        Status = AssetStatus.Uploaded;
    }

    /// <summary>
    /// Until when the pipeline run that claimed this asset owns it. Null when nobody does.
    /// </summary>
    /// <remarks>
    /// Two runs can be pointed at one asset — a Hangfire retry and the sweep's re-enqueue, say —
    /// and both would render the same variants and race to insert the same rows. The claim makes
    /// the second one step aside. It is a lease rather than a flag so that a Worker which dies
    /// mid-run does not hold the asset forever.
    /// </remarks>
    public DateTimeOffset? ProcessingClaimedUntil { get; private set; }

    /// <summary>
    /// Bumped by every claim, and checked by the database on every save.
    /// </summary>
    /// <remarks>
    /// The same optimistic-concurrency idiom as <c>Wallet.Version</c>. Two runs that read the row
    /// at once both see the lease free; only the first save of the bumped version succeeds.
    /// </remarks>
    public int Version { get; private set; }

    /// <summary>
    /// Claims this asset for one pipeline run. False when there is nothing to do, or when another
    /// run holds an unexpired claim.
    /// </summary>
    /// <remarks>
    /// Re-entry from <see cref="AssetStatus.Processing"/> is allowed once the lease has run out,
    /// because a Worker that dies mid-run leaves the row in Processing and the job has to be safe
    /// to run again. The row's concurrency token makes the claim atomic: two runs that read the
    /// row at the same moment cannot both save it.
    /// </remarks>
    public bool TryBeginProcessing(DateTimeOffset now)
    {
        if (Status is not (AssetStatus.Uploaded or AssetStatus.Processing))
        {
            return false;
        }

        if (Status == AssetStatus.Processing && ProcessingClaimedUntil > now)
        {
            return false;
        }

        Status = AssetStatus.Processing;
        ProcessingClaimedUntil = now + AssetRules.ProcessingLease;
        FailureReason = null;
        Version++;
        return true;
    }

    /// <summary>The scanner found nothing.</summary>
    public void RecordCleanScan(DateTimeOffset at)
    {
        ScanStatus = AssetScanStatus.Clean;
        ScannedAt = at;
        ScanSignature = null;
    }

    /// <summary>
    /// The scanner found something. The row survives; the bytes do not.
    /// </summary>
    /// <remarks>
    /// Kept rather than deleted so that "why did my upload disappear?" has an answer, and so a
    /// repeat offender is visible. The caller deletes the object from storage — an infected file
    /// nobody can reach is still an infected file sitting in our bucket.
    /// </remarks>
    public void Quarantine(string? signature, DateTimeOffset at)
    {
        ScanStatus = AssetScanStatus.Infected;
        ScanSignature = signature?[..Math.Min(signature.Length, AssetRules.MaxScanSignatureLength)];
        ScannedAt = at;
        Status = AssetStatus.Quarantined;
        FailureReason = "This file was rejected by the virus scanner.";
        ProcessingClaimedUntil = null;
    }

    /// <summary>
    /// The scanner gave no answer. Fails closed.
    /// </summary>
    /// <remarks>
    /// Recorded rather than left Pending so the reason is visible, and left retryable: the pipeline
    /// throws after recording it, so Hangfire tries again once the scanner is back.
    /// </remarks>
    public void RecordUnscannable(DateTimeOffset at)
    {
        ScanStatus = AssetScanStatus.Unscannable;
        ScannedAt = at;

        // Released, so the retry can claim it straight away rather than waiting out the lease.
        ProcessingClaimedUntil = null;
    }

    /// <summary>
    /// Points the asset at the copy the pipeline scanned, and forgets the raw upload.
    /// </summary>
    /// <remarks>
    /// <para>
    /// For an image that copy is the metadata-stripped rendition. The raw upload is the file that
    /// carries EXIF — including, on a phone photo, the GPS coordinates of wherever it was taken.
    /// Decoding it to pixels and re-encoding drops every metadata block there is, so once that
    /// rendition exists the raw file has no use and every reason to be gone.
    /// </para>
    /// <para>
    /// For anything else it is a byte-for-byte copy under a new key. That matters as much: the
    /// presigned upload URL stays valid until its window closes, so the raw key can be written
    /// again after the scan. Serving the key that was scanned, and that no URL can write to, is
    /// what makes "scanned clean" describe the bytes a traveller actually receives.
    /// </para>
    /// <para>The caller deletes the raw upload from storage after this returns.</para>
    /// </remarks>
    public void ReplaceOriginal(string storageKey, string contentType, long sizeBytes, string checksum)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);
        ArgumentException.ThrowIfNullOrWhiteSpace(checksum);

        StorageKey = storageKey;
        ContentType = contentType;
        SizeBytes = sizeBytes;
        Checksum = checksum;
    }

    /// <summary>Processing finished. From here the asset may be served.</summary>
    /// <param name="width">Pixel width, for an image. Null for anything else.</param>
    /// <param name="height">Pixel height, for an image. Null for anything else.</param>
    /// <param name="at">When processing finished.</param>
    public void MarkReady(int? width, int? height, DateTimeOffset at)
    {
        if (ScanStatus != AssetScanStatus.Clean)
        {
            throw new InvalidOperationException(
                $"This asset's scan status is {ScanStatus}, so it cannot be made servable. "
                + "Nothing unscanned is served — see issue #18.");
        }

        Width = width;
        Height = height;
        Status = AssetStatus.Ready;
        ProcessedAt = at;
        FailureReason = null;
        ProcessingClaimedUntil = null;
    }

    /// <summary>
    /// This asset cannot be used. Never served, and the reason is shown to the agent.
    /// </summary>
    public void MarkFailed(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        Status = AssetStatus.Failed;
        FailureReason = reason;
        ProcessingClaimedUntil = null;
    }

    /// <summary>
    /// Reduces a client-supplied filename to a bare name.
    /// </summary>
    /// <remarks>
    /// Nothing builds a path from this — the storage key is generated — but it is shown in the
    /// media library and may end up in a <c>Content-Disposition</c> header, so it does not get to
    /// contain a directory.
    /// </remarks>
    private static string SafeFileName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return "upload";
        }

        // Both separators, because a Windows client sends backslashes and Path.GetFileName on
        // Linux does not treat those as separators at all.
        var name = fileName.Replace('\\', '/');
        name = name[(name.LastIndexOf('/') + 1)..].Trim();

        return string.IsNullOrWhiteSpace(name)
            ? "upload"
            : name[..Math.Min(name.Length, AssetRules.MaxFileNameLength)];
    }
}

/// <summary>
/// One rendition of an image asset: a resized, re-encoded WebP.
/// </summary>
/// <remarks>
/// A row per rendition rather than a column per size, because the set of sizes will change — a
/// new storefront template wants a different hero width — and "add a variant kind" should be a
/// row, not a migration.
/// </remarks>
public sealed class AssetVariant : Entity, IAuditableEntity, ITenantScoped
{
    private AssetVariant()
    {
    }

    public static AssetVariant Create(
        Guid agencyId,
        Guid assetId,
        AssetVariantKind kind,
        string storageKey,
        string contentType,
        int width,
        int height,
        long sizeBytes)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(agencyId, Guid.Empty);
        ArgumentOutOfRangeException.ThrowIfEqual(assetId, Guid.Empty);
        ArgumentException.ThrowIfNullOrWhiteSpace(storageKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sizeBytes);

        return new AssetVariant
        {
            AgencyId = agencyId,
            AssetId = assetId,
            Kind = kind,
            StorageKey = storageKey,
            ContentType = contentType,
            Width = width,
            Height = height,
            SizeBytes = sizeBytes,
        };
    }

    /// <summary>
    /// Carried on the variant as well as the asset.
    /// </summary>
    /// <remarks>
    /// Denormalised on purpose: it is what makes a variant <see cref="ITenantScoped"/>, so the EF
    /// filter and the row-level security policy both apply directly rather than through a join to
    /// the parent asset.
    /// </remarks>
    public Guid AgencyId { get; private set; }

    public Guid AssetId { get; private set; }

    public AssetVariantKind Kind { get; private set; }

    public string StorageKey { get; private set; } = string.Empty;

    public string ContentType { get; private set; } = string.Empty;

    public int Width { get; private set; }

    public int Height { get; private set; }

    public long SizeBytes { get; private set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
