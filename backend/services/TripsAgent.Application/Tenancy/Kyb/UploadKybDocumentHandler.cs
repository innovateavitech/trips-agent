using Microsoft.EntityFrameworkCore;
using TripsAgent.Application.Persistence;
using TripsAgent.Application.Storage;
using TripsAgent.Application.Tenancy;
using TripsAgent.Domain.Common;
using TripsAgent.Domain.Tenancy.Kyb;

namespace TripsAgent.Application.Tenancy.Kyb;

/// <summary>What happened to an upload.</summary>
public abstract record UploadKybDocumentOutcome
{
    private UploadKybDocumentOutcome()
    {
    }

    public sealed record Uploaded(KybDocument Document) : UploadKybDocumentOutcome;

    /// <summary>The file was refused, with a message written for the person who chose it.</summary>
    public sealed record Rejected(string Reason) : UploadKybDocumentOutcome;

    /// <summary>The submission is with Trips, so its documents are frozen.</summary>
    public sealed record SubmissionLocked : UploadKybDocumentOutcome;
}

/// <summary>
/// Accepts one KYB document: checks it, stores the bytes, and records the row.
/// </summary>
/// <remarks>
/// <para>
/// The type is established by <b>sniffing the file's first bytes</b>, never by trusting the
/// filename or the browser's <c>Content-Type</c> — both are set by whoever is uploading, and
/// renaming <c>payload.exe</c> to <c>certificate.pdf</c> changes both and nothing else.
/// </para>
/// <para>
/// The storage key is generated, never derived from the filename. A key built from user input is
/// how one agency's upload overwrites another's, and how <c>../</c> escapes the storage root.
/// </para>
/// </remarks>
public sealed class UploadKybDocumentHandler
{
    private readonly IAppDbContext _db;
    private readonly IBlobStorage _storage;
    private readonly ITenantContext _tenant;
    private readonly TimeProvider _clock;

    public UploadKybDocumentHandler(
        IAppDbContext db,
        IBlobStorage storage,
        ITenantContext tenant,
        TimeProvider clock)
    {
        _db = db;
        _storage = storage;
        _tenant = tenant;
        _clock = clock;
    }

    public async Task<UploadKybDocumentOutcome> HandleAsync(
        KybDocumentType documentType,
        string fileName,
        long declaredSizeBytes,
        Stream content,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);

        if (_tenant.AgencyId is not { } agencyId)
        {
            throw new InvalidOperationException("An upload needs a resolved tenant; this endpoint requires authentication.");
        }

        // Checked before a byte is read. The endpoint also caps the request body, so an oversized
        // file is refused at the edge rather than buffered and then rejected.
        if (!KybDocumentRules.IsAllowedSize(declaredSizeBytes))
        {
            return new UploadKybDocumentOutcome.Rejected(
                declaredSizeBytes <= 0
                    ? "That file is empty."
                    : $"That file is larger than {KybDocumentRules.MaxSizeDescription}.");
        }

        var submission = await OpenSubmissionForAsync(agencyId, cancellationToken);

        if (!submission.IsEditable)
        {
            return new UploadKybDocumentOutcome.SubmissionLocked();
        }

        var (sniffed, rewound) = await SniffContentTypeAsync(content, cancellationToken);

        // Checked against KYB's own list, not just "recognised": the detector knows WebP because the
        // asset pipeline accepts it, and a WebP certificate of incorporation is still not one.
        if (!KybDocumentRules.IsAllowedContentType(sniffed))
        {
            return new UploadKybDocumentOutcome.Rejected(
                $"That file is not a {string.Join(", ", KybDocumentRules.AllowedExtensions)}. "
                + "Its contents do not match any accepted format, whatever the file is named.");
        }

        // Generated, never derived from the filename.
        var storageKey = $"kyb/{agencyId:N}/{submission.Id:N}/{Guid.CreateVersion7():N}";

        var stored = await _storage.StoreAsync(rewound, storageKey, sniffed!, cancellationToken);

