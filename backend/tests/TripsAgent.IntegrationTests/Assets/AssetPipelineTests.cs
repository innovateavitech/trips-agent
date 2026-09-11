using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using SkiaSharp;
using TripsAgent.Application.Assets;
using TripsAgent.Domain.Assets;
using TripsAgent.Domain.Common;
using TripsAgent.Domain.Tenancy;
using TripsAgent.Infrastructure.Assets;
using TripsAgent.Infrastructure.Identity;
using TripsAgent.Infrastructure.Persistence;
using TripsAgent.Infrastructure.Storage;
using TripsAgent.IntegrationTests.Identity;
using TripsAgent.IntegrationTests.Persistence;

namespace TripsAgent.IntegrationTests.Assets;

/// <summary>
/// The asset pipeline end to end against real PostgreSQL, as the policed application role, with
/// real files on disk and the real image processor. Issue #18.
/// </summary>
/// <remarks>
/// The browser's direct upload is played by writing straight to storage at the reserved key —
/// which is all a presigned PUT does. The HTTP half of that is in
/// <see cref="AssetUploadEndToEndTests"/>.
/// </remarks>
[Collection(PostgresCollection.Name)]
public sealed class AssetPipelineTests : IDisposable
{
    private readonly PostgresFixture _postgres;
    private readonly string _storageRoot = Path.Combine(Path.GetTempPath(), $"asset-tests-{Guid.NewGuid():N}");

    public AssetPipelineTests(PostgresFixture postgres) => _postgres = postgres;

    public void Dispose()
    {
        if (Directory.Exists(_storageRoot))
        {
            Directory.Delete(_storageRoot, recursive: true);
        }
    }

    // ------------------------------------------------------------------ the happy paths

    [Fact]
    public async Task An_image_goes_from_upload_to_servable_webp_renditions()
    {
        var world = await WorldAsync();

        var assetId = await world.UploadAsync(world.AgencyA, AssetPurpose.ProductMedia, "beach.jpg", Jpeg(400, 200), MediaTypes.Jpeg);
        var uploadKey = AssetRules.UploadKey(world.AgencyA, assetId);

        world.Enqueued.Should().Equal(assetId);

        await world.ProcessAsync(assetId);

        var asset = await world.ReloadAsync(assetId);
        asset.Status.Should().Be(AssetStatus.Ready);
        asset.ScanStatus.Should().Be(AssetScanStatus.Clean);
        asset.ContentType.Should().Be(MediaTypes.Webp);
        (asset.Width, asset.Height).Should().Be((400, 200));
        asset.Checksum.Should().MatchRegex("^[0-9a-f]{64}$");

        // The asset now points at the metadata-free rendition, and the raw upload is gone.
        asset.StorageKey.Should().Be(AssetRules.VariantKey(world.AgencyA, assetId, AssetVariantKind.Original));
        (await world.Storage.ExistsAsync(uploadKey)).Should().BeFalse("the raw upload is the file that carries EXIF");

        var view = await world.GetAsync(world.AgencyA, assetId);
        view!.Links.Select(l => l.Kind).Should().Equal(AssetVariantKind.Original, AssetVariantKind.Thumbnail);
        view.Links.Should().OnlyContain(l => l.ContentType == MediaTypes.Webp);

        foreach (var variant in await world.VariantsAsync(assetId))
        {
            await using var stored = await world.Storage.OpenReadAsync(variant.StorageKey);
            using var bitmap = SKBitmap.Decode(stored);
            (bitmap.Width, bitmap.Height).Should().Be((variant.Width, variant.Height));
        }
    }

    [Fact]
    public async Task A_pdf_is_served_from_a_scanned_copy_not_from_the_upload_key()
    {
        var world = await WorldAsync();

        var assetId = await world.UploadAsync(world.AgencyA, AssetPurpose.Attachment, "invoice.pdf", Pdf, MediaTypes.Pdf);
        await world.ProcessAsync(assetId);

        var asset = await world.ReloadAsync(assetId);
        asset.Status.Should().Be(AssetStatus.Ready);

        // The upload key stays writable through its presigned URL until the window closes, so the
        // served key must be a different one that nothing can write to.
        asset.StorageKey.Should().Be(AssetRules.VerbatimKey(world.AgencyA, assetId, MediaTypes.Pdf));
        (await world.Storage.ExistsAsync(AssetRules.UploadKey(world.AgencyA, assetId))).Should().BeFalse();
        (await world.VariantsAsync(assetId)).Should().BeEmpty("a PDF has no renditions");

        var view = await world.GetAsync(world.AgencyA, assetId);
        view!.Links.Should().ContainSingle().Which.ContentType.Should().Be(MediaTypes.Pdf);
    }

