using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SkiaSharp;
using TripsAgent.Application.Assets;
using TripsAgent.Application.Storage;
using TripsAgent.Domain.Assets;
using TripsAgent.Infrastructure.Persistence;

namespace TripsAgent.Infrastructure.Assets;

/// <summary>
/// Reads an agency's logo from the asset store and re-encodes it as PNG, for its documents and its
/// travellers' email.
/// </summary>
/// <remarks>
/// <para>
/// Only a servable logo: one the pipeline has scanned clean and processed. The Medium rendition is
/// read when there is one — at most 1,024 pixels on its longest edge, plenty for a 40-pixel header
/// on any screen or printer — then the metadata-free original, never the raw upload.
/// </para>
/// <para>
/// Reads through the caller's own scope: the notification dispatcher's platform scope, or the
/// document worker's single agency. Nothing here widens it.
/// </para>
/// </remarks>
public sealed partial class AgencyLogoSource(
    AppDbContext db,
    IBlobStorage storage,
    ILogger<AgencyLogoSource> logger) : IAgencyLogoSource
{
    /// <summary>The most a logo rendition is read into memory for. Far above what the pipeline produces.</summary>
    private const long MaxBytes = 4L * 1024 * 1024;

    public async Task<AgencyLogo?> LoadAsync(Guid agencyId, CancellationToken cancellationToken = default)
    {
        try
        {
            var logoAssetId = await db.AgencyBranding
                .AsNoTracking()
                .Where(branding => branding.AgencyId == agencyId)
                .Select(branding => branding.LogoAssetId)
                .FirstOrDefaultAsync(cancellationToken);

            if (logoAssetId is not { } assetId)
            {
                return null;
            }

            var asset = await db.Assets
                .AsNoTracking()
                .FirstOrDefaultAsync(a => a.Id == assetId && a.AgencyId == agencyId, cancellationToken);

            if (asset is null || !asset.IsServable || !asset.IsImage)
            {
                return null;
            }

            var renditions = await db.AssetVariants
                .AsNoTracking()
                .Where(variant => variant.AssetId == assetId)
                .Select(variant => new { variant.Kind, variant.StorageKey })
                .ToListAsync(cancellationToken);

            var key = renditions.FirstOrDefault(r => r.Kind == AssetVariantKind.Medium)?.StorageKey
                      ?? renditions.FirstOrDefault(r => r.Kind == AssetVariantKind.Original)?.StorageKey
                      ?? asset.StorageKey;

            var bytes = await ReadAsync(key, cancellationToken);

            if (bytes is null)
            {
                LogUnreadable(logger, agencyId, "it is missing from storage, or too large");
                return null;
            }

            using var bitmap = SKBitmap.Decode(bytes);

            if (bitmap is null)
            {
                LogUnreadable(logger, agencyId, "it could not be decoded");
                return null;
            }

            using var image = SKImage.FromBitmap(bitmap);
            using var png = image.Encode(SKEncodedImageFormat.Png, 100);

            return new AgencyLogo(png.ToArray(), bitmap.Width, bitmap.Height);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A logo is never worth failing a voucher or an email over. The name prints instead.
            LogFailed(logger, ex, agencyId);
            return null;
        }
    }

    private async Task<byte[]?> ReadAsync(string key, CancellationToken cancellationToken)
    {
        if (!await storage.ExistsAsync(key, cancellationToken))
        {
            return null;
        }

        await using var source = await storage.OpenReadAsync(key, cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[81_920];
        int read;

        while ((read = await source.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > MaxBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.Length == 0 ? null : buffer.ToArray();
    }

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Agency {AgencyId}'s logo was left off because {Reason}. Its name is shown instead.")]
    private static partial void LogUnreadable(ILogger logger, Guid agencyId, string reason);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Agency {AgencyId}'s logo could not be loaded. Its name is shown instead.")]
    private static partial void LogFailed(ILogger logger, Exception exception, Guid agencyId);
}