        // Re-checked against what was actually written, not what the request claimed: a client
        // can under-report Content-Length and stream more.
        if (!KybDocumentRules.IsAllowedSize(stored.SizeBytes))
        {
            await _storage.DeleteAsync(storageKey, cancellationToken);

            return new UploadKybDocumentOutcome.Rejected(
                $"That file is larger than {KybDocumentRules.MaxSizeDescription}.");
        }

        var document = KybDocument.Create(
            agencyId,
            submission.Id,
            documentType,
            fileName,
            storageKey,
            sniffed!,
            stored.SizeBytes,
            stored.Checksum);

        // One document per type: uploading again replaces the previous one, which is what
        // "re-upload after rejection" means to the person doing it.
        var existing = await _db.KybDocuments
            .Where(d => d.SubmissionId == submission.Id && d.DocumentType == documentType)
            .ToListAsync(cancellationToken);

        foreach (var superseded in existing)
        {
            _db.KybDocuments.Remove(superseded);
            await _storage.DeleteAsync(superseded.StorageKey, cancellationToken);
        }

        _db.KybDocuments.Add(document);
        await _db.SaveChangesAsync(cancellationToken);

        return new UploadKybDocumentOutcome.Uploaded(document);
    }

    /// <summary>The agency's open submission, started if there is not one.</summary>
    private async Task<KybSubmission> OpenSubmissionForAsync(Guid agencyId, CancellationToken cancellationToken)
    {
        var submission = await _db.KybSubmissions
            .Where(s => s.AgencyId == agencyId)
            .OrderByDescending(s => s.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        // A rejected submission is reopened rather than replaced, so the documents that were
        // already accepted stay attached and only the ones at fault need replacing.
        if (submission is null)
        {
            submission = KybSubmission.StartFor(agencyId);
            _db.KybSubmissions.Add(submission);
            await _db.SaveChangesAsync(cancellationToken);
        }

        return submission;
    }

    /// <summary>
    /// Reads the leading bytes to identify the format, then hands back a stream positioned at the
    /// start again.
    /// </summary>
    /// <remarks>
    /// A request body is forward-only, so the bytes consumed for sniffing have to be put back.
    /// They are copied into memory and concatenated ahead of the remaining stream rather than
    /// buffering the whole upload, which would make a 10MB limit cost 10MB of memory per request.
    /// </remarks>
    private static async Task<(string? ContentType, Stream Content)> SniffContentTypeAsync(
        Stream content,
        CancellationToken cancellationToken)
    {
        var header = new byte[FileSignature.RequiredBytes];
        var read = await content.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false, cancellationToken);

        var contentType = FileSignature.Detect(header.AsSpan(0, read));

        if (contentType is null)
        {
            return (null, content);
        }

        return (contentType, new PrefixedStream(header.AsMemory(0, read), content));
    }
}

/// <summary>A stream that replays a small prefix before continuing with the rest.</summary>
/// <remarks>
/// Exists so the bytes read for format sniffing can be put back without buffering the whole
/// upload in memory.
/// </remarks>
internal sealed class PrefixedStream : Stream
{
    private readonly ReadOnlyMemory<byte> _prefix;
    private readonly Stream _rest;
    private int _prefixPosition;

    public PrefixedStream(ReadOnlyMemory<byte> prefix, Stream rest)
    {
        _prefix = prefix;
        _rest = rest;
    }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (_prefixPosition < _prefix.Length)
        {
            var take = Math.Min(buffer.Length, _prefix.Length - _prefixPosition);
            _prefix.Span.Slice(_prefixPosition, take).CopyTo(buffer);
            _prefixPosition += take;
            return take;
        }

        return _rest.Read(buffer);
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_prefixPosition < _prefix.Length)
        {
            var take = Math.Min(buffer.Length, _prefix.Length - _prefixPosition);
            _prefix.Slice(_prefixPosition, take).CopyTo(buffer);
            _prefixPosition += take;
            return take;
        }

        return await _rest.ReadAsync(buffer, cancellationToken);
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
