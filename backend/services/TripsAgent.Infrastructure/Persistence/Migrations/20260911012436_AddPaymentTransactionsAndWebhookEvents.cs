using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TripsAgent.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPaymentTransactionsAndWebhookEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "payment_transactions",
                schema: "payments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    agency_id = table.Column<Guid>(type: "uuid", nullable: false),
                    initiated_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    purpose = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    amount_minor = table.Column<long>(type: "bigint", nullable: false),
                    verified_amount_minor = table.Column<long>(type: "bigint", nullable: true),
                    fee_minor = table.Column<long>(type: "bigint", nullable: false),
                    currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false),
                    reference = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    gateway_reference = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    verified_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ledger_transaction_group_id = table.Column<Guid>(type: "uuid", nullable: true),
                    idempotency_key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    failure_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_payment_transactions", x => x.id);
                    table.ForeignKey(
                        name: "fk_payment_transactions_agencies_agency_id",
                        column: x => x.agency_id,
                        principalSchema: "tenancy",
                        principalTable: "agencies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "payment_webhook_events",
                schema: "payments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    gateway = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    event_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    event_type = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    payload = table.Column<string>(type: "text", nullable: false),
                    signature_valid = table.Column<bool>(type: "boolean", nullable: false),
                    processing_status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    last_error = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    processed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_payment_webhook_events", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_payment_transactions_agency_id_created_at",
                schema: "payments",
                table: "payment_transactions",
                columns: new[] { "agency_id", "created_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_payment_transactions_awaiting_posting",
                schema: "payments",
                table: "payment_transactions",
                column: "status",
                filter: "ledger_transaction_group_id IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_payment_transactions_idempotency_key",
                schema: "payments",
                table: "payment_transactions",
                columns: new[] { "agency_id", "idempotency_key" },
                unique: true,
                filter: "idempotency_key IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_payment_transactions_reference",
                schema: "payments",
                table: "payment_transactions",
                column: "reference",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_payment_webhook_events_gateway_event_id",
                schema: "payments",
                table: "payment_webhook_events",
                columns: new[] { "gateway", "event_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_payment_webhook_events_pending",
                schema: "payments",
                table: "payment_webhook_events",
                column: "created_at",
                filter: "processing_status = 'Pending'");

            // The invariants, in the database rather than only in C#.
            //
            // The application already enforces all of these, and that is the right place for the
            // error messages. These exist because a migration, a support script or a future
            // handler bypasses the domain, and the two below about posting are the difference
            // between a bug and money appearing in a wallet that nobody paid.
            migrationBuilder.Sql(
                """
                ALTER TABLE payments.payment_transactions
                    ADD CONSTRAINT ck_payment_transactions_amount_positive
                        CHECK (amount_minor > 0),

                    ADD CONSTRAINT ck_payment_transactions_verified_amount_not_negative
                        CHECK (verified_amount_minor IS NULL OR verified_amount_minor >= 0),

                    ADD CONSTRAINT ck_payment_transactions_fee_not_negative
                        CHECK (fee_minor >= 0),

                    -- Posted to the ledger implies confirmed. Reversed, this says: money cannot
                    -- reach a wallet on the strength of a payment nobody verified.
                    ADD CONSTRAINT ck_payment_transactions_posted_implies_succeeded
                        CHECK (ledger_transaction_group_id IS NULL OR status = 'Succeeded'),

                    -- And a confirmed payment knows what was actually paid, which is the figure
                    -- the ledger credits. Succeeded with a null amount would be uncreditable.
                    ADD CONSTRAINT ck_payment_transactions_succeeded_has_verified_amount
                        CHECK (status <> 'Succeeded' OR verified_amount_minor IS NOT NULL);
                """);

            migrationBuilder.Sql(
                """
                ALTER TABLE payments.payment_webhook_events
                    ADD CONSTRAINT ck_payment_webhook_events_attempts_not_negative
                        CHECK (attempts >= 0);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "payment_transactions",
                schema: "payments");

            migrationBuilder.DropTable(
                name: "payment_webhook_events",
                schema: "payments");
        }
    }
}
