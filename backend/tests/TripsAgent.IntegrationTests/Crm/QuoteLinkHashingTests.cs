using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using TripsAgent.Api.Crm;
using TripsAgent.Application.Crm;
using TripsAgent.Application.Identity;
using TripsAgent.Contracts.Crm;
using TripsAgent.Domain.Identity;
using TripsAgent.Domain.Storefront;
using TripsAgent.Domain.Tenancy;
using TripsAgent.Infrastructure.Persistence;
using TripsAgent.Infrastructure.Storefront;
using TripsAgent.IntegrationTests.Persistence;
using TripsAgent.IntegrationTests.Pricing;

namespace TripsAgent.IntegrationTests.Crm;

/// <summary>
/// What is kept of a quote's link, and what became of the links kept before it (issue 175).
/// </summary>
/// <remarks>
/// <para>
/// A quote link is a bearer token: whoever holds it reads and answers the quote. It used to be stored
/// in clear, so a leaked backup was a set of live links. The row now keeps only the keyed hash, and the
/// token is a signature over the quote's id — which is what still lets the console show the agent the
/// link. The customers' old links keep working, because the backfill hashed what was there.
/// </para>
/// <para>
/// Each test starts with the database at <c>AddQuoteLinkTokenHash</c>: the hash column added and the
/// plaintext one still in place, which is the state a running system is in when the backfill starts.
/// </para>
/// </remarks>
[Collection(PostgresCollection.Name)]
public sealed class QuoteLinkHashingTests : IClassFixture<RedisFixture>, IAsyncLifetime, IDisposable
{
    private const string Host = "lagos-travel.localhost";
    private const string Quotes = "/api/v1/crm/quotes";

    private static readonly string[] Everything = [PermissionCodes.CustomerView, PermissionCodes.CustomerEdit];

    private readonly PostgresFixture _postgres;
    private readonly RedisFixture _redis;

    private (string Key, string Value)[] _overrides = [];
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _api = null!;
    private string _database = string.Empty;
    private Guid _agencyId;
    private User _ada = null!;

