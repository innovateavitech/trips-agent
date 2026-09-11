using TripsAgent.Domain.Common;

namespace TripsAgent.Domain.Tenancy.Kyb;

/// <summary>What a KYB document may be. FRD §2.2: PDF, JPG or PNG, at most 10MB.</summary>
/// <remarks>
/// The type is established by sniffing the file's bytes — see <see cref="FileSignature"/>, which
/// recognises more formats than this list accepts. Recognising a WebP is not the same as being
/// willing to take a WebP certificate of incorporation, so the sniffed answer is still checked
/// against <see cref="AllowedContentTypes"/>.
/// </remarks>
public static class KybDocumentRules
{
    /// <summary>Ten megabytes, counted in binary units as every file dialog does.</summary>
    public const long MaxSizeBytes = 10L * 1024 * 1024;

    public const string Pdf = MediaTypes.Pdf;
    public const string Jpeg = MediaTypes.Jpeg;
    public const string Png = MediaTypes.Png;

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
