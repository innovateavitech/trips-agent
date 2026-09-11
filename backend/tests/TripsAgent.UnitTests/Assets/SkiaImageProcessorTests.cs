using System.Buffers.Binary;
using System.Text;
using FluentAssertions;
using SkiaSharp;
using TripsAgent.Application.Assets;
using TripsAgent.Domain.Assets;
using TripsAgent.Domain.Common;
using TripsAgent.Infrastructure.Assets;

namespace TripsAgent.UnitTests.Assets;

/// <summary>
/// The image half of the pipeline, against real encoded files: resize, WebP, orientation, and the
/// metadata that must not survive.
/// </summary>
public class SkiaImageProcessorTests
{
    private const string HiddenLocation = "GPS-6.5244N-3.3792E-SECRET";

    private readonly SkiaImageProcessor _processor = new();

    [Fact]
    public void Every_rendition_is_webp()
    {
        var rendered = Render(TestImages.Jpeg(400, 200));

        rendered.Should().NotBeEmpty();
        rendered.Should().OnlyContain(r => FileSignature.Detect(r.Content) == MediaTypes.Webp);
    }

    [Fact]
    public void A_small_image_is_never_upscaled()
    {
        var rendered = Render(TestImages.Jpeg(400, 200));

        // 400px is under the Medium and Large bounds, so only the original and a thumbnail exist.
        rendered.Select(r => (r.Kind, r.Width, r.Height)).Should().Equal(
            (AssetVariantKind.Original, 400, 200),
            (AssetVariantKind.Thumbnail, 320, 160));
    }

    [Fact]
    public void A_large_image_gets_every_rendition_bounded_by_its_longest_edge()
    {
        var rendered = Render(TestImages.Jpeg(2400, 1200));

        rendered.Select(r => (r.Kind, r.Width, r.Height)).Should().Equal(
            (AssetVariantKind.Original, 2400, 1200),
            (AssetVariantKind.Large, 2048, 1024),
            (AssetVariantKind.Medium, 1024, 512),
            (AssetVariantKind.Thumbnail, 320, 160));
    }

    [Fact]
    public void The_exif_orientation_is_applied_to_the_pixels_before_it_is_dropped()
    {
        // Orientation 6: stored sideways, to be shown rotated 90° clockwise — a portrait phone photo.
        // The left half of the stored image is red, so upright, the top half must be.
        var original = Render(TestImages.Jpeg(400, 200, orientation: 6)).Single(r => r.Kind == AssetVariantKind.Original);

        (original.Width, original.Height).Should().Be((200, 400));

        using var upright = SKBitmap.Decode(original.Content);
        upright.GetPixel(100, 50).Red.Should().BeGreaterThan(200, "the stored left edge is now the top");
        upright.GetPixel(100, 350).Blue.Should().BeGreaterThan(200, "the stored right edge is now the bottom");
    }

    [Fact]
    public void No_metadata_survives_into_any_rendition()
    {
        var source = TestImages.Jpeg(400, 200, orientation: 1, hiddenText: HiddenLocation);
        Contains(source, HiddenLocation).Should().BeTrue("the test file really does carry the metadata");

        foreach (var rendition in Render(source))
        {
            Contains(rendition.Content, HiddenLocation).Should().BeFalse($"{rendition.Kind} must not carry the EXIF payload");

            // WebP keeps metadata in EXIF and XMP chunks. A clean encode has neither.
            Contains(rendition.Content, "EXIF").Should().BeFalse();
            Contains(rendition.Content, "XMP ").Should().BeFalse();
        }
    }

    [Fact]
    public void A_png_is_rendered_too()
    {
        using var bitmap = new SKBitmap(64, 64);
        bitmap.Erase(SKColors.Green);
        using var png = bitmap.Encode(SKEncodedImageFormat.Png, 100);

        Render(png.ToArray()).Should().ContainSingle(r => r.Kind == AssetVariantKind.Original);
    }

    [Fact]
    public void Bytes_that_are_not_an_image_are_unreadable()
    {
        var outcome = _processor.Render(Encoding.ASCII.GetBytes("%PDF-1.7 not an image"), AssetRules.VariantSpecs);

        outcome.Should().BeOfType<ImageRenderOutcome.Unreadable>();
    }

