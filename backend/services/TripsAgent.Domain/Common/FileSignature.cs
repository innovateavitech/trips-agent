namespace TripsAgent.Domain.Common;

/// <summary>The content types the platform is willing to store, written once.</summary>
/// <remarks>
/// Here rather than beside each feature's allowlist so that "application/pdf" is one string in
/// the codebase. Each feature still decides which of these it accepts — KYB takes three of them,
/// the asset pipeline takes a different set.
/// </remarks>
public static class MediaTypes
{
    public const string Pdf = "application/pdf";
    public const string Jpeg = "image/jpeg";
    public const string Png = "image/png";
    public const string Webp = "image/webp";
}

/// <summary>
/// Identifies a file's real type from its leading bytes.
/// </summary>
/// <remarks>
/// <para>
/// The filename and the browser-supplied content type are both attacker-controlled: renaming
/// <c>payload.exe</c> to <c>certificate.pdf</c> changes both and nothing else. The first few bytes
/// of a file are put there by whatever wrote it, so they are the only part of an upload worth
/// trusting.
/// </para>
/// <para>
/// This is not a general-purpose file identifier — it recognises the formats we accept and
/// says "no" to everything else, which is exactly the right bias for an allowlist. A caller still
/// has to check the answer against its own allowlist: recognising WebP is not the same as KYB
/// being willing to take a WebP certificate.
/// </para>
/// <para>
/// Notably absent: SVG (it is a script container), and GIF (animated frames make "resize this"
/// mean something we have not decided). Both are refused because they are not listed.
/// </para>
/// </remarks>
public static class FileSignature
{
    /// <summary>
    /// How many leading bytes are needed to identify any recognised format.
    /// </summary>
    /// <remarks>
    /// Twelve, because WebP's marker is at offset 8 — <c>RIFF</c>, four bytes of length, then
    /// <c>WEBP</c>. Everything else needs at most eight.
    /// </remarks>
    public const int RequiredBytes = 12;

    /// <summary>
    /// The content type the bytes actually say this is, or null when it is not one we recognise.
    /// </summary>
    public static string? Detect(ReadOnlySpan<byte> leadingBytes)
    {
        // %PDF
        if (StartsWith(leadingBytes, [0x25, 0x50, 0x44, 0x46]))
        {
            return MediaTypes.Pdf;
        }

        // JPEG SOI marker, then any of the JFIF/Exif application markers.
        if (StartsWith(leadingBytes, [0xFF, 0xD8, 0xFF]))
        {
            return MediaTypes.Jpeg;
        }

        // PNG signature: \x89 P N G \r \n \x1A \n — the trailing bytes catch a file mangled by a
        // transfer that "helpfully" converted line endings.
        if (StartsWith(leadingBytes, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]))
        {
            return MediaTypes.Png;
        }

        // RIFF container, with WEBP as the form type at offset 8. The RIFF prefix alone is not
        // enough — a WAV file starts identically.
        if (StartsWith(leadingBytes, [0x52, 0x49, 0x46, 0x46])
            && leadingBytes.Length >= 12
            && leadingBytes[8..12].SequenceEqual([0x57, 0x45, 0x42, 0x50]))
        {
            return MediaTypes.Webp;
        }

        return null;
    }

    private static bool StartsWith(ReadOnlySpan<byte> value, ReadOnlySpan<byte> prefix) =>
        value.Length >= prefix.Length && value[..prefix.Length].SequenceEqual(prefix);
}
