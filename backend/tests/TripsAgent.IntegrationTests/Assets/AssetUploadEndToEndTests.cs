using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TripsAgent.Application.Assets;
using TripsAgent.Application.Identity;
using TripsAgent.Contracts.Assets;
using TripsAgent.Domain.Common;
using TripsAgent.Domain.Identity;
using TripsAgent.Domain.Tenancy;
using TripsAgent.Infrastructure.Assets;
using TripsAgent.Infrastructure.Storage;
using TripsAgent.IntegrationTests.Persistence;

namespace TripsAgent.IntegrationTests.Assets;

/// <summary>
/// The upload flow over HTTP, the way the console will drive it: ask for a URL, PUT the file to it
/// with no credentials but the signature, complete, and read it back once the Worker has run.
/// </summary>
/// <remarks>
/// The Worker itself is not hosted here — the API never runs jobs — so its one run is made
/// directly, with the same handler, scanner and image processor the Worker registers.
/// </remarks>
[Collection(PostgresCollection.Name)]
public sealed class AssetUploadEndToEndTests : IAsyncLifetime, IDisposable
{
    private readonly PostgresFixture _postgres;
    private readonly string _storageRoot = Path.Combine(Path.GetTempPath(), $"asset-e2e-{Guid.NewGuid():N}");

    private (string Key, string Value)[] _overrides = [];
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _api = null!;
    private HttpClient _browser = null!;
    private string _database = string.Empty;

    public AssetUploadEndToEndTests(PostgresFixture postgres) => _postgres = postgres;