    [Fact]
    public void An_image_declaring_more_pixels_than_the_limit_is_refused_before_it_is_decoded()
    {
        // A few dozen bytes that claim 50,000 × 50,000 pixels: ten gigabytes if anyone believed it.
        var outcome = _processor.Render(TestImages.PngHeader(50_000, 50_000), AssetRules.VariantSpecs);

        outcome.Should().BeOfType<ImageRenderOutcome.Unreadable>()
            .Which.Reason.Should().Contain("50000", "it was refused for its size, not for failing to decode");
    }

    private IReadOnlyList<RenderedImage> Render(byte[] source) =>
        _processor.Render(source, AssetRules.VariantSpecs)
            .Should().BeOfType<ImageRenderOutcome.Rendered>().Subject.Renditions;

    private static bool Contains(byte[] haystack, string needle) =>
        haystack.AsSpan().IndexOf(Encoding.ASCII.GetBytes(needle)) >= 0;
}

/// <summary>Real image files, built in memory, with exactly the metadata a test needs.</summary>
internal static class TestImages
{
    /// <summary>A JPEG whose left half is red and right half blue, optionally carrying EXIF.</summary>
    public static byte[] Jpeg(int width, int height, ushort? orientation = null, string? hiddenText = null)
    {
        using var bitmap = new SKBitmap(width, height);

        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Blue);
            using var paint = new SKPaint { Color = SKColors.Red };
            canvas.DrawRect(0, 0, width / 2f, height, paint);
        }

        using var encoded = bitmap.Encode(SKEncodedImageFormat.Jpeg, 90);
        var jpeg = encoded.ToArray();

        return orientation is null && hiddenText is null
            ? jpeg
            : WithExif(jpeg, orientation ?? 1, hiddenText ?? string.Empty);
    }

    /// <summary>
    /// Splices an APP1 EXIF segment in after the JPEG's start-of-image marker: a one-entry TIFF
    /// directory holding the orientation, followed by whatever text stands in for a GPS position.
    /// </summary>
    public static byte[] WithExif(byte[] jpeg, ushort orientation, string hiddenText)
    {
        byte[] tiff =
        [
            (byte)'I', (byte)'I', 0x2A, 0x00, 0x08, 0x00, 0x00, 0x00, // little-endian header, IFD at 8
            0x01, 0x00,                                               // one entry
            0x12, 0x01, 0x03, 0x00, 0x01, 0x00, 0x00, 0x00,           // 0x0112 Orientation, SHORT, count 1
            (byte)orientation, (byte)(orientation >> 8), 0x00, 0x00,  // the value
            0x00, 0x00, 0x00, 0x00,                                   // no next IFD
            .. Encoding.ASCII.GetBytes(hiddenText),
        ];

        byte[] payload = [.. "Exif\0\0"u8, .. tiff];
        var length = payload.Length + 2;

        byte[] segment = [0xFF, 0xE1, (byte)(length >> 8), (byte)length, .. payload];

        return [.. jpeg.AsSpan(0, 2), .. segment, .. jpeg.AsSpan(2)];
    }

    /// <summary>A PNG with a real, correctly checksummed header and no meaningful pixel data.</summary>
    public static byte[] PngHeader(uint width, uint height)
    {
        var ihdr = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(0), width);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4), height);
        ihdr[8] = 8;  // bit depth
        ihdr[9] = 2;  // truecolour

        return
        [
            0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
            .. Chunk("IHDR", ihdr),
            .. Chunk("IDAT", [0x78, 0x9C]),
        ];
    }

    private static byte[] Chunk(string type, byte[] data)
    {
        byte[] typed = [.. Encoding.ASCII.GetBytes(type), .. data];

        var chunk = new byte[4 + typed.Length + 4];
        BinaryPrimitives.WriteUInt32BigEndian(chunk, (uint)data.Length);
        typed.CopyTo(chunk, 4);
        BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(4 + typed.Length), Crc32(typed));

        return chunk;
    }

    private static uint Crc32(ReadOnlySpan<byte> bytes)
    {
        var crc = 0xFFFFFFFFu;

        foreach (var b in bytes)
        {
            crc ^= b;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
        }

        return ~crc;
    }
}
