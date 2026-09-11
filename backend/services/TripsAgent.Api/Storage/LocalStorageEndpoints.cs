using Microsoft.AspNetCore.Http.Features;
using TripsAgent.Application.Storage;
using TripsAgent.Domain.Assets;
using TripsAgent.Infrastructure.Storage;

namespace TripsAgent.Api.Storage;

/// <summary>
/// The object store's half of a presigned URL, played by the API because locally there is no
/// object store.
/// </summary>
/// <remarks>
/// <para>
/// With S3 or Azure, the browser PUTs to the provider and the provider checks the signature. With
/// files on disk nothing can do that, so these two routes do exactly that job and nothing more:
/// check the signature <see cref="LocalBlobUrlSigner"/> made, then write or read one file. They
/// know nothing about assets, tenants or scan status — the signature is the whole authorisation,
/// just as it is at S3 — and they are mapped only while <see cref="LocalFileBlobStorage"/> is the
/// storage in use.
/// </para>
/// <para>
/// So "files never proxy through the API" holds in the sense that matters: no application code
/// handles the bytes, and swapping in a cloud adapter removes these routes without changing a
/// line anywhere else.
/// </para>
/// </remarks>
public static class LocalStorageEndpoints
{
    public static IEndpointRouteBuilder MapLocalStorageEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // A cloud adapter serves its own presigned URLs; these would only be an unused way in.
        if (app.ServiceProvider.GetRequiredService<IBlobStorage>() is not LocalFileBlobStorage)
        {
            return app;
        }

        // Anonymous, like the KYB document link: a browser uploading or an <img> loading sends no
        // Authorization header. The signed, expiring URL is the credential.
        app.MapPut("/api/v1/storage/uploads/{**key}", async (
                string key,
                long expires,
                long max,
                string? type,
                string? signature,
                HttpRequest request,
                LocalBlobUrlSigner signer,
                LocalFileBlobStorage storage,
                CancellationToken cancellationToken) =>
            {
                if (!signer.IsValidUpload(key, expires, max, type, signature))
                {
                    return Refused();
                }

                // The signature covers the type, as an S3 signature covers the Content-Type header.
                // Sending something else is sending a request that was not signed.
                if (!string.Equals(request.ContentType, type, StringComparison.OrdinalIgnoreCase))
                {
                    return Refused();
                }

                if (request.ContentLength > max)
                {
                    return TooLarge(max);
                }

                // Write once, as a conditional PUT (If-None-Match: *) is on S3. Otherwise the same
                // URL could replace a file after the Worker had scanned it.
                if (await storage.ExistsAsync(key, cancellationToken))
                {
                    return Results.Problem(
                        statusCode: StatusCodes.Status409Conflict,
                        title: "A file has already been uploaded to this link.",
                        detail: "Start a new upload to send a different file.");
                }

                var capped = new CappedReadStream(request.Body, max);

                try
                {
                    await storage.StoreAsync(capped, key, type!, cancellationToken);
                }
                catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
                {
                    // The server's own ceiling, which a purpose's cap never exceeds — so this only
                    // fires alongside the check below, whichever notices first.
                    await storage.DeleteAsync(key, cancellationToken);
                    return TooLarge(max);
                }

                if (capped.Exceeded)
                {
                    await storage.DeleteAsync(key, cancellationToken);
                    return TooLarge(max);
                }

                return Results.Ok();
            })
            .WithName("LocalStorageUpload")
            .ExcludeFromDescription()
            .DisableAntiforgery()
            .WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(AssetRules.AbsoluteMaxSizeBytes));

        app.MapGet("/api/v1/storage/objects/{**key}", async (
                string key,
                long expires,
                string? type,
                string? signature,
                HttpContext http,
                LocalBlobUrlSigner signer,
                LocalFileBlobStorage storage,
                CancellationToken cancellationToken) =>
            {
                if (!signer.IsValidDownload(key, expires, type, signature))
                {
                    return Refused();
                }

                if (!await storage.ExistsAsync(key, cancellationToken))
                {
                    return Results.NotFound();
                }

                var content = await storage.OpenReadAsync(key, cancellationToken);

                // The type was decided by sniffing when the file was accepted; a browser guessing
                // again from the bytes is how a file served as an image gets run as a page.
                http.Response.Headers.XContentTypeOptions = "nosniff";

                return Results.File(content, type, enableRangeProcessing: true);
            })
            .WithName("LocalStorageDownload")
            .ExcludeFromDescription();

        return app;
    }

    /// <summary>
    /// One answer for a bad signature, an expired link and a wrong header: which it was tells an
    /// attacker what to change.
    /// </summary>
    private static IResult Refused() => Results.Problem(
        statusCode: StatusCodes.Status403Forbidden,
        title: "This link is not valid, or it has expired.");

    private static IResult TooLarge(long max) => Results.Problem(
        statusCode: StatusCodes.Status413PayloadTooLarge,
        title: "That file is larger than this upload allows.",
        detail: $"The limit is {max / (1024 * 1024)}MB.");
}

/// <summary>
/// Reads a request body up to a limit, then reports end-of-stream and remembers that it stopped.
/// </summary>
/// <remarks>
/// Ending the stream rather than throwing lets storage finish writing cleanly; the caller checks
/// <see cref="Exceeded"/> and deletes what was written. The limit is enforced on what arrives,
/// because Content-Length is optional and can be a lie.
/// </remarks>
internal sealed class CappedReadStream : Stream
{
    private readonly Stream _inner;
    private readonly long _limit;
    private long _read;

    public CappedReadStream(Stream inner, long limit)
    {
        _inner = inner;
        _limit = limit;
    }

    /// <summary>True once more bytes arrived than the limit allows.</summary>
    public bool Exceeded { get; private set; }

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
        throw new NotSupportedException("Request bodies are read asynchronously.");

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (Exceeded)
        {
            return 0;
        }

        var read = await _inner.ReadAsync(buffer, cancellationToken);
        _read += read;

        if (_read > _limit)
        {
            Exceeded = true;
            return 0;
        }

        return read;
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
