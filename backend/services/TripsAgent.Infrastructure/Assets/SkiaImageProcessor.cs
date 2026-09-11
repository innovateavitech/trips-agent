using SkiaSharp;
using TripsAgent.Application.Assets;
using TripsAgent.Domain.Assets;

namespace TripsAgent.Infrastructure.Assets;

/// <summary>
/// Renders an uploaded image into metadata-free WebP renditions, with SkiaSharp.
/// </summary>
/// <remarks>
/// <para>
/// SkiaSharp is MIT-licensed, over Google's BSD-licensed Skia. SixLabors.ImageSharp was the other
/// obvious candidate and was ruled out on licence: its Six Labors Split License requires a paid
/// commercial licence above a revenue threshold, which a SaaS we charge for would cross.
/// </para>
/// <para>
/// EXIF stripping is not a separate step here, and that is deliberate. Metadata lives in the file
/// container, not in the pixels, so decoding to a bitmap and encoding a new file from it leaves
/// every EXIF, XMP and IPTC block behind — GPS coordinates included. Deleting segments from the
/// original file instead would leave anything a hand-rolled stripper did not know about.
/// </para>
/// <para>
/// The one piece of metadata that must survive is orientation: a phone stores a portrait photo
/// sideways and says so in EXIF. It is applied to the pixels before they are re-encoded, so the
/// rendition is upright with nothing left to say which way up it is.
/// </para>
/// </remarks>
public sealed class SkiaImageProcessor : IImageProcessor
{
    public ImageRenderOutcome Render(ReadOnlyMemory<byte> source, IReadOnlyList<AssetVariantSpec> specs)
    {
        ArgumentNullException.ThrowIfNull(specs);

        using var data = SKData.CreateCopy(source.Span);
        using var codec = SKCodec.Create(data);

        if (codec is null)
        {
            return new ImageRenderOutcome.Unreadable("The file is not an image we can decode.");
        }

        // Checked from the header, before a single pixel is allocated. See AssetRules.MaxPixels.
        if ((long)codec.Info.Width * codec.Info.Height > AssetRules.MaxPixels)
        {
            return new ImageRenderOutcome.Unreadable(
                $"The image is {codec.Info.Width} × {codec.Info.Height} pixels, which is more than we process.");
        }

        using var decoded = Decode(codec);

        if (decoded is null)
        {
            return new ImageRenderOutcome.Unreadable("The image is damaged or incomplete.");
        }

        using var upright = ApplyOrientation(decoded, codec.EncodedOrigin);

        var renditions = new List<RenderedImage>();

        foreach (var spec in specs)
        {
            var size = TargetSize(upright.Width, upright.Height, spec);

            if (size is not { } target)
            {
                continue;
            }

            var encoded = Encode(upright, target.Width, target.Height);

            if (encoded is null)
            {
                return new ImageRenderOutcome.Unreadable("The image could not be re-encoded.");
            }

            renditions.Add(new RenderedImage(spec.Kind, encoded, target.Width, target.Height));
        }

        return new ImageRenderOutcome.Rendered(renditions);
    }

    /// <summary>
    /// Decodes to premultiplied sRGB, or null when the file does not decode completely.
    /// </summary>
    /// <remarks>
    /// Converting to sRGB while decoding keeps colours right once the embedded ICC profile is gone
    /// with the rest of the metadata — a Display P3 phone photo would otherwise come out dull.
    /// A truncated file is refused rather than served with a grey band across the bottom.
    /// </remarks>
    private static SKBitmap? Decode(SKCodec codec)
    {
        var info = new SKImageInfo(
            codec.Info.Width,
            codec.Info.Height,
            SKImageInfo.PlatformColorType,
            SKAlphaType.Premul,
            SKColorSpace.CreateSrgb());

        var bitmap = new SKBitmap(info);

        if (codec.GetPixels(info, bitmap.GetPixels()) != SKCodecResult.Success)
        {
            bitmap.Dispose();
            return null;
        }

        return bitmap;
    }

    /// <summary>
    /// Returns a new bitmap with the EXIF orientation applied to the pixels themselves.
    /// </summary>
    /// <remarks>
    /// The eight EXIF orientations are the four rotations, each optionally mirrored. The canvas
    /// transforms below map each one back to upright; the last transform written is the first
    /// applied to a point, which is why they read backwards.
    /// </remarks>
    internal static SKBitmap ApplyOrientation(SKBitmap source, SKEncodedOrigin origin)
    {
        var swapsAxes = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop
            or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;

        var width = swapsAxes ? source.Height : source.Width;
        var height = swapsAxes ? source.Width : source.Height;

        var result = new SKBitmap(new SKImageInfo(width, height, source.ColorType, source.AlphaType, source.ColorSpace));

        using var canvas = new SKCanvas(result);

        switch (origin)
        {
            case SKEncodedOrigin.TopRight:
                canvas.Translate(width, 0);
                canvas.Scale(-1, 1);
                break;

            case SKEncodedOrigin.BottomRight:
                canvas.Translate(width, height);
                canvas.RotateDegrees(180);
                break;

            case SKEncodedOrigin.BottomLeft:
                canvas.Translate(0, height);
                canvas.Scale(1, -1);
                break;

            case SKEncodedOrigin.LeftTop:
                canvas.RotateDegrees(90);
                canvas.Scale(1, -1);
                break;

            case SKEncodedOrigin.RightTop:
                canvas.Translate(width, 0);
                canvas.RotateDegrees(90);
                break;

            case SKEncodedOrigin.RightBottom:
                canvas.Translate(width, height);
                canvas.RotateDegrees(270);
                canvas.Scale(1, -1);
                break;

            case SKEncodedOrigin.LeftBottom:
                canvas.Translate(0, height);
                canvas.RotateDegrees(270);
                break;

            default:
                break;
        }

        canvas.DrawBitmap(source, 0, 0);
        canvas.Flush();

        return result;
    }

    /// <summary>
    /// The size one rendition should be, or null to skip it. Never upscales: that invents detail
    /// and costs bytes.
    /// </summary>
    internal static (int Width, int Height)? TargetSize(int width, int height, AssetVariantSpec spec)
    {
        if (spec.MaxEdge <= 0)
        {
            return (width, height);
        }

        var longEdge = Math.Max(width, height);

        if (longEdge <= spec.MaxEdge)
        {
            return null;
        }

        var scale = (double)spec.MaxEdge / longEdge;

        return (Math.Max(1, (int)Math.Round(width * scale)), Math.Max(1, (int)Math.Round(height * scale)));
    }

    private static byte[]? Encode(SKBitmap upright, int width, int height)
    {
        if (width == upright.Width && height == upright.Height)
        {
            return EncodeWebp(upright);
        }

        // Mitchell cubic: sharp enough for text on a tour poster, without the ringing Lanczos puts
        // around a logo's hard edges.
        using var resized = upright.Resize(
            new SKImageInfo(width, height, upright.ColorType, upright.AlphaType, upright.ColorSpace),
            new SKSamplingOptions(SKCubicResampler.Mitchell));

        return resized is null ? null : EncodeWebp(resized);
    }

    private static byte[]? EncodeWebp(SKBitmap bitmap)
    {
        using var encoded = bitmap.Encode(SKEncodedImageFormat.Webp, AssetRules.VariantQuality);

        return encoded?.ToArray();
    }
}
