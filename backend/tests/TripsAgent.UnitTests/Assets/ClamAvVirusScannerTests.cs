using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using TripsAgent.Application.Assets;
using TripsAgent.Infrastructure.Assets;

namespace TripsAgent.UnitTests.Assets;

/// <summary>
/// The clamd INSTREAM protocol, against a stand-in clamd on a loopback socket: what goes over the
/// wire, and what every kind of answer — or silence — turns into. The rule under test throughout:
/// only a literal OK is clean.
/// </summary>
public sealed class ClamAvVirusScannerTests
{
    [Fact]
    public async Task The_file_goes_in_length_prefixed_chunks_and_ends_with_a_zero_length_chunk()
    {
        await using var clamd = FakeClamd.Replying("stream: OK\0");
        var file = RandomNumberGenerator.GetBytes((2 * ClamAvVirusScanner.ChunkSize) + 1_000);

        var result = await Scanner(clamd.Port).ScanAsync(file);

        result.Should().BeOfType<VirusScanResult.Clean>();

        var received = await clamd.ReceivedAsync();
        received.Command.Should().Be("zINSTREAM\0");
        received.ChunkLengths.Should().Equal(ClamAvVirusScanner.ChunkSize, ClamAvVirusScanner.ChunkSize, 1_000, 0);
        received.Content.Should().Equal(file, "clamd must scan exactly the bytes that will be served");
    }

    [Fact]
    public async Task An_empty_file_is_only_the_end_marker()
    {
        await using var clamd = FakeClamd.Replying("stream: OK\0");

        await Scanner(clamd.Port).ScanAsync(ReadOnlyMemory<byte>.Empty);

        (await clamd.ReceivedAsync()).ChunkLengths.Should().Equal(0);
    }

    [Fact]
    public async Task A_FOUND_answer_is_infected_and_keeps_the_signature_name()
    {
        await using var clamd = FakeClamd.Replying("stream: Win.Test.EICAR_HDB-1 FOUND\0");

        var result = await Scanner(clamd.Port).ScanAsync("anything"u8.ToArray());

        result.Should().BeOfType<VirusScanResult.Infected>()
            .Which.Signature.Should().Be("Win.Test.EICAR_HDB-1");
    }

    [Fact]
    public async Task clamds_own_ERROR_answer_is_unavailable_never_clean()
    {
        await using var clamd = FakeClamd.Replying("INSTREAM size limit exceeded. ERROR\0");

        var result = await Scanner(clamd.Port).ScanAsync("anything"u8.ToArray());

        result.Should().BeOfType<VirusScanResult.Unavailable>()
            .Which.Reason.Should().Contain("size limit exceeded");
    }

    [Fact]
    public async Task A_closed_connection_with_no_answer_is_unavailable()
    {
        await using var clamd = FakeClamd.Replying(string.Empty);

        var result = await Scanner(clamd.Port).ScanAsync("anything"u8.ToArray());

        result.Should().BeOfType<VirusScanResult.Unavailable>()
            .Which.Reason.Should().Contain("without an answer");
    }

    [Fact]
    public async Task A_clamd_that_is_not_there_is_unavailable()
    {
        var port = FreePort();

        var result = await Scanner(port).ScanAsync("anything"u8.ToArray());

        result.Should().BeOfType<VirusScanResult.Unavailable>()
            .Which.Reason.Should().Contain($"127.0.0.1:{port}");
    }

