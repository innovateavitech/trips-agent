using System.Runtime.CompilerServices;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using TripsAgent.Application.Tenancy;
using TripsAgent.Domain.Assets;
using TripsAgent.Domain.Catalog;
using TripsAgent.Domain.Common;
using TripsAgent.Domain.Tenancy;
using TripsAgent.Infrastructure.Persistence;
using TripsAgent.Infrastructure.Tenancy;
using TripsAgent.IntegrationTests.Persistence;

namespace TripsAgent.IntegrationTests.Catalog;

/// <summary>
/// What PostgreSQL itself holds true about the catalog, whatever the application does: tenant
/// isolation on every table, the shape of every row, and which agency's images and categories a
/// product may point at. Issue #160, ADR-0006.
/// </summary>
/// <remarks>
/// Raw SQL on purpose, and as the policed application role wherever the claim is about tenants — a
/// superuser skips every policy, so a test run as one would pass whether the policies worked or not.
/// </remarks>
[Collection(PostgresCollection.Name)]
public sealed class CatalogSchemaTests
{
    private const string CheckViolation = "23514";
    private const string UniqueViolation = "23505";
    private const string ForeignKeyViolation = "23503";
    private const string InsufficientPrivilege = "42501";

    private static readonly string[] CatalogTables =
    [
        "catalog.products",
        "catalog.product_media",
        "catalog.product_categories",
        "catalog.product_category_map",
        "catalog.tour_itinerary_days",
        "catalog.product_inclusions",
        "catalog.product_price_variants",
        "catalog.visa_details",
        "catalog.visa_document_requirements",
    ];

    private readonly PostgresFixture _postgres;

    public CatalogSchemaTests(PostgresFixture postgres) => _postgres = postgres;

    // ------------------------------------------------------------------ row-level security

    [Fact]
    public async Task Every_catalog_table_has_its_own_agency_id_and_a_forced_tenant_isolation_policy()
    {
        var world = await WorldAsync();

        var policed = await world.AdminListAsync(
            """
            SELECT n.nspname || '.' || c.relname
              FROM pg_class c
              JOIN pg_namespace n ON n.oid = c.relnamespace
             WHERE n.nspname = 'catalog'
               AND c.relkind = 'r'
               AND c.relrowsecurity
               AND c.relforcerowsecurity
               AND EXISTS (SELECT 1 FROM pg_policies p
                            WHERE p.schemaname = n.nspname AND p.tablename = c.relname
                              AND p.policyname = 'tenant_isolation')
               AND EXISTS (SELECT 1 FROM information_schema.columns col
                            WHERE col.table_schema = n.nspname AND col.table_name = c.relname
                              AND col.column_name = 'agency_id' AND col.is_nullable = 'NO')
            """);

        // The hand-written half of the migration, proved present: `ef migrations add` drops it.
        policed.Should().BeEquivalentTo(CatalogTables);
    }

    [Fact]
    public async Task Agency_B_sees_none_of_agency_As_catalog_through_EF_or_raw_SQL()
    {
        var world = await WorldAsync();

        await using (var asA = world.ActingAs(world.AgencyA))
        {
            // The control: the rows are there, and A can see them.
            (await asA.Products.IgnoreQueryFilters().CountAsync()).Should().Be(2);
            (await CountAsync(asA, "catalog.tour_itinerary_days")).Should().BeGreaterThan(0);
        }

        await using var asB = world.ActingAs(world.AgencyB);

        // The EF filter switched off on purpose: row-level security still hides every row.
        (await asB.Products.IgnoreQueryFilters().CountAsync()).Should().Be(0);
        (await asB.TourItineraryDays.IgnoreQueryFilters().CountAsync()).Should().Be(0);
        (await asB.ProductMedia.IgnoreQueryFilters().CountAsync()).Should().Be(0);

        foreach (var table in CatalogTables.Where(table => table != "catalog.product_categories"))
        {
            (await CountAsync(asB, table)).Should().Be(0, $"{table} belongs to agency A");
        }

        // B has a category of its own, and sees exactly that one.
        (await CountAsync(asB, "catalog.product_categories")).Should().Be(1);
    }

