using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TripsAgent.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddKybAndAdminAlerts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "admin_alerts",
                schema: "platform",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    severity = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    agency_id = table.Column<Guid>(type: "uuid", nullable: true),
                    entity_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    entity_id = table.Column<Guid>(type: "uuid", nullable: true),
                    message = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    acknowledged_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    acknowledged_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    resolved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_admin_alerts", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "kyb_submissions",
                schema: "tenancy",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    agency_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    submitted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    reviewed_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    reviewed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    rejection_reason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_kyb_submissions", x => x.id);
                    table.ForeignKey(
                        name: "fk_kyb_submissions_agencies_agency_id",
                        column: x => x.agency_id,
                        principalSchema: "tenancy",
                        principalTable: "agencies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "kyb_documents",
                schema: "tenancy",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    agency_id = table.Column<Guid>(type: "uuid", nullable: false),
                    submission_id = table.Column<Guid>(type: "uuid", nullable: false),
                    document_type = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    file_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    storage_key = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    content_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    checksum = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_kyb_documents", x => x.id);
                    table.ForeignKey(
                        name: "fk_kyb_documents_agencies_agency_id",
                        column: x => x.agency_id,
                        principalSchema: "tenancy",
                        principalTable: "agencies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_kyb_documents_kyb_submissions_submission_id",
                        column: x => x.submission_id,
                        principalSchema: "tenancy",
                        principalTable: "kyb_submissions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_admin_alerts_status_severity_created_at",
                schema: "platform",
                table: "admin_alerts",
                columns: new[] { "status", "severity", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_admin_alerts_type_entity_id",
                schema: "platform",
                table: "admin_alerts",
                columns: new[] { "type", "entity_id" });

            migrationBuilder.CreateIndex(
                name: "ix_kyb_documents_agency_id_submission_id",
                schema: "tenancy",
                table: "kyb_documents",
                columns: new[] { "agency_id", "submission_id" });

            migrationBuilder.CreateIndex(
                name: "ix_kyb_documents_storage_key",
                schema: "tenancy",
                table: "kyb_documents",
                column: "storage_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_kyb_documents_submission_id",
                schema: "tenancy",
                table: "kyb_documents",
                column: "submission_id");

            migrationBuilder.CreateIndex(
                name: "ix_kyb_submissions_agency_id_status",
                schema: "tenancy",
                table: "kyb_submissions",
                columns: new[] { "agency_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_kyb_submissions_submitted_at",
                schema: "tenancy",
                table: "kyb_submissions",
                column: "submitted_at");

            // The domain enforces all of this too. The database repeats it because a seed script,
            // a migration, or someone in psql does not go through the domain model — and a KYB
            // document is exactly the kind of row somebody eventually inserts by hand.
            migrationBuilder.Sql("""
                ALTER TABLE tenancy.kyb_documents
                    -- FRD §2.2: PDF, JPG or PNG only. The value stored here is the type
                    -- established by sniffing the file's bytes, never the one the browser claimed.
                    ADD CONSTRAINT ck_kyb_documents_content_type
                        CHECK (content_type IN ('application/pdf', 'image/jpeg', 'image/png')),

                    -- 10MB, and not empty. A zero-byte "document" passes every type check and
                    -- proves nothing.
                    ADD CONSTRAINT ck_kyb_documents_size
                        CHECK (size_bytes > 0 AND size_bytes <= 10485760),

                    ADD CONSTRAINT ck_kyb_documents_type
                        CHECK (document_type IN ('CertificateOfIncorporation', 'TaxIdentification',
                                                 'ProofOfAddress', 'DirectorIdentification', 'Other')),

                    -- SHA-256, hex-encoded.
                    ADD CONSTRAINT ck_kyb_documents_checksum
                        CHECK (checksum ~ '^[0-9a-f]{64}$');
                """);

            migrationBuilder.Sql("""
                ALTER TABLE tenancy.kyb_submissions
                    ADD CONSTRAINT ck_kyb_submissions_status
                        CHECK (status IN ('Draft', 'Submitted', 'UnderReview', 'Approved', 'Rejected')),

                    -- A rejection must say why: the agency is shown this text and has to know
                    -- what to fix. Enforced here so no code path can produce a silent refusal.
                    ADD CONSTRAINT ck_kyb_submissions_rejection_has_reason
                        CHECK (status <> 'Rejected' OR (rejection_reason IS NOT NULL
                                                        AND length(trim(rejection_reason)) > 0)),

                    -- A decided submission records who decided and when.
                    ADD CONSTRAINT ck_kyb_submissions_decision_is_attributed
                        CHECK (status NOT IN ('Approved', 'Rejected')
                               OR (reviewed_by_user_id IS NOT NULL AND reviewed_at IS NOT NULL));
                """);

            migrationBuilder.Sql("""
                ALTER TABLE platform.admin_alerts
                    ADD CONSTRAINT ck_admin_alerts_type
                        CHECK (type IN ('PendingKyb', 'GatewayError', 'Dispute',
                                        'ReversalRequired', 'TicketTimeLimitBreach')),

                    ADD CONSTRAINT ck_admin_alerts_severity
                        CHECK (severity IN ('Info', 'Warning', 'Critical')),

                    ADD CONSTRAINT ck_admin_alerts_status
                        CHECK (status IN ('Open', 'Acknowledged', 'Resolved'));
                """);

            // One open submission per agency. A second would make "what is my KYB status?"
            // ambiguous, and give the review queue two rows to decide between.
            migrationBuilder.Sql("""
                CREATE UNIQUE INDEX ix_kyb_submissions_one_open_per_agency
                    ON tenancy.kyb_submissions (agency_id)
                    WHERE status IN ('Draft', 'Submitted', 'UnderReview');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX IF EXISTS tenancy.ix_kyb_submissions_one_open_per_agency;");

            migrationBuilder.DropTable(
                name: "admin_alerts",
                schema: "platform");

            migrationBuilder.DropTable(
                name: "kyb_documents",
                schema: "tenancy");

            migrationBuilder.DropTable(
                name: "kyb_submissions",
                schema: "tenancy");
        }
    }
}