    public QuoteLinkHashingTests(PostgresFixture postgres, RedisFixture redis)
    {
        _postgres = postgres;
        _redis = redis;
    }

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow);

    public async Task InitializeAsync()
    {
        _database = $"quote_links_{Guid.NewGuid():N}";

        var tenancy = TestTenancy.None();

        await using (var setup = await _postgres.CreateEmptyDatabaseAsync(_database, tenancy.Tenant, tenancy.Scope))
        {
            // Everything up to and including the migration that adds the hash column.
            await setup.GetService<IMigrator>().MigrateAsync(DatabaseMigrator.QuoteLinkHashMigration);
            await ReferenceDataSeeder.EnsureAsync(setup, tenancy.Scope);

            using var seeding = tenancy.Scope.Enter("test setup — one verified agency with a website");
            var template = await setup.SiteTemplates.FirstAsync();

            var agency = Agency.RegisterPrincipal("Lagos Travel Limited", "lagos-travel", "NG", "NGN", "Africa/Lagos");
            agency.MarkVerified(DateTimeOffset.UtcNow);

            var ada = User.ForAgency(agency.Id, "ada@lagos-travel.test", "not-a-real-hash", "Ada", "Obi");

            setup.Agencies.Add(agency);
            setup.Users.Add(ada);
            await setup.SaveChangesAsync();

            AddSiteWithAddress(setup, agency, template.Id, Host);
            await setup.SaveChangesAsync();

            (_agencyId, _ada) = (agency.Id, ada);
        }

        _overrides =
        [
            ("ConnectionStrings__Postgres", _postgres.ConnectionStringFor(_database, asApplicationRole: true)),
            ("ConnectionStrings__PostgresAdmin", _postgres.ConnectionStringFor(_database, asApplicationRole: false)),
            ("ConnectionStrings__Redis", _redis.ConnectionString),
        ];

        foreach (var (key, value) in _overrides)
        {
            Environment.SetEnvironmentVariable(key, value);
        }

        // The host map is cached by hostname, and each test rebuilds the same hostname in a database of
        // its own, so the map from the test before is retired first.
        await _redis.Connection.GetDatabase().StringIncrementAsync(RedisStorefrontHostCache.GenerationKey);

        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(host => host.UseEnvironment("Development"));

        _api = _factory.CreateClient();
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

    public void Dispose()
    {
        _api?.Dispose();
        _api = null!;
    }

    [Fact]
    public async Task A_link_sent_before_the_change_still_opens_its_quote()
    {
        var quoteId = await SendAQuoteAsync();
        var legacy = await StoreTheOldWayAsync(quoteId);

        await HashStoredLinksAsync();
        await MigrateTheRestAsync();

        using var response = await OpenPublicQuoteAsync(legacy);

        // The whole point of hashing rather than reissuing: the customer's link is untouched.
        response.StatusCode.Should().Be(
            HttpStatusCode.OK, "the customer holds a link minted before the change: {0}", await response.Content.ReadAsStringAsync());

        (await ColumnNamesAsync()).Should().NotContain("public_token", "nothing is left in clear");
        (await StoredHashAsync(quoteId)).Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task The_plaintext_column_is_not_dropped_while_a_link_is_still_in_clear()
    {
        var quoteId = await SendAQuoteAsync();
        await StoreTheOldWayAsync(quoteId);

        // Migrating without the backfill — `dotnet ef database update`, or a deploy that skipped a step.
        var migrate = async () => await MigrateTheRestAsync();

        var failure = (await migrate.Should().ThrowAsync<Exception>()).Which;

        Innermost(failure).Should().BeOfType<PostgresException>()
            .Which.MessageText.Should().Contain("still stored in clear");

        // And it changed nothing, so the next run still has the token to hash.
        (await ColumnNamesAsync()).Should().Contain("public_token");
        (await StoredHashAsync(quoteId)).Should().BeNull();
    }

    [Fact]
    public async Task A_quote_sent_now_keeps_only_the_hash_of_its_link()
    {
        await MigrateTheRestAsync();

        var quoteId = await SendAQuoteAsync();
        var token = TokenIn(await PublicUrlAsync(quoteId));

        var stored = await StoredHashAsync(quoteId);

        stored.Should().NotBeNull();
        stored.Should().NotBe(token, "what the row keeps is the hash, never the token");

        // The agent can still be shown the link when they open the quote again, and it opens the quote.
        (await PublicUrlAsync(quoteId)).Should().EndWith(token);

        using var response = await OpenPublicQuoteAsync(token);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>
    /// An agency's website on its free address, which is verified from birth because we own the zone.
    /// It is what the storefront directory resolves an anonymous request's host by.
    /// </summary>
    private static void AddSiteWithAddress(AppDbContext db, Agency agency, Guid templateId, string hostname)
    {
        var site = Site.Create(agency.Id, templateId, agency.LegalName);
        var draft = SiteVersion.CreateDraft(site);
        site.AttachDraft(draft);

        var domain = SiteDomain.ForSubdomain(site, hostname, DateTimeOffset.UtcNow);
        site.SetPrimaryDomain(domain);

        db.Sites.Add(site);
        db.SiteVersions.Add(draft);
        db.SiteDomains.Add(domain);
    }

    private static Exception Innermost(Exception exception)
    {
        while (exception.InnerException is { } inner)
        {
            exception = inner;
        }

        return exception;
    }

    private static string TokenIn(string url) => url[(url.LastIndexOf('/') + 1)..];

    /// <summary>Creates a lead and a quote through the API and sends it, as an agent does.</summary>
    private async Task<Guid> SendAQuoteAsync()
    {
        var lead = await PostAsync<LeadResponse>("/api/v1/crm/leads", NewLead());
        var quote = await PostAsync<QuoteResponse>($"/api/v1/crm/leads/{lead.Id}/quotes", Draft());

        await PostAsync<QuoteResponse>($"{Quotes}/{quote.Id}/send", null);

        return quote.Id;
    }

    /// <summary>Puts the link back the way the column held it before issue 175: in clear, and unhashed.</summary>
    private async Task<string> StoreTheOldWayAsync(Guid quoteId)
    {
        var legacy = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

        await using var owner = _postgres.Connect(_database, asApplicationRole: false);

        await owner.Database.ExecuteSqlRawAsync(
            "update crm.quotes set public_token = {0}, public_token_hash = null where id = {1}",
            legacy,
            quoteId);

        return legacy;
    }

    /// <summary>Runs the backfill the migrate command runs between the two migrations.</summary>
    private async Task HashStoredLinksAsync()
    {
        await using var owner = _postgres.Connect(_database, asApplicationRole: false);

        // The API's own hasher, so the hash is made under the key the application looks links up by.
        using var scope = _factory.Services.CreateScope();
        var links = scope.ServiceProvider.GetRequiredService<QuoteLinks>();

        await new QuoteLinkTokenBackfill(owner, TestTenancy.None().Scope, links, NullLogger.Instance).RunAsync();
    }

    private async Task MigrateTheRestAsync()
    {
        await using var owner = _postgres.Connect(_database, asApplicationRole: false);

        await owner.Database.MigrateAsync();
    }

    private async Task<string?> StoredHashAsync(Guid quoteId)
    {
        await using var connection = new NpgsqlConnection(
            _postgres.ConnectionStringFor(_database, asApplicationRole: false));

        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT public_token_hash FROM crm.quotes WHERE id = @id";
        command.Parameters.AddWithValue("id", quoteId);

        return await command.ExecuteScalarAsync() as string;
    }

    private async Task<List<string>> ColumnNamesAsync()
    {
        await using var connection = new NpgsqlConnection(
            _postgres.ConnectionStringFor(_database, asApplicationRole: false));

        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT column_name FROM information_schema.columns
             WHERE table_schema = 'crm' AND table_name = 'quotes'
            """;

        var names = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    private async Task<string> PublicUrlAsync(Guid quoteId) =>
        (await GetAsync<QuoteResponse>($"{Quotes}/{quoteId}")).PublicUrl!;

    /// <summary>An anonymous GET, as a traveller's browser on the agency's own domain makes it.</summary>
    private async Task<HttpResponseMessage> OpenPublicQuoteAsync(string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/public/crm/quotes/{token}");
        request.Headers.Add(PublicCrmEndpoints.StorefrontHostHeader, Host);

        return await _api.SendAsync(request);
    }

    private async Task<T> PostAsync<T>(string path, object? body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path);

        if (body is not null)
        {
            request.Content = JsonContent.Create(body, body.GetType());
        }

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TokenFor());

        using var response = await _api.SendAsync(request);

        response.StatusCode.Should().BeOneOf(
            [HttpStatusCode.OK, HttpStatusCode.Created], "{0} failed with: {1}", path, await response.Content.ReadAsStringAsync());

        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private async Task<T> GetAsync<T>(string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TokenFor());

        using var response = await _api.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK, path);

        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    /// <summary>A real token from the API's own issuer, for the agency's own user.</summary>
    private string TokenFor()
    {
        var issuer = _factory.Services.GetRequiredService<IAccessTokenIssuer>();

        return issuer.Issue(_ada, ["Manager"], Everything, _agencyId).Value;
    }

    private static LeadRequest NewLead() =>
        new(
            new LeadCustomerRequest("Chiamaka Okonkwo", "chiamaka@example.test", "+234 803 000 1122"),
            "Dubai",
            Today.AddMonths(3),
            Today.AddMonths(3).AddDays(7),
            2,
            1,
            300_000_00L,
            500_000_00L,
            "Somewhere warm in March, please.");

    private static QuoteRequest Draft() =>
        new(
            "Dubai, seven nights",
            Today.AddDays(14),
            [
                new QuoteItemRequest("Hotel, two nights", 2, 45_000_000L, null),
                new QuoteItemRequest("Airport transfer", 1, 7_500_000L, null),
            ],
            [
                new QuoteDayRequest(1, "Arrive", "Transfer to the hotel."),
                new QuoteDayRequest(2, "Desert", "Dune drive and dinner."),
            ],
            "Prices hold until the date above.");
}
