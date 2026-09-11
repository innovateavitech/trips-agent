using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TripsAgent.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// The asset pipeline's tables (issue #18), and the row-level security that polices them.
    /// </summary>
    /// <remarks>
    /// Both tables carry <c>agency_id</c>, so both get the same policy as every other agency-owned
    /// table in <c>AddRowLevelSecurity</c> — added here, in the migration that creates them, so
    /// there is never a moment where a table exists unpoliced (ADR-0006). The Worker processes
    /// assets inside an audited platform scope, which the policy admits; an agency's request sees
    /// its own rows and nothing else.
    /// </remarks>
    public partial class AddAssetPipeline : Migration
    {
        private static readonly string[] PolicedTables = ["platform.assets", "platform.asset_variants"];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.CreateTable(
                name: "assets",
                schema: "platform",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    agency_id = table.Column<Guid>(type: "uuid", nullable: false),
                    purpose = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    file_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    storage_key = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    content_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    checksum = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    scan_status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    scanned_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    scan_signature = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    width = table.Column<int>(type: "integer", nullable: true),
                    height = table.Column<int>(type: "integer", nullable: true),
                    processed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    failure_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    upload_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    processing_claimed_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_assets", x => x.id);
                    table.CheckConstraint("ck_assets_dimensions", "(width IS NULL OR width > 0) AND (height IS NULL OR height > 0)");
                    table.CheckConstraint("ck_assets_ready_only_when_clean", "status <> 'Ready' OR scan_status = 'Clean'");
                    table.CheckConstraint("ck_assets_size_bytes", "size_bytes >= 0 AND size_bytes <= 20971520");
                    table.ForeignKey(
                        name: "fk_assets_agencies_agency_id",
                        column: x => x.agency_id,
                        principalSchema: "tenancy",
                        principalTable: "agencies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "asset_variants",
                schema: "platform",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    agency_id = table.Column<Guid>(type: "uuid", nullable: false),
                    asset_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    storage_key = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    content_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    width = table.Column<int>(type: "integer", nullable: false),
                    height = table.Column<int>(type: "integer", nullable: false),
                    size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_asset_variants", x => x.id);
                    table.CheckConstraint("ck_asset_variants_positive", "size_bytes > 0 AND width > 0 AND height > 0");
                    table.ForeignKey(
                        name: "fk_asset_variants_agencies_agency_id",
                        column: x => x.agency_id,
                        principalSchema: "tenancy",
                        principalTable: "agencies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_asset_variants_assets_asset_id",
                        column: x => x.asset_id,
                        principalSchema: "platform",
                        principalTable: "assets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_asset_variants_agency_id_asset_id_kind",
                schema: "platform",
                table: "asset_variants",
                columns: new[] { "agency_id", "asset_id", "kind" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_asset_variants_asset_id",
                schema: "platform",
                table: "asset_variants",
                column: "asset_id");

            migrationBuilder.CreateIndex(
                name: "ix_asset_variants_storage_key",
                schema: "platform",
                table: "asset_variants",
                column: "storage_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_assets_agency_id_status",
                schema: "platform",
                table: "assets",
                columns: new[] { "agency_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_assets_status_updated_at",
                schema: "platform",
                table: "assets",
                columns: new[] { "status", "updated_at" });

            migrationBuilder.CreateIndex(
                name: "ix_assets_storage_key",
                schema: "platform",
                table: "assets",
                column: "storage_key",
                unique: true);

            foreach (var table in PolicedTables)
            {
                // Mirrors AddRowLevelSecurity.EnableFor, including FORCE: without it the table's
                // owner is exempt, and a deployment that connects as the owner is silently unpoliced.
                migrationBuilder.Sql(
                    $"""
                     ALTER TABLE {table} ENABLE ROW LEVEL SECURITY;
                     ALTER TABLE {table} FORCE ROW LEVEL SECURITY;

                     CREATE POLICY tenant_isolation ON {table}
                         USING ((SELECT tenancy.platform_scope_active()) OR agency_id = (SELECT tenancy.current_agency_id()))
                         WITH CHECK ((SELECT tenancy.platform_scope_active()) OR agency_id = (SELECT tenancy.current_agency_id()));
                     """);
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            // The policies go with the tables; nothing else to undo.
            migrationBuilder.DropTable(
                name: "asset_variants",
                schema: "platform");

            migrationBuilder.DropTable(
                name: "assets",
                schema: "platform");
        }
    }
}
