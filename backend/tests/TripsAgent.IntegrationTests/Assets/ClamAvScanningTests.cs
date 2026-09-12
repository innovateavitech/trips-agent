using System.Security.Cryptography;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using FluentAssertions;
using TripsAgent.Application.Assets;
using TripsAgent.Infrastructure.Assets;

namespace TripsAgent.IntegrationTests.Assets;

/// <summary>
/// A real clamd in a throwaway container, started once for the tests that share it.
/// </summary>
/// <remarks>
/// The same image docker-compose runs: the <c>-debian</c> build, because <c>clamav/clamav</c> is
/// published for amd64 only and would not start on an Apple-silicon laptop. freshclam is off — the
/// image ships with signatures, and these tests need only the EICAR one — so clamd is answering in
/// seconds rather than after a download.
/// </remarks>
public sealed class ClamAvFixture : IAsyncLifetime
{
    public const string Image = "clamav/clamav-debian:1.4";

    private const int ClamdPort = 3310;

    private readonly IContainer _container = new ContainerBuilder(Image)
        .WithEnvironment("CLAMAV_NO_FRESHCLAMD", "true")
        .WithPortBinding(ClamdPort, assignRandomHostPort: true)

        // The image's entrypoint prints this once clamd has loaded its signatures and is listening.
        .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("socket found, clamd started"))
        .Build();

    /// <summary>Settings pointing a scanner at this container's clamd.</summary>
    public ClamAvOptions Options => new()
    {
        Host = _container.Hostname,
        Port = _container.GetMappedPublicPort(ClamdPort),
        ConnectTimeoutSeconds = 5,
        ScanTimeoutSeconds = 60,
    };

    public Task InitializeAsync() => _container.StartAsync();

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();
}

/// <summary>
/// <see cref="ClamAvVirusScanner"/> against a real ClamAV. The protocol tests use a stand-in; these
/// prove the stand-in and the real thing agree. Issue #18.
/// </summary>
public sealed class ClamAvScanningTests : IClassFixture<ClamAvFixture>
{
    private readonly ClamAvVirusScanner _scanner;

    public ClamAvScanningTests(ClamAvFixture clamd) => _scanner = new ClamAvVirusScanner(clamd.Options);

    [Fact]
    public async Task A_real_clamd_finds_the_EICAR_test_file()
    {
        // Assembled at run time from two halves, so no source file or assembly carries the test
        // string itself for a developer's own antivirus to flag.
        var eicar = System.Text.Encoding.ASCII.GetBytes(EicarTestVirusScanner.TestString);

        var result = await _scanner.ScanAsync(eicar);

        result.Should().BeOfType<VirusScanResult.Infected>()
            .Which.Signature.Should().ContainEquivalentOf("eicar");
    }

    [Fact]
    public async Task A_real_clamd_passes_an_ordinary_file()
    {
        var result = await _scanner.ScanAsync("%PDF-1.7\nAn ordinary itinerary, nothing more.\n"u8.ToArray());

        result.Should().BeOfType<VirusScanResult.Clean>();
    }

    [Fact]
    public async Task A_file_bigger_than_one_chunk_is_scanned_whole()
    {
        // Several chunks, so the length-prefixed framing is exercised against clamd's own parser.
        var file = RandomNumberGenerator.GetBytes((3 * ClamAvVirusScanner.ChunkSize) + 123);

        var result = await _scanner.ScanAsync(file);

        result.Should().BeOfType<VirusScanResult.Clean>();
    }
}
