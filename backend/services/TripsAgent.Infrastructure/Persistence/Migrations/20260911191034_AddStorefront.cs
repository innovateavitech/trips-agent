using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TripsAgent.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddStorefront : Migration
    {
        /// <summary>The tables this migration creates that belong to one agency through agency_id.</summary>
        internal static readonly string[] PolicedTables =
        [
            "storefront.sites",
            "storefront.site_versions",
            "storefront.site_pages",
            "storefront.site_blocks",
            "storefront.site_themes",
            "storefront.site_domains",
            "storefront.site_domain_checks",
        ];

        // The same InitPlan-wrapped calls AddRowLevelSecurity uses; see the note there.
        private const string PlatformScope = "(SELECT tenancy.platform_scope_active())";
        private const string CurrentAgency = "(SELECT tenancy.current_agency_id())";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "storefront");

            migrationBuilder.AddColumn<string>(
                name: "contact_email",
                schema: "tenancy",
                table: "agency_branding",
                type: "character varying(254)",
                maxLength: 254,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "contact_phone",
                schema: "tenancy",
                table: "agency_branding",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "whats_app_number",
                schema: "tenancy",
                table: "agency_branding",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "reserved_hostname_labels",
                schema: "storefront",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    label = table.Column<string>(type: "character varying(63)", maxLength: 63, nullable: false),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    reason = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_reserved_hostname_labels", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "site_templates",
                schema: "storefront",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    description = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    preview_image_url = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    block_schema = table.Column<string>(type: "jsonb", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_site_templates", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "sites",
                schema: "storefront",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    agency_id = table.Column<Guid>(type: "uuid", nullable: false),
                    template_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    seo_title = table.Column<string>(type: "character varying(70)", maxLength: 70, nullable: true),
                    seo_description = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                    language = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    flight_search_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    analytics_ids = table.Column<string>(type: "jsonb", nullable: false),
                    draft_version_id = table.Column<Guid>(type: "uuid", nullable: true),
                    published_version_id = table.Column<Guid>(type: "uuid", nullable: true),
                    primary_domain_id = table.Column<Guid>(type: "uuid", nullable: true),
                    version = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sites", x => x.id);
                    table.ForeignKey(
                        name: "fk_sites_agencies_agency_id",
                        column: x => x.agency_id,
                        principalSchema: "tenancy",
                        principalTable: "agencies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_sites_site_templates_template_id",
                        column: x => x.template_id,
                        principalSchema: "storefront",
                        principalTable: "site_templates",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "site_domains",
                schema: "storefront",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    agency_id = table.Column<Guid>(type: "uuid", nullable: false),
                    site_id = table.Column<Guid>(type: "uuid", nullable: false),
                    hostname = table.Column<string>(type: "citext", nullable: false),
                    type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    verification_status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    verification_token = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    verification_started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    verified_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_checked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    next_check_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    check_count = table.Column<int>(type: "integer", nullable: false),
                    ssl_status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ssl_issued_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ssl_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ssl_next_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ssl_attempt_count = table.Column<int>(type: "integer", nullable: false),
                    ssl_last_error = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    needs_review = table.Column<bool>(type: "boolean", nullable: false),
                    reviewed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    reviewed_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_site_domains", x => x.id);
                    table.ForeignKey(
                        name: "fk_site_domains_agencies_agency_id",
                        column: x => x.agency_id,
                        principalSchema: "tenancy",
                        principalTable: "agencies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_site_domains_sites_site_id",
                        column: x => x.site_id,
                        principalSchema: "storefront",
                        principalTable: "sites",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "site_themes",
                schema: "storefront",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    agency_id = table.Column<Guid>(type: "uuid", nullable: false),
                    site_id = table.Column<Guid>(type: "uuid", nullable: false),
                    typography = table.Column<string>(type: "jsonb", nullable: false),
                    colors = table.Column<string>(type: "jsonb", nullable: false),
                    custom_css = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_site_themes", x => x.id);
                    table.ForeignKey(
                        name: "fk_site_themes_agencies_agency_id",
                        column: x => x.agency_id,
                        principalSchema: "tenancy",
                        principalTable: "agencies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_site_themes_sites_site_id",
                        column: x => x.site_id,
                        principalSchema: "storefront",
                        principalTable: "sites",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "site_versions",
                schema: "storefront",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    agency_id = table.Column<Guid>(type: "uuid", nullable: false),
                    site_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version_no = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    content_snapshot = table.Column<string>(type: "jsonb", nullable: true),
                    theme_snapshot = table.Column<string>(type: "jsonb", nullable: true),
                    staged_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    staged_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    published_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    published_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    archived_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_site_versions", x => x.id);
                    table.ForeignKey(
                        name: "fk_site_versions_agencies_agency_id",
                        column: x => x.agency_id,
                        principalSchema: "tenancy",
                        principalTable: "agencies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_site_versions_sites_site_id",
                        column: x => x.site_id,
                        principalSchema: "storefront",
                        principalTable: "sites",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "site_domain_checks",
                schema: "storefront",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    agency_id = table.Column<Guid>(type: "uuid", nullable: false),
                    site_domain_id = table.Column<Guid>(type: "uuid", nullable: false),
                    checked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    kind = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    record_name = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    expected_value = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    observed_values = table.Column<string[]>(type: "text[]", nullable: false),
                    outcome = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    resolver = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    error_detail = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_site_domain_checks", x => x.id);
                    table.ForeignKey(
                        name: "fk_site_domain_checks_agencies_agency_id",
                        column: x => x.agency_id,
                        principalSchema: "tenancy",
                        principalTable: "agencies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_site_domain_checks_site_domains_site_domain_id",
                        column: x => x.site_domain_id,
                        principalSchema: "storefront",
                        principalTable: "site_domains",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "site_pages",
                schema: "storefront",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    agency_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    slug = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    page_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    title = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    is_system = table.Column<bool>(type: "boolean", nullable: false),
                    show_in_nav = table.Column<bool>(type: "boolean", nullable: false),
                    position = table.Column<int>(type: "integer", nullable: false),
                    meta_title = table.Column<string>(type: "character varying(70)", maxLength: 70, nullable: true),
                    meta_description = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                    revision = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_site_pages", x => x.id);
                    table.ForeignKey(
                        name: "fk_site_pages_agencies_agency_id",
                        column: x => x.agency_id,
                        principalSchema: "tenancy",
                        principalTable: "agencies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_site_pages_site_versions_version_id",
                        column: x => x.version_id,
                        principalSchema: "storefront",
                        principalTable: "site_versions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "site_blocks",
                schema: "storefront",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    agency_id = table.Column<Guid>(type: "uuid", nullable: false),
                    page_id = table.Column<Guid>(type: "uuid", nullable: false),
                    block_type = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    position = table.Column<int>(type: "integer", nullable: false),
                    config = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_site_blocks", x => x.id);
                    table.ForeignKey(
                        name: "fk_site_blocks_agencies_agency_id",
                        column: x => x.agency_id,
                        principalSchema: "tenancy",
                        principalTable: "agencies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_site_blocks_site_pages_page_id",
                        column: x => x.page_id,
                        principalSchema: "storefront",
                        principalTable: "site_pages",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_reserved_hostname_labels_label",
                schema: "storefront",
                table: "reserved_hostname_labels",
                column: "label",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_site_blocks_agency_id_page_id",
                schema: "storefront",
                table: "site_blocks",
                columns: new[] { "agency_id", "page_id" });

            migrationBuilder.CreateIndex(
                name: "ix_site_blocks_page_id",
                schema: "storefront",
                table: "site_blocks",
                column: "page_id");

            migrationBuilder.CreateIndex(
                name: "ix_site_domain_checks_agency_id_site_domain_id",
                schema: "storefront",
                table: "site_domain_checks",
                columns: new[] { "agency_id", "site_domain_id" });

            migrationBuilder.CreateIndex(
                name: "ix_site_domain_checks_site_domain_id_checked_at",
                schema: "storefront",
                table: "site_domain_checks",
                columns: new[] { "site_domain_id", "checked_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_site_domains_agency_id_site_id",
                schema: "storefront",
                table: "site_domains",
                columns: new[] { "agency_id", "site_id" });

            migrationBuilder.CreateIndex(
                name: "ix_site_domains_hostname",
                schema: "storefront",
                table: "site_domains",
                column: "hostname",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_site_domains_site_id",
                schema: "storefront",
                table: "site_domains",
                column: "site_id",
                unique: true,
                filter: "type = 'Subdomain'");

            migrationBuilder.CreateIndex(
                name: "ix_site_domains_ssl_status_ssl_next_attempt_at",
                schema: "storefront",
                table: "site_domains",
                columns: new[] { "ssl_status", "ssl_next_attempt_at" });

            migrationBuilder.CreateIndex(
                name: "ix_site_domains_verification_status_next_check_at",
                schema: "storefront",
                table: "site_domains",
                columns: new[] { "verification_status", "next_check_at" });

            migrationBuilder.CreateIndex(
                name: "ix_site_pages_agency_id_version_id",
                schema: "storefront",
                table: "site_pages",
                columns: new[] { "agency_id", "version_id" });

            migrationBuilder.CreateIndex(
                name: "ix_site_pages_version_id_page_type",
                schema: "storefront",
                table: "site_pages",
                columns: new[] { "version_id", "page_type" },
                unique: true,
                filter: "page_type <> 'Custom'");

            migrationBuilder.CreateIndex(
                name: "ix_site_pages_version_id_slug",
                schema: "storefront",
                table: "site_pages",
                columns: new[] { "version_id", "slug" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_site_templates_code",
                schema: "storefront",
                table: "site_templates",
                column: "code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_site_themes_agency_id",
                schema: "storefront",
                table: "site_themes",
                column: "agency_id");

            migrationBuilder.CreateIndex(
                name: "ix_site_themes_site_id",
                schema: "storefront",
                table: "site_themes",
                column: "site_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_site_versions_agency_id_site_id",
                schema: "storefront",
                table: "site_versions",
                columns: new[] { "agency_id", "site_id" });

            migrationBuilder.CreateIndex(
                name: "ix_site_versions_site_id",
                schema: "storefront",
                table: "site_versions",
                column: "site_id",
                unique: true,
                filter: "status = 'Draft'");

            migrationBuilder.CreateIndex(
                name: "ix_site_versions_site_id_version_no",
                schema: "storefront",
                table: "site_versions",
                columns: new[] { "site_id", "version_no" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_sites_agency_id",
                schema: "storefront",
                table: "sites",
                column: "agency_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_sites_template_id",
                schema: "storefront",
                table: "sites",
                column: "template_id");

            // ------------------------------------------------------------------------ shape
            //
            // Enum columns hold names, each held to the names that exist. Composite keys tie every child row
            // to a parent of its own agency, which a CHECK cannot see across tables.
            migrationBuilder.Sql("""
                ALTER TABLE storefront.site_templates
                    ADD CONSTRAINT ck_site_templates_code CHECK (code ~ '^[a-z0-9]+(-[a-z0-9]+)*$'),
                    ADD CONSTRAINT ck_site_templates_version CHECK (version >= 1),
                    ADD CONSTRAINT ck_site_templates_block_schema_is_object CHECK (jsonb_typeof(block_schema) = 'object');

                ALTER TABLE storefront.reserved_hostname_labels
                    ADD CONSTRAINT ck_reserved_hostname_labels_label CHECK (label ~ '^[a-z0-9]([a-z0-9-]*[a-z0-9])?$'),
                    ADD CONSTRAINT ck_reserved_hostname_labels_kind CHECK (kind IN ('Reserved', 'Brand'));

                ALTER TABLE storefront.sites
                    ADD CONSTRAINT ck_sites_status CHECK (status IN ('Draft', 'Published')),
                    -- Draft exactly when nothing is live, so the status can never disagree with the pointer.
                    ADD CONSTRAINT ck_sites_status_matches_published_version
                        CHECK ((status = 'Draft') = (published_version_id IS NULL)),
                    ADD CONSTRAINT ck_sites_analytics_ids_is_object CHECK (jsonb_typeof(analytics_ids) = 'object'),
                    ADD CONSTRAINT uq_sites_id_agency UNIQUE (id, agency_id);

                ALTER TABLE storefront.site_versions
                    ADD CONSTRAINT ck_site_versions_status CHECK (status IN ('Draft', 'Staged', 'Published', 'Archived')),
                    ADD CONSTRAINT ck_site_versions_draft_is_number_zero CHECK ((status = 'Draft') = (version_no = 0)),
                    ADD CONSTRAINT ck_site_versions_version_no_not_negative CHECK (version_no >= 0),
                    -- The draft's content is its page rows. Every other version is a snapshot, both halves of it.
                    ADD CONSTRAINT ck_site_versions_snapshot_present CHECK (
                        (status = 'Draft' AND content_snapshot IS NULL AND theme_snapshot IS NULL)
                        OR (status <> 'Draft' AND content_snapshot IS NOT NULL AND theme_snapshot IS NOT NULL)),
                    ADD CONSTRAINT ck_site_versions_snapshots_are_objects CHECK (
                        (content_snapshot IS NULL OR jsonb_typeof(content_snapshot) = 'object')
                        AND (theme_snapshot IS NULL OR jsonb_typeof(theme_snapshot) = 'object')),
                    ADD CONSTRAINT ck_site_versions_live_says_when CHECK (status <> 'Published' OR published_at IS NOT NULL),
                    ADD CONSTRAINT uq_site_versions_id_site UNIQUE (id, site_id),
                    ADD CONSTRAINT uq_site_versions_id_agency UNIQUE (id, agency_id),
                    ADD CONSTRAINT fk_site_versions_site_same_agency FOREIGN KEY (site_id, agency_id)
                        REFERENCES storefront.sites (id, agency_id),
                    -- At most one staged and one live version per site, checked at commit: a publish archives
                    -- the live version and puts the staged one live in the same save.
                    ADD CONSTRAINT ex_site_versions_one_staged_per_site
                        EXCLUDE USING btree (site_id WITH =) WHERE (status = 'Staged') DEFERRABLE INITIALLY DEFERRED,
                    ADD CONSTRAINT ex_site_versions_one_published_per_site
                        EXCLUDE USING btree (site_id WITH =) WHERE (status = 'Published') DEFERRABLE INITIALLY DEFERRED;

                ALTER TABLE storefront.site_pages
                    ADD CONSTRAINT ck_site_pages_page_type
                        CHECK (page_type IN ('Home', 'About', 'Contact', 'Terms', 'Catalog', 'Custom')),
                    ADD CONSTRAINT ck_site_pages_slug CHECK (slug ~ '^[a-z0-9]+(-[a-z0-9]+)*$'),
                    ADD CONSTRAINT ck_site_pages_home_is_home CHECK ((page_type = 'Home') = (slug = 'home')),
                    ADD CONSTRAINT ck_site_pages_system_matches_type CHECK (is_system = (page_type <> 'Custom')),
                    ADD CONSTRAINT ck_site_pages_position_not_negative CHECK (position >= 0),
                    ADD CONSTRAINT uq_site_pages_id_agency UNIQUE (id, agency_id),
                    ADD CONSTRAINT fk_site_pages_version_same_agency FOREIGN KEY (version_id, agency_id)
                        REFERENCES storefront.site_versions (id, agency_id);

                ALTER TABLE storefront.site_blocks
                    ADD CONSTRAINT ck_site_blocks_block_type CHECK (block_type IN ('Hero', 'ProductGrid', 'Text', 'Contact')),
                    ADD CONSTRAINT ck_site_blocks_config_is_object CHECK (jsonb_typeof(config) = 'object'),
                    ADD CONSTRAINT ck_site_blocks_position_not_negative CHECK (position >= 0),
                    ADD CONSTRAINT fk_site_blocks_page_same_agency FOREIGN KEY (page_id, agency_id)
                        REFERENCES storefront.site_pages (id, agency_id) ON DELETE CASCADE,
                    -- Deferrable, so a reorder that swaps two blocks passes through a moment of duplicates.
                    ADD CONSTRAINT uq_site_blocks_page_id_position UNIQUE (page_id, position) DEFERRABLE INITIALLY DEFERRED;

                ALTER TABLE storefront.site_themes
                    ADD CONSTRAINT ck_site_themes_typography_is_object CHECK (jsonb_typeof(typography) = 'object'),
                    ADD CONSTRAINT ck_site_themes_colors_is_object CHECK (jsonb_typeof(colors) = 'object'),
                    ADD CONSTRAINT fk_site_themes_site_same_agency FOREIGN KEY (site_id, agency_id)
                        REFERENCES storefront.sites (id, agency_id);

                ALTER TABLE storefront.site_domains
                    -- Lower-case ASCII labels, as Hostnames.TryNormalise leaves them. Compared as text: citext
                    -- would let a capital through a case-insensitive pattern.
                    ADD CONSTRAINT ck_site_domains_hostname CHECK (
                        length(hostname::text) <= 253
                        AND hostname::text ~ '^[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?(\.[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?)+$'),
                    ADD CONSTRAINT ck_site_domains_type CHECK (type IN ('Subdomain', 'Custom')),
                    ADD CONSTRAINT ck_site_domains_verification_status
                        CHECK (verification_status IN ('Pending', 'Verified', 'Abandoned')),
                    ADD CONSTRAINT ck_site_domains_ssl_status
                        CHECK (ssl_status IN ('None', 'Pending', 'Issued', 'Failed', 'Expired')),
                    -- A custom hostname is proven with its token; a free subdomain needs no proof.
                    ADD CONSTRAINT ck_site_domains_custom_has_token CHECK (type <> 'Custom' OR verification_token IS NOT NULL),
                    ADD CONSTRAINT ck_site_domains_subdomain_is_verified
                        CHECK (type <> 'Subdomain' OR verification_status = 'Verified'),
                    ADD CONSTRAINT uq_site_domains_id_site UNIQUE (id, site_id),
                    ADD CONSTRAINT uq_site_domains_id_agency UNIQUE (id, agency_id),
                    ADD CONSTRAINT fk_site_domains_site_same_agency FOREIGN KEY (site_id, agency_id)
                        REFERENCES storefront.sites (id, agency_id);

                ALTER TABLE storefront.site_domain_checks
                    ADD CONSTRAINT ck_site_domain_checks_kind CHECK (kind IN ('Txt', 'Cname')),
                    ADD CONSTRAINT ck_site_domain_checks_outcome
                        CHECK (outcome IN ('Match', 'Mismatch', 'NotFound', 'Timeout', 'ServerFailure', 'Error')),
                    ADD CONSTRAINT fk_site_domain_checks_domain_same_agency FOREIGN KEY (site_domain_id, agency_id)
                        REFERENCES storefront.site_domains (id, agency_id) ON DELETE CASCADE;

                -- The site's three pointers, each held to this site's own rows. Deferred to commit, because a
                -- site, its first draft and its free address are inserted together and refer to one another.
                ALTER TABLE storefront.sites
                    ADD CONSTRAINT fk_sites_draft_version FOREIGN KEY (draft_version_id, id)
                        REFERENCES storefront.site_versions (id, site_id) DEFERRABLE INITIALLY DEFERRED,
                    ADD CONSTRAINT fk_sites_published_version FOREIGN KEY (published_version_id, id)
                        REFERENCES storefront.site_versions (id, site_id) DEFERRABLE INITIALLY DEFERRED,
                    ADD CONSTRAINT fk_sites_primary_domain FOREIGN KEY (primary_domain_id, id)
                        REFERENCES storefront.site_domains (id, site_id) DEFERRABLE INITIALLY DEFERRED;
                """);

            // ------------------------------------------------------------- published versions are frozen
            //
            // The same guarantee hard rule 5 gives a placed order line's price, and a trigger for the same
            // reason: it binds the schema owner too, where a REVOKE would not. A rollback moves a pointer
            // and a status, never content, so it passes.
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION storefront.reject_frozen_version_change()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $$
                BEGIN
                    IF TG_OP = 'DELETE' THEN
                        RAISE EXCEPTION 'site version % cannot be deleted; versions are the site''s history', OLD.id
                            USING ERRCODE = 'restrict_violation';
                    END IF;

                    IF OLD.status = 'Draft' THEN
                        IF NEW.status <> 'Draft' THEN
                            RAISE EXCEPTION 'the draft is never staged in place; staging freezes a copy of it'
                                USING ERRCODE = 'restrict_violation';
                        END IF;

                        RETURN NEW;
                    END IF;

                    IF NEW.status = 'Draft'
                    OR NEW.content_snapshot  IS DISTINCT FROM OLD.content_snapshot
                    OR NEW.theme_snapshot    IS DISTINCT FROM OLD.theme_snapshot
                    OR NEW.version_no        IS DISTINCT FROM OLD.version_no
                    OR NEW.site_id           IS DISTINCT FROM OLD.site_id
                    OR NEW.agency_id         IS DISTINCT FROM OLD.agency_id
                    OR NEW.staged_at         IS DISTINCT FROM OLD.staged_at
                    OR NEW.staged_by_user_id IS DISTINCT FROM OLD.staged_by_user_id
                    THEN
                        RAISE EXCEPTION 'site version % is % and frozen', OLD.id, OLD.status
                            USING ERRCODE = 'restrict_violation',
                                  HINT = 'Edit the draft, stage it and publish it, or roll back to an earlier version.';
                    END IF;

                    RETURN NEW;
                END;
                $$;

                CREATE TRIGGER site_versions_frozen_trg
                    BEFORE UPDATE OR DELETE ON storefront.site_versions
                    FOR EACH ROW
                    EXECUTE FUNCTION storefront.reject_frozen_version_change();

                -- Pages and blocks change only while their version is the draft. Two functions, one per table:
                -- each reads only the columns its own table has. Both run as their owner, so the version they
                -- look up is found whatever the caller's tenant.
                CREATE OR REPLACE FUNCTION storefront.reject_frozen_page_change()
                RETURNS trigger
                LANGUAGE plpgsql
                SECURITY DEFINER
                SET search_path = storefront, public
                AS $$
                DECLARE
                    version_status text;
                BEGIN
                    SELECT v.status INTO version_status
                      FROM storefront.site_versions v
                     WHERE v.id = CASE WHEN TG_OP = 'DELETE' THEN OLD.version_id ELSE NEW.version_id END;

                    IF version_status IS NOT NULL AND version_status <> 'Draft' THEN
                        RAISE EXCEPTION 'this page belongs to a % site version, which is frozen', version_status
                            USING ERRCODE = 'restrict_violation';
                    END IF;

                    IF TG_OP = 'DELETE' THEN
                        RETURN OLD;
                    END IF;

                    RETURN NEW;
                END;
                $$;

                CREATE OR REPLACE FUNCTION storefront.reject_frozen_block_change()
                RETURNS trigger
                LANGUAGE plpgsql
                SECURITY DEFINER
                SET search_path = storefront, public
                AS $$
                DECLARE
                    version_status text;
                BEGIN
                    SELECT v.status INTO version_status
                      FROM storefront.site_pages p
                      JOIN storefront.site_versions v ON v.id = p.version_id
                     WHERE p.id = CASE WHEN TG_OP = 'DELETE' THEN OLD.page_id ELSE NEW.page_id END;

                    -- Nothing found means the page is being deleted and its blocks go with it: nothing to protect.
                    IF version_status IS NOT NULL AND version_status <> 'Draft' THEN
                        RAISE EXCEPTION 'this block belongs to a % site version, which is frozen', version_status
                            USING ERRCODE = 'restrict_violation';
                    END IF;

                    IF TG_OP = 'DELETE' THEN
                        RETURN OLD;
                    END IF;

                    RETURN NEW;
                END;
                $$;

                CREATE TRIGGER site_pages_frozen_trg
                    BEFORE INSERT OR UPDATE OR DELETE ON storefront.site_pages
                    FOR EACH ROW
                    EXECUTE FUNCTION storefront.reject_frozen_page_change();

                CREATE TRIGGER site_blocks_frozen_trg
                    BEFORE INSERT OR UPDATE OR DELETE ON storefront.site_blocks
                    FOR EACH ROW
                    EXECUTE FUNCTION storefront.reject_frozen_block_change();
                """);

            // ------------------------------------------------------------------ admin alerts
            //
            // A hostname that looks like a well-known brand is set aside with an alert (open question 20).
            // LedgerIntegrity joins the list too: PlatformAlerter has written it since the nightly ledger
            // audit landed, but this constraint was never widened to allow it.
            migrationBuilder.Sql("""
                ALTER TABLE platform.admin_alerts DROP CONSTRAINT ck_admin_alerts_type;
                ALTER TABLE platform.admin_alerts ADD CONSTRAINT ck_admin_alerts_type
                    CHECK (type IN ('PendingKyb', 'GatewayError', 'Dispute', 'ReversalRequired',
                                    'TicketTimeLimitBreach', 'LedgerIntegrity', 'HostnameReview'));
                """);

            // ------------------------------------------------------------------ the application role
            //
            // storefront is a new schema, so AddRowLevelSecurity's default privileges never covered it.
            migrationBuilder.Sql($"""
                GRANT USAGE ON SCHEMA storefront TO {AddRowLevelSecurity.ApplicationRole};

                -- Reference data: every agency reads it and none writes it. The seeder runs as the owner.
                GRANT SELECT ON storefront.site_templates TO {AddRowLevelSecurity.ApplicationRole};
                GRANT SELECT ON storefront.reserved_hostname_labels TO {AddRowLevelSecurity.ApplicationRole};

                -- Never deleted: a site outlives any one version of it, and the versions are its history.
                GRANT SELECT, INSERT, UPDATE ON storefront.sites TO {AddRowLevelSecurity.ApplicationRole};
                GRANT SELECT, INSERT, UPDATE ON storefront.site_versions TO {AddRowLevelSecurity.ApplicationRole};
                GRANT SELECT, INSERT, UPDATE ON storefront.site_themes TO {AddRowLevelSecurity.ApplicationRole};

                -- Draft pages and blocks come and go as the agent edits; a custom hostname can be removed.
                GRANT SELECT, INSERT, UPDATE, DELETE ON storefront.site_pages TO {AddRowLevelSecurity.ApplicationRole};
                GRANT SELECT, INSERT, UPDATE, DELETE ON storefront.site_blocks TO {AddRowLevelSecurity.ApplicationRole};
                GRANT SELECT, INSERT, UPDATE, DELETE ON storefront.site_domains TO {AddRowLevelSecurity.ApplicationRole};

                -- Append-only: what DNS said. Removing a hostname removes its checks through the foreign key's
                -- cascade, which runs as the table's owner rather than as this role.
                GRANT SELECT, INSERT ON storefront.site_domain_checks TO {AddRowLevelSecurity.ApplicationRole};
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
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var table in PolicedTables)
            {
                migrationBuilder.Sql($"DROP POLICY IF EXISTS tenant_isolation ON {table};");
            }

            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS site_blocks_frozen_trg ON storefront.site_blocks;
                DROP TRIGGER IF EXISTS site_pages_frozen_trg ON storefront.site_pages;
                DROP TRIGGER IF EXISTS site_versions_frozen_trg ON storefront.site_versions;
                DROP FUNCTION IF EXISTS storefront.reject_frozen_page_change();
                DROP FUNCTION IF EXISTS storefront.reject_frozen_block_change();
                DROP FUNCTION IF EXISTS storefront.reject_frozen_version_change();

                -- The hand-written keys that tie the tables together, before the tables are dropped.
                ALTER TABLE storefront.sites
                    DROP CONSTRAINT IF EXISTS fk_sites_draft_version,
                    DROP CONSTRAINT IF EXISTS fk_sites_published_version,
                    DROP CONSTRAINT IF EXISTS fk_sites_primary_domain;
                ALTER TABLE storefront.site_domain_checks DROP CONSTRAINT IF EXISTS fk_site_domain_checks_domain_same_agency;
                ALTER TABLE storefront.site_domains DROP CONSTRAINT IF EXISTS fk_site_domains_site_same_agency;
                ALTER TABLE storefront.site_themes DROP CONSTRAINT IF EXISTS fk_site_themes_site_same_agency;
                ALTER TABLE storefront.site_blocks DROP CONSTRAINT IF EXISTS fk_site_blocks_page_same_agency;
                ALTER TABLE storefront.site_pages DROP CONSTRAINT IF EXISTS fk_site_pages_version_same_agency;
                ALTER TABLE storefront.site_versions DROP CONSTRAINT IF EXISTS fk_site_versions_site_same_agency;

                -- LedgerIntegrity stays allowed: dropping it again would only restore the old gap.
                DELETE FROM platform.admin_alerts WHERE type = 'HostnameReview';
                ALTER TABLE platform.admin_alerts DROP CONSTRAINT ck_admin_alerts_type;
                ALTER TABLE platform.admin_alerts ADD CONSTRAINT ck_admin_alerts_type
                    CHECK (type IN ('PendingKyb', 'GatewayError', 'Dispute', 'ReversalRequired',
                                    'TicketTimeLimitBreach', 'LedgerIntegrity'));
                """);

            migrationBuilder.DropTable(
                name: "reserved_hostname_labels",
                schema: "storefront");

            migrationBuilder.DropTable(
                name: "site_blocks",
                schema: "storefront");

            migrationBuilder.DropTable(
                name: "site_domain_checks",
                schema: "storefront");

            migrationBuilder.DropTable(
                name: "site_themes",
                schema: "storefront");

            migrationBuilder.DropTable(
                name: "site_pages",
                schema: "storefront");

            migrationBuilder.DropTable(
                name: "site_domains",
                schema: "storefront");

            migrationBuilder.DropTable(
                name: "site_versions",
                schema: "storefront");

            migrationBuilder.DropTable(
                name: "sites",
                schema: "storefront");

            migrationBuilder.DropTable(
                name: "site_templates",
                schema: "storefront");

            migrationBuilder.DropColumn(
                name: "contact_email",
                schema: "tenancy",
                table: "agency_branding");

            migrationBuilder.DropColumn(
                name: "contact_phone",
                schema: "tenancy",
                table: "agency_branding");

            migrationBuilder.DropColumn(
                name: "whats_app_number",
                schema: "tenancy",
                table: "agency_branding");
        }
    }
}
