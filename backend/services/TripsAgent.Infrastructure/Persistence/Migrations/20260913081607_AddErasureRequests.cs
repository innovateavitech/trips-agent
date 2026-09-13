using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TripsAgent.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddErasureRequests : Migration
    {
        private const string PlatformScope = "(SELECT tenancy.platform_scope_active())";
        private const string CurrentAgency = "(SELECT tenancy.current_agency_id())";

        /// <summary>
        /// <c>platform.erasure_requests</c>: the record that somebody's details were erased (issue 106).
        /// </summary>
        /// <remarks>
        /// The row survives what it records, which is the point: NDPA gives a person the right to be erased
        /// and gives us the duty to show we did it. It holds no name, email or phone number — only the
        /// customer it anonymised, the reason given, who ran it, and counts per table.
        /// </remarks>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.CreateTable(
                name: "erasure_requests",
                schema: "platform",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    agency_id = table.Column<Guid>(type: "uuid", nullable: false),
                    customer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    requested_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    requested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    outcome = table.Column<string>(type: "jsonb", nullable: true),
                    refusal_reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_erasure_requests", x => x.id);
                    table.CheckConstraint("ck_erasure_requests_finished_is_explained", "(status = 'Requested' AND completed_at IS NULL) OR (status = 'Completed' AND completed_at IS NOT NULL AND outcome IS NOT NULL) OR (status = 'Refused' AND completed_at IS NOT NULL AND refusal_reason IS NOT NULL)");
                    table.ForeignKey(
                        name: "fk_erasure_requests_agencies_agency_id",
                        column: x => x.agency_id,
                        principalSchema: "tenancy",
                        principalTable: "agencies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_erasure_requests_customers_customer_id",
                        column: x => x.customer_id,
                        principalSchema: "crm",
                        principalTable: "customers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_erasure_requests_agency_id_requested_at",
                schema: "platform",
                table: "erasure_requests",
                columns: new[] { "agency_id", "requested_at" });

            migrationBuilder.CreateIndex(
                name: "ix_erasure_requests_customer_id",
                schema: "platform",
                table: "erasure_requests",
                column: "customer_id");

            // ------------------------------------------------------------------ the application role
            //
            // No DELETE and no UPDATE of a finished request: this is the evidence that an erasure was
            // carried out, and a record of an erasure that can be quietly removed is no record at all.
            // The service only ever inserts.
            migrationBuilder.Sql($"""
                GRANT SELECT, INSERT ON platform.erasure_requests TO {AddRowLevelSecurity.ApplicationRole};
                """);

            // ------------------------------------------------------------------ row-level security
            migrationBuilder.Sql($"""
                ALTER TABLE platform.erasure_requests ENABLE ROW LEVEL SECURITY;

                -- FORCE: without it the table's owner is exempt, and a deployment that connects as the
                -- owner would be silently unpoliced (ADR-0006).
                ALTER TABLE platform.erasure_requests FORCE ROW LEVEL SECURITY;

                CREATE POLICY tenant_isolation ON platform.erasure_requests
                    USING ({PlatformScope} OR agency_id = {CurrentAgency})
                    WITH CHECK ({PlatformScope} OR agency_id = {CurrentAgency});
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            // The policy and the grants go with the table.
            migrationBuilder.DropTable(
                name: "erasure_requests",
                schema: "platform");
        }
    }
}
