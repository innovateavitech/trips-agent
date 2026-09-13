using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using TripsAgent.Application.Assets;

namespace TripsAgent.Infrastructure.Assets;

/// <summary>Where clamd listens, and how long to wait for it. Bound from <c>Assets:ClamAv</c>.</summary>
public sealed class ClamAvOptions
{
    /// <summary>The configuration section: <c>Assets__ClamAv__Host</c> and friends in the environment.</summary>
    public const string SectionName = "Assets:ClamAv";

    /// <summary>clamd's host name or address. Required.</summary>
    public string? Host { get; set; }

    /// <summary>clamd's TCP port. 3310 is clamd's own default.</summary>
    public int Port { get; set; } = 3310;

    /// <summary>
    /// How long to wait for the connection. A scanner that cannot be reached this quickly is treated
    /// as down: the asset waits, and the job is retried.
    /// </summary>
    public int ConnectTimeoutSeconds { get; set; } = 5;

    /// <summary>
    /// How long one whole scan may take, from connecting to the verdict. A 20MB upload scans in
    /// seconds; a minute leaves room for a busy clamd without letting a hung one hold a job forever.
    /// </summary>
    public int ScanTimeoutSeconds { get; set; } = 60;

    /// <summary>What is wrong with these settings, in a sentence an operator can act on. Null when nothing is.</summary>
    public string? Problem()
    {
        if (string.IsNullOrWhiteSpace(Host))
        {
            return $"{SectionName}:Host is not set, so there is no clamd to send files to.";
        }

        if (Port is < 1 or > 65_535)
        {
            return $"{SectionName}:Port is {Port}, which is not a TCP port.";
        }

        if (ConnectTimeoutSeconds < 1 || ScanTimeoutSeconds < 1)
        {
            return $"{SectionName} timeouts must be at least one second.";
        }

        return null;
    }
}

/// <summary>
/// Scans files with ClamAV, by streaming them to a clamd daemon over TCP. Issue #18.
/// </summary>
/// <remarks>
/// <para>
/// The protocol is clamd's <c>INSTREAM</c>: the command, then the file in chunks each preceded by
/// its length as a four-byte big-endian number, then a zero length to say the file is finished.
/// clamd answers with one line — <c>stream: OK</c>, or <c>stream: &lt;signature&gt; FOUND</c>.
/// The file never touches clamd's disk, and clamd needs no access to our storage.
/// </para>
/// <para>
/// <b>It fails closed.</b> Only a literal <c>OK</c> is clean. A refused connection, a timeout, a
/// dropped socket, an <c>ERROR</c> reply or anything this class does not recognise is
/// <see cref="VirusScanResult.Unavailable"/> — never clean — so the asset stays unserved and the
/// pipeline retries until clamd gives a real answer.
/// </para>
/// </remarks>
public sealed class ClamAvVirusScanner : IVirusScanner
{
    /// <summary>How much of the file goes in one chunk. Far below clamd's StreamMaxLength.</summary>
    public const int ChunkSize = 64 * 1024;

    /// <summary>The longest reply read. clamd's answer is one short line; anything longer is not it.</summary>
    private const int MaxReplyLength = 4_096;

    /// <summary>
    /// <c>INSTREAM</c> with the <c>z</c> prefix, which makes clamd end its reply with a NUL byte
    /// rather than a newline — unambiguous, since a signature name could in principle hold neither.
    /// </summary>
    private static readonly byte[] InstreamCommand = Encoding.ASCII.GetBytes("zINSTREAM\0");

    private readonly ClamAvOptions _options;

    public ClamAvVirusScanner(ClamAvOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.Problem() is { } problem)
        {
            throw new ArgumentException(problem, nameof(options));
        }

