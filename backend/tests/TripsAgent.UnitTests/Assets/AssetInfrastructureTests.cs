using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using TripsAgent.Application.Assets;
using TripsAgent.Infrastructure.Assets;
using TripsAgent.Infrastructure.Identity;
using TripsAgent.Infrastructure.Storage;

namespace TripsAgent.UnitTests.Assets;

/// <summary>
/// The Worker refuses to run the pipeline without a real virus scanner, except in Development.
/// </summary>
public class VirusScannerRegistrationTests
{
    [Theory]
    [InlineData("Development")]
    [InlineData("Staging")]
    [InlineData("Production")]
    public void With_no_scanner_configured_the_worker_does_not_start(string environment)
    {
        var act = () => Register(scanner: null, environment);

        act.Should().Throw<InvalidOperationException>().WithMessage("*No virus scanner is configured*");
    }

    [Theory]
    [InlineData("Staging")]
    [InlineData("Production")]
    public void The_development_scanner_is_refused_outside_development(string environment)
    {
        var act = () => Register(AssetProcessingRegistration.EicarTestOnly, environment);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage($"*{AssetProcessingRegistration.EicarTestOnly}*{environment}*");
    }

    [Fact]
    public void The_development_scanner_is_allowed_in_development()
    {
        var services = Register(AssetProcessingRegistration.EicarTestOnly, "Development");

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IVirusScanner>().Should().BeOfType<EicarTestVirusScanner>();
    }

    [Fact]
    public void An_unknown_scanner_name_is_refused_rather_than_ignored()
    {
        var act = () => Register("clamav-typo", "Production");

        act.Should().Throw<InvalidOperationException>().WithMessage("*clamav-typo*");
    }

    private static ServiceCollection Register(string? scanner, string environment)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [AssetProcessingRegistration.VirusScannerSetting] = scanner,
            })
            .Build();

        var services = new ServiceCollection();
        services.AddVirusScanner(configuration, new TestEnvironment(environment));
        return services;
    }

    private sealed class TestEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;

        public string ApplicationName { get; set; } = "TripsAgent.Worker";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}

public class EicarTestVirusScannerTests
{
    private readonly EicarTestVirusScanner _scanner = new();

    [Fact]
    public async Task The_eicar_test_file_is_reported_infected()
    {
        var result = await _scanner.ScanAsync(Encoding.ASCII.GetBytes(EicarTestVirusScanner.TestString));

        result.Should().BeOfType<VirusScanResult.Infected>()
            .Which.Signature.Should().Be(EicarTestVirusScanner.Signature);
    }

    [Fact]
    public async Task The_test_string_is_found_inside_an_otherwise_valid_file()
    {
        byte[] pdf = [.. "%PDF-1.7\n"u8, .. Encoding.ASCII.GetBytes(EicarTestVirusScanner.TestString)];

        (await _scanner.ScanAsync(pdf)).Should().BeOfType<VirusScanResult.Infected>();
    }

    [Fact]
    public async Task Anything_else_is_reported_clean()
    {
        (await _scanner.ScanAsync("%PDF-1.7 an ordinary document"u8.ToArray())).Should().BeOfType<VirusScanResult.Clean>();
    }

    [Fact]
    public void Its_name_says_what_it_is() => _scanner.Name.Should().Contain("test-only");
}

/// <summary>
/// The local stand-in for a presigned URL: valid for exactly what was signed, and nothing else.
/// </summary>
public sealed class LocalBlobUrlSignerTests : IDisposable
{
    private const string Key = "assets/0192a1b2c3d4e5f6a7b8c9d0e1f2a3b4/0192a1b2c3d4e5f6a7b8c9d0e1f2a3b5/upload";
    private const string Jpeg = "image/jpeg";
    private const long Max = 2 * 1024 * 1024;

    private static readonly DateTimeOffset Now = new(2026, 9, 11, 9, 0, 0, TimeSpan.Zero);

