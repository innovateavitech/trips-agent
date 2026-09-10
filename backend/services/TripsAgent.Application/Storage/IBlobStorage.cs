namespace TripsAgent.Application.Storage;

/// <summary>What was stored, and what it turned out to be.</summary>
/// <param name="StorageKey">Where the bytes now live.</param>
/// <param name="SizeBytes">How many bytes were actually written.</param>
/// <param name="Checksum">SHA-256 of the stored content, hex-encoded.</param>
public sealed record StoredBlob(string StorageKey, long SizeBytes, string Checksum);

/// <summary>
/// Somewhere to put files. A port, not a provider.
/// </summary>
/// <remarks>
/// <para>
/// The cloud is not chosen yet (CLAUDE.md), so nothing above this line may know whether the bytes
/// end up in S3, Azure Blob Storage or a directory on disk. Local development uses the filesystem;
/// MinIO and then a real provider slot in behind the same interface.
/// </para>
/// <para>
/// This is deliberately the minimum KYB uploads need. The full pipeline — presigned direct-to-
/// storage uploads, virus scanning, EXIF stripping, generated variants — is issue #18, and it will
/// extend this rather than replace it.
/// </para>
/// </remarks>
public interface IBlobStorage
{
    /// <summary>
    /// Stores a stream and returns where it went, how big it was and its checksum.
    /// </summary>
    /// <param name="key">
    /// The full key to store under. Callers generate it; storage never derives one from a
    /// user-supplied filename.
    /// </param>
    public Task<StoredBlob> StoreAsync(
        Stream content,
        string key,
        string contentType,
        CancellationToken cancellationToken = default);

    /// <summary>Opens a stored object for reading. Throws if the key does not exist.</summary>
    public Task<Stream> OpenReadAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>True when an object exists at that key.</summary>
    public Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>Removes an object. Succeeds quietly when it was already gone.</summary>
    public Task DeleteAsync(string key, CancellationToken cancellationToken = default);
}
