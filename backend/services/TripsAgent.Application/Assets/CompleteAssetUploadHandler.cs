using Microsoft.EntityFrameworkCore;
using TripsAgent.Application.Persistence;
using TripsAgent.Application.Storage;
using TripsAgent.Domain.Assets;
using TripsAgent.Domain.Common;

namespace TripsAgent.Application.Assets;

/// <summary>What happened when the browser said its upload had finished.</summary>
public abstract record CompleteAssetUploadOutcome
{
    private CompleteAssetUploadOutcome()
    {
    }

    /// <summary>The file checks out and is queued for scanning. Not yet servable.</summary>
    public sealed record Accepted(Asset Asset) : CompleteAssetUploadOutcome;

    /// <summary>No such asset — or it belongs to another agency, which looks the same on purpose.</summary>
    public sealed record NotFound : CompleteAssetUploadOutcome;

    /// <summary>Nothing is in storage yet. The client can finish sending and ask again.</summary>
    public sealed record NotArrived : CompleteAssetUploadOutcome;

    /// <summary>The file was refused, and the asset will never be served.</summary>
    public sealed record Rejected(Asset Asset, string Reason) : CompleteAssetUploadOutcome;
}

/// <summary>
/// Step two of an upload: check what actually landed in storage, and queue it for the pipeline.
/// </summary>
/// <remarks>
/// <para>
/// Everything is re-established from storage, because none of what the client said can be
/// trusted. The size comes from the object store, not the declared length. The type comes from
/// <b>sniffing the file's first bytes</b>, never from the filename or the <c>Content-Type</c>
/// header — renaming <c>payload.exe</c> to <c>beach.jpg</c> changes both and nothing else.
/// </para>
/// <para>
/// Only the first few bytes are read. Hashing and scanning mean reading all of them, and that is
/// the Worker's job; the API staying out of the byte path is the point of a direct upload.
/// </para>
/// </remarks>
public sealed class CompleteAssetUploadHandler
{
    private readonly IAppDbContext _db;
    private readonly IBlobStorage _storage;
    private readonly IAssetPipelineDispatcher _pipeline;
    private readonly TimeProvider _clock;

    public CompleteAssetUploadHandler(
        IAppDbContext db,
        IBlobStorage storage,
        IAssetPipelineDispatcher pipeline,
        TimeProvider clock)
    {
        _db = db;
        _storage = storage;
        _pipeline = pipeline;
        _clock = clock;
    }

    public async Task<CompleteAssetUploadOutcome> HandleAsync(Guid assetId, CancellationToken cancellationToken = default)
    {
        // The tenant filter does the ownership check: another agency's asset is simply not found.
        var asset = await _db.Assets.FirstOrDefaultAsync(a => a.Id == assetId, cancellationToken);

        if (asset is null)
        {
            return new CompleteAssetUploadOutcome.NotFound();
        }

        // A retried "complete" — a double-click, a flaky connection — answers with where the asset
        // already is rather than re-checking bytes the pipeline may already have moved.
        if (asset.Status != AssetStatus.AwaitingUpload)
        {
            return asset.Status is AssetStatus.Failed or AssetStatus.Quarantined
                ? new CompleteAssetUploadOutcome.Rejected(asset, asset.FailureReason ?? "This file cannot be used.")
                : new CompleteAssetUploadOutcome.Accepted(asset);
        }

        var sizeBytes = await _storage.GetSizeAsync(asset.StorageKey, cancellationToken);

        if (sizeBytes is null)
        {
            // The window only matters while nothing has arrived. A slow upload that started inside
            // it and finished outside it is still a good upload.
            if (_clock.GetUtcNow() <= asset.UploadExpiresAt)
            {
                return new CompleteAssetUploadOutcome.NotArrived();
            }

            return await RejectAsync(asset, "The upload window closed before the file arrived. Start the upload again.", cancellationToken);
        }

        if (!AssetRules.IsAllowedSize(asset.Purpose, sizeBytes.Value))
        {
            return await RejectAsync(
                asset,
                sizeBytes.Value <= 0 ? "That file is empty." : $"That file is larger than {AssetRules.SizeDescription(asset.Purpose)}.",
                cancellationToken);
        }

        var contentType = await SniffAsync(asset.StorageKey, cancellationToken);

        if (!AssetRules.IsAllowedContentType(asset.Purpose, contentType))
        {
            return await RejectAsync(
                asset,
                $"That file is not a {string.Join(", ", AssetRules.AllowedExtensions(asset.Purpose))}. "
                + "Its contents do not match any accepted format, whatever the file is named.",
                cancellationToken);
        }

        asset.RecordUpload(contentType!, sizeBytes.Value);
        await _db.SaveChangesAsync(cancellationToken);

        // After the save, so the Worker never picks up a row still saying AwaitingUpload. If this
        // process dies in between, the sweep finds the asset and enqueues it.
        await _pipeline.EnqueueAsync(asset.Id, cancellationToken);

        return new CompleteAssetUploadOutcome.Accepted(asset);
    }

    private async Task<string?> SniffAsync(string key, CancellationToken cancellationToken)
    {
        await using var content = await _storage.OpenReadAsync(key, cancellationToken);

        var header = new byte[FileSignature.RequiredBytes];
        var read = await content.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false, cancellationToken);

        return FileSignature.Detect(header.AsSpan(0, read));
    }

    /// <summary>
    /// Refuses the file for good. The bytes are deleted: a file we will never serve has no reason
    /// to sit in the bucket, and every reason not to if it was refused for being something else.
    /// </summary>
    private async Task<CompleteAssetUploadOutcome> RejectAsync(Asset asset, string reason, CancellationToken cancellationToken)
    {
        asset.MarkFailed(reason);
        await _storage.DeleteAsync(asset.StorageKey, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);

        return new CompleteAssetUploadOutcome.Rejected(asset, reason);
    }
}