    private readonly StepClock _clock = new(Now);
    private readonly LocalBlobUrlSigner _signer;
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"signer-tests-{Guid.NewGuid():N}");

    public LocalBlobUrlSignerTests()
    {
        var hasher = new HmacTokenHasher(RandomNumberGenerator.GetBytes(HmacTokenHasher.MinimumKeyBytes));
        _signer = new LocalBlobUrlSigner(() => hasher, _clock);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void An_upload_url_is_valid_for_what_was_signed()
    {
        var url = Parse(_signer.UploadPath(Key, Jpeg, Max, Now.AddMinutes(15)));

        url.Path.Should().Be(LocalBlobUrlSigner.UploadPathPrefix + Key);
        _signer.IsValidUpload(Key, url.Expires, url.Max, url.Type, url.Signature).Should().BeTrue();
    }

    [Fact]
    public void Changing_anything_that_was_signed_invalidates_it()
    {
        var url = Parse(_signer.UploadPath(Key, Jpeg, Max, Now.AddMinutes(15)));

        _signer.IsValidUpload(Key.Replace("upload", "large.webp", StringComparison.Ordinal), url.Expires, url.Max, url.Type, url.Signature)
            .Should().BeFalse("another key");
        _signer.IsValidUpload(Key, url.Expires, url.Max * 10, url.Type, url.Signature)
            .Should().BeFalse("a bigger cap");
        _signer.IsValidUpload(Key, url.Expires + 3600, url.Max, url.Type, url.Signature)
            .Should().BeFalse("a later deadline");
        _signer.IsValidUpload(Key, url.Expires, url.Max, "text/html", url.Signature)
            .Should().BeFalse("another content type");
    }

    [Fact]
    public void An_upload_url_stops_working_when_it_expires()
    {
        var url = Parse(_signer.UploadPath(Key, Jpeg, Max, Now.AddMinutes(15)));

        _clock.Advance(TimeSpan.FromMinutes(15));

        _signer.IsValidUpload(Key, url.Expires, url.Max, url.Type, url.Signature).Should().BeFalse();
    }

    [Fact]
    public void A_download_signature_does_not_authorise_an_upload()
    {
        var download = Parse(_signer.DownloadPath(Key, Jpeg, Now.AddMinutes(15)));

        _signer.IsValidDownload(Key, download.Expires, download.Type, download.Signature).Should().BeTrue();
        _signer.IsValidUpload(Key, download.Expires, 0, download.Type, download.Signature).Should().BeFalse();
    }

    [Fact]
    public async Task Local_storage_signs_a_put_carrying_the_headers_the_signature_covers()
    {
        var storage = new LocalFileBlobStorage(new LocalBlobStorageOptions { RootPath = _root }, _signer);

        var upload = await storage.CreateUploadUrlAsync(Key, Jpeg, Max, Now.AddMinutes(15));

        upload.Method.Should().Be("PUT");
        upload.Headers.Should().Contain("Content-Type", Jpeg);
        upload.MaxSizeBytes.Should().Be(Max);
        upload.Url.Should().StartWith(LocalBlobUrlSigner.UploadPathPrefix);
    }

    [Fact]
    public async Task Local_storage_reports_a_size_only_for_what_exists()
    {
        var storage = new LocalFileBlobStorage(new LocalBlobStorageOptions { RootPath = _root });

        (await storage.GetSizeAsync(Key)).Should().BeNull();

        await storage.StoreAsync(new MemoryStream(new byte[1234]), Key, Jpeg);

        (await storage.GetSizeAsync(Key)).Should().Be(1234);
    }

    [Fact]
    public async Task Local_storage_refuses_to_sign_a_key_outside_its_root()
    {
        var storage = new LocalFileBlobStorage(new LocalBlobStorageOptions { RootPath = _root }, _signer);

        var act = () => storage.CreateUploadUrlAsync("../../etc/passwd", Jpeg, Max, Now.AddMinutes(15));

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Local_storage_built_without_a_signer_says_so_when_asked_for_a_url()
    {
        var storage = new LocalFileBlobStorage(new LocalBlobStorageOptions { RootPath = _root });

        var act = () => storage.CreateDownloadUrlAsync(Key, Jpeg, Now.AddMinutes(15));

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*without a LocalBlobUrlSigner*");
    }

    private static (string Path, long Expires, long Max, string? Type, string? Signature) Parse(string url)
    {
        var parts = url.Split('?', 2);
        var query = parts[1].Split('&')
            .Select(pair => pair.Split('=', 2))
            .ToDictionary(pair => pair[0], pair => Uri.UnescapeDataString(pair[1]), StringComparer.Ordinal);

        return (
            parts[0],
            long.Parse(query["expires"], System.Globalization.CultureInfo.InvariantCulture),
            query.TryGetValue("max", out var max) ? long.Parse(max, System.Globalization.CultureInfo.InvariantCulture) : 0,
            query.GetValueOrDefault("type"),
            query.GetValueOrDefault("signature"));
    }

    private sealed class StepClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now = _now.Add(by);
    }
}
