using TripsAgent.Domain.Assets;

namespace TripsAgent.Application.Assets;

/// <summary>What a virus scanner said about one file.</summary>
public abstract record VirusScanResult
{
    private VirusScanResult()
    {
    }

    public sealed record Clean : VirusScanResult;

    /// <param name="Signature">What the scanner called it, for the incident record.</param>
    public sealed record Infected(string? Signature) : VirusScanResult;

    /// <summary>
    /// The scanner could not answer. Never treated as clean — see
    /// <see cref="AssetScanStatus.Unscannable"/>.
    /// </summary>
    public sealed record Unavailable(string Reason) : VirusScanResult;
}

/// <summary>
/// Checks a file for malware. A port: ClamAV sits behind it today (<c>ClamAvVirusScanner</c>), and
/// another engine would be another implementation, not a change to the pipeline.
/// </summary>
/// <remarks>
/// Implementations must answer <see cref="VirusScanResult.Unavailable"/> rather than throw or guess
/// when they cannot reach whatever does the scanning. The pipeline fails closed on that answer and
/// retries; a scanner that returned Clean because it was down would switch the whole control off.
/// </remarks>
public interface IVirusScanner
{
    /// <summary>A name for logs and for the startup check, e.g. "clamav".</summary>
    public string Name { get; }

    public Task<VirusScanResult> ScanAsync(ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default);
}

/// <summary>One rendition the image processor produced.</summary>
/// <param name="Kind">Which rendition.</param>
/// <param name="Content">The encoded bytes, in <see cref="AssetRules.VariantContentType"/>.</param>
/// <param name="Width">Pixel width, after orientation is applied.</param>
/// <param name="Height">Pixel height, after orientation is applied.</param>
public sealed record RenderedImage(AssetVariantKind Kind, byte[] Content, int Width, int Height);

/// <summary>What happened when an image was rendered.</summary>
public abstract record ImageRenderOutcome
{
    private ImageRenderOutcome()
    {
    }

    /// <summary>
    /// Every rendition that applies, <see cref="AssetVariantKind.Original"/> always among them.
    /// </summary>
    public sealed record Rendered(IReadOnlyList<RenderedImage> Renditions) : ImageRenderOutcome;

    /// <summary>The bytes are not an image we can decode, or decoding them would cost too much.</summary>
    public sealed record Unreadable(string Reason) : ImageRenderOutcome;
}

/// <summary>
/// Decodes an image and re-encodes it as metadata-free WebP renditions.
/// </summary>
/// <remarks>
/// A port so Application does not carry an imaging library, and so the library can be swapped
/// without touching the pipeline's rules. The implementation must apply EXIF orientation before
/// dropping the metadata — otherwise every portrait phone photo comes out on its side.
/// </remarks>
public interface IImageProcessor
{
    public ImageRenderOutcome Render(ReadOnlyMemory<byte> source, IReadOnlyList<AssetVariantSpec> specs);
}

/// <summary>Hands an uploaded asset to the background pipeline.</summary>
/// <remarks>
/// The API enqueues and the Worker executes, the same split as <c>IWebhookDispatcher</c>. A lost
/// enqueue is not a lost asset: <see cref="IAssetProcessor.SweepAsync"/> finds it.
/// </remarks>
public interface IAssetPipelineDispatcher
{
    public Task EnqueueAsync(Guid assetId, CancellationToken cancellationToken = default);
}

/// <summary>The background pipeline, as the job runner sees it.</summary>
public interface IAssetProcessor
{
    /// <summary>Scans, strips and renders one asset. Safe to run more than once.</summary>
    public Task ProcessAsync(Guid assetId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Expires uploads that never arrived and re-enqueues processing that was lost. Returns how
    /// many assets it touched.
    /// </summary>
    public Task<int> SweepAsync(CancellationToken cancellationToken = default);
}