    [Fact]
    public async Task A_tenant_cannot_write_a_row_into_another_agencys_product()
    {
        var world = await WorldAsync();
        await using var asB = world.ActingAs(world.AgencyB);

        var act = () => asB.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO catalog.product_inclusions (id, agency_id, product_id, kind, text, position)
            VALUES (gen_random_uuid(), {0}, {1}, 'Inclusion', 'A line slipped into a competitor', 99)
            """,
            world.AgencyA,
            world.TourId);

        (await act.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(InsufficientPrivilege);
    }

    [Fact]
    public async Task The_application_can_replace_a_products_rows_but_can_never_delete_a_product()
    {
        var world = await WorldAsync();
        await using var asA = world.ActingAs(world.AgencyA);

        var deleteRows = await asA.Database.ExecuteSqlRawAsync(
            "DELETE FROM catalog.product_inclusions WHERE product_id = {0}", world.TourId);
        deleteRows.Should().BeGreaterThan(0, "a save replaces the lines a product is made of");

        var deleteProduct = () => asA.Database.ExecuteSqlRawAsync(
            "DELETE FROM catalog.products WHERE id = {0}", world.TourId);

        (await deleteProduct.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be(InsufficientPrivilege, "products are archived, never deleted: order lines point at them");
    }

    // ------------------------------------------------------------------ uniqueness

    [Fact]
    public async Task A_slug_is_unique_within_an_agency_but_two_agencies_can_share_one()
    {
        var world = await WorldAsync();

        var again = () => world.Owner.Database.ExecuteSqlRawAsync(InsertProduct, world.AgencyA, "zanzibar-escape");

        (await again.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(UniqueViolation);

        (await world.Owner.Database.ExecuteSqlRawAsync(InsertProduct, world.AgencyB, "zanzibar-escape"))
            .Should().Be(1, "another agency's storefront can have the same address");
    }

    [Fact]
    public async Task A_day_number_is_unique_within_a_product()
    {
        var world = await WorldAsync();

        var act = () => world.Owner.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO catalog.tour_itinerary_days
                (id, agency_id, product_id, day_number, title, description,
                 breakfast_included, lunch_included, dinner_included)
            VALUES (gen_random_uuid(), {0}, {1}, 1, 'Day one, again', '', false, false, false)
            """,
            world.AgencyA,
            world.TourId);

        (await act.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(UniqueViolation);
    }

    // ------------------------------------------------------------------ shape

