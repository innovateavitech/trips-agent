using Microsoft.EntityFrameworkCore;
using TripsAgent.Application.Persistence;
using TripsAgent.Application.Storage;
using TripsAgent.Domain.Assets;

namespace TripsAgent.Application.Assets;

/// <summary>A link to one rendition of a servable asset.</summary>
/// <param name="Kind">Which rendition.</param>
/// <param name="Url">Signed and time-limited; it is the credential, so it is never logged.</param>
/// <param name="ContentType">What the link returns.</param>
/// <param name="Width">Pixel width, for an image.</param>
/// <param name="Height">Pixel height, for an image.</param>
/// <param name="ExpiresAt">When the link stops working.</param>
public sealed record AssetLink(
    AssetVariantKind Kind,
    string Url,
    string ContentType,
    int? Width,
    int? Height,
    DateTimeOffset ExpiresAt);

/// <summary>
/// The one place a link to an asset's bytes is created — and so the one place that decides
/// whether an asset may be seen at all.
/// </summary>
/// <remarks>
/// <para>
/// Issue #18: nothing unscanned is served. Storage will sign a link to any key it is asked about,
/// so the rule cannot live there; it lives here, and every path that shows an asset to anyone —
/// the console today, the storefront and generated documents later — has to come through this
/// class to get a URL.
/// </para>
/// <para>
/// The database holds the same line independently: a CHECK constraint refuses a row that is
/// Ready without being Clean, so a hand-written UPDATE cannot make an unscanned file look servable
/// either.
/// </para>
/// </remarks>
public sealed class AssetDelivery
{
    private readonly IBlobStorage _storage;
    private readonly TimeProvider _clock;

    public AssetDelivery(IBlobStorage storage, TimeProvider clock)
    {
        _storage = storage;
        _clock = clock;
    }

    /// <summary>
    /// Links to every rendition of <paramref name="asset"/>, or none at all unless it has been
    /// scanned clean and fully processed.
    /// </summary>
    public async Task<IReadOnlyList<AssetLink>> LinksForAsync(
        Asset asset,
        IReadOnlyCollection<AssetVariant> variants,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentNullException.ThrowIfNull(variants);

        if (!asset.IsServable)
        {
            return [];
        }

        var expiresAt = _clock.GetUtcNow().Add(AssetRules.DownloadWindow);
        var links = new List<AssetLink>();

        // An image is served as its renditions. Anything else is served as the verbatim copy the
        // pipeline scanned, which is where the asset points once it is Ready.
        if (!asset.IsImage)
        {
            var signed = await _storage.CreateDownloadUrlAsync(asset.StorageKey, asset.ContentType!, expiresAt, cancellationToken);
            links.Add(new AssetLink(AssetVariantKind.Original, signed.Url, asset.ContentType!, null, null, signed.ExpiresAt));
            return links;
        }

        foreach (var variant in variants.Where(v => v.AssetId == asset.Id).OrderBy(v => v.Kind))
        {
            var signed = await _storage.CreateDownloadUrlAsync(variant.StorageKey, variant.ContentType, expiresAt, cancellationToken);
            links.Add(new AssetLink(variant.Kind, signed.Url, variant.ContentType, variant.Width, variant.Height, signed.ExpiresAt));
        }

        return links;
    }
}

/// <summary>An asset, and the links to it that its state allows.</summary>
public sealed record AssetView(Asset Asset, IReadOnlyList<AssetLink> Links);

/// <summary>Reads one of the caller's own assets.</summary>
public sealed class GetAssetHandler
{
    private readonly IAppDbContext _db;
    private readonly AssetDelivery _delivery;

    public GetAssetHandler(IAppDbContext db, AssetDelivery delivery)
    {
        _db = db;
        _delivery = delivery;
    }

    /// <summary>Null when the asset does not exist or belongs to another agency.</summary>
    public async Task<AssetView?> HandleAsync(Guid assetId, CancellationToken cancellationToken = default)
    {
        var asset = await _db.Assets.AsNoTracking().FirstOrDefaultAsync(a => a.Id == assetId, cancellationToken);

        if (asset is null)
        {
            return null;
        }

        var variants = await _db.AssetVariants.AsNoTracking()
            .Where(v => v.AssetId == assetId)
            .ToListAsync(cancellationToken);

        return new AssetView(asset, await _delivery.LinksForAsync(asset, variants, cancellationToken));
    }
}
