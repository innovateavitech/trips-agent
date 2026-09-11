using System.Globalization;
using System.Security.Cryptography;
using TripsAgent.Application.Storage;

namespace TripsAgent.Infrastructure.Storage;

/// <summary>Where local blob storage keeps its files.</summary>
public sealed class LocalBlobStorageOptions
{
    /// <summary>The directory everything is written beneath.</summary>
    public string RootPath { get; init; } =
        Path.Combine(Path.GetTempPath(), "tripsagent-storage");
}

/// <summary>
/// Stores blobs as files on disk. For local development, and for tests.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately the simplest thing that satisfies <see cref="IBlobStorage"/>. It exists so uploads
/// work on a laptop with nothing else running — no MinIO, no cloud account, no credentials.
/// </para>
/// <para>
/// A directory cannot sign a URL, so presigned requests are signed by
/// <see cref="LocalBlobUrlSigner"/> and answered by the API's local storage endpoints, which do
/// what S3 would: check the signature, then read or write the object. A real provider's adapter
/// signs its own URLs and those endpoints go unused.
/// </para>
/// <para>
/// Not suitable for production: files sit on one machine's disk, so a second API instance cannot
/// read what the first wrote, and nothing replicates or backs them up.
/// </para>
/// </remarks>
public sealed class LocalFileBlobStorage : IBlobStorage
{
    private readonly string _root;
    private readonly LocalBlobUrlSigner? _signer;

    /// <param name="options">Where the files go.</param>
    /// <param name="signer">
    /// Signs upload and download URLs. Optional so a test that only stores and reads can build
    /// this with nothing else; asking such an instance for a URL fails loudly.
    /// </param>
    public LocalFileBlobStorage(LocalBlobStorageOptions options, LocalBlobUrlSigner? signer = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        _root = Path.GetFullPath(options.RootPath);
        _signer = signer;
        Directory.CreateDirectory(_root);
    }

    public async Task<StoredBlob> StoreAsync(
        Stream content,
        string key,
        string contentType,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);

        var path = ResolvePath(key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        long size;
        byte[] checksum;

        await using (var file = File.Create(path))
        using (var sha = SHA256.Create())
        {
            // Hash while writing rather than re-reading afterwards: one pass over the bytes, and
            // the checksum describes exactly what landed on disk.
            await using var hashing = new CryptoStream(file, sha, CryptoStreamMode.Write, leaveOpen: true);

            await content.CopyToAsync(hashing, cancellationToken);
            await hashing.FlushFinalBlockAsync(cancellationToken);

            size = file.Length;
            checksum = sha.Hash!;
        }

        return new StoredBlob(key, size, Convert.ToHexString(checksum).ToLower(CultureInfo.InvariantCulture));
    }

    public Task<Stream> OpenReadAsync(string key, CancellationToken cancellationToken = default)
    {
        var path = ResolvePath(key);

        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"No object stored under key '{key}'.", path);
        }

        return Task.FromResult<Stream>(File.OpenRead(path));
    }

    public Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default) =>
        Task.FromResult(File.Exists(ResolvePath(key)));

    public Task<long?> GetSizeAsync(string key, CancellationToken cancellationToken = default)
    {
        var file = new FileInfo(ResolvePath(key));

        return Task.FromResult(file.Exists ? file.Length : (long?)null);
    }

    public Task<PresignedUpload> CreateUploadUrlAsync(
        string key,
        string contentType,
        long maxSizeBytes,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken = default)
    {
        // Resolved now, so a key that would escape the root is refused when the URL is made rather
        // than when somebody uses it.
        ResolvePath(key);

        var signer = RequireSigner();

        return Task.FromResult(new PresignedUpload(
            signer.UploadPath(key, contentType, maxSizeBytes, expiresAt),
            "PUT",
            new Dictionary<string, string> { ["Content-Type"] = contentType },
            maxSizeBytes,
            expiresAt));
    }

    public Task<SignedDownload> CreateDownloadUrlAsync(
        string key,
        string contentType,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken = default)
    {
        ResolvePath(key);

        return Task.FromResult(new SignedDownload(RequireSigner().DownloadPath(key, contentType, expiresAt), expiresAt));
    }

    public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        var path = ResolvePath(key);

        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return Task.CompletedTask;
    }

    private LocalBlobUrlSigner RequireSigner() =>
        _signer ?? throw new InvalidOperationException(
            "This LocalFileBlobStorage was built without a LocalBlobUrlSigner, so it cannot sign URLs. "
            + "AddInfrastructure registers one; construct it with a signer anywhere URLs are needed.");

    /// <summary>
    /// Turns a storage key into a path inside the root, refusing anything that would escape it.
    /// </summary>
    /// <remarks>
    /// Keys are generated by the application, never taken from a filename — but this is the last
    /// place a traversal could turn into writing outside the storage directory, so it is checked
    /// here rather than trusted.
    /// </remarks>
    private string ResolvePath(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        var combined = Path.GetFullPath(Path.Combine(_root, key.Replace('\\', '/')));

        if (!combined.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !string.Equals(combined, _root, StringComparison.Ordinal))
        {
            throw new ArgumentException($"Storage key '{key}' resolves outside the storage root.", nameof(key));
        }

        return combined;
    }
}
