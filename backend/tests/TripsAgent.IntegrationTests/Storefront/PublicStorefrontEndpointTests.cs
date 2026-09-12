using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using TripsAgent.Application.Storefront;
using TripsAgent.Contracts.Storefront;
using TripsAgent.Domain.Assets;
using TripsAgent.Domain.Catalog;
using TripsAgent.Domain.Common;
using TripsAgent.Domain.Storefront;
using TripsAgent.Domain.Tenancy;
using TripsAgent.Infrastructure.Persistence;
using TripsAgent.IntegrationTests.Persistence;

namespace TripsAgent.IntegrationTests.Storefront;

/// <summary>
/// The traveller-facing API (issue 60): which agency a hostname serves, what a published site shows,
/// what an unpublished or suspended one shows instead, and the catalog a traveller browses.
/// </summary>
/// <remarks>
/// Every request here is anonymous and carries no agency: the hostname alone decides whose site is
/// read. The test that matters most is the one that points one agency's hostname at the API and
/// checks it can never see the other's products.
/// </remarks>
[Collection(PostgresCollection.Name)]
public sealed class PublicStorefrontEndpointTests : IAsyncLifetime, IDisposable
{
    private const string SitePath = "/api/v1/public/storefront/site";
    private const string CatalogPath = "/api/v1/public/storefront/catalog";
    private const string SitemapPath = "/api/v1/public/storefront/sitemap";

    private const string LagosHost = "lagos-travel.localhost";
    private const string LagosSecondHost = "www.lagostravel.test";
    private const string AbujaHost = "abuja-tours.localhost";
    private const string QuietHost = "quiet-travel.localhost";
    private const string ShutHost = "shut-travel.localhost";

    private static readonly DateTimeOffset Now = new(2026, 9, 12, 9, 0, 0, TimeSpan.Zero);

    private readonly PostgresFixture _postgres;

    private (string Key, string Value)[] _overrides = [];
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _api = null!;
    private string _database = string.Empty;

    public PublicStorefrontEndpointTests(PostgresFixture postgres) => _postgres = postgres;

