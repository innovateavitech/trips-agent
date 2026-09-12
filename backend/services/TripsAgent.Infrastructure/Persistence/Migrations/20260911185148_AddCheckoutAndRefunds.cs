using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TripsAgent.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCheckoutAndRefunds : Migration
    {
        // The same InitPlan-wrapped calls AddRowLevelSecurity uses; see the note there.
        private const string PlatformScope = "(SELECT tenancy.platform_scope_active())";
        private const string CurrentAgency = "(SELECT tenancy.current_agency_id())";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "paid_at",
                schema: "orders",
                table: "orders",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "paid_from",
                schema: "orders",
                table: "orders",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "payment_idempotency_key",
                schema: "orders",
                table: "orders",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "resolution_opened_at",
                schema: "orders",
                table: "order_lines",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "refunds",
                schema: "payments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    agency_id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_line_id = table.Column<Guid>(type: "uuid", nullable: false),
                    supplier_booking_id = table.Column<Guid>(type: "uuid", nullable: true),
                    supplier_status_poll_id = table.Column<Guid>(type: "uuid", nullable: true),
                    reason = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    method = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    amount_minor = table.Column<long>(type: "bigint", nullable: false),
                    currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false),
                    ledger_transaction_group_id = table.Column<Guid>(type: "uuid", nullable: true),
                    refunded_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    refunded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_refunds", x => x.id);
                    table.ForeignKey(
                        name: "fk_refunds_agencies_agency_id",
                        column: x => x.agency_id,
                        principalSchema: "tenancy",
                        principalTable: "agencies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_refunds_order_lines_order_line_id",
                        column: x => x.order_line_id,
                        principalSchema: "orders",
                        principalTable: "order_lines",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_refunds_orders_order_id",
                        column: x => x.order_id,
                        principalSchema: "orders",
                        principalTable: "orders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_refunds_supplier_bookings_supplier_booking_id",
                        column: x => x.supplier_booking_id,
                        principalSchema: "supplier",
                        principalTable: "supplier_bookings",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_refunds_supplier_status_polls_supplier_status_poll_id",
                        column: x => x.supplier_status_poll_id,
                        principalSchema: "supplier",
                        principalTable: "supplier_status_polls",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_orders_agency_id_payment_idempotency_key",
                schema: "orders",
                table: "orders",
                columns: new[] { "agency_id", "payment_idempotency_key" },
                unique: true,
                filter: "payment_idempotency_key IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_refunds_agency_id_refunded_at",
                schema: "payments",
                table: "refunds",
                columns: new[] { "agency_id", "refunded_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_refunds_order_id",
                schema: "payments",
                table: "refunds",
                column: "order_id");

            migrationBuilder.CreateIndex(
                name: "ix_refunds_order_line_id",
                schema: "payments",
                table: "refunds",
                column: "order_line_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_refunds_supplier_booking_id",
                schema: "payments",
                table: "refunds",
                column: "supplier_booking_id");

            migrationBuilder.CreateIndex(
                name: "ix_refunds_supplier_status_poll_id",
                schema: "payments",
                table: "refunds",
                column: "supplier_status_poll_id");

            // ------------------------------------------------------------------------ shape
            //
            // Every IS NOT NULL below is load-bearing: a CHECK constraint PASSES when its expression is
            // NULL, so each rule says the NULL case out loud.
            migrationBuilder.Sql("""
                -- A line already waiting for a decision before this column existed opened when it last changed.
                UPDATE orders.order_lines
                   SET resolution_opened_at = updated_at
                 WHERE fulfilment_status = 'FailedNeedsResolution' AND resolution_opened_at IS NULL;

                ALTER TABLE orders.orders
                    ADD CONSTRAINT ck_orders_paid_from
                        CHECK (paid_from IS NULL OR paid_from IN ('Wallet', 'Card')),
                    -- Paid means all three — how, when, and the key the payment came with — never some of them.
                    ADD CONSTRAINT ck_orders_payment_is_whole
                        CHECK ((paid_at IS NULL AND paid_from IS NULL AND payment_idempotency_key IS NULL)
                            OR (paid_at IS NOT NULL AND paid_from IS NOT NULL AND payment_idempotency_key IS NOT NULL));

                ALTER TABLE orders.order_lines
                    ADD CONSTRAINT ck_order_lines_resolution_is_timed
                        CHECK (fulfilment_status <> 'FailedNeedsResolution' OR resolution_opened_at IS NOT NULL);

                ALTER TABLE payments.refunds
                    ADD CONSTRAINT ck_refunds_reason
                        CHECK (reason IN ('SupplierReversal', 'AgentResolution')),
                    ADD CONSTRAINT ck_refunds_method
                        CHECK (method IN ('WalletHoldReleased', 'WalletCredited', 'Gateway')),
                    ADD CONSTRAINT ck_refunds_amount_not_negative
                        CHECK (amount_minor >= 0),
                    ADD CONSTRAINT ck_refunds_currency
                        CHECK (currency ~ '^[A-Z]{3}$'),
                    -- #43: never a reversal without the status poll that justified it.
                    ADD CONSTRAINT ck_refunds_supplier_reversal_has_evidence
                        CHECK (reason <> 'SupplierReversal' OR supplier_status_poll_id IS NOT NULL),
                    ADD CONSTRAINT ck_refunds_agent_refund_names_the_agent
                        CHECK (reason <> 'AgentResolution' OR refunded_by_user_id IS NOT NULL),
                    -- Money credited back moved through the ledger, and says where.
                    ADD CONSTRAINT ck_refunds_credit_is_posted
                        CHECK (method <> 'WalletCredited' OR ledger_transaction_group_id IS NOT NULL);
                """);

            // ------------------------------------------------------------ a refund is a record, not a draft
            //
            // A trigger as well as a REVOKE, because a REVOKE does not bind the table's owner.
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION payments.reject_refund_rewrite()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $$
                BEGIN
                    RAISE EXCEPTION 'payments.refunds is append-only (attempted %)', TG_OP
                        USING ERRCODE = 'restrict_violation',
                              HINT = 'A refund records money that moved. Record what happens next; never change what happened.';
                END;
                $$;

                CREATE TRIGGER refunds_append_only_trg
                    BEFORE UPDATE OR DELETE ON payments.refunds
                    FOR EACH ROW
                    EXECUTE FUNCTION payments.reject_refund_rewrite();
                """);

            migrationBuilder.Sql($"""
                GRANT SELECT, INSERT ON payments.refunds TO {AddRowLevelSecurity.ApplicationRole};
                REVOKE UPDATE, DELETE ON payments.refunds FROM {AddRowLevelSecurity.ApplicationRole};
                """);

            // ------------------------------------------------------------------ row-level security
            migrationBuilder.Sql($"""
                ALTER TABLE payments.refunds ENABLE ROW LEVEL SECURITY;
                -- FORCE: without it the table's owner is exempt, and a deployment that connects as the
                -- owner would be silently unpoliced.
                ALTER TABLE payments.refunds FORCE ROW LEVEL SECURITY;
                CREATE POLICY tenant_isolation ON payments.refunds
                    USING ({PlatformScope} OR agency_id = {CurrentAgency})
                    WITH CHECK ({PlatformScope} OR agency_id = {CurrentAgency});
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP POLICY IF EXISTS tenant_isolation ON payments.refunds;
                DROP TRIGGER IF EXISTS refunds_append_only_trg ON payments.refunds;
                DROP FUNCTION IF EXISTS payments.reject_refund_rewrite();

                ALTER TABLE orders.orders
                    DROP CONSTRAINT IF EXISTS ck_orders_paid_from,
                    DROP CONSTRAINT IF EXISTS ck_orders_payment_is_whole;

                ALTER TABLE orders.order_lines
                    DROP CONSTRAINT IF EXISTS ck_order_lines_resolution_is_timed;
                """);

            migrationBuilder.DropTable(
                name: "refunds",
                schema: "payments");

            migrationBuilder.DropIndex(
                name: "ix_orders_agency_id_payment_idempotency_key",
                schema: "orders",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "paid_at",
                schema: "orders",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "paid_from",
                schema: "orders",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "payment_idempotency_key",
                schema: "orders",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "resolution_opened_at",
                schema: "orders",
                table: "order_lines");
        }
    }
}