    // ------------------------------------------------------------------ checking what arrived

    [Fact]
    public async Task The_file_type_comes_from_its_bytes_not_its_name()
    {
        var world = await WorldAsync();

        // MZ — a Windows executable, named and declared as a photo.
        var reserved = await world.RequestAsync(world.AgencyA, AssetPurpose.ProductMedia, "beach.jpg", Executable.Length, MediaTypes.Jpeg);
        await world.PutAsync(reserved.Asset.StorageKey, Executable);

        var outcome = await world.CompleteAsync(world.AgencyA, reserved.Asset.Id);

        outcome.Should().BeOfType<CompleteAssetUploadOutcome.Rejected>()
            .Which.Reason.Should().Contain("contents do not match");
        (await world.ReloadAsync(reserved.Asset.Id)).Status.Should().Be(AssetStatus.Failed);
        (await world.Storage.ExistsAsync(reserved.Asset.StorageKey)).Should().BeFalse("a refused file is deleted");
        world.Enqueued.Should().BeEmpty();
    }

    [Fact]
    public async Task A_file_over_the_limit_is_refused_when_it_arrives_whatever_was_declared()
    {
        var world = await WorldAsync();

        // Declared as a small logo, then something bigger sent — Content-Length is the client's word.
        var reserved = await world.RequestAsync(world.AgencyA, AssetPurpose.AgencyLogo, "logo.png", 10_000, MediaTypes.Png);

        var oversized = new byte[AssetRules.MaxSizeBytes(AssetPurpose.AgencyLogo) + 1];
        Png(8, 8).CopyTo(oversized, 0);
        await world.PutAsync(reserved.Asset.StorageKey, oversized);

        var outcome = await world.CompleteAsync(world.AgencyA, reserved.Asset.Id);

        outcome.Should().BeOfType<CompleteAssetUploadOutcome.Rejected>().Which.Reason.Should().Contain("2MB");
        (await world.Storage.ExistsAsync(reserved.Asset.StorageKey)).Should().BeFalse();
    }

