namespace TripsAgent.Domain.Tenancy.Kyb;

/// <summary>What a KYB document may be. FRD §2.2: PDF, JPG or PNG, at most 10MB.</summary>
public static class KybDocumentRules
{
    /// <summary>Ten megabytes, counted in binary units as every file dialog does.</summary>
    public const long MaxSizeBytes = 10L * 1024 * 1024;

    public const string Pdf = "application/pdf";
    public const string Jpeg = "image/jpeg";
    public const string Png = "image/png";

    /// <summary>The only content types accepted, in the order a person would list them.</summary>
    public static IReadOnlyList<string> AllowedContentTypes { get; } = [Pdf, Jpeg, Png];

    /// <summary>Extensions matching <see cref="AllowedContentTypes"/>, for the file picker.</summary>
    public static IReadOnlyList<string> AllowedExtensions { get; } = [".pdf", ".jpg", ".jpeg", ".png"];

    public static bool IsAllowedContentType(string? contentType) =>
        contentType is not null
        && AllowedContentTypes.Contains(contentType, StringComparer.OrdinalIgnoreCase);

    public static bool IsAllowedSize(long sizeBytes) => sizeBytes > 0 && sizeBytes <= MaxSizeBytes;

    /// <summary>"10MB" — for a message someone actually reads.</summary>
    public static string MaxSizeDescription => $"{MaxSizeBytes / (1024 * 1024)}MB";
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
/// This is not a general-purpose file identifier — it recognises the three formats we accept and
/// says "no" to everything else, which is exactly the right bias for an allowlist.
/// </para>
/// </remarks>
public static class FileSignature
{
    /// <summary>How many leading bytes are needed to identify any accepted format.</summary>
    public const int RequiredBytes = 8;

    /// <summary>
    /// The content type the bytes actually say this is, or null when it is not one we accept.
    /// </summary>
    public static string? Detect(ReadOnlySpan<byte> leadingBytes)
    {
        // %PDF
        if (StartsWith(leadingBytes, [0x25, 0x50, 0x44, 0x46]))
        {
            return KybDocumentRules.Pdf;
        }

        // JPEG SOI marker, then any of the JFIF/Exif application markers.
        if (StartsWith(leadingBytes, [0xFF, 0xD8, 0xFF]))
        {
            return KybDocumentRules.Jpeg;
        }

        // PNG signature: \x89 P N G \r \n \x1A \n — the trailing bytes catch a file mangled by a
        // transfer that "helpfully" converted line endings.
        if (StartsWith(leadingBytes, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]))
        {
            return KybDocumentRules.Png;
        }

        return null;
    }

    private static bool StartsWith(ReadOnlySpan<byte> value, ReadOnlySpan<byte> prefix) =>
        value.Length >= prefix.Length && value[..prefix.Length].SequenceEqual(prefix);
}
