using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TripsAgent.Application.Persistence;
using TripsAgent.Application.Storage;
using TripsAgent.Application.Tenancy;
using TripsAgent.Domain.Assets;
using TripsAgent.Domain.Common;

namespace TripsAgent.Application.Assets;

/// <summary>
/// The background half of an upload: virus scan, then EXIF strip, resize and WebP.
/// </summary>
/// <remarks>
/// <para>
/// Runs in the Worker, never in a request. The order is the point. Nothing is decoded before it is
/// scanned, because an image decoder is exactly the kind of complicated parser a malicious file is
/// built to exploit. Nothing is served until every step has finished, because
/// <see cref="Asset.MarkReady"/> refuses an asset that is not scanned clean.
/// </para>
/// <para>
/// The bytes are read again here rather than trusted from the complete step. The presigned URL
/// stays writable until its window closes, so what is in storage now is not necessarily what was
/// sniffed then — and what gets scanned has to be what gets served. So the scanned bytes are
/// written to a new key that no URL can write to, and the asset points there.
/// </para>
/// <para>
/// Every run is safe to repeat. A claim with a lease stops two runs working one asset at once, and
/// a run that finds nothing to do returns quietly — Hangfire retries, the sweep re-enqueues, and
/// either may arrive after the work is done.
/// </para>
/// </remarks>
public sealed partial class ProcessAssetHandler : IAssetProcessor
{
    /// <summary>How many assets one sweep handles, so a backlog is worked through rather than loaded at once.</summary>
    private const int SweepBatchSize = 200;

    private readonly IAppDbContext _db;
    private readonly IBlobStorage _storage;
    private readonly IVirusScanner _scanner;
    private readonly IImageProcessor _images;
    private readonly IAssetPipelineDispatcher _dispatcher;
    private readonly IPlatformScope _platformScope;
    private readonly TimeProvider _clock;
    private readonly ILogger<ProcessAssetHandler> _logger;

    public ProcessAssetHandler(
        IAppDbContext db,
        IBlobStorage storage,
        IVirusScanner scanner,
        IImageProcessor images,
        IAssetPipelineDispatcher dispatcher,
        IPlatformScope platformScope,
        TimeProvider clock,
        ILogger<ProcessAssetHandler> logger)
    {
        _db = db;
        _storage = storage;
        _scanner = scanner;
        _images = images;
        _dispatcher = dispatcher;
        _platformScope = platformScope;
        _clock = clock;
        _logger = logger;
    }

    public async Task ProcessAsync(Guid assetId, CancellationToken cancellationToken = default)
    {
        // A job has no tenant: it was queued by one agency's request and runs on its own. The scope
        // is what lets it find that agency's row, and it is logged with the reason.
        using var scope = _platformScope.Enter("asset pipeline — processing one agency's uploaded file");

        var asset = await _db.Assets.FirstOrDefaultAsync(a => a.Id == assetId, cancellationToken);

        if (asset is null || !asset.TryBeginProcessing(_clock.GetUtcNow()))
        {
            LogNothingToDo(_logger, assetId);
            return;
        }

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Another run read the row at the same moment and saved its claim first. It has this.
            LogNothingToDo(_logger, assetId);
            return;
        }

        var uploadKey = asset.StorageKey;
        var (content, problem) = await ReadWithinLimitAsync(asset, cancellationToken);

        if (content is null)
        {
            await FailAsync(asset, problem!, cancellationToken);
            return;
        }

        var contentType = FileSignature.Detect(content.AsSpan(0, Math.Min(content.Length, FileSignature.RequiredBytes)));

        if (!AssetRules.IsAllowedContentType(asset.Purpose, contentType))
        {
            await FailAsync(
                asset,
                "This file's contents do not match any accepted format, whatever the file is named.",
                cancellationToken);
            return;
        }

        if (!await ScanAsync(asset, content, cancellationToken))
        {
            return;
        }

        int? width = null;
        int? height = null;