        _options = options;
    }

    public string Name => "clamav";

    private string Endpoint => $"{_options.Host}:{_options.Port}";

    public async Task<VirusScanResult> ScanAsync(ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default)
    {
        using var scanTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        scanTimeout.CancelAfter(TimeSpan.FromSeconds(_options.ScanTimeoutSeconds));

        string reply;

        try
        {
            using var client = new TcpClient { NoDelay = true };

            using (var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(scanTimeout.Token))
            {
                connectTimeout.CancelAfter(TimeSpan.FromSeconds(_options.ConnectTimeoutSeconds));

                try
                {
                    await client.ConnectAsync(_options.Host!, _options.Port, connectTimeout.Token);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    return new VirusScanResult.Unavailable(
                        $"clamd at {Endpoint} did not accept a connection within {_options.ConnectTimeoutSeconds} seconds.");
                }
            }

            await using var stream = client.GetStream();

            await SendAsync(stream, content, scanTimeout.Token);
            reply = await ReadReplyAsync(stream, scanTimeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Our own timeout, not the caller giving up: the scanner is too slow to count on.
            return new VirusScanResult.Unavailable(
                $"clamd at {Endpoint} did not answer within {_options.ScanTimeoutSeconds} seconds.");
        }
        catch (SocketException ex)
        {
            return new VirusScanResult.Unavailable($"clamd at {Endpoint} could not be reached: {ex.Message}");
        }
        catch (IOException ex)
        {
            // Includes clamd closing the connection mid-file, which is what it does when a file is
            // over its StreamMaxLength — having first sent the reason, which a closed socket loses.
            return new VirusScanResult.Unavailable($"The connection to clamd at {Endpoint} failed during the scan: {ex.Message}");
        }

        return Interpret(reply);
    }

    /// <summary>
    /// What clamd's reply means. Only <c>stream: OK</c> is clean; <c>... FOUND</c> is infected;
    /// everything else — including clamd's own <c>... ERROR</c> — is unavailable.
    /// </summary>
    public static VirusScanResult Interpret(string reply)
    {
        ArgumentNullException.ThrowIfNull(reply);

        var answer = reply.Trim();
        const string streamPrefix = "stream:";
        const string foundSuffix = " FOUND";

        if (answer.StartsWith(streamPrefix, StringComparison.Ordinal))
        {
            var verdict = answer[streamPrefix.Length..].Trim();

            if (verdict == "OK")
            {
                return new VirusScanResult.Clean();
            }

            if (verdict.EndsWith(foundSuffix, StringComparison.Ordinal))
            {
                var signature = verdict[..^foundSuffix.Length].Trim();
                return new VirusScanResult.Infected(signature.Length > 0 ? signature : null);
            }
        }

        if (answer.EndsWith("ERROR", StringComparison.Ordinal))
        {
            return new VirusScanResult.Unavailable($"clamd could not scan the file: {Shorten(answer)}");
        }

        return new VirusScanResult.Unavailable(
            answer.Length == 0
                ? "clamd closed the connection without an answer."
                : $"clamd gave an answer this scanner does not recognise: {Shorten(answer)}");
    }

    /// <summary>Writes the command, the file in length-prefixed chunks, and the zero-length end marker.</summary>
    private static async Task SendAsync(Stream stream, ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
    {
        await stream.WriteAsync(InstreamCommand, cancellationToken);

        var length = new byte[4];

        for (var offset = 0; offset < content.Length; offset += ChunkSize)
        {
            var chunk = content.Slice(offset, Math.Min(ChunkSize, content.Length - offset));

            BinaryPrimitives.WriteUInt32BigEndian(length, (uint)chunk.Length);
            await stream.WriteAsync(length, cancellationToken);
            await stream.WriteAsync(chunk, cancellationToken);
        }

        BinaryPrimitives.WriteUInt32BigEndian(length, 0);
        await stream.WriteAsync(length, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    /// <summary>Reads up to the NUL that ends clamd's reply, or to the end of the stream.</summary>
    private static async Task<string> ReadReplyAsync(Stream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[MaxReplyLength];
        var length = 0;

        while (length < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(length), cancellationToken);

            if (read == 0)
            {
                break;
            }

            var end = Array.IndexOf(buffer, (byte)0, length, read);
            length += read;

            if (end >= 0)
            {
                length = end;
                break;
            }
        }

        return Encoding.UTF8.GetString(buffer, 0, length);
    }

    private static string Shorten(string text) => text.Length <= 200 ? text : text[..200] + "…";
}
