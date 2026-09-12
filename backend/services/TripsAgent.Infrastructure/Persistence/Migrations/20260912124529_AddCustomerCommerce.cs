using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TripsAgent.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCustomerCommerce : Migration
    {
        /// <summary>The table this migration creates, owned by one agency through agency_id.</summary>
        internal static readonly string[] PolicedTables =
        [
            "orders.booking_access_tokens",
        ];

        // The same InitPlan-wrapped calls AddRowLevelSecurity uses; see the note there.
        private const string PlatformScope = "(SELECT tenancy.platform_scope_active())";
        private const string CurrentAgency = "(SELECT tenancy.current_agency_id())";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "order_line_id",
                schema: "payments",
                table: "wallet_holds",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "order_id",
                schema: "payments",
                table: "payment_transactions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "order_line_id",
                schema: "catalog",
                table: "departure_holds",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "departure_hold_id",
                schema: "orders",
                table: "cart_items",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "departure_id",
                schema: "orders",
                table: "cart_items",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "hold_expires_at",
                schema: "orders",
                table: "cart_items",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "pax_count",
                schema: "orders",
                table: "cart_items",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<Guid>(
                name: "supplier_offer_id",
                schema: "orders",
                table: "cart_items",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "booking_access_tokens",
                schema: "orders",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    agency_id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    token_hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    issued_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_used_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_booking_access_tokens", x => x.id);
                    table.ForeignKey(
                        name: "fk_booking_access_tokens_agencies_agency_id",
                        column: x => x.agency_id,
                        principalSchema: "tenancy",
                        principalTable: "agencies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_booking_access_tokens_orders_order_id",
                        column: x => x.order_id,
                        principalSchema: "orders",
                        principalTable: "orders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_wallet_holds_order_line_id",
                schema: "payments",
                table: "wallet_holds",
                column: "order_line_id",
                filter: "order_line_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_payment_transactions_order_id",
                schema: "payments",
                table: "payment_transactions",
                column: "order_id",
                filter: "order_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_departure_holds_order_line_id",
                schema: "catalog",
                table: "departure_holds",
                column: "order_line_id",
                filter: "order_line_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_cart_items_departure_hold_id",
                schema: "orders",
                table: "cart_items",
                column: "departure_hold_id",
                filter: "departure_hold_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_cart_items_departure_id",
                schema: "orders",
                table: "cart_items",
                column: "departure_id");

            migrationBuilder.CreateIndex(
                name: "ix_booking_access_tokens_agency_id",
                schema: "orders",
                table: "booking_access_tokens",
                column: "agency_id");

            migrationBuilder.CreateIndex(
                name: "ix_booking_access_tokens_order_id_expires_at",
                schema: "orders",
                table: "booking_access_tokens",
                columns: new[] { "order_id", "expires_at" });

            migrationBuilder.CreateIndex(
                name: "ix_booking_access_tokens_token_hash",
                schema: "orders",
                table: "booking_access_tokens",
                column: "token_hash",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "fk_cart_items_departures_departure_id",
                schema: "orders",
                table: "cart_items",
                column: "departure_id",
                principalSchema: "catalog",
                principalTable: "departures",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            // ------------------------------------------------------------------------- shape CHECKs
            //
            // EF Core can express none of these, so they are written by hand — and they are here
            // rather than left to the application because a hand-typed UPDATE at a psql prompt is not
            // bound by C#.
            migrationBuilder.Sql("""
                -- A cart line is for at least one person. Rows written before this column existed
                -- default to 0, so they are given the one traveller their pax_breakdown describes.
                UPDATE orders.cart_items SET pax_count = 1 WHERE pax_count < 1;

                ALTER TABLE orders.cart_items
                    ADD CONSTRAINT ck_cart_items_pax_count_positive CHECK (pax_count >= 1);

                -- A cart line names at most one of the three things it can be: a catalog product, a
                -- dated departure, or a searched fare. A departure line also names its product, so
                -- the two are counted together.
                ALTER TABLE orders.cart_items
                    ADD CONSTRAINT ck_cart_items_one_subject CHECK (
                        (CASE WHEN departure_id IS NOT NULL THEN 1 ELSE 0 END)
                        + (CASE WHEN supplier_offer_id IS NOT NULL THEN 1 ELSE 0 END) <= 1);

                -- Seats are held for a departure, and only for a departure.
                ALTER TABLE orders.cart_items
                    ADD CONSTRAINT ck_cart_items_hold_needs_departure CHECK (
                        departure_hold_id IS NULL OR departure_id IS NOT NULL);

                -- A hold has a deadline exactly while it exists: a cart that says it holds seats
                -- without saying until when cannot be counted down.
                ALTER TABLE orders.cart_items
                    ADD CONSTRAINT ck_cart_items_hold_has_deadline CHECK (
                        (departure_hold_id IS NULL) = (hold_expires_at IS NULL));

                -- A link is issued before it expires, and a booking it opens is never in the past of
                -- its own issue.
                ALTER TABLE orders.booking_access_tokens
                    ADD CONSTRAINT ck_booking_access_tokens_expires_after_issue CHECK (expires_at > issued_at);

                -- A payment that names an order is an order payment, and one that does not is not.
                -- The other purposes — a top-up, a subscription — pay for no order.
                ALTER TABLE payments.payment_transactions
                    ADD CONSTRAINT ck_payment_transactions_order_purpose CHECK (
                        (order_id IS NULL) OR (purpose = 'OrderPayment'));

                -- A traveller's payment on a storefront is a new kind of statement line: money in,
                -- but not a top-up anybody at the agency made (build plan F5). The CHECK from
                -- AddLedgerAndWallets lists the kinds by name, so it has to learn this one.
                ALTER TABLE payments.wallet_transactions
                    DROP CONSTRAINT ck_wallet_transactions_type;

                ALTER TABLE payments.wallet_transactions
                    ADD CONSTRAINT ck_wallet_transactions_type
                        CHECK (type IN ('TopUp', 'BookingPayment', 'Refund', 'Reversal', 'Adjustment', 'CustomerPayment'));
                """);

            // ------------------------------------------------------------------ the application role
            //
            // orders already has USAGE from AddOrders; only the new table needs its own grants.
            //
            // UPDATE, because opening a link records that it was used and retiring one revokes it.
            // No DELETE: a link that has lapsed is evidence of what a traveller was sent, and the
            // retention purge is what removes it with its order.
            migrationBuilder.Sql($"""
                GRANT SELECT, INSERT, UPDATE ON orders.booking_access_tokens TO {AddRowLevelSecurity.ApplicationRole};
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
                ALTER TABLE payments.wallet_transactions
                    DROP CONSTRAINT IF EXISTS ck_wallet_transactions_type;

                ALTER TABLE payments.wallet_transactions
                    ADD CONSTRAINT ck_wallet_transactions_type
                        CHECK (type IN ('TopUp', 'BookingPayment', 'Refund', 'Reversal', 'Adjustment'));

                ALTER TABLE payments.payment_transactions
                    DROP CONSTRAINT IF EXISTS ck_payment_transactions_order_purpose;
                ALTER TABLE orders.booking_access_tokens
                    DROP CONSTRAINT IF EXISTS ck_booking_access_tokens_expires_after_issue;
                ALTER TABLE orders.cart_items DROP CONSTRAINT IF EXISTS ck_cart_items_hold_has_deadline;
                ALTER TABLE orders.cart_items DROP CONSTRAINT IF EXISTS ck_cart_items_hold_needs_departure;
                ALTER TABLE orders.cart_items DROP CONSTRAINT IF EXISTS ck_cart_items_one_subject;
                ALTER TABLE orders.cart_items DROP CONSTRAINT IF EXISTS ck_cart_items_pax_count_positive;
                """);

            migrationBuilder.DropForeignKey(
                name: "fk_cart_items_departures_departure_id",
                schema: "orders",
                table: "cart_items");

            migrationBuilder.DropTable(
                name: "booking_access_tokens",
                schema: "orders");

            migrationBuilder.DropIndex(
                name: "ix_wallet_holds_order_line_id",
                schema: "payments",
                table: "wallet_holds");

            migrationBuilder.DropIndex(
                name: "ix_payment_transactions_order_id",
                schema: "payments",
                table: "payment_transactions");

            migrationBuilder.DropIndex(
                name: "ix_departure_holds_order_line_id",
                schema: "catalog",
                table: "departure_holds");

            migrationBuilder.DropIndex(
                name: "ix_cart_items_departure_hold_id",
                schema: "orders",
                table: "cart_items");

            migrationBuilder.DropIndex(
                name: "ix_cart_items_departure_id",
                schema: "orders",
                table: "cart_items");

            migrationBuilder.DropColumn(
                name: "order_line_id",
                schema: "payments",
                table: "wallet_holds");

            migrationBuilder.DropColumn(
                name: "order_id",
                schema: "payments",
                table: "payment_transactions");

            migrationBuilder.DropColumn(
                name: "order_line_id",
                schema: "catalog",
                table: "departure_holds");

            migrationBuilder.DropColumn(
                name: "departure_hold_id",
                schema: "orders",
                table: "cart_items");

            migrationBuilder.DropColumn(
                name: "departure_id",
                schema: "orders",
                table: "cart_items");

            migrationBuilder.DropColumn(
                name: "hold_expires_at",
                schema: "orders",
                table: "cart_items");

            migrationBuilder.DropColumn(
                name: "pax_count",
                schema: "orders",
                table: "cart_items");

            migrationBuilder.DropColumn(
                name: "supplier_offer_id",
                schema: "orders",
                table: "cart_items");
        }
    }
}