    public async Task InitializeAsync()
    {
        _database = $"storefront_public_{Guid.NewGuid():N}";
        var tenancy = TestTenancy.None();

        await using (var setup = await _postgres.CreateEmptyDatabaseAsync(_database, tenancy.Tenant, tenancy.Scope))
        {
            await setup.Database.MigrateAsync();
            await ReferenceDataSeeder.EnsureAsync(setup, tenancy.Scope);

            using var _ = tenancy.Scope.Enter("test setup — four agencies, each with a website");

            var template = await setup.SiteTemplates.FirstAsync();

            var lagos = Verified(Agency.RegisterPrincipal(
                "Lagos Travel Limited", "lagos-travel", "NG", "NGN", "Africa/Lagos", tradingName: "Lagos Travel"));
            var abuja = Verified(Agency.RegisterPrincipal("Abuja Tours Limited", "abuja-tours", "NG", "NGN", "Africa/Lagos"));
            var quiet = Verified(Agency.RegisterPrincipal("Quiet Travel Limited", "quiet-travel", "NG", "NGN", "Africa/Lagos"));
            var shut = Verified(Agency.RegisterPrincipal("Shut Travel Limited", "shut-travel", "NG", "NGN", "Africa/Lagos"));
            shut.Suspend();

            setup.Agencies.AddRange(lagos, abuja, quiet, shut);
            await setup.SaveChangesAsync();

            // Lagos answers on two names; the free subdomain is the main one, so the second is not
            // the address search engines should be sent to.
            AddSite(setup, lagos, template.Id, LagosHost, publish: true, extraHostname: LagosSecondHost);
            AddSite(setup, abuja, template.Id, AbujaHost, publish: true);
            AddSite(setup, quiet, template.Id, QuietHost, publish: false);
            AddSite(setup, shut, template.Id, ShutHost, publish: true);

            AddProducts(setup, lagos.Id);
            AddProducts(setup, abuja.Id, prefix: "abuja");

            await setup.SaveChangesAsync();
        }

        _overrides =
        [
            ("ConnectionStrings__Postgres", _postgres.ConnectionStringFor(_database, asApplicationRole: true)),
            ("ConnectionStrings__PostgresAdmin", _postgres.ConnectionStringFor(_database, asApplicationRole: false)),
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
    public async Task A_hostname_nobody_has_claimed_has_no_website()
    {
        using var response = await GetAsync(SitePath, "somebody-elses-domain.test");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);

        // The same answer as a claimed-but-unproved name, on purpose: neither says anything about
        // the other.
        (await response.Content.ReadAsStringAsync()).Should().NotContain("Trips");
    }

    [Fact]
    public async Task A_published_site_is_served_in_the_agencys_own_name()
    {
        var site = await GetSiteAsync(LagosHost);

        site.Status.Should().Be(PublicSiteStatuses.Live);
        site.Hostname.Should().Be(LagosHost);
        site.PrimaryHostname.Should().Be(LagosHost);
        site.IsPrimaryHostname.Should().BeTrue();
        site.Indexable.Should().BeTrue("a live site on its main address is the one to index");
        site.VersionNumber.Should().Be(1);
        site.Content.Site.Name.Should().Be("Lagos Travel Limited");
        site.Content.Pages.Should().NotBeEmpty();

        site.Theme.PrimaryColor.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Nothing_a_traveller_is_sent_mentions_the_platform()
    {
        foreach (var host in new[] { LagosHost, QuietHost, ShutHost })
        {
            using var site = await GetAsync(SitePath, host);
            using var catalog = await GetAsync(CatalogPath, host);

            foreach (var response in new[] { site, catalog })
            {
                var body = await response.Content.ReadAsStringAsync();

                body.Should().NotContain("Trips", $"nothing traveller-facing may name the platform ({host})");
                body.ToLowerInvariant().Should().NotContain("tripsagent");
            }
        }
    }

    [Fact]
    public async Task A_second_hostname_serves_the_same_site_but_points_search_engines_at_the_first()
    {
        var site = await GetSiteAsync(LagosSecondHost);

        site.Status.Should().Be(PublicSiteStatuses.Live);
        site.Hostname.Should().Be(LagosSecondHost);
        site.PrimaryHostname.Should().Be(LagosHost);
        site.IsPrimaryHostname.Should().BeFalse();
        site.Indexable.Should().BeFalse("two addresses showing one site would compete with each other");
    }

    [Fact]
    public async Task A_site_that_has_never_published_says_so_rather_than_failing()
    {
        var site = await GetSiteAsync(QuietHost);

        site.Status.Should().Be(PublicSiteStatuses.OpeningSoon);
        site.Content.Pages.Should().BeEmpty();
        site.Content.Site.Name.Should().Be("Quiet Travel Limited", "an unopened shop is still theirs");
        site.Indexable.Should().BeFalse();
    }

    [Fact]
    public async Task A_suspended_agencys_shop_is_shut()
    {
        var site = await GetSiteAsync(ShutHost);

        site.Status.Should().Be(PublicSiteStatuses.Offline);
        site.Content.Pages.Should().BeEmpty("a shut shop shows nothing for sale");
        site.Indexable.Should().BeFalse();

        using var catalog = await GetAsync(CatalogPath, ShutHost);
        catalog.StatusCode.Should().Be(HttpStatusCode.OK, "the hostname still resolves; there is simply nothing on it");
    }

    [Fact]
    public async Task The_catalog_shows_only_this_agencys_published_products()
    {
        var lagos = await GetCatalogAsync(LagosHost);
        var abuja = await GetCatalogAsync(AbujaHost);

        lagos.Products.Select(product => product.Title)
            .Should().BeEquivalentTo("Kano Heritage Tour", "UK Visitor Visa");

        lagos.Products.Should().NotContain(
            product => product.Title.Contains("abuja", StringComparison.OrdinalIgnoreCase),
            "one agency's hostname must never show another's catalog");

        lagos.Products.Should().NotContain(product => product.Title == "Unfinished Tour", "a draft is not for sale");

        abuja.Products.Should().OnlyContain(product => product.Title.StartsWith("Abuja", StringComparison.Ordinal));
        abuja.Total.Should().Be(2);
    }

    [Fact]
    public async Task Prices_are_the_lowest_a_traveller_can_actually_pay()
    {
        var catalog = await GetCatalogAsync(LagosHost);
        var tour = catalog.Products.Single(product => product.Title == "Kano Heritage Tour");

        // The cheapest variant, not the base price: "from" must never be more than something that
        // can be bought.
        tour.FromPriceMinor.Should().Be(9_500_000);
        tour.Currency.Should().Be("NGN");
    }

    [Fact]
    public async Task The_filters_offered_are_the_ones_this_agency_actually_has()
    {
        var catalog = await GetCatalogAsync(LagosHost);

        catalog.Filters.ProductTypes.Should().BeEquivalentTo("Tour", "Visa");
        catalog.Filters.Destinations.Should().BeEquivalentTo("Kano", "the visa has no city, so it adds nothing to browse by");
        catalog.Filters.Categories.Should().BeEquivalentTo("Cultural");
        catalog.Filters.MinPriceMinor.Should().BeLessThanOrEqualTo(catalog.Filters.MaxPriceMinor!.Value);
    }

    [Theory]
    [InlineData("?type=Visa", "UK Visitor Visa")]
    [InlineData("?destination=kano", "Kano Heritage Tour")]
    [InlineData("?category=Cultural", "Kano Heritage Tour")]
    [InlineData("?q=heritage", "Kano Heritage Tour")]
    [InlineData("?maxPrice=9500000", "Kano Heritage Tour")]
    public async Task A_traveller_can_narrow_the_catalog_down(string query, string expected)
    {
        var catalog = await GetCatalogAsync(LagosHost, query);

        catalog.Products.Should().ContainSingle().Which.Title.Should().Be(expected);
    }

    [Fact]
    public async Task A_product_page_is_found_by_its_slug_and_a_draft_is_not()
    {
        var product = await Get<PublicProductResponse>($"{CatalogPath}/kano-heritage-tour", LagosHost);

        product.Title.Should().Be("Kano Heritage Tour");
        product.Itinerary.Should().HaveCount(2);
        product.Inclusions.Should().ContainSingle();
        product.Prices.Should().HaveCount(2);
        product.Prices[0].PriceMinor.Should().BeLessThanOrEqualTo(product.Prices[1].PriceMinor, "cheapest first");

        using var draft = await GetAsync($"{CatalogPath}/unfinished-tour", LagosHost);
        draft.StatusCode.Should().Be(HttpStatusCode.NotFound, "a draft is not a page a traveller can reach");

        // Another agency's product is the same "no such page" as one that does not exist at all.
        using var foreign = await GetAsync($"{CatalogPath}/abuja-heritage-tour", LagosHost);
        foreign.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_description_is_returned_as_the_agent_typed_it_and_never_as_markup()
    {
        var product = await Get<PublicProductResponse>($"{CatalogPath}/kano-heritage-tour", LagosHost);

        // Stored and returned as text. The storefront renders it as text, so this is safe — but the
        // API must not have decided to "clean" it either, or the agent's own words would change.
        product.Description.Should().Be("Ancient walls, dye pits & markets. <b>Not markup.</b>");
    }

    [Fact]
    public async Task A_visa_shows_one_fee_rather_than_the_split_behind_it()
    {
        var visa = await Get<PublicProductResponse>($"{CatalogPath}/uk-visitor-visa", LagosHost);

        visa.Visa.Should().NotBeNull();
        visa.Visa!.TotalFeeMinor.Should().Be(22_500_000);
        visa.Visa.Documents.Should().ContainSingle();
    }

    [Fact]
    public async Task The_sitemap_lists_the_sites_pages_and_its_products_against_its_main_address()
    {
        var sitemap = await Get<PublicSitemapResponse>(SitemapPath, LagosSecondHost);

        sitemap.BaseUrl.Should().Contain(LagosHost, "canonical addresses use the main hostname");
        sitemap.Entries.Select(entry => entry.Path).Should().Contain("/");
        sitemap.Entries.Select(entry => entry.Path).Should().Contain("/tours/kano-heritage-tour");

        var quiet = await Get<PublicSitemapResponse>(SitemapPath, QuietHost);
        quiet.Entries.Should().BeEmpty("a site that has not opened has nothing to list");
    }

    [Fact]
    public async Task Traveller_facing_responses_may_be_cached_and_vary_by_hostname()
    {
        using var response = await GetAsync(SitePath, LagosHost);

        response.Headers.CacheControl!.Public.Should().BeTrue();
        response.Headers.CacheControl.MaxAge.Should().BeGreaterThan(TimeSpan.Zero);
        response.Headers.Vary.Should().Contain("Host", "the whole answer depends on which hostname was asked for");
    }

    [Fact]
    public async Task No_header_or_parameter_can_choose_whose_site_is_served()
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            new Uri($"http://{LagosHost}{CatalogPath}?agencyId={Guid.NewGuid()}"));

        // X-Forwarded-Host from an untrusted caller would be a claim about itself. The test server is
        // loopback, which is trusted, so this asserts the rule that matters in production: the value
        // used is the one the pipeline settled on, never a second header read by the endpoint.
        request.Headers.Add("X-Tenant-Id", Guid.NewGuid().ToString());

        using var response = await _api.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var catalog = (await response.Content.ReadFromJsonAsync<PublicCatalogResponse>())!;
        catalog.Products.Should().OnlyContain(product => !product.Title.StartsWith("Abuja", StringComparison.Ordinal));
    }

    /// <summary>An image that went through the whole pipeline: uploaded, scanned clean, processed.</summary>
    private static Asset ReadyImage(Guid agencyId)
    {
        var asset = Asset.Reserve(agencyId, AssetPurpose.ProductMedia, "photo.jpg", Now.AddMinutes(15));
        asset.RecordUpload("image/jpeg", 250_000);
        asset.TryBeginProcessing(Now);
        asset.RecordCleanScan(Now);
        asset.MarkReady(1_600, 1_067, Now);

        return asset;
    }

    private static Agency Verified(Agency agency)
    {
        agency.MarkVerified(Now);
        return agency;
    }

    private static void AddSite(
        AppDbContext db,
        Agency agency,
        Guid templateId,
        string hostname,
        bool publish,
        string? extraHostname = null)
    {
        var site = Site.Create(agency.Id, templateId, agency.LegalName);
        var draft = SiteVersion.CreateDraft(site);
        site.AttachDraft(draft);

        var home = SitePage.Create(draft, SitePageType.Home, SitePageRules.HomeSlug, "Home", true, 0);
        home.ReplaceBlocks(
        [
            new SiteBlockContent(
                null,
                SiteBlockType.Text,
                SiteBlocks.ToStored(SiteBlocks.Text(new TextBlockConfig(null, $"Welcome to {agency.LegalName}.")))),
        ]);

        var domain = SiteDomain.ForSubdomain(site, hostname, Now);
        site.SetPrimaryDomain(domain);

        db.Sites.Add(site);
        db.SiteVersions.Add(draft);
        db.SitePages.Add(home);
        db.SiteDomains.Add(domain);

        if (extraHostname is not null)
        {
            var second = SiteDomain.ForCustom(site, extraHostname, new string('a', 40), Now);
            second.RecordVerificationAttempt(bothRecordsFound: true, Now);
            db.SiteDomains.Add(second);
        }

        if (!publish)
        {
            return;
        }

        var content = SiteSnapshots.Serialize(new SiteContentSnapshot(
            SiteSnapshots.SchemaVersion,
            new SiteSnapshotSettings(agency.LegalName, "en", null, null, false),
            new SiteSnapshotBusiness(null, "hello@agency.test", null, null, []),
            [new SiteSnapshotPage(SitePageRules.HomeSlug, "Home", "Home", true, 0, null, null, [])]));

        var theme = SiteSnapshots.Serialize(new SiteThemeSnapshot(
            SiteSnapshots.SchemaVersion, "horizon", null, "#1F2933", null, SiteFonts.DefaultHeading, SiteFonts.Body));

        var live = SiteVersion.Stage(site, 1, content, theme, null, Now);
        db.SiteVersions.Add(live);
        site.Publish(live, null, null, Now);
    }

    private static void AddProducts(AppDbContext db, Guid agencyId, string? prefix = null)
    {
        var title = prefix is null ? string.Empty : "Abuja ";
        var slug = prefix is null ? string.Empty : "abuja-";

        var category = ProductCategory.Create(agencyId, "Cultural", CategoryType.Category);
        db.ProductCategories.Add(category);

        var tourImage = ReadyImage(agencyId);
        var visaImage = ReadyImage(agencyId);
        db.Assets.AddRange(tourImage, visaImage);

        var tour = Product.CreateDraft(
            agencyId,
            new ProductContent
            {
                ProductType = ProductType.Tour,
                Title = $"{title}Kano Heritage Tour",
                Summary = "Four days in the old city.",
                Description = "Ancient walls, dye pits & markets. <b>Not markup.</b>",
                DestinationCity = "Kano",
                DestinationCountry = "NG",
                DurationDays = 4,
                Currency = "NGN",
                BasePriceMinor = new Money(12_000_000),
                AvailableFrom = DateOnly.FromDateTime(Now.UtcDateTime).AddDays(10),
                CategoryIds = [category.Id],
                HeroAssetId = tourImage.Id,
                Media = [new ProductMediaContent(tourImage.Id, "The old city walls")],
                Itinerary =
                [
                    new ItineraryDayContent(1, "Arrival", "Transfer to the hotel.", [Meal.Dinner], "City hotel"),
                    new ItineraryDayContent(2, "The old city", "Walls and dye pits.", [Meal.Breakfast], "City hotel"),
                ],
                Inclusions = [new InclusionContent(InclusionKind.Inclusion, "Airport transfers")],
                PriceVariants =
                [
                    new PriceVariantContent("Twin share", PaxType.Adult, 2, 1, 12, new Money(9_500_000)),
                    new PriceVariantContent("Single", PaxType.Adult, 1, 1, 12, new Money(12_000_000)),
                ],
            },
            $"{slug}kano-heritage-tour");

        var visa = Product.CreateDraft(
            agencyId,
            new ProductContent
            {
                ProductType = ProductType.Visa,
                Title = $"{title}UK Visitor Visa",
                Summary = "Standard visitor visa, six-month validity.",
                Description = "We prepare and submit the application.",
                DestinationCountry = "GB",
                Currency = "NGN",
                BasePriceMinor = new Money(22_500_000),
                Media = [new ProductMediaContent(visaImage.Id, "A UK visa sticker")],
                Visa = new VisaContent(
                    "Tourist",
                    VisaEntryType.Multiple,
                    15,
                    180,
                    new Money(17_500_000),
                    new Money(5_000_000),
                    [new VisaDocumentContent("Passport valid for six months", true)]),
            },
            $"{slug}uk-visitor-visa");

        // Never published, so it must be invisible everywhere on the storefront.
        var unfinished = Product.CreateDraft(
            agencyId,
            new ProductContent
            {
                ProductType = ProductType.Tour,
                Title = $"{title}Unfinished Tour",
                Currency = "NGN",
                BasePriceMinor = new Money(1_000_000),
            },
            $"{slug}unfinished-tour");

        var today = DateOnly.FromDateTime(Now.UtcDateTime);
        tour.TryPublish(Now, today, out _).Should().BeTrue();
        visa.TryPublish(Now, today, out _).Should().BeTrue();

        db.Products.AddRange(tour, visa, unfinished);
    }

    private async Task<PublicSiteResponse> GetSiteAsync(string host) => await Get<PublicSiteResponse>(SitePath, host);

    private async Task<PublicCatalogResponse> GetCatalogAsync(string host, string query = "") =>
        await Get<PublicCatalogResponse>(CatalogPath + query, host);

    private async Task<T> Get<T>(string path, string host)
    {
        using var response = await GetAsync(path, host);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    /// <summary>
    /// A request with no token at all, identified only by the hostname a traveller typed.
    /// </summary>
    private async Task<HttpResponseMessage> GetAsync(string path, string host)
    {
        // An absolute address, so the hostname is the one under test rather than the test server's.
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri($"http://{host}{path}"));

        return await _api.SendAsync(request);
    }
}