    [Fact]
    public async Task A_hopeless_upload_is_refused_before_anything_is_reserved()
    {
        var world = await WorldAsync();

        var tooBig = await world.RequestOutcomeAsync(world.AgencyA, AssetPurpose.AgencyLogo, "logo.png", 50L * 1024 * 1024, MediaTypes.Png);
        var wrongType = await world.RequestOutcomeAsync(world.AgencyA, AssetPurpose.AgencyLogo, "logo.pdf", 1_000, MediaTypes.Pdf);
        var empty = await world.RequestOutcomeAsync(world.AgencyA, AssetPurpose.ProductMedia, "empty.jpg", 0, MediaTypes.Jpeg);

        tooBig.Should().BeOfType<RequestAssetUploadOutcome.Rejected>().Which.Reason.Should().Contain("2MB");
        wrongType.Should().BeOfType<RequestAssetUploadOutcome.Rejected>();
        empty.Should().BeOfType<RequestAssetUploadOutcome.Rejected>().Which.Reason.Should().Contain("empty");

        (await world.CountAssetsAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Completing_before_the_file_arrives_says_so_and_after_the_window_gives_up()
    {
        var world = await WorldAsync();

        var reserved = await world.RequestAsync(world.AgencyA, AssetPurpose.ProductMedia, "beach.jpg", 1_000, MediaTypes.Jpeg);

        (await world.CompleteAsync(world.AgencyA, reserved.Asset.Id)).Should().BeOfType<CompleteAssetUploadOutcome.NotArrived>();

        world.Clock.Advance(AssetRules.UploadWindow + TimeSpan.FromSeconds(1));

        (await world.CompleteAsync(world.AgencyA, reserved.Asset.Id)).Should().BeOfType<CompleteAssetUploadOutcome.Rejected>()
            .Which.Reason.Should().Contain("window closed");
    }

    [Fact]
    public async Task The_bytes_are_checked_again_when_they_are_processed()
    {
        var world = await WorldAsync();

        var assetId = await world.UploadAsync(world.AgencyA, AssetPurpose.ProductMedia, "beach.jpg", Jpeg(64, 64), MediaTypes.Jpeg);

        // The presigned URL is still open, so the file can change between "complete" and the
        // Worker picking it up. What the Worker checks is what is there now.
        await world.PutAsync(AssetRules.UploadKey(world.AgencyA, assetId), Executable);

        await world.ProcessAsync(assetId);

        var asset = await world.ReloadAsync(assetId);
        asset.Status.Should().Be(AssetStatus.Failed);
        asset.IsServable.Should().BeFalse();
    }

    // ------------------------------------------------------------------ scanning

    [Fact]
    public async Task An_infected_file_is_quarantined_and_its_bytes_deleted()
    {
        var world = await WorldAsync();

        var assetId = await world.UploadAsync(world.AgencyA, AssetPurpose.Attachment, "invoice.pdf", EicarPdf, MediaTypes.Pdf);
        await world.ProcessAsync(assetId);

        var asset = await world.ReloadAsync(assetId);
        asset.Status.Should().Be(AssetStatus.Quarantined);
        asset.ScanStatus.Should().Be(AssetScanStatus.Infected);
        asset.ScanSignature.Should().Be(EicarTestVirusScanner.Signature);
        (await world.Storage.ExistsAsync(asset.StorageKey)).Should().BeFalse("an infected file nobody can reach is still in our bucket");
    }

    [Fact]
    public async Task While_the_scanner_is_down_the_asset_stays_unservable_and_a_retry_finishes_it()
    {
        var world = await WorldAsync();
        var assetId = await world.UploadAsync(world.AgencyA, AssetPurpose.ProductMedia, "beach.jpg", Jpeg(64, 64), MediaTypes.Jpeg);

        world.Scanner.Down = true;

        // Thrown, so the job runner retries — never quietly treated as clean.
        var act = () => world.ProcessAsync(assetId);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*could not scan*");

        var waiting = await world.ReloadAsync(assetId);
        waiting.ScanStatus.Should().Be(AssetScanStatus.Unscannable);
        waiting.IsServable.Should().BeFalse();
        (await world.GetAsync(world.AgencyA, assetId))!.Links.Should().BeEmpty();

        world.Scanner.Down = false;
        await world.ProcessAsync(assetId);

        (await world.ReloadAsync(assetId)).Status.Should().Be(AssetStatus.Ready);
    }

    // ------------------------------------------------------------------ the serving gate

    [Fact]
    public async Task An_asset_that_is_not_scanned_clean_is_never_served()
    {
        var world = await WorldAsync();

        // Pending: uploaded and accepted, not yet scanned.
        var pending = await world.UploadAsync(world.AgencyA, AssetPurpose.ProductMedia, "pending.jpg", Jpeg(64, 64), MediaTypes.Jpeg);

        // Infected.
        var infected = await world.UploadAsync(world.AgencyA, AssetPurpose.Attachment, "bad.pdf", EicarPdf, MediaTypes.Pdf);
        await world.ProcessAsync(infected);

        // Unscannable.
        var unscannable = await world.UploadAsync(world.AgencyA, AssetPurpose.ProductMedia, "later.jpg", Jpeg(64, 64), MediaTypes.Jpeg);
        world.Scanner.Down = true;
        await FluentActions.Awaiting(() => world.ProcessAsync(unscannable)).Should().ThrowAsync<InvalidOperationException>();
        world.Scanner.Down = false;

        foreach (var (assetId, expected) in new[]
                 {
                     (pending, AssetScanStatus.Pending),
                     (infected, AssetScanStatus.Infected),
                     (unscannable, AssetScanStatus.Unscannable),
                 })
        {
            var view = await world.GetAsync(world.AgencyA, assetId);

            view!.Asset.ScanStatus.Should().Be(expected);
            view.Links.Should().BeEmpty($"an asset whose scan status is {expected} must not be served");
        }
    }

    [Fact]
    public async Task The_database_refuses_an_asset_that_is_ready_without_being_scanned_clean()
    {
        var world = await WorldAsync();
        var assetId = await world.UploadAsync(world.AgencyA, AssetPurpose.ProductMedia, "beach.jpg", Jpeg(64, 64), MediaTypes.Jpeg);

        // As the schema owner, around every rule in the application: the CHECK constraint still holds.
        var act = () => world.AdminExecuteAsync($"UPDATE platform.assets SET status = 'Ready' WHERE id = '{assetId}'");

        (await act.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be(PostgresErrorCodes.CheckViolation);
    }

    // ------------------------------------------------------------------ tenancy

    [Fact]
    public async Task Another_agency_cannot_complete_or_read_an_asset()
    {
        var world = await WorldAsync();
        var assetId = await world.UploadAsync(world.AgencyA, AssetPurpose.ProductMedia, "beach.jpg", Jpeg(64, 64), MediaTypes.Jpeg);
        await world.ProcessAsync(assetId);

        (await world.GetAsync(world.AgencyB, assetId)).Should().BeNull();
        (await world.CompleteAsync(world.AgencyB, assetId)).Should().BeOfType<CompleteAssetUploadOutcome.NotFound>();
    }

    [Fact]
    public async Task Row_level_security_hides_assets_and_variants_even_without_the_filter()
    {
        var world = await WorldAsync();
        var assetId = await world.UploadAsync(world.AgencyA, AssetPurpose.ProductMedia, "beach.jpg", Jpeg(64, 64), MediaTypes.Jpeg);
        await world.ProcessAsync(assetId);

        await using var asB = world.ActingAs(world.AgencyB);
        await using var asA = world.ActingAs(world.AgencyA);

        // The EF filter switched off on purpose: this is PostgreSQL refusing, not EF.
        (await asB.Assets.IgnoreQueryFilters().CountAsync()).Should().Be(0);
        (await asB.AssetVariants.IgnoreQueryFilters().CountAsync()).Should().Be(0);

        (await asA.Assets.IgnoreQueryFilters().CountAsync()).Should().Be(1);
        (await asA.AssetVariants.IgnoreQueryFilters().CountAsync()).Should().BeGreaterThan(0);
    }

    // ------------------------------------------------------------------ running more than once

    [Fact]
    public async Task Processing_a_finished_asset_again_changes_nothing()
    {
        var world = await WorldAsync();
        var assetId = await world.UploadAsync(world.AgencyA, AssetPurpose.ProductMedia, "beach.jpg", Jpeg(64, 64), MediaTypes.Jpeg);

        await world.ProcessAsync(assetId);
        var first = await world.ReloadAsync(assetId);

        await world.ProcessAsync(assetId);

        var second = await world.ReloadAsync(assetId);
        second.StorageKey.Should().Be(first.StorageKey);
        second.ProcessedAt.Should().Be(first.ProcessedAt);
        (await world.VariantsAsync(assetId)).Should().HaveCount(1);
    }

    [Fact]
    public async Task A_run_that_finds_the_asset_claimed_by_another_steps_aside()
    {
        var world = await WorldAsync();
        var assetId = await world.UploadAsync(world.AgencyA, AssetPurpose.ProductMedia, "beach.jpg", Jpeg(64, 64), MediaTypes.Jpeg);

        await world.ClaimAsync(assetId);

        await world.ProcessAsync(assetId);

        var asset = await world.ReloadAsync(assetId);
        asset.Status.Should().Be(AssetStatus.Processing, "the other run still holds it");
        (await world.VariantsAsync(assetId)).Should().BeEmpty();
    }

    [Fact]
    public async Task The_sweep_gives_up_on_abandoned_uploads_and_re_enqueues_lost_ones()
    {
        var world = await WorldAsync();

        var abandoned = await world.RequestAsync(world.AgencyA, AssetPurpose.ProductMedia, "never.jpg", 1_000, MediaTypes.Jpeg);
        var lost = await world.UploadAsync(world.AgencyA, AssetPurpose.ProductMedia, "lost.jpg", Jpeg(64, 64), MediaTypes.Jpeg);
        world.Enqueued.Clear();

        // Inside the grace period nothing is touched: an upload may still be arriving.
        world.Clock.Advance(AssetRules.UploadWindow);
        (await world.SweepAsync()).Should().Be(0);

        world.Clock.Advance(AssetRules.StalledAfter + TimeSpan.FromMinutes(1));
        (await world.SweepAsync()).Should().Be(2);

        (await world.ReloadAsync(abandoned.Asset.Id)).Status.Should().Be(AssetStatus.Failed);
        world.Enqueued.Should().Equal(lost);
    }

    // ------------------------------------------------------------------ fixtures

    private static readonly byte[] Executable = [0x4D, 0x5A, 0x90, 0x00, 0x03, 0x00, 0x00, 0x00, 0x04, 0x00, 0x00, 0x00];

    private static readonly byte[] Pdf = "%PDF-1.7\nan invoice\n"u8.ToArray();

    private static readonly byte[] EicarPdf =
        [.. "%PDF-1.7\n"u8, .. Encoding.ASCII.GetBytes(EicarTestVirusScanner.TestString)];

    internal static byte[] Jpeg(int width, int height)
    {
        using var bitmap = new SKBitmap(width, height);
        bitmap.Erase(SKColors.Orange);
        using var encoded = bitmap.Encode(SKEncodedImageFormat.Jpeg, 90);
        return encoded.ToArray();
    }

    private static byte[] Png(int width, int height)
    {
        using var bitmap = new SKBitmap(width, height);
        bitmap.Erase(SKColors.Teal);
        using var encoded = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return encoded.ToArray();
    }

    private async Task<World> WorldAsync([CallerMemberName] string testName = "")
    {
        var name = $"assets_{testName.ToLowerInvariant()}";
        name = name[..Math.Min(name.Length, 60)];

        var clock = new ManualClock(new DateTimeOffset(2026, 9, 11, 9, 0, 0, TimeSpan.Zero));
        var tenancy = TestTenancy.None();

        await using (var setup = await _postgres.CreateEmptyDatabaseAsync(name, tenancy.Tenant, tenancy.Scope, clock))
        {
            await setup.Database.MigrateAsync();
        }

        Guid agencyA;
        Guid agencyB;

        await using (var seed = _postgres.Connect(name, tenancy.Tenant, tenancy.Scope, clock))
        {
            using var _ = tenancy.Scope.Enter("test setup — two agencies, as a seed script would");

            var a = Agency.RegisterPrincipal("Lagos Travel Limited", "lagos-travel", "NG", "NGN", "Africa/Lagos");
            var b = Agency.RegisterPrincipal("Abuja Tours Limited", "abuja-tours", "NG", "NGN", "Africa/Lagos");

            seed.Agencies.AddRange(a, b);
            await seed.SaveChangesAsync();

            agencyA = a.Id;
            agencyB = b.Id;
        }

        var hasher = new HmacTokenHasher(RandomNumberGenerator.GetBytes(HmacTokenHasher.MinimumKeyBytes));
        var storage = new LocalFileBlobStorage(
            new LocalBlobStorageOptions { RootPath = Path.Combine(_storageRoot, name) },
            new LocalBlobUrlSigner(() => hasher, clock));

        return new World(_postgres, name, clock, storage, agencyA, agencyB);
    }

    private sealed class World(
        PostgresFixture postgres,
        string database,
        ManualClock clock,
        LocalFileBlobStorage storage,
        Guid agencyA,
        Guid agencyB)
    {
        public ManualClock Clock { get; } = clock;

        public LocalFileBlobStorage Storage { get; } = storage;

        public Guid AgencyA { get; } = agencyA;

        public Guid AgencyB { get; } = agencyB;

        public SwitchableScanner Scanner { get; } = new();

        public RecordingDispatcher Dispatcher { get; } = new();

        public List<Guid> Enqueued => Dispatcher.Enqueued;

        /// <summary>A context acting as <paramref name="agencyId"/>, as the policed application role.</summary>
        public AppDbContext ActingAs(Guid agencyId)
        {
            var tenancy = TestTenancy.For(agencyId);
            return postgres.Connect(database, tenancy.Tenant, tenancy.Scope, Clock);
        }

        public async Task<RequestAssetUploadOutcome> RequestOutcomeAsync(
            Guid agencyId, AssetPurpose purpose, string fileName, long sizeBytes, string contentType)
        {
            var tenancy = TestTenancy.For(agencyId);
            await using var db = postgres.Connect(database, tenancy.Tenant, tenancy.Scope, Clock);

            return await new RequestAssetUploadHandler(db, Storage, tenancy.Tenant, Clock)
                .HandleAsync(purpose, fileName, sizeBytes, contentType);
        }

        public async Task<RequestAssetUploadOutcome.Reserved> RequestAsync(
            Guid agencyId, AssetPurpose purpose, string fileName, long sizeBytes, string contentType) =>
            (RequestAssetUploadOutcome.Reserved)await RequestOutcomeAsync(agencyId, purpose, fileName, sizeBytes, contentType);

        /// <summary>What the browser's presigned PUT does: bytes straight into storage at the reserved key.</summary>
        public async Task PutAsync(string key, byte[] content)
        {
            await Storage.DeleteAsync(key);
            await Storage.StoreAsync(new MemoryStream(content), key, "application/octet-stream");
        }

        public async Task<CompleteAssetUploadOutcome> CompleteAsync(Guid agencyId, Guid assetId)
        {
            await using var db = ActingAs(agencyId);
            return await new CompleteAssetUploadHandler(db, Storage, Dispatcher, Clock).HandleAsync(assetId);
        }

        /// <summary>Request, upload and complete, the way the console will. Returns the asset id.</summary>
        public async Task<Guid> UploadAsync(Guid agencyId, AssetPurpose purpose, string fileName, byte[] content, string contentType)
        {
            var reserved = await RequestAsync(agencyId, purpose, fileName, content.Length, contentType);
            await PutAsync(reserved.Asset.StorageKey, content);

            (await CompleteAsync(agencyId, reserved.Asset.Id)).Should().BeOfType<CompleteAssetUploadOutcome.Accepted>();

            return reserved.Asset.Id;
        }

        /// <summary>A Worker run: no tenant, the policed role, the real image processor.</summary>
        public async Task ProcessAsync(Guid assetId)
        {
            await using var worker = Worker(out var handler);
            await handler.ProcessAsync(assetId);
        }

        public async Task<int> SweepAsync()
        {
            await using var worker = Worker(out var handler);
            return await handler.SweepAsync();
        }

        /// <summary>Claims the asset as another run would, and leaves the claim in place.</summary>
        public async Task ClaimAsync(Guid assetId)
        {
            var tenancy = TestTenancy.None();
            await using var db = postgres.Connect(database, tenancy.Tenant, tenancy.Scope, Clock);
            using var _ = tenancy.Scope.Enter("test — another pipeline run claiming the asset");

            var asset = await db.Assets.SingleAsync(a => a.Id == assetId);
            asset.TryBeginProcessing(Clock.GetUtcNow()).Should().BeTrue();
            await db.SaveChangesAsync();
        }

        public async Task<AssetView?> GetAsync(Guid agencyId, Guid assetId)
        {
            await using var db = ActingAs(agencyId);
            return await new GetAssetHandler(db, new AssetDelivery(Storage, Clock)).HandleAsync(assetId);
        }

        public async Task<Asset> ReloadAsync(Guid assetId)
        {
            var tenancy = TestTenancy.None();
            await using var db = postgres.Connect(database, tenancy.Tenant, tenancy.Scope, Clock);
            using var _ = tenancy.Scope.Enter("test — reading the asset back");

            return await db.Assets.AsNoTracking().SingleAsync(a => a.Id == assetId);
        }

        public async Task<List<AssetVariant>> VariantsAsync(Guid assetId)
        {
            var tenancy = TestTenancy.None();
            await using var db = postgres.Connect(database, tenancy.Tenant, tenancy.Scope, Clock);
            using var _ = tenancy.Scope.Enter("test — reading the variants back");

            return await db.AssetVariants.AsNoTracking().Where(v => v.AssetId == assetId).ToListAsync();
        }

        public async Task<int> CountAssetsAsync()
        {
            var tenancy = TestTenancy.None();
            await using var db = postgres.Connect(database, tenancy.Tenant, tenancy.Scope, Clock);
            using var _ = tenancy.Scope.Enter("test — counting every agency's assets");

            return await db.Assets.CountAsync();
        }

        public async Task AdminExecuteAsync(string sql)
        {
            await using var connection = new NpgsqlConnection(postgres.ConnectionStringFor(database, asApplicationRole: false));
            await connection.OpenAsync();

            await using var command = new NpgsqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync();
        }

        private AppDbContext Worker(out ProcessAssetHandler handler)
        {
            var tenancy = TestTenancy.None();
            var db = postgres.Connect(database, tenancy.Tenant, tenancy.Scope, Clock);

            handler = new ProcessAssetHandler(
                db,
                Storage,
                Scanner,
                new SkiaImageProcessor(),
                Dispatcher,
                tenancy.Scope,
                Clock,
                NullLogger<ProcessAssetHandler>.Instance);

            return db;
        }
    }

    /// <summary>The development scanner, with a switch that makes it behave like a scanner that is down.</summary>
    private sealed class SwitchableScanner : IVirusScanner
    {
        private readonly EicarTestVirusScanner _inner = new();

        public bool Down { get; set; }

        public string Name => "switchable-test-scanner";

        public Task<VirusScanResult> ScanAsync(ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default) =>
            Down
                ? Task.FromResult<VirusScanResult>(new VirusScanResult.Unavailable("connection refused"))
                : _inner.ScanAsync(content, cancellationToken);
    }

    private sealed class RecordingDispatcher : IAssetPipelineDispatcher
    {
        public List<Guid> Enqueued { get; } = [];

        public Task EnqueueAsync(Guid assetId, CancellationToken cancellationToken = default)
        {
            Enqueued.Add(assetId);
            return Task.CompletedTask;
        }
    }
}
