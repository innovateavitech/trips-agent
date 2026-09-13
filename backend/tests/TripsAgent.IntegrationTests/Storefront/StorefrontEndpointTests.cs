using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TripsAgent.Application.Identity;
using TripsAgent.Contracts.Storefront;
using TripsAgent.Domain.Identity;
using TripsAgent.Domain.Tenancy;
using TripsAgent.Infrastructure.Persistence;
using TripsAgent.IntegrationTests.Persistence;

namespace TripsAgent.IntegrationTests.Storefront;

/// <summary>
/// The website builder through the API, as the agent console drives it: create, edit, stage, publish,
/// roll back — and the publish gate, the permissions and the tenant boundary on the way.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class StorefrontEndpointTests : IAsyncLifetime, IDisposable
{
    private static readonly string[] Edit = [PermissionCodes.StorefrontEdit];
    private static readonly string[] EditAndPublish = [PermissionCodes.StorefrontEdit, PermissionCodes.StorefrontPublish];

    private readonly PostgresFixture _postgres;

    private (string Key, string Value)[] _overrides = [];
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _api = null!;

    private Guid _agencyId;
    private Guid _subAgentId;
    private Guid _unverifiedId;

    public StorefrontEndpointTests(PostgresFixture postgres) => _postgres = postgres;

    public async Task InitializeAsync()
    {
        var database = $"storefront_api_{Guid.NewGuid():N}";
        var tenancy = TestTenancy.None();

        await using (var setup = await _postgres.CreateEmptyDatabaseAsync(database, tenancy.Tenant, tenancy.Scope))
        {
            await setup.Database.MigrateAsync();
            await ReferenceDataSeeder.EnsureAsync(setup, tenancy.Scope);

            var agency = Agency.RegisterPrincipal("Lagos Travel Limited", "lagos-travel", "NG", "NGN", "Africa/Lagos", tradingName: "Lagos Travel");
            agency.MarkVerified(DateTimeOffset.UtcNow);
            var unverified = Agency.RegisterPrincipal("Abuja Tours Limited", "abuja-tours", "NG", "NGN", "Africa/Lagos");
            setup.Agencies.AddRange(agency, unverified);
            await setup.SaveChangesAsync();

            var subAgent = Agency.RegisterSubAgent(agency, "Lagos Travel Ikeja Limited", "lagos-travel-ikeja");
            subAgent.MarkVerified(DateTimeOffset.UtcNow);
            setup.Agencies.Add(subAgent);
            await setup.SaveChangesAsync();

            (_agencyId, _subAgentId, _unverifiedId) = (agency.Id, subAgent.Id, unverified.Id);
        }

        // Environment variables, because a configuration source added through the factory loses to
        // appsettings.Development.json. See RegistrationEndToEndTests.
        _overrides =
        [
            ("ConnectionStrings__Postgres", _postgres.ConnectionStringFor(database, asApplicationRole: true)),
            ("ConnectionStrings__PostgresAdmin", _postgres.ConnectionStringFor(database, asApplicationRole: false)),
        ];

        foreach (var (key, value) in _overrides)
        {
            Environment.SetEnvironmentVariable(key, value);
        }

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host => host.UseEnvironment("Development"));
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
    public async Task Creating_a_site_makes_the_draft_its_pages_and_a_free_address()
    {
        var site = await CreateSiteAsync(_agencyId);

        site.Status.Should().Be("Draft");
        site.TemplateCode.Should().Be("horizon");
        site.Pages.Select(page => page.PageType).Should().Equal("Home", "About", "Catalog", "Contact", "Terms");
        site.PrimaryHostname.Should().Be("lagos-travel.localhost");
        site.SiteUrl.Should().Be("http://lagos-travel.localhost:3000");
        site.Settings.Name.Should().Be("Lagos Travel");
        site.HasUnstagedChanges.Should().BeTrue();
        site.PublishCheck.Problems.Select(problem => problem.Code).Should().Equal("nothing-to-sell");

        var home = await GetAsync<SitePageResponse>($"/api/v1/storefront/pages/{site.Pages[0].Id}", _agencyId, Edit);
        home.Blocks[0].Block.Hero!.Heading.Should().Be("Your next journey starts with Lagos Travel");
    }

    [Fact]
    public async Task An_agency_has_one_site_and_a_sub_agent_has_none()
    {
        await CreateSiteAsync(_agencyId);

        using (var again = await SendAsync(HttpMethod.Post, "/api/v1/storefront/site", new CreateSiteRequest("harbour"), _agencyId, Edit))
        {
            again.StatusCode.Should().Be(HttpStatusCode.Conflict);
        }

        using (var subAgent = await SendAsync(HttpMethod.Post, "/api/v1/storefront/site", new CreateSiteRequest("horizon"), _subAgentId, Edit))
        {
            subAgent.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
            (await subAgent.Content.ReadAsStringAsync()).Should().Contain("principal");
        }

        using var unknown = await SendAsync(HttpMethod.Post, "/api/v1/storefront/site", new CreateSiteRequest("nope"), _unverifiedId, Edit);
        unknown.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_page_saves_whole_and_a_stale_revision_is_refused()
    {
        var site = await CreateSiteAsync(_agencyId);
        var home = await GetAsync<SitePageResponse>($"/api/v1/storefront/pages/{site.Pages[0].Id}", _agencyId, Edit);

        var hero = home.Blocks[0];
        var edited = ToSave(home) with
        {
            Blocks =
            [
                new SitePageBlockRequest(hero.Id, hero.Block with { Hero = hero.Block.Hero! with { Heading = "Holidays that just work" } }),
                .. home.Blocks.Skip(1).Select(block => new SitePageBlockRequest(block.Id, block.Block)),
            ],
        };

        using (var saved = await SendAsync(HttpMethod.Put, $"/api/v1/storefront/pages/{home.Id}", edited, _agencyId, Edit))
        {
            saved.StatusCode.Should().Be(HttpStatusCode.OK);
            var page = await saved.Content.ReadFromJsonAsync<SitePageResponse>();
            page!.Revision.Should().Be(home.Revision + 1);
            page.Blocks[0].Id.Should().Be(hero.Id, "a block that was already on the page keeps its id");
            page.Blocks[0].Block.Hero!.Heading.Should().Be("Holidays that just work");
        }

        // The same save again carries the revision it loaded, which is now out of date.
        using var stale = await SendAsync(HttpMethod.Put, $"/api/v1/storefront/pages/{home.Id}", edited, _agencyId, Edit);
        stale.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task An_invalid_block_is_refused_field_by_field()
    {
        var site = await CreateSiteAsync(_agencyId);
        var home = await GetAsync<SitePageResponse>($"/api/v1/storefront/pages/{site.Pages[0].Id}", _agencyId, Edit);

        var broken = ToSave(home) with
        {
            Blocks = [new SitePageBlockRequest(null, new SiteBlockDto("Hero", new HeroBlockConfig("", null, null, "Go", "javascript:alert(1)"), null, null, null))],
        };

        using var response = await SendAsync(HttpMethod.Put, $"/api/v1/storefront/pages/{home.Id}", broken, _agencyId, Edit);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("errors").EnumerateObject().Select(field => field.Name)
            .Should().Contain(["blocks[0].hero.heading", "blocks[0].hero.ctaHref"]);
    }

    [Fact]
    public async Task Publishing_is_gated_then_goes_live_and_rolls_back()
    {
        var site = await CreateSiteAsync(_agencyId);

        var first = await PostAsync<SiteVersionResponse>("/api/v1/storefront/versions/stage", null, Edit);
        first.VersionNumber.Should().Be(1);
        (await PostAsync<SiteVersionResponse>("/api/v1/storefront/versions/stage", null, Edit)).Id
            .Should().Be(first.Id, "nothing changed since it was staged");

        using (var refused = await SendAsync(HttpMethod.Post, $"/api/v1/storefront/versions/{first.Id}/publish", null, _agencyId, EditAndPublish))
        {
            refused.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
            (await refused.Content.ReadAsStringAsync()).Should().Contain("nothing-to-sell");
        }

        // Open question 11: flight sales count as something to sell.
        using (var settings = await SendAsync(
                   HttpMethod.Put, "/api/v1/storefront/site/settings", ToSettings(site.Settings) with { FlightSearchEnabled = true }, _agencyId, Edit))
        {
            settings.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        var second = await PostAsync<SiteVersionResponse>("/api/v1/storefront/versions/stage", null, Edit);
        second.VersionNumber.Should().Be(2);
        (await PostAsync<SiteVersionResponse>($"/api/v1/storefront/versions/{second.Id}/publish", null, EditAndPublish)).IsLive.Should().BeTrue();

        var live = await GetAsync<SiteResponse>("/api/v1/storefront/site", _agencyId, Edit);
        live.Status.Should().Be("Published");
        live.Published!.Id.Should().Be(second.Id);
        live.HasUnpublishedChanges.Should().BeFalse();

        var third = await ChangeHomeAndStageAsync(live);
        await PostAsync<SiteVersionResponse>($"/api/v1/storefront/versions/{third.Id}/publish", null, EditAndPublish);

        var rolledBack = await PostAsync<SiteVersionResponse>($"/api/v1/storefront/versions/{second.Id}/rollback", null, EditAndPublish);
        rolledBack.IsLive.Should().BeTrue();

        var history = await GetAsync<List<SiteVersionResponse>>("/api/v1/storefront/versions", _agencyId, Edit);
        history.Select(version => (version.VersionNumber, version.Status, version.CanRollBackTo)).Should().Equal(
            (3, "Archived", true),
            (2, "Published", false),
            (1, "Archived", false));
    }

    [Fact]
    public async Task An_unverified_agency_is_told_why_it_cannot_publish()
    {
        var site = await CreateSiteAsync(_unverifiedId);

        site.PublishCheck.CanPublish.Should().BeFalse();
        site.PublishCheck.Problems.Select(problem => problem.Code).Should().Contain("agency-not-verified");
    }

    [Fact]
    public async Task Changing_what_travellers_see_needs_storefront_publish()
    {
        await CreateSiteAsync(_agencyId);
        var staged = await PostAsync<SiteVersionResponse>("/api/v1/storefront/versions/stage", null, Edit);

        using (var publish = await SendAsync(HttpMethod.Post, $"/api/v1/storefront/versions/{staged.Id}/publish", null, _agencyId, Edit))
        {
            publish.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }

        using var read = await SendAsync(HttpMethod.Get, "/api/v1/storefront/site", null, _agencyId, [PermissionCodes.BookingSearch]);
        read.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_pale_primary_colour_is_refused_beside_its_field()
    {
        await CreateSiteAsync(_agencyId);

        using (var pale = await SendAsync(HttpMethod.Put, "/api/v1/storefront/theme", new SiteThemeRequest(null, "#FFFF00", null), _agencyId, Edit))
        {
            pale.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await pale.Content.ReadAsStringAsync()).Should().Contain("primaryColor").And.Contain("4.5");
        }

        using var dark = await SendAsync(HttpMethod.Put, "/api/v1/storefront/theme", new SiteThemeRequest(null, "#0f766e", null), _agencyId, Edit);
        dark.StatusCode.Should().Be(HttpStatusCode.OK);
        (await dark.Content.ReadFromJsonAsync<SiteThemeResponse>())!.PrimaryColor.Should().Be("#0F766E");
    }

    [Fact]
    public async Task A_preview_link_is_on_the_sites_own_address()
    {
        await CreateSiteAsync(_agencyId);

        var link = await PostAsync<SitePreviewLinkResponse>("/api/v1/storefront/preview-links", new SitePreviewLinkRequest(null), Edit);

        link.Url.Should().StartWith("http://lagos-travel.localhost:3000/preview/");
        link.ExpiresAt.Should().BeAfter(DateTimeOffset.UtcNow.AddHours(11));
    }

    [Fact]
    public async Task Another_agency_cannot_reach_the_site()
    {
        var site = await CreateSiteAsync(_agencyId);

        using (var otherSite = await SendAsync(HttpMethod.Get, "/api/v1/storefront/site", null, _unverifiedId, Edit))
        {
            otherSite.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }

        using var otherPage = await SendAsync(HttpMethod.Get, $"/api/v1/storefront/pages/{site.Pages[0].Id}", null, _unverifiedId, Edit);
        otherPage.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ------------------------------------------------------------------ helpers

    private async Task<SiteVersionResponse> ChangeHomeAndStageAsync(SiteResponse site)
    {
        var home = await GetAsync<SitePageResponse>($"/api/v1/storefront/pages/{site.Pages[0].Id}", _agencyId, Edit);

        using (var saved = await SendAsync(HttpMethod.Put, $"/api/v1/storefront/pages/{home.Id}", ToSave(home) with { Title = "Welcome home" }, _agencyId, Edit))
        {
            saved.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        return await PostAsync<SiteVersionResponse>("/api/v1/storefront/versions/stage", null, Edit);
    }

    private async Task<SiteResponse> CreateSiteAsync(Guid agencyId)
    {
        using var response = await SendAsync(HttpMethod.Post, "/api/v1/storefront/site", new CreateSiteRequest("horizon"), agencyId, Edit);
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());

        return (await response.Content.ReadFromJsonAsync<SiteResponse>())!;
    }

    private async Task<T> GetAsync<T>(string path, Guid agencyId, IReadOnlyCollection<string> permissions)
    {
        using var response = await SendAsync(HttpMethod.Get, path, null, agencyId, permissions);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private async Task<T> PostAsync<T>(string path, object? body, IReadOnlyCollection<string> permissions)
    {
        using var response = await SendAsync(HttpMethod.Post, path, body, _agencyId, permissions);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string path,
        object? body,
        Guid agencyId,
        IReadOnlyCollection<string> permissions)
    {
        using var request = new HttpRequestMessage(method, path);

        if (body is not null)
        {
            request.Content = JsonContent.Create(body, body.GetType());
        }

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TokenFor(agencyId, permissions));

        return await _api.SendAsync(request);
    }

    private string TokenFor(Guid agencyId, IReadOnlyCollection<string> permissions)
    {
        var issuer = _factory.Services.GetRequiredService<IAccessTokenIssuer>();
        var user = User.ForAgency(agencyId, "owner@lagos-travel.test", "not-a-real-hash", "Ngozi", "Adeyemi");

        return issuer.Issue(user, ["Owner"], permissions, agencyId).Value;
    }

    private static SaveSitePageRequest ToSave(SitePageResponse page) => new(
        page.Title,
        page.Slug,
        page.ShowInNav,
        page.MetaTitle,
        page.MetaDescription,
        page.Revision,
        page.Blocks.Select(block => new SitePageBlockRequest(block.Id, block.Block)).ToList());

    private static SiteSettingsRequest ToSettings(SiteSettingsResponse settings) => new(
        settings.Name,
        settings.SeoTitle,
        settings.SeoDescription,
        settings.FlightSearchEnabled,
        settings.ContactEmail,
        settings.ContactPhone,
        settings.WhatsAppNumber,
        settings.ContactAddress,
        settings.SocialLinks);
}
