using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using TripsAgent.Application.Identity;

namespace TripsAgent.Infrastructure.Storage;

/// <summary>
/// Signs and checks the URLs that stand in for an object store's presigned URLs locally.
/// </summary>
/// <remarks>
/// <para>
/// An S3 or Azure presigned URL is a request the store will honour because it is signed with a
/// key only the store and we hold. Locally there is no store, so the API plays its part: this
/// signs, and <c>LocalStorageEndpoints</c> checks the signature and reads or writes the file. It
/// is the same idea as <c>KybDocumentLink</c>, signed with the same server-side key.
/// </para>
/// <para>
/// The signature covers the operation, the key, the expiry, the size cap and the content type.
/// Changing any of them — reading with an upload URL, pointing it at another key, stretching the
/// deadline — invalidates it rather than widening it.
/// </para>
/// </remarks>
public sealed class LocalBlobUrlSigner
{
    /// <summary>Where uploads are PUT. Matches the route in <c>LocalStorageEndpoints</c>.</summary>
    public const string UploadPathPrefix = "/api/v1/storage/uploads/";

    /// <summary>Where objects are read. Matches the route in <c>LocalStorageEndpoints</c>.</summary>
    public const string DownloadPathPrefix = "/api/v1/storage/objects/";

    private const string Put = "put";
    private const string Get = "get";

    private readonly Func<ITokenHasher> _hasher;
    private readonly TimeProvider _clock;

    /// <param name="hasher">
    /// Resolved on first use rather than at construction, so a process that only stores and reads
    /// — the Worker — does not need the signing key configured just to start.
    /// </param>
    /// <param name="clock">Decides whether a URL has expired.</param>
    public LocalBlobUrlSigner(Func<ITokenHasher> hasher, TimeProvider clock)
    {
        _hasher = hasher;
        _clock = clock;
    }

    public string UploadPath(string key, string contentType, long maxSizeBytes, DateTimeOffset expiresAt)
    {
        var expires = expiresAt.ToUnixTimeSeconds();

        return $"{UploadPathPrefix}{EscapeKey(key)}"
               + $"?expires={expires.ToString(CultureInfo.InvariantCulture)}"
               + $"&max={maxSizeBytes.ToString(CultureInfo.InvariantCulture)}"
               + $"&type={Uri.EscapeDataString(contentType)}"
               + $"&signature={Uri.EscapeDataString(Sign(Put, key, expires, maxSizeBytes, contentType))}";
    }

    public string DownloadPath(string key, string contentType, DateTimeOffset expiresAt)
    {
        var expires = expiresAt.ToUnixTimeSeconds();

        return $"{DownloadPathPrefix}{EscapeKey(key)}"
               + $"?expires={expires.ToString(CultureInfo.InvariantCulture)}"
               + $"&type={Uri.EscapeDataString(contentType)}"
               + $"&signature={Uri.EscapeDataString(Sign(Get, key, expires, 0, contentType))}";
    }

    public bool IsValidUpload(string key, long expires, long maxSizeBytes, string? contentType, string? signature) =>
        IsValid(Put, key, expires, maxSizeBytes, contentType, signature);

    public bool IsValidDownload(string key, long expires, string? contentType, string? signature) =>
        IsValid(Get, key, expires, 0, contentType, signature);

    private bool IsValid(string operation, string key, long expires, long maxSizeBytes, string? contentType, string? signature)
    {
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(signature) || contentType is null)
        {
            return false;
        }

        if (DateTimeOffset.FromUnixTimeSeconds(expires) <= _clock.GetUtcNow())
        {
            return false;
        }

        // Fixed-time comparison: a byte-by-byte one leaks how much of the signature matched,
        // which is enough to forge one a character at a time.
        var expected = Encoding.UTF8.GetBytes(Sign(operation, key, expires, maxSizeBytes, contentType));
        var presented = Encoding.UTF8.GetBytes(signature);

        return CryptographicOperations.FixedTimeEquals(expected, presented);
    }

    /// <summary>
    /// Every field is escaped before joining, so no value can contain the separator and pass
    /// itself off as a different split of the same string.
    /// </summary>
    private string Sign(string operation, string key, long expires, long maxSizeBytes, string contentType) =>
        _hasher().Hash(string.Join(
            ':',
            "local-blob",
            operation,
            Uri.EscapeDataString(key),
            expires.ToString(CultureInfo.InvariantCulture),
            maxSizeBytes.ToString(CultureInfo.InvariantCulture),
            Uri.EscapeDataString(contentType)));

    private static string EscapeKey(string key) =>
        string.Join('/', key.Split('/').Select(Uri.EscapeDataString));
}