    public async Task InitializeAsync()
    {
        // A unique database per test instance; see RegistrationEndToEndTests for why not a fixed name.
        _database = $"asset_e2e_{Guid.NewGuid():N}";

        await using (var setup = await _postgres.CreateEmptyDatabaseAsync(_database))
        {
            await setup.Database.MigrateAsync();
        }

        Guid agencyId;
        var tenancy = TestTenancy.None();

        await using (var seed = _postgres.Connect(_database, tenancy.Tenant, tenancy.Scope))
        {
            using var _ = tenancy.Scope.Enter("test setup — the agency uploading");

            var agency = Agency.RegisterPrincipal("Lagos Travel Limited", "lagos-travel", "NG", "NGN", "Africa/Lagos");
            seed.Agencies.Add(agency);
            await seed.SaveChangesAsync();
            agencyId = agency.Id;
        }

        // Environment variables, because a configuration source added through the factory loses to
        // appsettings.Development.json. See RegistrationEndToEndTests.
        _overrides =
        [
            ("ConnectionStrings__Postgres", _postgres.ConnectionStringFor(_database, asApplicationRole: true)),
            ("ConnectionStrings__PostgresAdmin", _postgres.ConnectionStringFor(_database, asApplicationRole: false)),
            ("Storage__LocalRoot", _storageRoot),
        ];

        foreach (var (key, value) in _overrides)
        {
            Environment.SetEnvironmentVariable(key, value);
        }

        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(host => host.UseEnvironment("Development"));

        _api = _factory.CreateClient();
        _browser = _factory.CreateClient();

        // A real token from the real issuer, for a user of that agency. The tenant middleware reads
        // only the claims, so the user row itself is not needed.
        var issuer = _factory.Services.GetRequiredService<IAccessTokenIssuer>();
        var user = User.ForAgency(agencyId, "owner@lagos-travel.test", "argon2-hash-not-used", "Ada", "Obi");
        var token = issuer.Issue(user, ["Owner"], [], agencyId);

        _api.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Value);
    }

    [Fact]
    public async Task A_photo_goes_straight_to_storage_and_is_served_only_once_it_is_scanned()
    {
        var jpeg = AssetPipelineTests.Jpeg(400, 200);

        var upload = await RequestUploadAsync("ProductMedia", "beach.jpg", jpeg.Length, "image/jpeg");
        upload.Method.Should().Be("PUT");

        using (var put = await PutAsync(upload.UploadUrl, jpeg, upload.Headers["Content-Type"]))
        {
            put.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        using (var again = await PutAsync(upload.UploadUrl, jpeg, upload.Headers["Content-Type"]))
        {
            again.StatusCode.Should().Be(HttpStatusCode.Conflict, "an upload URL writes once, so a scanned file cannot be swapped");
        }

        using var complete = await _api.PostAsync(new Uri($"/api/v1/assets/{upload.AssetId}/complete", UriKind.Relative), null);
        complete.StatusCode.Should().Be(HttpStatusCode.Accepted);

        var accepted = await complete.Content.ReadFromJsonAsync<AssetResponse>();
        accepted!.Status.Should().Be("Uploaded");
        accepted.ContentType.Should().Be(MediaTypes.Jpeg, "established by sniffing the stored bytes");

        (await GetAssetAsync(upload.AssetId)).Links.Should().BeEmpty("it has not been scanned");

        await RunWorkerAsync(upload.AssetId);

        var ready = await GetAssetAsync(upload.AssetId);
        ready.Status.Should().Be("Ready");
        ready.ScanStatus.Should().Be("Clean");
        ready.Links.Select(l => l.Kind).Should().Equal("Original", "Thumbnail");

        using var image = await _browser.GetAsync(new Uri(ready.Links[0].Url, UriKind.Relative));
        image.StatusCode.Should().Be(HttpStatusCode.OK);
        image.Content.Headers.ContentType!.MediaType.Should().Be(MediaTypes.Webp);
        image.Headers.GetValues("X-Content-Type-Options").Should().Contain("nosniff");
        FileSignature.Detect(await image.Content.ReadAsByteArrayAsync()).Should().Be(MediaTypes.Webp);
    }

    [Fact]
    public async Task An_upload_url_accepts_only_what_it_was_signed_for()
    {
        var png = new byte[1024];
        var upload = await RequestUploadAsync("AgencyLogo", "logo.png", png.Length, "image/png");

        var widened = upload.UploadUrl.Replace("max=2097152", "max=20971520", StringComparison.Ordinal);
        widened.Should().NotBe(upload.UploadUrl);

        using (var tampered = await PutAsync(widened, png, "image/png"))
        {
            tampered.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }

        using (var otherType = await PutAsync(upload.UploadUrl, png, "text/html"))
        {
            otherType.StatusCode.Should().Be(HttpStatusCode.Forbidden, "the signature covers the Content-Type");
        }

        using (var oversized = await PutAsync(upload.UploadUrl, new byte[upload.MaxSizeBytes + 1], "image/png"))
        {
            oversized.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
        }
    }

    [Fact]
    public async Task A_forged_download_link_is_refused()
    {
        var jpeg = AssetPipelineTests.Jpeg(64, 64);
        var upload = await RequestUploadAsync("ProductMedia", "beach.jpg", jpeg.Length, "image/jpeg");

        using (await PutAsync(upload.UploadUrl, jpeg, "image/jpeg"))
        {
        }

        using (await _api.PostAsync(new Uri($"/api/v1/assets/{upload.AssetId}/complete", UriKind.Relative), null))
        {
        }

        await RunWorkerAsync(upload.AssetId);

        var link = (await GetAssetAsync(upload.AssetId)).Links[0].Url;
        var forged = link[..link.IndexOf("signature=", StringComparison.Ordinal)] + "signature=AAAA";

        using var response = await _browser.GetAsync(new Uri(forged, UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Asking_for_an_upload_needs_a_signed_in_agency()
    {
        using var response = await _browser.PostAsJsonAsync(
            new Uri("/api/v1/assets/uploads", UriKind.Relative),
            new RequestAssetUploadRequest("ProductMedia", "beach.jpg", 1_000, "image/jpeg"));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task The_limits_describe_every_purpose()
    {
        var limits = await _api.GetFromJsonAsync<AssetUploadLimitsResponse>(new Uri("/api/v1/assets/limits", UriKind.Relative));

        limits!.Purposes.Select(p => p.Purpose).Should().BeEquivalentTo(Enum.GetNames<Domain.Assets.AssetPurpose>());
        limits.Purposes.Single(p => p.Purpose == "AgencyLogo").AllowedContentTypes.Should().NotContain(MediaTypes.Pdf);
    }

    private async Task<AssetUploadResponse> RequestUploadAsync(string purpose, string fileName, long sizeBytes, string contentType)
    {
        using var response = await _api.PostAsJsonAsync(
            new Uri("/api/v1/assets/uploads", UriKind.Relative),
            new RequestAssetUploadRequest(purpose, fileName, sizeBytes, contentType));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<AssetUploadResponse>())!;
    }

    /// <summary>The browser's PUT: no Authorization header, only the signed URL.</summary>
    private async Task<HttpResponseMessage> PutAsync(string url, byte[] content, string contentType)
    {
        using var body = new ByteArrayContent(content);
        body.Headers.ContentType = new MediaTypeHeaderValue(contentType);

        return await _browser.PutAsync(new Uri(url, UriKind.Relative), body);
    }

    private async Task<AssetResponse> GetAssetAsync(Guid assetId) =>
        (await _api.GetFromJsonAsync<AssetResponse>(new Uri($"/api/v1/assets/{assetId}", UriKind.Relative)))!;

    /// <summary>One Worker run, with what AddAssetProcessing registers in the Worker.</summary>
    private async Task RunWorkerAsync(Guid assetId)
    {
        var tenancy = TestTenancy.None();
        await using var db = _postgres.Connect(_database, tenancy.Tenant, tenancy.Scope);

        var handler = new ProcessAssetHandler(
            db,
            _factory.Services.GetRequiredService<LocalFileBlobStorage>(),
            new EicarTestVirusScanner(),
            new SkiaImageProcessor(),
            new NoDispatch(),
            tenancy.Scope,
            TimeProvider.System,
            NullLogger<ProcessAssetHandler>.Instance);

        await handler.ProcessAsync(assetId);
    }

    public async Task DisposeAsync()
    {
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }

        foreach (var (key, _) in _overrides)
        {
            Environment.SetEnvironmentVariable(key, null);
        }

        if (Directory.Exists(_storageRoot))
        {
            Directory.Delete(_storageRoot, recursive: true);
        }
    }

    /// <summary>See the note on <c>RegistrationEndToEndTests.Dispose</c>: CA1001 needs this.</summary>
    public void Dispose()
    {
        _api?.Dispose();
        _browser?.Dispose();
        _api = null!;
        _browser = null!;
    }

    private sealed class NoDispatch : IAssetPipelineDispatcher
    {
        public Task EnqueueAsync(Guid assetId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