        if (AssetRules.IsImage(contentType))
        {
            if (_images.Render(content, AssetRules.VariantSpecs) is not ImageRenderOutcome.Rendered rendered)
            {
                await FailAsync(asset, "This image could not be read. Try saving it again as a JPG or PNG.", cancellationToken);
                return;
            }

            var original = await StoreRenditionsAsync(asset, rendered.Renditions, cancellationToken);
            width = original.Width;
            height = original.Height;
        }
        else
        {
            var key = AssetRules.VerbatimKey(asset.AgencyId, asset.Id, contentType!);

            using var copy = new MemoryStream(content, writable: false);
            var stored = await _storage.StoreAsync(copy, key, contentType!, cancellationToken);

            asset.ReplaceOriginal(key, contentType!, stored.SizeBytes, stored.Checksum);
        }

        asset.MarkReady(width, height, _clock.GetUtcNow());
        await _db.SaveChangesAsync(cancellationToken);

        // After the save: if it had failed, the retry would need the raw upload to read again.
        if (!string.Equals(uploadKey, asset.StorageKey, StringComparison.Ordinal))
        {
            await _storage.DeleteAsync(uploadKey, cancellationToken);
        }

        LogReady(_logger, asset.Id, asset.AgencyId);
    }

    public async Task<int> SweepAsync(CancellationToken cancellationToken = default)
    {
        var now = _clock.GetUtcNow();

        // The grace beyond the window is for an upload that started inside it and is still
        // arriving: storage checks the signature when the request starts, not when it ends.
        var abandonedBefore = now - AssetRules.StalledAfter;
        var stalledBefore = now - AssetRules.StalledAfter;

        List<Asset> abandoned;
        List<Guid> stalled;

        using (_platformScope.Enter("asset pipeline — expiring abandoned uploads and finding lost jobs, across agencies"))
        {
            abandoned = await _db.Assets
                .Where(a => a.Status == AssetStatus.AwaitingUpload && a.UploadExpiresAt < abandonedBefore)
                .OrderBy(a => a.UploadExpiresAt)
                .Take(SweepBatchSize)
                .ToListAsync(cancellationToken);

            foreach (var asset in abandoned)
            {
                asset.MarkFailed("The upload window closed before the file arrived. Start the upload again.");

                // Anything that did land is litter now, and litter nobody will ever scan.
                await _storage.DeleteAsync(asset.StorageKey, cancellationToken);
            }

            await _db.SaveChangesAsync(cancellationToken);

            // Uploaded but never picked up — the enqueue was lost — or claimed by a run whose lease
            // ran out, which is what a Worker that died mid-run leaves behind.
            stalled = await _db.Assets
                .Where(a => a.UpdatedAt < stalledBefore
                            && (a.Status == AssetStatus.Uploaded
                                || (a.Status == AssetStatus.Processing
                                    && (a.ProcessingClaimedUntil == null || a.ProcessingClaimedUntil < now))))
                .OrderBy(a => a.UpdatedAt)
                .Select(a => a.Id)
                .Take(SweepBatchSize)
                .ToListAsync(cancellationToken);
        }

        foreach (var assetId in stalled)
        {
            await _dispatcher.EnqueueAsync(assetId, cancellationToken);
        }

        return abandoned.Count + stalled.Count;
    }

    /// <summary>
    /// Scans the bytes and records the answer. False when the pipeline must stop here.
    /// </summary>
    private async Task<bool> ScanAsync(Asset asset, byte[] content, CancellationToken cancellationToken)
    {
        var verdict = await _scanner.ScanAsync(content, cancellationToken);
        var now = _clock.GetUtcNow();

        switch (verdict)
        {
            case VirusScanResult.Clean:
                asset.RecordCleanScan(now);
                return true;

            case VirusScanResult.Infected infected:
                asset.Quarantine(infected.Signature, now);

                // The row stays as the record; the bytes go. An infected file nobody can reach is
                // still an infected file sitting in our bucket.
                await _storage.DeleteAsync(asset.StorageKey, cancellationToken);
                await _db.SaveChangesAsync(cancellationToken);

                LogQuarantined(_logger, asset.Id, asset.AgencyId, infected.Signature ?? "unnamed", _scanner.Name);
                return false;

            case VirusScanResult.Unavailable unavailable:
                asset.RecordUnscannable(now);
                await _db.SaveChangesAsync(cancellationToken);

                // Thrown, not returned: the job runner retries a failure, and the sweep picks up
                // whatever the retries leave. Fails closed in the meantime — the asset stays unservable.
                throw new InvalidOperationException(
                    $"The virus scanner '{_scanner.Name}' could not scan asset {asset.Id}: {unavailable.Reason}. "
                    + "It will not be served until a scan succeeds; the job will be retried.");

            default:
                throw new InvalidOperationException($"Unknown scan result {verdict.GetType().Name}.");
        }
    }

    /// <summary>
    /// Writes every rendition and points the asset at the metadata-free original.
    /// </summary>
    /// <remarks>
    /// The keys are fixed per asset and kind, so a re-run overwrites the same objects. The rows are
    /// replaced rather than duplicated — removed and saved first, because a unique index guards
    /// one row per kind.
    /// </remarks>
    private async Task<RenderedImage> StoreRenditionsAsync(
        Asset asset,
        IReadOnlyList<RenderedImage> renditions,
        CancellationToken cancellationToken)
    {
        var stale = await _db.AssetVariants.Where(v => v.AssetId == asset.Id).ToListAsync(cancellationToken);

        if (stale.Count > 0)
        {
            _db.AssetVariants.RemoveRange(stale);
            await _db.SaveChangesAsync(cancellationToken);
        }

        var original = renditions.Single(r => r.Kind == AssetVariantKind.Original);

        foreach (var rendition in renditions)
        {
            var key = AssetRules.VariantKey(asset.AgencyId, asset.Id, rendition.Kind);

            using var stream = new MemoryStream(rendition.Content, writable: false);
            var stored = await _storage.StoreAsync(stream, key, AssetRules.VariantContentType, cancellationToken);

            _db.AssetVariants.Add(AssetVariant.Create(
                asset.AgencyId,
                asset.Id,
                rendition.Kind,
                key,
                AssetRules.VariantContentType,
                rendition.Width,
                rendition.Height,
                stored.SizeBytes));

            if (rendition.Kind == AssetVariantKind.Original)
            {
                asset.ReplaceOriginal(key, AssetRules.VariantContentType, stored.SizeBytes, stored.Checksum);
            }
        }

        return original;
    }

    /// <summary>
    /// Reads the whole upload, stopping the moment it passes the purpose's limit.
    /// </summary>
    /// <remarks>
    /// Bounded while reading, not measured first: the size the complete step saw is not
    /// necessarily the size of what is there now.
    /// </remarks>
    private async Task<(byte[]? Content, string? Problem)> ReadWithinLimitAsync(Asset asset, CancellationToken cancellationToken)
    {
        if (!await _storage.ExistsAsync(asset.StorageKey, cancellationToken))
        {
            return (null, "The uploaded file is no longer in storage. Upload it again.");
        }

        var limit = AssetRules.MaxSizeBytes(asset.Purpose);

        await using var source = await _storage.OpenReadAsync(asset.StorageKey, cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[81_920];
        int read;

        while ((read = await source.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > limit)
            {
                return (null, $"That file is larger than {AssetRules.SizeDescription(asset.Purpose)}.");
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.Length == 0
            ? (null, "That file is empty.")
            : (buffer.ToArray(), null);
    }

    private async Task FailAsync(Asset asset, string reason, CancellationToken cancellationToken)
    {
        asset.MarkFailed(reason);
        await _storage.DeleteAsync(asset.StorageKey, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);

        LogFailed(_logger, asset.Id, asset.AgencyId, reason);
    }

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "Asset {AssetId} has nothing to process, or another run has it.")]
    private static partial void LogNothingToDo(ILogger logger, Guid assetId);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Asset {AssetId} for agency {AgencyId} is scanned, processed and servable.")]
    private static partial void LogReady(ILogger logger, Guid assetId, Guid agencyId);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Asset {AssetId} for agency {AgencyId} failed processing: {Reason}")]
    private static partial void LogFailed(ILogger logger, Guid assetId, Guid agencyId, string reason);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Asset {AssetId} for agency {AgencyId} was quarantined: {Signature}, found by {Scanner}.")]
    private static partial void LogQuarantined(ILogger logger, Guid assetId, Guid agencyId, string signature, string scanner);
}
