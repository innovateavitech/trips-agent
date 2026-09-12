using TripsAgent.Application.Persistence;
using TripsAgent.Application.Storage;
using TripsAgent.Application.Tenancy;
using TripsAgent.Domain.Assets;

namespace TripsAgent.Application.Assets;

/// <summary>What happened when an upload slot was asked for.</summary>
public abstract record RequestAssetUploadOutcome
{
    private RequestAssetUploadOutcome()
    {
    }

    /// <summary>A row is reserved and the browser has somewhere to send the bytes.</summary>
    public sealed record Reserved(Asset Asset, PresignedUpload Upload) : RequestAssetUploadOutcome;

    /// <summary>Refused before anything was reserved, with a message for the person choosing the file.</summary>
    public sealed record Rejected(string Reason) : RequestAssetUploadOutcome;
}

/// <summary>
/// Step one of an upload: reserve a row and hand the browser a signed URL to put the file at.
/// </summary>
/// <remarks>
/// <para>
/// The bytes never pass through the API. The browser sends them straight to storage, which is
/// what lets a slow 10MB upload from a phone occupy an object store's connection rather than one
/// of ours.
/// </para>
/// <para>
/// What the client declares here — the size, the type — is checked only so a hopeless upload is
/// refused before it starts. None of it is trusted: <see cref="CompleteAssetUploadHandler"/>
/// measures and sniffs what actually arrived.
/// </para>
/// </remarks>
public sealed class RequestAssetUploadHandler
{
    private readonly IAppDbContext _db;
    private readonly IBlobStorage _storage;
    private readonly ITenantContext _tenant;
    private readonly TimeProvider _clock;

    public RequestAssetUploadHandler(
        IAppDbContext db,
        IBlobStorage storage,
        ITenantContext tenant,
        TimeProvider clock)
    {
        _db = db;
        _storage = storage;
        _tenant = tenant;
        _clock = clock;
    }

    public async Task<RequestAssetUploadOutcome> HandleAsync(
        AssetPurpose purpose,
        string? fileName,
        long declaredSizeBytes,
        string? declaredContentType,
        CancellationToken cancellationToken = default)
    {
        if (_tenant.AgencyId is not { } agencyId)
        {
            throw new InvalidOperationException("An upload needs a resolved tenant; this endpoint requires authentication.");
        }

        // IsUploadable rather than IsDefined: a generated document is a real purpose, but one only
        // the platform's own renderer may create — it is the purpose that skips the virus scan.
        if (!AssetRules.IsUploadable(purpose))
        {
            return new RequestAssetUploadOutcome.Rejected("Say what the file is for.");
        }

        if (!AssetRules.IsAllowedSize(purpose, declaredSizeBytes))
        {
            return new RequestAssetUploadOutcome.Rejected(
                declaredSizeBytes <= 0
                    ? "That file is empty."
                    : $"That file is larger than {AssetRules.SizeDescription(purpose)}.");
        }

        // The allowlist's own spelling, not the client's: an S3 signature covers the Content-Type
        // header byte for byte, so "IMAGE/JPEG" signed and "image/jpeg" sent would be refused.
        var contentType = AssetRules.AllowedContentTypes(purpose)
            .FirstOrDefault(allowed => string.Equals(allowed, declaredContentType, StringComparison.OrdinalIgnoreCase));

        if (contentType is null)
        {
            return new RequestAssetUploadOutcome.Rejected(
                $"Upload a {string.Join(", ", AssetRules.AllowedExtensions(purpose))} file.");
        }

        var expiresAt = _clock.GetUtcNow().Add(AssetRules.UploadWindow);
        var asset = Asset.Reserve(agencyId, purpose, fileName ?? string.Empty, expiresAt);

        // Capped at the purpose's limit rather than the declared size, so a client that lied low
        // is still stopped by storage where the provider can enforce it — and by the complete step
        // where it cannot.
        var upload = await _storage.CreateUploadUrlAsync(
            asset.StorageKey,
            contentType,
            AssetRules.MaxSizeBytes(purpose),
            expiresAt,
            cancellationToken);

        _db.Assets.Add(asset);
        await _db.SaveChangesAsync(cancellationToken);

        return new RequestAssetUploadOutcome.Reserved(asset, upload);
    }
}
