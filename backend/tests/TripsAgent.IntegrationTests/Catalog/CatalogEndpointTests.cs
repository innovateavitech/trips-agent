using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TripsAgent.Application.Identity;
using TripsAgent.Contracts.Catalog;
using TripsAgent.Domain.Assets;
using TripsAgent.Domain.Catalog;
using TripsAgent.Domain.Identity;
using TripsAgent.Domain.Tenancy;
using TripsAgent.IntegrationTests.Persistence;

namespace TripsAgent.IntegrationTests.Catalog;

/// <summary>
/// The product management API (#161) through the real host: routes, permissions, tenant isolation,
/// publish validation and the whole-product save — against PostgreSQL as the policed application role.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class CatalogEndpointTests : IAsyncLifetime, IDisposable
{
    private const string Products = "/api/v1/catalog/products";
    private const string Categories = "/api/v1/catalog/categories";

    private static readonly string[] Everything =
        [PermissionCodes.CatalogView, PermissionCodes.CatalogEdit, PermissionCodes.CatalogPublish];

    private readonly PostgresFixture _postgres;

    private (string Key, string Value)[] _overrides = [];
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _api = null!;
    private string _database = string.Empty;
    private string _storageRoot = string.Empty;

    private Guid _agencyA;
    private Guid _agencyB;
    private Guid _imageA;
    private Guid _secondImageA;
    private Guid _pendingImageA;
    private Guid _imageB;
    private Guid _beachA;
    private Guid _honeymoonA;
    private Guid _beachB;

    public CatalogEndpointTests(PostgresFixture postgres) => _postgres = postgres;

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow);

    public async Task InitializeAsync()
    {
        _database = $"catalog_api_{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;

        await using (var setup = await _postgres.CreateEmptyDatabaseAsync(_database))
        {
            await setup.Database.MigrateAsync();

            var a = Agency.RegisterPrincipal("Lagos Travel Limited", "lagos-travel", "NG", "NGN", "Africa/Lagos");
            var b = Agency.RegisterPrincipal("Abuja Tours Limited", "abuja-tours", "NG", "NGN", "Africa/Lagos");

            var imageA = ReadyImage(a.Id, now, "nungwi.jpg");
            var secondImageA = ReadyImage(a.Id, now, "stone-town.jpg");
            var pendingImageA = PendingImage(a.Id, now);
            var imageB = ReadyImage(b.Id, now, "abuja.jpg");

            var beachA = ProductCategory.Create(a.Id, "Beach holidays", CategoryType.Category);
            var honeymoonA = ProductCategory.Create(a.Id, "Honeymoon", CategoryType.Theme);
            var beachB = ProductCategory.Create(b.Id, "Beach holidays", CategoryType.Category);

            setup.Agencies.AddRange(a, b);
            setup.Assets.AddRange(imageA, secondImageA, pendingImageA, imageB);
            setup.AssetVariants.AddRange([.. Renditions(imageA), .. Renditions(secondImageA)]);
            setup.ProductCategories.AddRange(beachA, honeymoonA, beachB);
            await setup.SaveChangesAsync();

            (_agencyA, _agencyB) = (a.Id, b.Id);
            (_imageA, _secondImageA, _pendingImageA, _imageB) = (imageA.Id, secondImageA.Id, pendingImageA.Id, imageB.Id);
            (_beachA, _honeymoonA, _beachB) = (beachA.Id, honeymoonA.Id, beachB.Id);
        }

        _storageRoot = Path.Combine(Path.GetTempPath(), $"tripsagent-catalog-{Guid.NewGuid():N}");

        // Environment variables, because a configuration source added through the factory loses to
        // appsettings.Development.json. The API runs as the role row-level security polices and
        // migrates as the owner — the same split as production (ADR-0006).
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

    public void Dispose()
    {
        _api?.Dispose();
        _api = null!;
    }

    // ------------------------------------------------------------------ permissions

    [Fact]
    public async Task Without_a_token_nothing_is_answered()
    {
        using var response = await _api.GetAsync(Products);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Reading_needs_catalog_view()
    {
        var product = await CreateAsync(FullTour());

        (await StatusOf(HttpMethod.Get, Products, null, PermissionCodes.CatalogEdit, PermissionCodes.CatalogPublish))
            .Should().Be(HttpStatusCode.Forbidden);
        (await StatusOf(HttpMethod.Get, $"{Products}/{product.Id}", null, PermissionCodes.CatalogEdit))
            .Should().Be(HttpStatusCode.Forbidden);
        (await StatusOf(HttpMethod.Get, Categories, null, PermissionCodes.CatalogEdit))
            .Should().Be(HttpStatusCode.Forbidden);

        (await StatusOf(HttpMethod.Get, Products, null, PermissionCodes.CatalogView)).Should().Be(HttpStatusCode.OK);
        (await StatusOf(HttpMethod.Get, $"{Products}/{product.Id}", null, PermissionCodes.CatalogView)).Should().Be(HttpStatusCode.OK);
        (await StatusOf(HttpMethod.Get, Categories, null, PermissionCodes.CatalogView)).Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Creating_and_saving_need_catalog_edit()
    {
        var product = await CreateAsync(FullTour());
        string[] readAndPublish = [PermissionCodes.CatalogView, PermissionCodes.CatalogPublish];

        (await StatusOf(HttpMethod.Post, Products, FullTour("Lamu Retreat"), readAndPublish)).Should().Be(HttpStatusCode.Forbidden);
        (await StatusOf(HttpMethod.Put, $"{Products}/{product.Id}", FullTour(), readAndPublish)).Should().Be(HttpStatusCode.Forbidden);
        (await StatusOf(HttpMethod.Post, Categories, new CategoryRequest("Safari", "Category"), readAndPublish))
            .Should().Be(HttpStatusCode.Forbidden);

        (await StatusOf(HttpMethod.Post, Products, FullTour("Lamu Retreat"), PermissionCodes.CatalogEdit)).Should().Be(HttpStatusCode.Created);
        (await StatusOf(HttpMethod.Put, $"{Products}/{product.Id}", FullTour(), PermissionCodes.CatalogEdit)).Should().Be(HttpStatusCode.OK);
        (await StatusOf(HttpMethod.Post, Categories, new CategoryRequest("Safari", "Category"), PermissionCodes.CatalogEdit))
            .Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Publishing_unpublishing_and_archiving_need_catalog_publish()
    {
        var product = await CreateAsync(FullTour());

        foreach (var action in new[] { "publish", "unpublish", "archive" })
        {
            (await StatusOf(HttpMethod.Post, $"{Products}/{product.Id}/{action}", null, PermissionCodes.CatalogView, PermissionCodes.CatalogEdit))
                .Should().Be(HttpStatusCode.Forbidden, $"{action} changes what travellers see");
        }

        (await StatusOf(HttpMethod.Post, $"{Products}/{product.Id}/publish", null, PermissionCodes.CatalogPublish)).Should().Be(HttpStatusCode.OK);
        (await StatusOf(HttpMethod.Post, $"{Products}/{product.Id}/unpublish", null, PermissionCodes.CatalogPublish)).Should().Be(HttpStatusCode.OK);
        (await StatusOf(HttpMethod.Post, $"{Products}/{product.Id}/archive", null, PermissionCodes.CatalogPublish)).Should().Be(HttpStatusCode.OK);
    }

    // ------------------------------------------------------------------ tenant isolation

    [Fact]
    public async Task Another_agencys_product_does_not_exist_as_far_as_you_can_tell()
    {
        var product = await CreateAsync(FullTour());

        (await StatusOf(HttpMethod.Get, $"{Products}/{product.Id}", null, _agencyB, Everything)).Should().Be(HttpStatusCode.NotFound);
        (await StatusOf(HttpMethod.Put, $"{Products}/{product.Id}", Blank("Tour"), _agencyB, Everything)).Should().Be(HttpStatusCode.NotFound);

        foreach (var action in new[] { "publish", "unpublish", "archive" })
        {
            (await StatusOf(HttpMethod.Post, $"{Products}/{product.Id}/{action}", null, _agencyB, Everything))
                .Should().Be(HttpStatusCode.NotFound);
        }

        (await ListAsync(string.Empty, _agencyB)).Should().BeEmpty();

        var untouched = await GetAsync(product.Id);
        untouched.Status.Should().Be("Draft");
        untouched.Title.Should().Be("Zanzibar Escape");
    }

    [Fact]
    public async Task Only_your_own_images_and_categories_can_be_attached()
    {
        using var response = await SendAsync(
            HttpMethod.Post,
            Products,
            FullTour() with { Media = [new ProductMediaRequest(_imageB, "Someone else's photo")], HeroAssetId = null, CategoryIds = [_beachB] },
            _agencyA,
            Everything);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        ErrorFields(await JsonAsync(response)).Should().Contain(["media[0].assetId", "categoryIds[0]"]);
    }

    [Fact]
    public async Task An_image_still_being_scanned_cannot_be_attached()
    {
        using var response = await SendAsync(
            HttpMethod.Post,
            Products,
            FullTour() with { Media = [new ProductMediaRequest(_pendingImageA, string.Empty)], HeroAssetId = null },
            _agencyA,
            Everything);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await JsonAsync(response);
        body.GetProperty("errors").GetProperty("media[0].assetId")[0].GetString().Should().Contain("still being checked");
    }

    // ------------------------------------------------------------------ publishing

    [Fact]
    public async Task A_blank_draft_saves_and_lists_every_reason_it_cannot_be_published()
    {
        var draft = await CreateAsync(Blank("Tour"));

        draft.Status.Should().Be("Draft");
        draft.Slug.Should().Be("untitled");
        draft.PublishProblems.Select(problem => problem.Field)
            .Should().BeEquivalentTo(["title", "basePriceMinor", "media", "availableFrom"]);

        using var response = await SendAsync(HttpMethod.Post, $"{Products}/{draft.Id}/publish", null, _agencyA, Everything);

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        var body = await JsonAsync(response);
        ProblemFields(body).Should().BeEquivalentTo(["title", "basePriceMinor", "media", "availableFrom"]);
        body.GetProperty("publishProblems").EnumerateArray()
            .Should().OnlyContain(problem => !string.IsNullOrWhiteSpace(problem.GetProperty("message").GetString()));

        (await GetAsync(draft.Id)).Status.Should().Be("Draft");
    }

    [Fact]
    public async Task A_complete_tour_publishes_and_cannot_be_published_twice()
    {
        var created = await CreateAsync(FullTour());
        created.PublishProblems.Should().BeEmpty();

        var published = await ChangeStatusAsync(created.Id, "publish");

        published.Status.Should().Be("Published");
        published.PublishedAt.Should().NotBeNull();

        (await StatusOf(HttpMethod.Post, $"{Products}/{created.Id}/publish", null, Everything)).Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task A_visa_needs_its_details_and_a_document_before_it_can_be_published()
    {
        var complete = FullVisa();
        var visa = await CreateAsync(complete with { Visa = complete.Visa! with { Documents = [] } });

        visa.PublishProblems.Select(problem => problem.Field).Should().Equal("visa.documents");

        using (var refused = await SendAsync(HttpMethod.Post, $"{Products}/{visa.Id}/publish", null, _agencyA, Everything))
        {
            refused.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
            ProblemFields(await JsonAsync(refused)).Should().Equal("visa.documents");
        }

        (await SaveAsync(visa.Id, complete)).PublishProblems.Should().BeEmpty();
        (await ChangeStatusAsync(visa.Id, "publish")).Status.Should().Be("Published");
    }

    [Fact]
    public async Task A_live_product_cannot_be_saved_into_a_state_it_could_not_be_published_in()
    {
        var live = await PublishedAsync(FullTour());

        using var response = await SendAsync(
            HttpMethod.Put,
            $"{Products}/{live.Id}",
            FullTour("Renamed") with { Media = [], HeroAssetId = null },
            _agencyA,
            Everything);

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        ProblemFields(await JsonAsync(response)).Should().Equal("media");

        var after = await GetAsync(live.Id);
        after.Status.Should().Be("Published");
        after.Title.Should().Be("Zanzibar Escape");
        after.Media.Should().HaveCount(2);
    }

    [Fact]
    public async Task An_archived_product_is_restored_by_unpublishing_it_before_it_can_be_changed()
    {
        var product = await CreateAsync(FullTour());

        (await ChangeStatusAsync(product.Id, "archive")).Status.Should().Be("Archived");

        using (var save = await SendAsync(HttpMethod.Put, $"{Products}/{product.Id}", FullTour(), _agencyA, Everything))
        {
            save.StatusCode.Should().Be(HttpStatusCode.Conflict);
            (await JsonAsync(save)).GetProperty("detail").GetString().Should().Contain("Restore");
        }

        (await StatusOf(HttpMethod.Post, $"{Products}/{product.Id}/archive", null, Everything)).Should().Be(HttpStatusCode.Conflict);
        (await StatusOf(HttpMethod.Post, $"{Products}/{product.Id}/publish", null, Everything)).Should().Be(HttpStatusCode.Conflict);

        (await ChangeStatusAsync(product.Id, "unpublish")).Status.Should().Be("Draft");
        (await StatusOf(HttpMethod.Put, $"{Products}/{product.Id}", FullTour(), Everything)).Should().Be(HttpStatusCode.OK);
        (await StatusOf(HttpMethod.Post, $"{Products}/{product.Id}/unpublish", null, Everything))
            .Should().Be(HttpStatusCode.Conflict, "a draft is already off the storefront");
    }

    // ------------------------------------------------------------------ the whole-product save

    [Fact]
    public async Task One_PUT_saves_the_whole_product_in_the_order_it_was_sent()
    {
        var draft = await CreateAsync(Blank("Tour"));
        var request = FullTour() with
        {
            Media = [new ProductMediaRequest(_secondImageA, "Stone Town"), new ProductMediaRequest(_imageA, "Nungwi beach")],
            HeroAssetId = _imageA,
            CategoryIds = [_beachA, _honeymoonA],
        };

        var saved = await SaveAsync(draft.Id, request);
        var read = await GetAsync(draft.Id);

        foreach (var product in new[] { saved, read })
        {
            product.Title.Should().Be("Zanzibar Escape");
            product.Slug.Should().Be("zanzibar-escape", "a draft's slug follows its title until it is published");
            product.DestinationCountry.Should().Be("TZ");
            product.Currency.Should().Be("NGN", "a blank currency means the agency's own");
            product.Media.Select(item => item.AssetId).Should().Equal(_secondImageA, _imageA);
            product.Media.Should().OnlyContain(item => item.PreviewUrl != null, "both images are scanned clean");
            product.HeroAssetId.Should().Be(_imageA);
            product.CategoryIds.Should().BeEquivalentTo([_beachA, _honeymoonA]);
            product.Itinerary.Select(day => day.DayNumber).Should().Equal(1, 2, 3);
            product.Itinerary[1].Meals.Should().Equal("Breakfast", "Lunch");
            product.Itinerary[2].Accommodation.Should().BeEmpty();
            product.Inclusions.Select(line => line.Kind).Should().Equal("Inclusion", "Exclusion");
            product.PriceVariants.Select(variant => variant.PriceMinor).Should().Equal(15_000_000, 7_500_000);
            product.PublishProblems.Should().BeEmpty();
        }

        // A second whole save that takes most of it away again.
        var trimmed = await SaveAsync(draft.Id, request with
        {
            Media = [new ProductMediaRequest(_imageA, string.Empty)],
            CategoryIds = [],
            Itinerary = [request.Itinerary[0]],
            Inclusions = [],
        });

        trimmed.Media.Should().ContainSingle().Which.AssetId.Should().Be(_imageA);
        trimmed.CategoryIds.Should().BeEmpty();
        trimmed.Itinerary.Should().ContainSingle();
        trimmed.Inclusions.Should().BeEmpty();

        await using var owner = _postgres.Connect(_database, asApplicationRole: false);
        (await owner.Database.SqlQueryRaw<long>(
                "SELECT count(*) AS \"Value\" FROM platform.assets WHERE id = {0}", _secondImageA).SingleAsync())
            .Should().Be(1, "detaching an image never deletes the asset");
    }

    [Fact]
    public async Task A_save_that_fails_part_way_through_saves_nothing_at_all()
    {
        var product = await CreateAsync(FullTour());

        // A test-only trigger that refuses price rows. The save has to write the product's own row,
        // the days and the gallery as well as the prices; with this in place it fails on the prices,
        // and none of the rest may survive.
        await using (var owner = _postgres.Connect(_database, asApplicationRole: false))
        {
            await owner.Database.ExecuteSqlRawAsync(
                """
                CREATE FUNCTION catalog.test_refuse_prices() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    RAISE EXCEPTION 'test: refusing to store a price';
                END
                $$;

                CREATE TRIGGER test_refuse_prices BEFORE INSERT ON catalog.product_price_variants
                    FOR EACH ROW EXECUTE FUNCTION catalog.test_refuse_prices();
                """);
        }

        var tour = FullTour("Zanzibar Escape Deluxe");
        using var response = await SendAsync(
            HttpMethod.Put,
            $"{Products}/{product.Id}",
            tour with { Itinerary = [tour.Itinerary[0]], Media = [new ProductMediaRequest(_imageA, "Kendwa")] },
            _agencyA,
            Everything);

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);

        var after = await GetAsync(product.Id);
        after.Title.Should().Be("Zanzibar Escape");
        after.Itinerary.Should().HaveCount(3);
        after.Media.Should().HaveCount(2);
        after.Media[0].Caption.Should().Be("Nungwi beach");
        after.PriceVariants.Should().HaveCount(2);
    }

    [Fact]
    public async Task A_save_that_cannot_be_stored_is_refused_with_every_problem_by_field()
    {
        var tour = FullTour();

        using var response = await SendAsync(
            HttpMethod.Post,
            Products,
            tour with
            {
                DestinationCountry = "Tanzania",
                HeroAssetId = Guid.CreateVersion7(),
                Itinerary = [tour.Itinerary[0], tour.Itinerary[2]],
                PriceVariants = [tour.PriceVariants[0] with { PriceMinor = -1 }],
                Inclusions = [new InclusionRequest("Maybe", " ")],
            },
            _agencyA,
            Everything);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        ErrorFields(await JsonAsync(response)).Should().BeEquivalentTo(
        [
            "destinationCountry",
            "heroAssetId",
            "itinerary[1].dayNumber",
            "priceVariants[0].priceMinor",
            "inclusions[0].kind",
            "inclusions[0].text",
        ]);
    }

    [Fact]
    public async Task Names_that_are_not_on_the_list_are_refused_not_guessed()
    {
        var tour = FullTour();

        using var response = await SendAsync(
            HttpMethod.Post,
            Products,
            tour with
            {
                // Enum.TryParse would OR these two into 3 — a perfectly good Visa.
                ProductType = "Tour,Package",
                Itinerary = [tour.Itinerary[0] with { Meals = ["Supper"] }],
                PriceVariants = [tour.PriceVariants[0] with { PaxType = "1" }],
            },
            _agencyA,
            Everything);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        ErrorFields(await JsonAsync(response)).Should().Contain(["productType", "itinerary[0].meals", "priceVariants[0].paxType"]);
    }

    [Theory]
    [InlineData("1500.5")]
    [InlineData("\"1500.00\"")]
    public async Task A_price_is_whole_kobo_and_nothing_else(string price)
    {
        var json =
            $$"""
              {"productType":"Tour","title":"Zanzibar","summary":"","description":"","destinationCountry":"",
               "destinationCity":"","currency":"","basePriceMinor":{{price}},"media":[],"categoryIds":[],
               "itinerary":[],"inclusions":[],"priceVariants":[]}
              """;

        using var request = new HttpRequestMessage(HttpMethod.Post, Products)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TokenFor(_agencyA, Everything));

        using var response = await _api.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ------------------------------------------------------------------ slugs

    [Fact]
    public async Task A_slug_comes_from_the_title_and_is_numbered_when_the_title_is_taken()
    {
        (await CreateAsync(FullTour())).Slug.Should().Be("zanzibar-escape");
        (await CreateAsync(FullTour())).Slug.Should().Be("zanzibar-escape-2");
        (await CreateAsync(FullTour("Zanzibar  Escape!"))).Slug.Should().Be("zanzibar-escape-3");
    }

    [Fact]
    public async Task Asking_for_a_slug_already_in_use_is_a_409_that_names_a_free_one()
    {
        await CreateAsync(FullTour());
        await CreateAsync(FullTour());

        using var response = await SendAsync(
            HttpMethod.Post,
            Products,
            FullTour("Something else") with { Slug = "Zanzibar Escape" },
            _agencyA,
            Everything);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var body = await JsonAsync(response);
        body.GetProperty("slug").GetString().Should().Be("zanzibar-escape");
        body.GetProperty("suggestedSlug").GetString().Should().Be("zanzibar-escape-3");
    }

    [Fact]
    public async Task Two_agencies_can_sell_under_the_same_slug()
    {
        (await CreateAsync(FullTour())).Slug.Should().Be("zanzibar-escape");
        (await CreateAsync(Blank("Tour") with { Title = "Zanzibar Escape" }, _agencyB)).Slug.Should().Be("zanzibar-escape");
    }

    [Fact]
    public async Task A_live_product_keeps_its_address_when_its_title_changes_and_a_draft_does_not()
    {
        var live = await PublishedAsync(FullTour());
        (await SaveAsync(live.Id, FullTour("Zanzibar Escape Deluxe"))).Slug.Should().Be("zanzibar-escape");

        var draft = await CreateAsync(FullTour("Lamu Retreat"));
        (await SaveAsync(draft.Id, FullTour("Lamu Island Retreat"))).Slug.Should().Be("lamu-island-retreat");
    }

    // ------------------------------------------------------------------ the list

    [Fact]
    public async Task The_list_filters_by_type_status_and_search_and_counts_what_each_product_is_missing()
    {
        var tour = await PublishedAsync(FullTour());
        var package = await CreateAsync(Blank("Package") with { Title = "Lagos Weekend" });
        var visa = await CreateAsync(FullVisa());

        var all = await ListAsync(string.Empty);

        all.Select(row => row.Id).Should().BeEquivalentTo([tour.Id, package.Id, visa.Id]);

        var tourRow = all.Single(row => row.Id == tour.Id);
        tourRow.Status.Should().Be("Published");
        tourRow.ProductType.Should().Be("Tour");
        tourRow.BasePriceMinor.Should().Be(15_000_000);
        tourRow.HeroPreviewUrl.Should().NotBeNullOrEmpty();
        tourRow.PublishProblemCount.Should().Be(0);

        var packageRow = all.Single(row => row.Id == package.Id);
        packageRow.PublishProblemCount.Should().Be(3, "it has a title, and lacks a price, an image and dates");
        packageRow.HeroPreviewUrl.Should().BeNull();

        (await ListAsync("?type=Tour")).Select(row => row.Id).Should().Equal(tour.Id);
        (await ListAsync("?status=Draft")).Select(row => row.Id).Should().BeEquivalentTo([package.Id, visa.Id]);
        (await ListAsync("?type=visa&status=draft")).Select(row => row.Id).Should().Equal(visa.Id);
        (await ListAsync("?q=zanzibar")).Select(row => row.Id).Should().Equal(tour.Id);
        (await ListAsync("?q=GB")).Select(row => row.Id).Should().Equal(visa.Id);
    }

    [Theory]
    [InlineData("?type=Hotel")]
    [InlineData("?type=1")]
    [InlineData("?status=Live")]
    public async Task A_filter_that_names_nothing_is_a_400(string query)
    {
        (await StatusOf(HttpMethod.Get, Products + query, null, PermissionCodes.CatalogView))
            .Should().Be(HttpStatusCode.BadRequest);
    }

    // ------------------------------------------------------------------ categories

    [Fact]
    public async Task Categories_are_unique_per_type_ignoring_case()
    {
        (await StatusOf(HttpMethod.Post, Categories, new CategoryRequest("Safari", "Category"), Everything))
            .Should().Be(HttpStatusCode.Created);
        (await StatusOf(HttpMethod.Post, Categories, new CategoryRequest("  safari ", "Category"), Everything))
            .Should().Be(HttpStatusCode.Conflict);
        (await StatusOf(HttpMethod.Post, Categories, new CategoryRequest("Safari", "Theme"), Everything))
            .Should().Be(HttpStatusCode.Created, "a theme may share a category's name");
        (await StatusOf(HttpMethod.Post, Categories, new CategoryRequest("Safari", "Category"), _agencyB, Everything))
            .Should().Be(HttpStatusCode.Created, "another agency's categories are its own business");

        using var response = await SendAsync(HttpMethod.Get, Categories, null, _agencyA, Everything);
        var listed = (await response.Content.ReadFromJsonAsync<List<CategoryResponse>>())!;

        listed.Select(category => (category.Name, category.Type)).Should().Equal(
            ("Beach holidays", "Category"),
            ("Safari", "Category"),
            ("Honeymoon", "Theme"),
            ("Safari", "Theme"));
    }

    [Theory]
    [InlineData("", "Category", "name")]
    [InlineData("Safari", "Tag", "type")]
    public async Task A_category_needs_a_name_and_a_type(string name, string type, string field)
    {
        using var response = await SendAsync(HttpMethod.Post, Categories, new CategoryRequest(name, type), _agencyA, Everything);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        ErrorFields(await JsonAsync(response)).Should().Contain(field);
    }

    // ------------------------------------------------------------------ requests

    private ProductRequest FullTour(string title = "Zanzibar Escape") => new(
        ProductType: "Tour",
        Title: title,
        Slug: null,
        Summary: "Five days on the spice island.",
        Description: "Stone Town, the north coast beaches and a spice farm.",
        DestinationCountry: "tz",
        DestinationCity: "Zanzibar",
        DurationDays: 3,
        Currency: string.Empty,
        BasePriceMinor: 15_000_000,
        AvailableFrom: Today.AddDays(10),
        AvailableTo: Today.AddDays(100),
        HeroAssetId: _imageA,
        Media: [new ProductMediaRequest(_imageA, "Nungwi beach"), new ProductMediaRequest(_secondImageA, "Stone Town")],
        CategoryIds: [_beachA],
        Itinerary:
        [
            new ItineraryDayRequest(1, "Arrival", "Transfer to Stone Town.", ["Dinner"], "Stone Town hotel"),
            new ItineraryDayRequest(2, "Spice farm", "A morning among the cloves.", ["Lunch", "Breakfast"], "Stone Town hotel"),
            new ItineraryDayRequest(3, "Departure", "Transfer to the airport.", ["Breakfast"], string.Empty),
        ],
        Inclusions: [new InclusionRequest("Inclusion", "Airport transfers"), new InclusionRequest("Exclusion", "International flights")],
        PriceVariants:
        [
            new PriceVariantRequest("Double occupancy", "Adult", 2, null, null, 15_000_000),
            new PriceVariantRequest("Child sharing", "Child", null, null, null, 7_500_000),
        ],
        Visa: null);

    private ProductRequest FullVisa() => new(
        ProductType: "Visa",
        Title: "UK Standard Visitor Visa",
        Slug: null,
        Summary: "Six months, multiple entry.",
        Description: string.Empty,
        DestinationCountry: "GB",
        DestinationCity: "London",
        DurationDays: null,
        Currency: "NGN",
        BasePriceMinor: 25_000_000,
        AvailableFrom: null,
        AvailableTo: null,
        HeroAssetId: null,
        Media: [new ProductMediaRequest(_imageA, string.Empty)],
        CategoryIds: [],
        Itinerary: [],
        Inclusions: [],
        PriceVariants: [],
        Visa: new VisaDetailsRequest(
            "Tourist",
            "Multiple",
            15,
            180,
            17_500_000,
            5_000_000,
            [new VisaDocumentRequest("Passport valid for six months", true), new VisaDocumentRequest("Letter of invitation", false)]));

    private static ProductRequest Blank(string type) =>
        new(type, string.Empty, null, string.Empty, string.Empty, string.Empty, string.Empty, null, string.Empty, 0,
            null, null, null, [], [], [], [], [], null);

    // ------------------------------------------------------------------ helpers

    private async Task<ProductResponse> CreateAsync(ProductRequest request, Guid? agencyId = null)
    {
        using var response = await SendAsync(HttpMethod.Post, Products, request, agencyId ?? _agencyA, Everything);
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        response.Headers.Location.Should().NotBeNull();
        return (await response.Content.ReadFromJsonAsync<ProductResponse>())!;
    }

    private async Task<ProductResponse> SaveAsync(Guid productId, ProductRequest request)
    {
        using var response = await SendAsync(HttpMethod.Put, $"{Products}/{productId}", request, _agencyA, Everything);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<ProductResponse>())!;
    }

    private async Task<ProductResponse> GetAsync(Guid productId)
    {
        using var response = await SendAsync(HttpMethod.Get, $"{Products}/{productId}", null, _agencyA, Everything);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<ProductResponse>())!;
    }

    private async Task<ProductResponse> ChangeStatusAsync(Guid productId, string action)
    {
        using var response = await SendAsync(HttpMethod.Post, $"{Products}/{productId}/{action}", null, _agencyA, Everything);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<ProductResponse>())!;
    }

    private async Task<ProductResponse> PublishedAsync(ProductRequest request) =>
        await ChangeStatusAsync((await CreateAsync(request)).Id, "publish");

    private async Task<List<ProductSummaryResponse>> ListAsync(string query, Guid? agencyId = null)
    {
        using var response = await SendAsync(HttpMethod.Get, Products + query, null, agencyId ?? _agencyA, Everything);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<List<ProductSummaryResponse>>())!;
    }

    private Task<HttpStatusCode> StatusOf(HttpMethod method, string path, object? body, params string[] permissions) =>
        StatusOf(method, path, body, _agencyA, permissions);

    private async Task<HttpStatusCode> StatusOf(HttpMethod method, string path, object? body, Guid agencyId, params string[] permissions)
    {
        using var response = await SendAsync(method, path, body, agencyId, permissions);
        return response.StatusCode;
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string path,
        object? body,
        Guid agencyId,
        params string[] permissions)
    {
        using var request = new HttpRequestMessage(method, path);

        if (body is not null)
        {
            request.Content = JsonContent.Create(body, body.GetType());
        }

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TokenFor(agencyId, permissions));

        return await _api.SendAsync(request);
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    private static List<string> ProblemFields(JsonElement body) =>
        body.GetProperty("publishProblems").EnumerateArray()
            .Select(problem => problem.GetProperty("field").GetString()!)
            .ToList();

    private static List<string> ErrorFields(JsonElement body) =>
        body.GetProperty("errors").EnumerateObject().Select(property => property.Name).ToList();

    /// <summary>A real token from the API's own issuer, carrying exactly <paramref name="permissions"/>.</summary>
    private string TokenFor(Guid agencyId, IReadOnlyCollection<string> permissions)
    {
        var issuer = _factory.Services.GetRequiredService<IAccessTokenIssuer>();
        var user = User.ForAgency(agencyId, "catalog@lagos-travel.test", "not-a-real-hash", "Ada", "Obi");

        return issuer.Issue(user, ["Manager"], permissions, agencyId).Value;
    }

    /// <summary>An image that went through the whole pipeline: uploaded, scanned clean, processed.</summary>
    private static Asset ReadyImage(Guid agencyId, DateTimeOffset now, string fileName)
    {
        var asset = Asset.Reserve(agencyId, AssetPurpose.ProductMedia, fileName, now.AddMinutes(15));
        asset.RecordUpload("image/jpeg", 250_000);
        asset.TryBeginProcessing(now);
        asset.RecordCleanScan(now);
        asset.MarkReady(1_600, 1_067, now);
        return asset;
    }

    /// <summary>An image whose bytes have arrived and which the scanner has not looked at yet.</summary>
    private static Asset PendingImage(Guid agencyId, DateTimeOffset now)
    {
        var asset = Asset.Reserve(agencyId, AssetPurpose.ProductMedia, "unscanned.jpg", now.AddMinutes(15));
        asset.RecordUpload("image/jpeg", 250_000);
        return asset;
    }

    private static AssetVariant[] Renditions(Asset asset) =>
    [
        AssetVariant.Create(
            asset.AgencyId,
            asset.Id,
            AssetVariantKind.Medium,
            AssetRules.VariantKey(asset.AgencyId, asset.Id, AssetVariantKind.Medium),
            AssetRules.VariantContentType,
            1_024,
            683,
            80_000),
        AssetVariant.Create(
            asset.AgencyId,
            asset.Id,
            AssetVariantKind.Thumbnail,
            AssetRules.VariantKey(asset.AgencyId, asset.Id, AssetVariantKind.Thumbnail),
            AssetRules.VariantContentType,
            320,
            213,
            12_000),
    ];
}