    [Fact]
    public async Task A_clamd_that_never_answers_is_unavailable_once_the_timeout_passes()
    {
        await using var clamd = FakeClamd.Silent(TimeSpan.FromSeconds(10));
        var clock = Stopwatch.StartNew();

        var result = await Scanner(clamd.Port, scanTimeoutSeconds: 1).ScanAsync("anything"u8.ToArray());

        result.Should().BeOfType<VirusScanResult.Unavailable>()
            .Which.Reason.Should().Contain("did not answer within 1 seconds");
        clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5), "a hung clamd must not hold the job");
    }

    [Fact]
    public async Task The_caller_giving_up_is_not_mistaken_for_a_scanner_that_is_down()
    {
        // A Worker shutting down cancels the job. That must stop the scan, not record the file
        // as unscannable — the scanner did nothing wrong.
        await using var clamd = FakeClamd.Silent(TimeSpan.FromSeconds(10));
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        var act = () => Scanner(clamd.Port, scanTimeoutSeconds: 30).ScanAsync("anything"u8.ToArray(), cancel.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Theory]
    [InlineData("stream: OK", "clean")]
    [InlineData("  stream: OK  ", "clean")]
    [InlineData("stream: Eicar-Signature FOUND", "infected")]
    [InlineData("stream: OKAY", "unavailable")]
    [InlineData("stream: FOUND", "unavailable")]
    [InlineData("OK", "unavailable")]
    [InlineData("PONG", "unavailable")]
    [InlineData("", "unavailable")]
    [InlineData("stream: lstat() failed: No such file. ERROR", "unavailable")]
    public void Only_a_literal_OK_is_clean(string reply, string meaning)
    {
        var result = ClamAvVirusScanner.Interpret(reply);

        (meaning switch
        {
            "clean" => result is VirusScanResult.Clean,
            "infected" => result is VirusScanResult.Infected,
            _ => result is VirusScanResult.Unavailable,
        }).Should().BeTrue($"'{reply}' should read as {meaning}, not {result.GetType().Name}");
    }

    [Fact]
    public void Settings_without_a_host_are_refused_with_a_reason()
    {
        var options = new ClamAvOptions();

        options.Problem().Should().Contain("Host");
        FluentActions.Invoking(() => new ClamAvVirusScanner(options)).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Its_name_says_what_it_is() =>
        Scanner(3310).Name.Should().Be("clamav");

    private static ClamAvVirusScanner Scanner(int port, int scanTimeoutSeconds = 10) =>
        new(new ClamAvOptions
        {
            Host = "127.0.0.1",
            Port = port,
            ConnectTimeoutSeconds = 2,
            ScanTimeoutSeconds = scanTimeoutSeconds,
        });

    /// <summary>A port nothing listens on: bound for a moment to find one, then released.</summary>
    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        listener.Dispose();
        return port;
    }

    /// <summary>What the stand-in clamd was sent.</summary>
    private sealed record Received(string Command, IReadOnlyList<int> ChunkLengths, byte[] Content);

    /// <summary>
    /// A clamd that takes one INSTREAM session, decodes it the way the real one does, and then
    /// answers — or does not.
    /// </summary>
    private sealed class FakeClamd : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly Task<Received> _session;

        private FakeClamd(Func<NetworkStream, Received, Task> afterReading)
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;

            _session = Task.Run(async () =>
            {
                using var client = await _listener.AcceptTcpClientAsync();
                await using var stream = client.GetStream();

                var received = await ReadInstreamAsync(stream);
                await afterReading(stream, received);

                return received;
            });
        }

        public int Port { get; }

        public static FakeClamd Replying(string reply) =>
            new(async (stream, _) => await stream.WriteAsync(Encoding.ASCII.GetBytes(reply)));

        public static FakeClamd Silent(TimeSpan hold) =>
            new(async (_, _) => await Task.Delay(hold));

        public Task<Received> ReceivedAsync() => _session.WaitAsync(TimeSpan.FromSeconds(10));

        public ValueTask DisposeAsync()
        {
            _listener.Stop();
            _listener.Dispose();
            return ValueTask.CompletedTask;
        }

        private static async Task<Received> ReadInstreamAsync(NetworkStream stream)
        {
            var command = new byte["zINSTREAM\0".Length];
            await stream.ReadExactlyAsync(command);

            var lengths = new List<int>();
            using var content = new MemoryStream();
            var header = new byte[4];

            while (true)
            {
                await stream.ReadExactlyAsync(header);
                var length = (int)BinaryPrimitives.ReadUInt32BigEndian(header);
                lengths.Add(length);

                if (length == 0)
                {
                    break;
                }

                var chunk = new byte[length];
                await stream.ReadExactlyAsync(chunk);
                content.Write(chunk);
            }

            return new Received(Encoding.ASCII.GetString(command), lengths, content.ToArray());
        }
    }
}
