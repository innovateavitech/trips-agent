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
/// Without a real virus scanner the asset pipeline is disabled — and only the pipeline. The Worker
/// still starts, because it also runs the payment and ledger jobs.
/// </summary>
public class VirusScannerRegistrationTests
{
    [Theory]
    [InlineData("Development")]
    [InlineData("Staging")]
    [InlineData("Production")]
    public void With_no_scanner_configured_the_pipeline_is_disabled_and_nothing_throws(string environment)
    {
        using var provider = Register(scanner: null, environment).BuildServiceProvider();

        var status = provider.GetRequiredService<AssetPipelineStatus>();
        status.IsEnabled.Should().BeFalse();
        status.DisabledReason.Should().Contain("No virus scanner is configured");
        provider.GetRequiredService<IVirusScanner>().Should().BeOfType<UnavailableVirusScanner>();
    }

    [Theory]
    [InlineData("Staging")]
    [InlineData("Production")]
    public void The_development_scanner_is_never_used_outside_development(string environment)
    {
        using var provider = Register(AssetProcessingRegistration.EicarTestOnly, environment).BuildServiceProvider();

        var status = provider.GetRequiredService<AssetPipelineStatus>();
        status.IsEnabled.Should().BeFalse();
        status.DisabledReason.Should().Contain(environment);
        provider.GetRequiredService<IVirusScanner>().Should().BeOfType<UnavailableVirusScanner>();
    }

    [Fact]
    public void The_development_scanner_is_allowed_in_development()
    {
        using var provider = Register(AssetProcessingRegistration.EicarTestOnly, "Development").BuildServiceProvider();

        provider.GetRequiredService<AssetPipelineStatus>().IsEnabled.Should().BeTrue();
        provider.GetRequiredService<IVirusScanner>().Should().BeOfType<EicarTestVirusScanner>();
    }

    [Fact]
    public void An_unknown_scanner_name_disables_the_pipeline_and_names_the_typo()
    {
        using var provider = Register("clamav-typo", "Production").BuildServiceProvider();

        var status = provider.GetRequiredService<AssetPipelineStatus>();
        status.IsEnabled.Should().BeFalse();
        status.DisabledReason.Should().Contain("clamav-typo");
    }

    [Fact]
    public async Task With_no_real_scanner_nothing_can_ever_come_back_clean()
    {
        var scanner = new UnavailableVirusScanner("no scanner configured");

        (await scanner.ScanAsync("%PDF-1.7 an ordinary document"u8.ToArray()))
            .Should().BeOfType<VirusScanResult.Unavailable>();
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Staging")]
    [InlineData("Production")]
    public void ClamAV_with_a_host_switches_the_pipeline_on_in_every_environment(string environment)
    {
        using var provider = Register(
                AssetProcessingRegistration.ClamAv,
                environment,
                new() { ["Assets:ClamAv:Host"] = "clamav.internal", ["Assets:ClamAv:Port"] = "3310" })
            .BuildServiceProvider();

        provider.GetRequiredService<AssetPipelineStatus>().IsEnabled.Should().BeTrue();
        provider.GetRequiredService<IVirusScanner>().Should().BeOfType<ClamAvVirusScanner>()
            .Which.Name.Should().Be("clamav");
    }

    [Fact]
    public void ClamAV_is_chosen_whatever_the_case_it_is_written_in()
    {
        using var provider = Register("clamav", "Production", new() { ["Assets:ClamAv:Host"] = "clamav.internal" })
            .BuildServiceProvider();

        provider.GetRequiredService<IVirusScanner>().Should().BeOfType<ClamAvVirusScanner>();
    }

    [Fact]
    public void ClamAV_without_a_host_leaves_the_pipeline_off_and_says_what_is_missing()
    {
        using var provider = Register(AssetProcessingRegistration.ClamAv, "Production").BuildServiceProvider();

        var status = provider.GetRequiredService<AssetPipelineStatus>();
        status.IsEnabled.Should().BeFalse();
        status.DisabledReason.Should().Contain("Assets:ClamAv:Host");
        provider.GetRequiredService<IVirusScanner>().Should().BeOfType<UnavailableVirusScanner>();
    }

    private static ServiceCollection Register(
        string? scanner,
        string environment,
        Dictionary<string, string?>? settings = null)
    {
        var values = new Dictionary<string, string?>(settings ?? [])
        {
            [AssetProcessingRegistration.VirusScannerSetting] = scanner,
        };

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
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

/// <summary>
/// A disabled pipeline skips its jobs rather than failing them into hours of retries.
/// </summary>
public class AssetPipelineJobTests
{
    [Fact]
    public async Task A_disabled_pipeline_never_calls_the_processor()
    {
        var processor = new CountingProcessor();

        await new AssetProcessingJob(processor, AssetPipelineStatus.Disabled("no scanner")).RunAsync(Guid.NewGuid(), CancellationToken.None);
        var swept = await new AssetSweepJob(processor, AssetPipelineStatus.Disabled("no scanner")).RunAsync(CancellationToken.None);

        processor.Processed.Should().Be(0);
        processor.Swept.Should().Be(0);
        swept.Should().Be(0);
    }

    [Fact]
    public async Task An_enabled_pipeline_runs_both_jobs()
    {
        var processor = new CountingProcessor();

        await new AssetProcessingJob(processor, AssetPipelineStatus.Enabled).RunAsync(Guid.NewGuid(), CancellationToken.None);
        await new AssetSweepJob(processor, AssetPipelineStatus.Enabled).RunAsync(CancellationToken.None);

        processor.Processed.Should().Be(1);
        processor.Swept.Should().Be(1);
    }

    private sealed class CountingProcessor : IAssetProcessor
    {
        public int Processed { get; private set; }

        public int Swept { get; private set; }

        public Task ProcessAsync(Guid assetId, CancellationToken cancellationToken = default)
        {
            Processed++;
            return Task.CompletedTask;
        }

        public Task<int> SweepAsync(CancellationToken cancellationToken = default)
        {
            Swept++;
            return Task.FromResult(0);
        }
    }
}
