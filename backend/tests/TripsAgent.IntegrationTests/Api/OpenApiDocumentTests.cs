using System.Net;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using TripsAgent.IntegrationTests.Persistence;

namespace TripsAgent.IntegrationTests.Api;

/// <summary>
/// Keeps the committed OpenAPI document — the one the frontend's typed client is generated from —
/// identical to what the API actually serves.
/// </summary>
/// <remarks>
/// <para>
/// The frontend never talks to the C# contracts directly. <c>pnpm generate:api</c> turns
/// <c>frontend/packages/api-client/openapi.json</c> into TypeScript types, and every screen is
/// typed against those. If a contract changes and nobody regenerates, the frontend compiles
/// happily against a shape the API no longer returns, and the first sign is a blank screen in
/// production. This test is what turns that into a red build instead.
/// </para>
/// <para>
/// Set <c>TRIPS_UPDATE_OPENAPI=1</c> to rewrite the file rather than compare against it. That is
/// what <c>pnpm generate:api</c> does; nobody should need to set it by hand.
/// </para>
/// </remarks>
[Collection(PostgresCollection.Name)]
public sealed class OpenApiDocumentTests : IAsyncLifetime, IDisposable
{
    /// <summary>When set to <c>1</c>, the test writes the live document instead of comparing.</summary>
    public const string UpdateVariable = "TRIPS_UPDATE_OPENAPI";

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,

        // Keep "—" and "₦" readable in the committed file rather than as \u escapes. The file is
        // written to disk for a code generator, never embedded in HTML, so relaxed escaping is safe.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly PostgresFixture _postgres;

    private (string Key, string Value)[] _overrides = [];
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _api = null!;

    public OpenApiDocumentTests(PostgresFixture postgres) => _postgres = postgres;

    public async Task InitializeAsync()
    {
        // A database of its own, like every other WebApplicationFactory test here. Serving the
        // document does not read the database, but the host still starts against one, and pointing
        // it at appsettings.Development.json's localhost would depend on whatever that machine has.
        var database = $"openapi_{Guid.NewGuid():N}";

        await using (var setup = await _postgres.CreateEmptyDatabaseAsync(database))
        {
            await setup.Database.MigrateAsync();
        }

        // The API runs as the role row-level security polices and migrates as the owner, the same
        // split as production (ADR-0006) and as RegistrationEndToEndTests.
        var connectionString = _postgres.ConnectionStringFor(database, asApplicationRole: true);
        var adminConnectionString = _postgres.ConnectionStringFor(database, asApplicationRole: false);

        // Environment variables, because a configuration source added through the factory loses
        // to appsettings.Development.json. See RegistrationEndToEndTests.
        _overrides =
        [
            ("ConnectionStrings__Postgres", connectionString),
            ("ConnectionStrings__PostgresAdmin", adminConnectionString),
        ];

        foreach (var (key, value) in _overrides)
        {
            Environment.SetEnvironmentVariable(key, value);
        }

        // Development, because that is the only environment Program.cs maps the document in.
        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(host => host.UseEnvironment("Development"));

        _api = _factory.CreateClient();
    }

    [Fact]
    public async Task The_committed_document_matches_the_one_the_api_serves()
    {
        var live = await FetchLiveDocumentAsync();
        var path = CommittedDocumentPath();

        if (Environment.GetEnvironmentVariable(UpdateVariable) == "1")
        {
            await File.WriteAllTextAsync(path, live.ToJsonString(WriteOptions) + "\n");
            return;
        }

        File.Exists(path).Should().BeTrue(
            $"the frontend's API client is generated from {path}; run `pnpm generate:api` from frontend/ to create it");

        var committed = JsonNode.Parse(await File.ReadAllTextAsync(path));

        // Compared as JSON, not as text, so a reformatted file is not a failure — only a changed
        // contract is.
        JsonNode.DeepEquals(live, committed).Should().BeTrue(
            "an API contract changed without regenerating the frontend client; run `pnpm generate:api` "
            + "from frontend/ and commit openapi.json together with src/generated/schema.ts");
    }

    [Fact]
    public async Task The_document_describes_the_endpoints_the_agent_console_session_depends_on()
    {
        var live = await FetchLiveDocumentAsync();
        var paths = live["paths"]!.AsObject();

        // The console's sign-in, silent refresh, sign-out and session bootstrap are written against
        // these four operations. If one disappears from the document, the generated client loses
        // its type and the console stops compiling — this names the reason directly instead.
        paths.Should().ContainKey("/api/v1/auth/login");
        paths.Should().ContainKey("/api/v1/auth/refresh");
        paths.Should().ContainKey("/api/v1/auth/logout");
        paths.Should().ContainKey("/api/v1/auth/me");

        var schemas = live["components"]!["schemas"]!.AsObject();
        schemas.Should().ContainKey("TokenPairResponse");
        schemas.Should().ContainKey("CurrentUserResponse");
    }

    private async Task<JsonNode> FetchLiveDocumentAsync()
    {
        using var response = await _api.GetAsync(new Uri("/openapi/v1.json", UriKind.Relative));
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        return JsonNode.Parse(await response.Content.ReadAsStringAsync())
            ?? throw new InvalidOperationException("The API served an empty OpenAPI document.");
    }

    /// <summary>
    /// <c>frontend/packages/api-client/openapi.json</c>, found by walking up from the test binary
    /// to the repository root — the directory holding both <c>backend/</c> and <c>frontend/</c>.
    /// </summary>
    private static string CommittedDocumentPath()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "backend", "TripsAgent.slnx"))
                && Directory.Exists(Path.Combine(directory.FullName, "frontend")))
            {
                return Path.Combine(directory.FullName, "frontend", "packages", "api-client", "openapi.json");
            }
        }

        throw new InvalidOperationException(
            $"Could not find the repository root above {AppContext.BaseDirectory}.");
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
    }

    /// <summary>See the note on <c>RegistrationEndToEndTests.Dispose</c>: CA1001 needs this.</summary>
    public void Dispose()
    {
        _api?.Dispose();
        _api = null!;
    }
}