    [Theory]
    [InlineData("UPDATE catalog.products SET base_price_minor = -1")]
    [InlineData("UPDATE catalog.products SET status = 'Live'")]
    [InlineData("UPDATE catalog.products SET product_type = 'Hotel'")]
    [InlineData("UPDATE catalog.products SET published_at = now()")]
    [InlineData("UPDATE catalog.products SET status = 'Published'")]
    [InlineData("UPDATE catalog.products SET slug = 'Not A Slug'")]
    [InlineData("UPDATE catalog.products SET currency = 'ngn'")]
    [InlineData("UPDATE catalog.products SET destination_country = 'ke'")]
    [InlineData("UPDATE catalog.products SET duration_days = 0")]
    [InlineData("UPDATE catalog.products SET available_from = '2026-10-10', available_to = '2026-10-01'")]
    [InlineData("UPDATE catalog.product_media SET position = -1")]
    [InlineData("UPDATE catalog.tour_itinerary_days SET day_number = 0 WHERE day_number = 1")]
    [InlineData("UPDATE catalog.product_inclusions SET kind = 'Maybe'")]
    [InlineData("UPDATE catalog.product_inclusions SET text = '   '")]
    [InlineData("UPDATE catalog.product_price_variants SET price_minor = -1")]
    [InlineData("UPDATE catalog.product_price_variants SET pax_type = 'Senior'")]
    [InlineData("UPDATE catalog.product_price_variants SET occupancy = 0")]
    [InlineData("UPDATE catalog.product_price_variants SET min_group_size = 5, max_group_size = 2")]
    [InlineData("UPDATE catalog.visa_details SET entry_type = 'Triple'")]
    [InlineData("UPDATE catalog.visa_details SET consular_fee_minor = -1")]
    [InlineData("UPDATE catalog.visa_details SET processing_time_days = -1")]
    [InlineData("UPDATE catalog.visa_document_requirements SET label = ''")]
    [InlineData("UPDATE catalog.product_categories SET type = 'Tag'")]
    [InlineData("UPDATE catalog.product_categories SET name = '  '")]
    public async Task A_row_that_breaks_the_shape_is_refused_even_for_the_table_owner(string statement)
    {
        var world = await WorldAsync();

        var act = () => world.Owner.Database.ExecuteSqlRawAsync(statement);

        (await act.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(CheckViolation);
    }

    [Fact]
    public async Task Status_and_published_at_move_together()
    {
        var world = await WorldAsync();

        var published = await world.Owner.Database.ExecuteSqlRawAsync(
            "UPDATE catalog.products SET status = 'Published', published_at = now() WHERE id = {0}", world.TourId);

        published.Should().Be(1, "published_at is required exactly when the product is live");
    }

    // ------------------------------------------------------------------ whose images, whose categories

    [Fact]
    public async Task Another_agencys_image_cannot_be_attached_even_by_the_table_owner()
    {
        var world = await WorldAsync();

        // Foreign-key checks run as the owner and skip row-level security, so a plain key would
        // accept this. The composite key (agency_id, asset_id) is what refuses it.
        var gallery = () => world.Owner.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO catalog.product_media (id, agency_id, product_id, asset_id, position)
            VALUES (gen_random_uuid(), {0}, {1}, {2}, 5)
            """,
            world.AgencyA,
            world.TourId,
            world.ImageB);

        var cover = () => world.Owner.Database.ExecuteSqlRawAsync(
            "UPDATE catalog.products SET hero_asset_id = {0} WHERE id = {1}", world.ImageB, world.TourId);

        (await gallery.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(ForeignKeyViolation);
        (await cover.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(ForeignKeyViolation);
    }

    [Fact]
    public async Task Another_agencys_category_cannot_be_tagged_even_by_the_table_owner()
    {
        var world = await WorldAsync();

        var act = () => world.Owner.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO catalog.product_category_map (id, agency_id, product_id, category_id)
            VALUES (gen_random_uuid(), {0}, {1}, {2})
            """,
            world.AgencyA,
            world.TourId,
            world.CategoryB);

        (await act.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(ForeignKeyViolation);
    }

    [Fact]
    public async Task An_image_in_a_gallery_cannot_be_deleted_from_under_it()
    {
        var world = await WorldAsync();

        var act = () => world.Owner.Database.ExecuteSqlRawAsync(
            "DELETE FROM platform.assets WHERE id = {0}", world.ImageA);

        (await act.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(ForeignKeyViolation);
    }

    // ------------------------------------------------------------------ concurrency

    [Fact]
    public async Task Two_saves_of_the_same_version_cannot_both_win()
    {
        var world = await WorldAsync();

        await using var first = world.ActingAs(world.AgencyA);
        await using var second = world.ActingAs(world.AgencyA);

        var mine = await first.Products.SingleAsync(product => product.Id == world.TourId);
        var theirs = await second.Products.SingleAsync(product => product.Id == world.TourId);

        mine.Archive();
        await first.SaveChangesAsync();

        theirs.Archive();
        var act = () => second.SaveChangesAsync();

        await act.Should().ThrowAsync<DbUpdateConcurrencyException>(
            "the second save was made against a version that no longer exists");
    }

    // ------------------------------------------------------------------ packages

    [Fact]
    public async Task Packages_can_be_priced_and_sold()
    {
        var world = await WorldAsync();

        var definitions = await world.AdminListAsync(
            """
            SELECT pg_get_constraintdef(oid)
              FROM pg_constraint
             WHERE conname IN ('ck_markup_rules_product_type', 'ck_price_quotes_product_type',
                               'ck_order_lines_item_type', 'ck_cart_items_item_type')
            """);

        definitions.Should().HaveCount(4).And.OnlyContain(definition => definition.Contains("'Package'"));
    }

    // ------------------------------------------------------------------ helpers

    private const string InsertProduct =
        """
        INSERT INTO catalog.products
            (id, agency_id, product_type, title, slug, summary, description, currency,
             base_price_minor, status, version, created_at, updated_at)
        VALUES (gen_random_uuid(), {0}, 'Tour', 'Another tour', {1}, '', '', 'NGN', 0, 'Draft', 0, now(), now())
        """;

    // EF1002 guards against user input reaching raw SQL. `table` is always one of the CatalogTables
    // constants above, and a table name cannot be sent as a parameter.
#pragma warning disable EF1002
    private static Task<long> CountAsync(AppDbContext db, string table) =>
        db.Database.SqlQueryRaw<long>($"SELECT count(*) AS \"Value\" FROM {table}").SingleAsync();
#pragma warning restore EF1002

    private async Task<World> WorldAsync([CallerMemberName] string testName = "")
    {
        var name = $"catalog_{testName.ToLowerInvariant()}";
        name = name[..Math.Min(name.Length, 60)];

        var tenancy = TestTenancy.None();
        var now = DateTimeOffset.UtcNow;

        await using var setup = await _postgres.CreateEmptyDatabaseAsync(name, tenancy.Tenant, tenancy.Scope);
        await setup.Database.MigrateAsync();

        using var _ = tenancy.Scope.Enter("test setup: two agencies, a catalog for one of them");

        var a = Agency.RegisterPrincipal("Lagos Travel Limited", "lagos-travel", "NG", "NGN", "Africa/Lagos");
        var b = Agency.RegisterPrincipal("Abuja Tours Limited", "abuja-tours", "NG", "NGN", "Africa/Lagos");

        var imageA = ReadyImage(a.Id, now);
        var imageB = ReadyImage(b.Id, now);
        var categoryA = ProductCategory.Create(a.Id, "Beach holidays", CategoryType.Category);
        var categoryB = ProductCategory.Create(b.Id, "Beach holidays", CategoryType.Category);

        setup.Agencies.AddRange(a, b);
        setup.Assets.AddRange(imageA, imageB);
        setup.ProductCategories.AddRange(categoryA, categoryB);
        await setup.SaveChangesAsync();

        var today = DateOnly.FromDateTime(now.UtcDateTime);

        var tour = Product.CreateDraft(
            a.Id,
            new ProductContent
            {
                ProductType = ProductType.Tour,
                Title = "Zanzibar Escape",
                Currency = "NGN",
                BasePriceMinor = new Money(15_000_000),
                AvailableFrom = today.AddDays(10),
                HeroAssetId = imageA.Id,
                Media = [new ProductMediaContent(imageA.Id, "Nungwi beach")],
                CategoryIds = [categoryA.Id],
                Itinerary =
                [
                    new ItineraryDayContent(1, "Arrival", "Transfer to Stone Town.", [Meal.Dinner], "Stone Town hotel"),
                    new ItineraryDayContent(2, "Departure", "Transfer to the airport.", [Meal.Breakfast], null),
                ],
                Inclusions = [new InclusionContent(InclusionKind.Inclusion, "Airport transfers")],
                PriceVariants = [new PriceVariantContent("Double occupancy", PaxType.Adult, 2, 1, 10, new Money(15_000_000))],
            },
            "zanzibar-escape");

        var visa = Product.CreateDraft(
            a.Id,
            new ProductContent
            {
                ProductType = ProductType.Visa,
                Title = "UK Standard Visitor Visa",
                Currency = "NGN",
                Visa = new VisaContent(
                    "Tourist",
                    VisaEntryType.Multiple,
                    15,
                    180,
                    new Money(17_500_000),
                    new Money(5_000_000),
                    [new VisaDocumentContent("Passport valid for six months", true)]),
            },
            "uk-visitor-visa");

        setup.Products.AddRange(tour, visa);
        await setup.SaveChangesAsync();

        return new World(_postgres, name, a.Id, b.Id, tour.Id, imageA.Id, imageB.Id, categoryB.Id);
    }

    /// <summary>An image that went through the whole pipeline: uploaded, scanned clean, processed.</summary>
    private static Asset ReadyImage(Guid agencyId, DateTimeOffset now)
    {
        var asset = Asset.Reserve(agencyId, AssetPurpose.ProductMedia, "beach.jpg", now.AddMinutes(15));
        asset.RecordUpload("image/jpeg", 250_000);
        asset.TryBeginProcessing(now);
        asset.RecordCleanScan(now);
        asset.MarkReady(1_600, 1_067, now);
        return asset;
    }

    private sealed class World
    {
        private readonly PostgresFixture _postgres;
        private readonly string _database;

        public World(
            PostgresFixture postgres,
            string database,
            Guid agencyA,
            Guid agencyB,
            Guid tourId,
            Guid imageA,
            Guid imageB,
            Guid categoryB)
        {
            _postgres = postgres;
            _database = database;
            AgencyA = agencyA;
            AgencyB = agencyB;
            TourId = tourId;
            ImageA = imageA;
            ImageB = imageB;
            CategoryB = categoryB;

            // The schema owner: it bypasses row-level security, so it is the right connection for
            // the shape and foreign-key checks and the wrong one for anything about tenants.
            Owner = postgres.Connect(database, asApplicationRole: false);
        }

        public Guid AgencyA { get; }

        public Guid AgencyB { get; }

        public Guid TourId { get; }

        public Guid ImageA { get; }

        public Guid ImageB { get; }

        public Guid CategoryB { get; }

        public AppDbContext Owner { get; }

        /// <summary>A context acting as <paramref name="agencyId"/>, as the policed application role.</summary>
        public AppDbContext ActingAs(Guid agencyId)
        {
            var (tenant, scope) = TestTenancy.For(agencyId);
            return _postgres.Connect(_database, tenant, scope);
        }

        public async Task<List<string>> AdminListAsync(string sql)
        {
            await using var connection = new NpgsqlConnection(_postgres.ConnectionStringFor(_database, asApplicationRole: false));
            await connection.OpenAsync();

            await using var command = new NpgsqlCommand(sql, connection);
            await using var reader = await command.ExecuteReaderAsync();

            var rows = new List<string>();
            while (await reader.ReadAsync())
            {
                rows.Add(reader.GetString(0));
            }

            return rows;
        }
    }
}
