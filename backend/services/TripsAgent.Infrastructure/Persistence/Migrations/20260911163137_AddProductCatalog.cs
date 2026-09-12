using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TripsAgent.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// The catalog of agent-authored products — tours, packages and visas — and everything they are
    /// made of. Issue #160, plan §2.5.
    /// </summary>
    /// <remarks>
    /// Everything below the generated tables is hand-written, because EF Core can express none of
    /// it: the CHECK constraints, the grants to the application role and row-level security.
    /// <b>Regenerating this migration drops those blocks</b> — carry them across, and grep the new
    /// file for <c>tenant_isolation</c>, <c>FORCE ROW LEVEL SECURITY</c> and <c>GRANT</c> to prove it.
    /// </remarks>
    public partial class AddProductCatalog : Migration
    {
        /// <summary>
        /// The tables this migration creates. Every one carries its own agency_id — the child tables
        /// too, rather than trusting that they are reachable through product_id — so each is policed
        /// directly (ADR-0006).
        /// </summary>
        internal static readonly string[] PolicedTables =
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

        // The same InitPlan-wrapped calls AddRowLevelSecurity uses; see the note there.
        private const string PlatformScope = "(SELECT tenancy.platform_scope_active())";
        private const string CurrentAgency = "(SELECT tenancy.current_agency_id())";

        // The product types the pricing and order tables accept, before and after this migration.
        private const string WithoutPackage = "'Flight', 'Bus', 'Tour', 'Visa', 'GroupDeparture'";
        private const string WithPackage = "'Flight', 'Bus', 'Tour', 'Package', 'Visa', 'GroupDeparture'";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.EnsureSchema(
                name: "catalog");

            migrationBuilder.AddUniqueConstraint(
                name: "ak_assets_agency_id_id",
                schema: "platform",
                table: "assets",
                columns: new[] { "agency_id", "id" });

            migrationBuilder.CreateTable(
                name: "product_categories",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    agency_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "citext", maxLength: 100, nullable: false),
                    type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_product_categories", x => x.id);
                    table.UniqueConstraint("ak_product_categories_agency_id_id", x => new { x.agency_id, x.id });
                    table.ForeignKey(
                        name: "fk_product_categories_agencies_agency_id",
                        column: x => x.agency_id,
                        principalSchema: "tenancy",
                        principalTable: "agencies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "products",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    agency_id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    slug = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    summary = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    description = table.Column<string>(type: "character varying(20000)", maxLength: 20000, nullable: false),
                    destination_country = table.Column<string>(type: "character(2)", fixedLength: true, maxLength: 2, nullable: true),
                    destination_city = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    duration_days = table.Column<int>(type: "integer", nullable: true),
                    currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false),
                    base_price_minor = table.Column<long>(type: "bigint", nullable: false),
                    available_from = table.Column<DateOnly>(type: "date", nullable: true),
                    available_to = table.Column<DateOnly>(type: "date", nullable: true),
                    hero_asset_id = table.Column<Guid>(type: "uuid", nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    published_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    version = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_products", x => x.id);
                    table.ForeignKey(
                        name: "fk_products_agencies_agency_id",
                        column: x => x.agency_id,
                        principalSchema: "tenancy",
                        principalTable: "agencies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_products_assets_hero_asset",
                        columns: x => new { x.agency_id, x.hero_asset_id },
                        principalSchema: "platform",
                        principalTable: "assets",
                        principalColumns: new[] { "agency_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "product_category_map",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    agency_id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    category_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_product_category_map", x => x.id);
                    table.ForeignKey(
                        name: "fk_product_category_map_agencies_agency_id",
                        column: x => x.agency_id,
                        principalSchema: "tenancy",
                        principalTable: "agencies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_product_category_map_product_categories_agency_id_category_",
                        columns: x => new { x.agency_id, x.category_id },
                        principalSchema: "catalog",
                        principalTable: "product_categories",
                        principalColumns: new[] { "agency_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_product_category_map_products_product_id",
                        column: x => x.product_id,
                        principalSchema: "catalog",
                        principalTable: "products",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "product_inclusions",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    agency_id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    text = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    position = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_product_inclusions", x => x.id);
                    table.ForeignKey(
                        name: "fk_product_inclusions_agencies_agency_id",
                        column: x => x.agency_id,
                        principalSchema: "tenancy",
                        principalTable: "agencies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_product_inclusions_products_product_id",
                        column: x => x.product_id,
                        principalSchema: "catalog",
                        principalTable: "products",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "product_media",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    agency_id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    asset_id = table.Column<Guid>(type: "uuid", nullable: false),
                    position = table.Column<int>(type: "integer", nullable: false),
                    caption = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_product_media", x => x.id);
                    table.ForeignKey(
                        name: "fk_product_media_agencies_agency_id",
                        column: x => x.agency_id,
                        principalSchema: "tenancy",
                        principalTable: "agencies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_product_media_assets_agency_id_asset_id",
                        columns: x => new { x.agency_id, x.asset_id },
                        principalSchema: "platform",
                        principalTable: "assets",
                        principalColumns: new[] { "agency_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_product_media_products_product_id",
                        column: x => x.product_id,
                        principalSchema: "catalog",
                        principalTable: "products",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "product_price_variants",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    agency_id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    pax_type = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    occupancy = table.Column<int>(type: "integer", nullable: true),
                    min_group_size = table.Column<int>(type: "integer", nullable: true),
                    max_group_size = table.Column<int>(type: "integer", nullable: true),
                    price_minor = table.Column<long>(type: "bigint", nullable: false),
                    position = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_product_price_variants", x => x.id);
                    table.ForeignKey(
                        name: "fk_product_price_variants_agencies_agency_id",
                        column: x => x.agency_id,
                        principalSchema: "tenancy",
                        principalTable: "agencies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_product_price_variants_products_product_id",
                        column: x => x.product_id,
                        principalSchema: "catalog",
                        principalTable: "products",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "tour_itinerary_days",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    agency_id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    day_number = table.Column<int>(type: "integer", nullable: false),
                    title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "character varying(5000)", maxLength: 5000, nullable: false),
                    breakfast_included = table.Column<bool>(type: "boolean", nullable: false),
                    lunch_included = table.Column<bool>(type: "boolean", nullable: false),
                    dinner_included = table.Column<bool>(type: "boolean", nullable: false),
                    accommodation = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tour_itinerary_days", x => x.id);
                    table.ForeignKey(
                        name: "fk_tour_itinerary_days_agencies_agency_id",
                        column: x => x.agency_id,
                        principalSchema: "tenancy",
                        principalTable: "agencies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_tour_itinerary_days_products_product_id",
                        column: x => x.product_id,
                        principalSchema: "catalog",
                        principalTable: "products",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "visa_details",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    agency_id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    visa_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    entry_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    processing_time_days = table.Column<int>(type: "integer", nullable: false),
                    validity_days = table.Column<int>(type: "integer", nullable: false),
                    consular_fee_minor = table.Column<long>(type: "bigint", nullable: false),
                    service_fee_minor = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_visa_details", x => x.id);
                    table.ForeignKey(
                        name: "fk_visa_details_agencies_agency_id",
                        column: x => x.agency_id,
                        principalSchema: "tenancy",
                        principalTable: "agencies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_visa_details_products_product_id",
                        column: x => x.product_id,
                        principalSchema: "catalog",
                        principalTable: "products",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "visa_document_requirements",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    agency_id = table.Column<Guid>(type: "uuid", nullable: false),
                    visa_details_id = table.Column<Guid>(type: "uuid", nullable: false),
                    label = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    is_mandatory = table.Column<bool>(type: "boolean", nullable: false),
                    position = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_visa_document_requirements", x => x.id);
                    table.ForeignKey(
                        name: "fk_visa_document_requirements_agencies_agency_id",
                        column: x => x.agency_id,
                        principalSchema: "tenancy",
                        principalTable: "agencies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_visa_document_requirements_visa_details_visa_details_id",
                        column: x => x.visa_details_id,
                        principalSchema: "catalog",
                        principalTable: "visa_details",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_product_categories_agency_id_type_name",
                schema: "catalog",
                table: "product_categories",
                columns: new[] { "agency_id", "type", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_product_category_map_agency_id_category_id",
                schema: "catalog",
                table: "product_category_map",
                columns: new[] { "agency_id", "category_id" });

            migrationBuilder.CreateIndex(
                name: "ix_product_category_map_product_id_category_id",
                schema: "catalog",
                table: "product_category_map",
                columns: new[] { "product_id", "category_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_product_inclusions_agency_id",
                schema: "catalog",
                table: "product_inclusions",
                column: "agency_id");

            migrationBuilder.CreateIndex(
                name: "ix_product_inclusions_product_id",
                schema: "catalog",
                table: "product_inclusions",
                column: "product_id");

            migrationBuilder.CreateIndex(
                name: "ix_product_media_agency_id_asset_id",
                schema: "catalog",
                table: "product_media",
                columns: new[] { "agency_id", "asset_id" });

            migrationBuilder.CreateIndex(
                name: "ix_product_media_product_id_asset_id",
                schema: "catalog",
                table: "product_media",
                columns: new[] { "product_id", "asset_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_product_price_variants_agency_id",
                schema: "catalog",
                table: "product_price_variants",
                column: "agency_id");

            migrationBuilder.CreateIndex(
                name: "ix_product_price_variants_product_id",
                schema: "catalog",
                table: "product_price_variants",
                column: "product_id");

            migrationBuilder.CreateIndex(
                name: "ix_products_agency_id_hero_asset_id",
                schema: "catalog",
                table: "products",
                columns: new[] { "agency_id", "hero_asset_id" });

            migrationBuilder.CreateIndex(
                name: "ix_products_agency_id_slug",
                schema: "catalog",
                table: "products",
                columns: new[] { "agency_id", "slug" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_products_agency_id_status",
                schema: "catalog",
                table: "products",
                columns: new[] { "agency_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_tour_itinerary_days_agency_id",
                schema: "catalog",
                table: "tour_itinerary_days",
                column: "agency_id");

            migrationBuilder.CreateIndex(
                name: "ix_tour_itinerary_days_product_id_day_number",
                schema: "catalog",
                table: "tour_itinerary_days",
                columns: new[] { "product_id", "day_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_visa_details_agency_id",
                schema: "catalog",
                table: "visa_details",
                column: "agency_id");

            migrationBuilder.CreateIndex(
                name: "ix_visa_details_product_id",
                schema: "catalog",
                table: "visa_details",
                column: "product_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_visa_document_requirements_agency_id",
                schema: "catalog",
                table: "visa_document_requirements",
                column: "agency_id");

            migrationBuilder.CreateIndex(
                name: "ix_visa_document_requirements_visa_details_id",
                schema: "catalog",
                table: "visa_document_requirements",
                column: "visa_details_id");

            // ------------------------------------------------------------------------ shape
            //
            // The domain refuses all of this first; these are for the writes that get past it — a
            // script, a hand-fix in psql, a future bug. A CHECK passes when its expression is NULL,
            // not only when it is true, so where NULL is allowed it is said out loud ("x IS NULL
            // OR ..."), and where a column is NOT NULL the expression can never be NULL anyway.
            migrationBuilder.Sql("""
                ALTER TABLE catalog.products
                    ADD CONSTRAINT ck_products_product_type
                        CHECK (product_type IN ('Tour', 'Package', 'Visa')),
                    ADD CONSTRAINT ck_products_status
                        CHECK (status IN ('Draft', 'Published', 'Archived')),
                    ADD CONSTRAINT ck_products_slug_shape
                        CHECK (slug ~ '^[a-z0-9]+(-[a-z0-9]+)*$'),
                    ADD CONSTRAINT ck_products_currency
                        CHECK (currency ~ '^[A-Z]{3}$'),
                    ADD CONSTRAINT ck_products_destination_country
                        CHECK (destination_country IS NULL OR destination_country ~ '^[A-Z]{2}$'),
                    ADD CONSTRAINT ck_products_base_price_not_negative
                        CHECK (base_price_minor >= 0),
                    ADD CONSTRAINT ck_products_duration
                        CHECK (duration_days IS NULL OR duration_days BETWEEN 1 AND 365),
                    -- An open end on either side is fine; two real dates must run forwards.
                    ADD CONSTRAINT ck_products_window_runs_forwards
                        CHECK (available_from IS NULL OR available_to IS NULL OR available_to >= available_from),
                    -- published_at is set exactly while the product is live. Neither side of the = can
                    -- be NULL (status is NOT NULL; IS NOT NULL never is), so this cannot pass by default.
                    ADD CONSTRAINT ck_products_published_at_matches_status
                        CHECK ((status = 'Published') = (published_at IS NOT NULL)),
                    ADD CONSTRAINT ck_products_version_not_negative
                        CHECK (version >= 0);

                ALTER TABLE catalog.product_media
                    ADD CONSTRAINT ck_product_media_position
                        CHECK (position >= 0);

                ALTER TABLE catalog.product_categories
                    ADD CONSTRAINT ck_product_categories_type
                        CHECK (type IN ('Category', 'Theme')),
                    -- citext has no length of its own, so the domain's 100 characters is said here.
                    ADD CONSTRAINT ck_product_categories_name
                        CHECK (btrim(name::text) <> '' AND length(name::text) <= 100);

                ALTER TABLE catalog.tour_itinerary_days
                    ADD CONSTRAINT ck_tour_itinerary_days_day_number
                        CHECK (day_number >= 1);

                ALTER TABLE catalog.product_inclusions
                    ADD CONSTRAINT ck_product_inclusions_kind
                        CHECK (kind IN ('Inclusion', 'Exclusion')),
                    ADD CONSTRAINT ck_product_inclusions_text
                        CHECK (btrim(text) <> ''),
                    ADD CONSTRAINT ck_product_inclusions_position
                        CHECK (position >= 0);

                ALTER TABLE catalog.product_price_variants
                    ADD CONSTRAINT ck_product_price_variants_pax_type
                        CHECK (pax_type IN ('Adult', 'Child', 'Infant')),
                    ADD CONSTRAINT ck_product_price_variants_name
                        CHECK (btrim(name) <> ''),
                    ADD CONSTRAINT ck_product_price_variants_price_not_negative
                        CHECK (price_minor >= 0),
                    ADD CONSTRAINT ck_product_price_variants_occupancy
                        CHECK (occupancy IS NULL OR occupancy BETWEEN 1 AND 10),
                    ADD CONSTRAINT ck_product_price_variants_group_sizes
                        CHECK ((min_group_size IS NULL OR min_group_size >= 1)
                           AND (max_group_size IS NULL OR max_group_size >= 1)),
                    -- NULL is an open end; only two real bounds can be the wrong way round.
                    ADD CONSTRAINT ck_product_price_variants_group_range
                        CHECK (min_group_size IS NULL OR max_group_size IS NULL OR min_group_size <= max_group_size),
                    ADD CONSTRAINT ck_product_price_variants_position
                        CHECK (position >= 0);

                ALTER TABLE catalog.visa_details
                    ADD CONSTRAINT ck_visa_details_entry_type
                        CHECK (entry_type IN ('Single', 'Multiple')),
                    -- Zero means "not filled in yet" on a draft; publishing asks for more than zero.
                    ADD CONSTRAINT ck_visa_details_days_not_negative
                        CHECK (processing_time_days >= 0 AND validity_days >= 0),
                    -- Two columns, never one total: the consular fee is the embassy's money passed
                    -- through, the service fee is the agency's own (open questions 2 and 4).
                    ADD CONSTRAINT ck_visa_details_fees_not_negative
                        CHECK (consular_fee_minor >= 0 AND service_fee_minor >= 0);

                ALTER TABLE catalog.visa_document_requirements
                    ADD CONSTRAINT ck_visa_document_requirements_label
                        CHECK (btrim(label) <> ''),
                    ADD CONSTRAINT ck_visa_document_requirements_position
                        CHECK (position >= 0);
                """);

            // ------------------------------------------------------------------ the application role
            //
            // catalog is a new schema, so AddRowLevelSecurity's default privileges never covered it.
            migrationBuilder.Sql($"""
                GRANT USAGE ON SCHEMA catalog TO {AddRowLevelSecurity.ApplicationRole};

                -- No DELETE: a product is archived, never deleted, because order lines point at it.
                GRANT SELECT, INSERT, UPDATE ON catalog.products TO {AddRowLevelSecurity.ApplicationRole};

                -- No DELETE either: a category in use would silently empty a storefront filter.
                GRANT SELECT, INSERT, UPDATE ON catalog.product_categories TO {AddRowLevelSecurity.ApplicationRole};

                -- The rows a product is made of are replaced when it is saved, so these do get DELETE.
                GRANT SELECT, INSERT, UPDATE, DELETE ON
                    catalog.product_media,
                    catalog.product_category_map,
                    catalog.tour_itinerary_days,
                    catalog.product_inclusions,
                    catalog.product_price_variants,
                    catalog.visa_details,
                    catalog.visa_document_requirements
                    TO {AddRowLevelSecurity.ApplicationRole};
                """);

            // ------------------------------------------------------------------ row-level security
            foreach (var table in PolicedTables)
            {
                migrationBuilder.Sql($"""
                    ALTER TABLE {table} ENABLE ROW LEVEL SECURITY;
                    -- FORCE: without it the table's owner is exempt, and a deployment that connects as
                    -- the owner would be silently unpoliced.
                    ALTER TABLE {table} FORCE ROW LEVEL SECURITY;
                    CREATE POLICY tenant_isolation ON {table}
                        USING ({PlatformScope} OR agency_id = {CurrentAgency})
                        WITH CHECK ({PlatformScope} OR agency_id = {CurrentAgency});
                    """);
            }

            // ------------------------------------------------------------- packages can be sold
            //
            // A catalog product can be a Package, so a markup rule can target packages, a quote can
            // price one and an order line can sell one. The product-type columns elsewhere list the
            // names they accept, and until now Package was not among them.
            migrationBuilder.Sql($"""
                ALTER TABLE pricing.markup_rules
                    DROP CONSTRAINT ck_markup_rules_product_type,
                    ADD CONSTRAINT ck_markup_rules_product_type
                        CHECK (product_type IS NULL OR product_type IN ({WithPackage}));

                ALTER TABLE pricing.price_quotes
                    DROP CONSTRAINT ck_price_quotes_product_type,
                    ADD CONSTRAINT ck_price_quotes_product_type
                        CHECK (product_type IN ({WithPackage}));

                ALTER TABLE orders.order_lines
                    DROP CONSTRAINT ck_order_lines_item_type,
                    ADD CONSTRAINT ck_order_lines_item_type
                        CHECK (item_type IN ({WithPackage}));

                ALTER TABLE orders.cart_items
                    DROP CONSTRAINT ck_cart_items_item_type,
                    ADD CONSTRAINT ck_cart_items_item_type
                        CHECK (item_type IN ({WithPackage}));
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            // Fails if a package has been priced or sold since — which is right: dropping the type
            // would leave those rows describing something the schema no longer allows.
            migrationBuilder.Sql($"""
                ALTER TABLE orders.cart_items
                    DROP CONSTRAINT ck_cart_items_item_type,
                    ADD CONSTRAINT ck_cart_items_item_type
                        CHECK (item_type IN ({WithoutPackage}));

                ALTER TABLE orders.order_lines
                    DROP CONSTRAINT ck_order_lines_item_type,
                    ADD CONSTRAINT ck_order_lines_item_type
                        CHECK (item_type IN ({WithoutPackage}));

                ALTER TABLE pricing.price_quotes
                    DROP CONSTRAINT ck_price_quotes_product_type,
                    ADD CONSTRAINT ck_price_quotes_product_type
                        CHECK (product_type IN ({WithoutPackage}));

                ALTER TABLE pricing.markup_rules
                    DROP CONSTRAINT ck_markup_rules_product_type,
                    ADD CONSTRAINT ck_markup_rules_product_type
                        CHECK (product_type IS NULL OR product_type IN ({WithoutPackage}));
                """);

            // The CHECKs and grants go with their tables; the policies are dropped by name first so
            // the Down reads as the exact reverse of the Up.
            foreach (var table in PolicedTables)
            {
                migrationBuilder.Sql($"DROP POLICY IF EXISTS tenant_isolation ON {table};");
            }

            migrationBuilder.DropTable(
                name: "product_category_map",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "product_inclusions",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "product_media",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "product_price_variants",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "tour_itinerary_days",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "visa_document_requirements",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "product_categories",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "visa_details",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "products",
                schema: "catalog");

            migrationBuilder.DropUniqueConstraint(
                name: "ak_assets_agency_id_id",
                schema: "platform",
                table: "assets");
        }
    }
}
