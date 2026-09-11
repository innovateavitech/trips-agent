using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TripsAgent.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddReconciliationExceptions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "reconciliation_exceptions",
                schema: "payments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    check_name = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    severity = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    subject = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    detail = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    expected_minor = table.Column<long>(type: "bigint", nullable: false),
                    actual_minor = table.Column<long>(type: "bigint", nullable: false),
                    agency_id = table.Column<Guid>(type: "uuid", nullable: true),
                    detected_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    times_seen = table.Column<int>(type: "integer", nullable: false),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    resolved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    resolution_note = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_reconciliation_exceptions", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_reconciliation_exceptions_check_name_subject",
                schema: "payments",
                table: "reconciliation_exceptions",
                columns: new[] { "check_name", "subject" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_reconciliation_exceptions_status_severity_detected_at",
                schema: "payments",
                table: "reconciliation_exceptions",
                columns: new[] { "status", "severity", "detected_at" });

            // The invariants, in the database as well as the domain.
            migrationBuilder.Sql(
                """
                ALTER TABLE payments.reconciliation_exceptions
                    -- A row exists because a run saw the problem, so it has been seen at least once.
                    ADD CONSTRAINT ck_reconciliation_exceptions_times_seen_positive
                        CHECK (times_seen >= 1),

                    -- Resolved means somebody wrote down what they did about it. A resolved row
                    -- with no note is the answer to "what happened in March" being lost.
                    ADD CONSTRAINT ck_reconciliation_exceptions_resolved_has_note
                        CHECK (
                            status <> 'Resolved'
                            OR (resolved_at IS NOT NULL AND resolution_note IS NOT NULL)
                        );
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "reconciliation_exceptions",
                schema: "payments");
        }
    }
}
