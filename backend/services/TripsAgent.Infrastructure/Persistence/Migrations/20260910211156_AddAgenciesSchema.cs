using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TripsAgent.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAgenciesSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "tenancy");

            migrationBuilder.CreateTable(
                name: "agencies",
                schema: "tenancy",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    parent_agency_id = table.Column<Guid>(type: "uuid", nullable: true),
                    path = table.Column<string>(type: "ltree", nullable: false),
                    type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    legal_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    trading_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    slug = table.Column<string>(type: "character varying(63)", maxLength: 63, nullable: false),
                    country_code = table.Column<string>(type: "character(2)", fixedLength: true, maxLength: 2, nullable: false),
                    base_currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false),
                    timezone = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    verified_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    account_manager_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    tax_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    vat_rate_basis_points = table.Column<int>(type: "integer", nullable: false),
                    onboarding_completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_agencies", x => x.id);
                    table.ForeignKey(
                        name: "fk_agencies_agencies_parent_agency_id",
                        column: x => x.parent_agency_id,
                        principalSchema: "tenancy",
                        principalTable: "agencies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "agency_branding",
                schema: "tenancy",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    agency_id = table.Column<Guid>(type: "uuid", nullable: false),
                    logo_asset_id = table.Column<Guid>(type: "uuid", nullable: true),
                    primary_color = table.Column<string>(type: "character varying(7)", maxLength: 7, nullable: false),
                    secondary_color = table.Column<string>(type: "character varying(7)", maxLength: 7, nullable: true),
                    font_family = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    contact_address = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    social_links = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_agency_branding", x => x.id);
                    table.ForeignKey(
                        name: "fk_agency_branding_agencies_agency_id",
                        column: x => x.agency_id,
                        principalSchema: "tenancy",
                        principalTable: "agencies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "agency_settings",
                schema: "tenancy",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    agency_id = table.Column<Guid>(type: "uuid", nullable: false),
                    supported_currencies = table.Column<string>(type: "jsonb", nullable: false),
                    invoice_prefix = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    booking_reference_prefix = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    notification_preferences = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_agency_settings", x => x.id);
                    table.ForeignKey(
                        name: "fk_agency_settings_agencies_agency_id",
                        column: x => x.agency_id,
                        principalSchema: "tenancy",
                        principalTable: "agencies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_agencies_parent_agency_id",
                schema: "tenancy",
                table: "agencies",
                column: "parent_agency_id");

            migrationBuilder.CreateIndex(
                name: "ix_agencies_path_gist",
                schema: "tenancy",
                table: "agencies",
                column: "path")
                .Annotation("Npgsql:IndexMethod", "gist");

            migrationBuilder.CreateIndex(
                name: "ix_agencies_slug",
                schema: "tenancy",
                table: "agencies",
                column: "slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_agencies_status",
                schema: "tenancy",
                table: "agencies",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "ix_agency_branding_agency_id",
                schema: "tenancy",
                table: "agency_branding",
                column: "agency_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_agency_settings_agency_id",
                schema: "tenancy",
                table: "agency_settings",
                column: "agency_id",
                unique: true);

            // ---------------------------------------------------------------- hierarchy path
            //
            // The path is maintained by the database, not by the application. A path computed in
            // C# is wrong the moment anyone runs an UPDATE in psql, and reparenting has to rewrite
            // every descendant in the same statement to stay consistent.
            //
            // ltree labels may only contain letters, digits and underscores, so a row's label is
            // its UUID with the hyphens swapped for underscores. A principal's path is its own
            // label; a sub-agent's is parent.child.
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION tenancy.agencies_compute_path()
                RETURNS trigger
                LANGUAGE plpgsql
                SET search_path = tenancy, public
                AS $$
                DECLARE
                    parent_path ltree;
                BEGIN
                    IF NEW.parent_agency_id IS NULL THEN
                        NEW.path := text2ltree(replace(NEW.id::text, '-', '_'));
                    ELSE
                        SELECT a.path INTO parent_path
                        FROM tenancy.agencies a
                        WHERE a.id = NEW.parent_agency_id;

                        IF parent_path IS NULL THEN
                            RAISE EXCEPTION 'parent agency % does not exist', NEW.parent_agency_id
                                USING ERRCODE = 'foreign_key_violation';
                        END IF;

                        NEW.path := parent_path || text2ltree(replace(NEW.id::text, '-', '_'));
                    END IF;

                    RETURN NEW;
                END;
                $$;
                """);

            migrationBuilder.Sql("""
                CREATE TRIGGER agencies_compute_path_trg
                    BEFORE INSERT OR UPDATE OF parent_agency_id ON tenancy.agencies
                    FOR EACH ROW
                    EXECUTE FUNCTION tenancy.agencies_compute_path();
                """);

            // Moving an agency has to drag its subtree along. Depth is capped at 2 today, so a
            // reparented sub-agent has no descendants and this does nothing — it is here so that
            // raising the cap does not silently leave stale paths behind.
            //
            // No recursion risk: this updates `path`, and the triggers above fire only on
            // INSERT or on an UPDATE that touches `parent_agency_id`.
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION tenancy.agencies_reparent_descendants()
                RETURNS trigger
                LANGUAGE plpgsql
                SET search_path = tenancy, public
                AS $$
                BEGIN
                    IF NEW.path IS DISTINCT FROM OLD.path THEN
                        UPDATE tenancy.agencies
                        SET path = NEW.path || subpath(path, nlevel(OLD.path))
                        WHERE path <@ OLD.path
                          AND id <> NEW.id;
                    END IF;

                    RETURN NULL;
                END;
                $$;
                """);

            migrationBuilder.Sql("""
                CREATE TRIGGER agencies_reparent_descendants_trg
                    AFTER UPDATE OF parent_agency_id ON tenancy.agencies
                    FOR EACH ROW
                    EXECUTE FUNCTION tenancy.agencies_reparent_descendants();
                """);

            // ------------------------------------------------------------------- constraints
            //
            // The domain validates all of this too. It is repeated here because the database is
            // the last line of defence: a seed script, a migration, or someone in psql at 2am
            // does not go through the domain model.
            migrationBuilder.Sql("""
                ALTER TABLE tenancy.agencies
                    ADD CONSTRAINT ck_agencies_depth
                        CHECK (nlevel(path) <= 2),

                    ADD CONSTRAINT ck_agencies_hierarchy
                        CHECK (
                            (type = 'Principal' AND parent_agency_id IS NULL)
                         OR (type = 'SubAgent'  AND parent_agency_id IS NOT NULL)),

                    ADD CONSTRAINT ck_agencies_not_own_parent
                        CHECK (parent_agency_id IS NULL OR parent_agency_id <> id),

                    ADD CONSTRAINT ck_agencies_slug_url_safe
                        CHECK (slug ~ '^[a-z0-9]+(-[a-z0-9]+)*$'),

                    ADD CONSTRAINT ck_agencies_type
                        CHECK (type IN ('Principal', 'SubAgent')),

                    ADD CONSTRAINT ck_agencies_status
                        CHECK (status IN ('PendingVerification', 'Verified', 'Rejected', 'Suspended', 'Terminated')),

                    ADD CONSTRAINT ck_agencies_country_code
                        CHECK (country_code ~ '^[A-Z]{2}$'),

                    ADD CONSTRAINT ck_agencies_base_currency
                        CHECK (base_currency ~ '^[A-Z]{3}$'),

                    ADD CONSTRAINT ck_agencies_vat_rate
                        CHECK (vat_rate_basis_points BETWEEN 0 AND 10000);
                """);

            migrationBuilder.Sql("""
                ALTER TABLE tenancy.agency_branding
                    ADD CONSTRAINT ck_agency_branding_primary_color
                        CHECK (primary_color ~ '^#([0-9A-F]{3}|[0-9A-F]{6})$'),

                    ADD CONSTRAINT ck_agency_branding_secondary_color
                        CHECK (secondary_color IS NULL
                               OR secondary_color ~ '^#([0-9A-F]{3}|[0-9A-F]{6})$');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Triggers go with their table, but the functions live in the schema and would
            // survive DropTable, so drop them explicitly.
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS tenancy.agencies_reparent_descendants() CASCADE;");
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS tenancy.agencies_compute_path() CASCADE;");

            migrationBuilder.DropTable(
                name: "agency_branding",
                schema: "tenancy");

            migrationBuilder.DropTable(
                name: "agency_settings",
                schema: "tenancy");

            migrationBuilder.DropTable(
                name: "agencies",
                schema: "tenancy");
        }
    }
}
