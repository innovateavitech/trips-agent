using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TripsAgent.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddOrders : Migration
    {
        /// <summary>The tables this migration creates, each owned by one agency through agency_id.</summary>
        internal static readonly string[] PolicedTables =
        [
            "orders.orders",
            "orders.order_lines",
            "orders.order_travellers",
            "orders.order_status_history",
            "orders.carts",
            "orders.cart_items",
        ];

        // The same InitPlan-wrapped calls AddRowLevelSecurity uses; see the note there.
        private const string PlatformScope = "(SELECT tenancy.platform_scope_active())";
        private const string CurrentAgency = "(SELECT tenancy.current_agency_id())";
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "orders");

            migrationBuilder.CreateTable(
                name: "carts",
                schema: "orders",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    agency_id = table.Column<Guid>(type: "uuid", nullable: false),
                    customer_id = table.Column<Guid>(type: "uuid", nullable: true),
                    session_token = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    converted_order_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_carts", x => x.id);
                    table.ForeignKey(
                        name: "fk_carts_agencies_agency_id",
                        column: x => x.agency_id,
                        principalSchema: "tenancy",
                        principalTable: "agencies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "orders",
                schema: "orders",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    agency_id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_number = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    customer_id = table.Column<Guid>(type: "uuid", nullable: true),
                    buyer_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    channel = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false),
                    total_net_minor = table.Column<long>(type: "bigint", nullable: false),
                    total_markup_minor = table.Column<long>(type: "bigint", nullable: false),
                    total_tax_minor = table.Column<long>(type: "bigint", nullable: false),
                    total_platform_fee_minor = table.Column<long>(type: "bigint", nullable: false),
                    total_gross_minor = table.Column<long>(type: "bigint", nullable: false),
                    placed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_orders", x => x.id);
                    table.ForeignKey(
                        name: "fk_orders_agencies_agency_id",
                        column: x => x.agency_id,
                        principalSchema: "tenancy",
                        principalTable: "agencies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "cart_items",
                schema: "orders",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    agency_id = table.Column<Guid>(type: "uuid", nullable: false),
                    cart_id = table.Column<Guid>(type: "uuid", nullable: false),
                    item_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: true),
                    price_quote_id = table.Column<Guid>(type: "uuid", nullable: false),
                    title_snapshot = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    pax_breakdown = table.Column<string>(type: "jsonb", nullable: false),
                    currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false),
                    indicative_gross_minor = table.Column<long>(type: "bigint", nullable: false),
                    added_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_cart_items", x => x.id);
                    table.ForeignKey(
                        name: "fk_cart_items_agencies_agency_id",
                        column: x => x.agency_id,
                        principalSchema: "tenancy",
                        principalTable: "agencies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_cart_items_carts_cart_id",
                        column: x => x.cart_id,
                        principalSchema: "orders",
                        principalTable: "carts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_cart_items_price_quotes_price_quote_id",
                        column: x => x.price_quote_id,
                        principalSchema: "pricing",
                        principalTable: "price_quotes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "order_lines",
                schema: "orders",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    agency_id = table.Column<Guid>(type: "uuid", nullable: false),
                    item_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: true),
                    supplier_offer_id = table.Column<Guid>(type: "uuid", nullable: true),
                    supplier_booking_id = table.Column<Guid>(type: "uuid", nullable: true),
                    price_quote_id = table.Column<Guid>(type: "uuid", nullable: false),
                    title_snapshot = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    pax_breakdown = table.Column<string>(type: "jsonb", nullable: false),
                    currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false),
                    net_amount_minor = table.Column<long>(type: "bigint", nullable: false),
                    markup_amount_minor = table.Column<long>(type: "bigint", nullable: false),
                    tax_amount_minor = table.Column<long>(type: "bigint", nullable: false),
                    platform_fee_minor = table.Column<long>(type: "bigint", nullable: false),
                    gross_amount_minor = table.Column<long>(type: "bigint", nullable: false),
                    markup_rule_id = table.Column<Guid>(type: "uuid", nullable: true),
                    fulfilment_status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    failure_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    resolution_status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    resolved_by = table.Column<Guid>(type: "uuid", nullable: true),
                    resolved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    placed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_order_lines", x => x.id);
                    table.ForeignKey(
                        name: "fk_order_lines_agencies_agency_id",
                        column: x => x.agency_id,
                        principalSchema: "tenancy",
                        principalTable: "agencies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_order_lines_orders_order_id",
                        column: x => x.order_id,
                        principalSchema: "orders",
                        principalTable: "orders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_order_lines_price_quotes_price_quote_id",
                        column: x => x.price_quote_id,
                        principalSchema: "pricing",
                        principalTable: "price_quotes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "order_status_history",
                schema: "orders",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    agency_id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    from_status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    to_status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    changed_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    changed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_order_status_history", x => x.id);
                    table.ForeignKey(
                        name: "fk_order_status_history_agencies_agency_id",
                        column: x => x.agency_id,
                        principalSchema: "tenancy",
                        principalTable: "agencies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_order_status_history_orders_order_id",
                        column: x => x.order_id,
                        principalSchema: "orders",
                        principalTable: "orders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "order_travellers",
                schema: "orders",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_line_id = table.Column<Guid>(type: "uuid", nullable: false),
                    agency_id = table.Column<Guid>(type: "uuid", nullable: false),
                    traveller_type = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    first_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    last_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    birth_date = table.Column<DateOnly>(type: "date", nullable: true),
                    passport_number_encrypted = table.Column<byte[]>(type: "bytea", nullable: true),
                    passport_expiry = table.Column<DateOnly>(type: "date", nullable: true),
                    nationality = table.Column<string>(type: "character(2)", fixedLength: true, maxLength: 2, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_order_travellers", x => x.id);
                    table.ForeignKey(
                        name: "fk_order_travellers_agencies_agency_id",
                        column: x => x.agency_id,
                        principalSchema: "tenancy",
                        principalTable: "agencies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_order_travellers_order_lines_order_line_id",
                        column: x => x.order_line_id,
                        principalSchema: "orders",
                        principalTable: "order_lines",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_cart_items_agency_id",
                schema: "orders",
                table: "cart_items",
                column: "agency_id");

            migrationBuilder.CreateIndex(
                name: "ix_cart_items_cart_id",
                schema: "orders",
                table: "cart_items",
                column: "cart_id");

            migrationBuilder.CreateIndex(
                name: "ix_cart_items_price_quote_id",
                schema: "orders",
                table: "cart_items",
                column: "price_quote_id");

            migrationBuilder.CreateIndex(
                name: "ix_carts_agency_id_session_token",
                schema: "orders",
                table: "carts",
                columns: new[] { "agency_id", "session_token" },
                unique: true,
                filter: "session_token IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_carts_status_expires_at",
                schema: "orders",
                table: "carts",
                columns: new[] { "status", "expires_at" });

            migrationBuilder.CreateIndex(
                name: "ix_order_lines_agency_id_fulfilment_status",
                schema: "orders",
                table: "order_lines",
                columns: new[] { "agency_id", "fulfilment_status" });

            migrationBuilder.CreateIndex(
                name: "ix_order_lines_order_id",
                schema: "orders",
                table: "order_lines",
                column: "order_id");

            migrationBuilder.CreateIndex(
                name: "ix_order_lines_price_quote_id",
                schema: "orders",
                table: "order_lines",
                column: "price_quote_id");

            migrationBuilder.CreateIndex(
                name: "ix_order_status_history_agency_id",
                schema: "orders",
                table: "order_status_history",
                column: "agency_id");

            migrationBuilder.CreateIndex(
                name: "ix_order_status_history_order_id_changed_at",
                schema: "orders",
                table: "order_status_history",
                columns: new[] { "order_id", "changed_at" });

            migrationBuilder.CreateIndex(
                name: "ix_order_travellers_agency_id",
                schema: "orders",
                table: "order_travellers",
                column: "agency_id");

            migrationBuilder.CreateIndex(
                name: "ix_order_travellers_order_line_id",
                schema: "orders",
                table: "order_travellers",
                column: "order_line_id");

            migrationBuilder.CreateIndex(
                name: "ix_orders_agency_id_order_number",
                schema: "orders",
                table: "orders",
                columns: new[] { "agency_id", "order_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_orders_agency_id_status",
                schema: "orders",
                table: "orders",
                columns: new[] { "agency_id", "status" });

            // ------------------------------------------------------------------------ shape
            //
            // Every IS NOT NULL below is load-bearing: a CHECK constraint PASSES when its expression
            // is NULL, not only when it is true, so "the failed line has a resolution" has to say so
            // explicitly or a NULL slips straight through.
            migrationBuilder.Sql("""
                ALTER TABLE orders.orders
                    ADD CONSTRAINT ck_orders_status
                        CHECK (status IN ('PendingPayment', 'Paid', 'PartiallyFulfilled', 'Confirmed',
                                          'PartiallyFailed', 'Cancelled', 'Refunded')),
                    ADD CONSTRAINT ck_orders_buyer_type
                        CHECK (buyer_type IN ('Customer', 'AgentAssisted')),
                    ADD CONSTRAINT ck_orders_channel
                        CHECK (channel IN ('Storefront', 'Console')),
                    ADD CONSTRAINT ck_orders_currency
                        CHECK (currency ~ '^[A-Z]{3}$'),
                    ADD CONSTRAINT ck_orders_number_shape
                        CHECK (order_number ~ '^[A-Z0-9]([A-Z0-9/-]*[A-Z0-9])?$'),
                    ADD CONSTRAINT ck_orders_totals_not_negative
                        CHECK (total_net_minor >= 0 AND total_markup_minor >= 0
                           AND total_tax_minor >= 0 AND total_platform_fee_minor >= 0),
                    -- The traveller pays net + markup + tax. Trips' fee comes out of the agency's
                    -- margin and is deliberately NOT in the gross, the same rule price quotes follow.
                    ADD CONSTRAINT ck_orders_total_gross
                        CHECK (total_gross_minor = total_net_minor + total_markup_minor + total_tax_minor);

                ALTER TABLE orders.order_lines
                    ADD CONSTRAINT ck_order_lines_item_type
                        CHECK (item_type IN ('Flight', 'Bus', 'Tour', 'Visa', 'GroupDeparture')),
                    ADD CONSTRAINT ck_order_lines_fulfilment_status
                        CHECK (fulfilment_status IN ('Pending', 'Reserved', 'Confirming', 'Confirmed',
                                                     'FailedNeedsResolution', 'Cancelled', 'Refunded')),
                    ADD CONSTRAINT ck_order_lines_resolution_status
                        CHECK (resolution_status IS NULL
                            OR resolution_status IN ('Open', 'InProgress', 'ResolvedRebooked', 'ResolvedRefunded')),
                    -- A line whose money is taken and whose supplier did not deliver must say what is
                    -- being done about it; that pair is what the resolution queue reads.
                    ADD CONSTRAINT ck_order_lines_failure_is_explained
                        CHECK (fulfilment_status <> 'FailedNeedsResolution'
                            OR (resolution_status IS NOT NULL AND failure_reason IS NOT NULL)),
                    ADD CONSTRAINT ck_order_lines_amounts_not_negative
                        CHECK (net_amount_minor >= 0 AND markup_amount_minor >= 0
                           AND tax_amount_minor >= 0 AND platform_fee_minor >= 0),
                    ADD CONSTRAINT ck_order_lines_gross
                        CHECK (gross_amount_minor = net_amount_minor + markup_amount_minor + tax_amount_minor),
                    ADD CONSTRAINT ck_order_lines_markup_explained
                        CHECK (markup_amount_minor = 0 OR markup_rule_id IS NOT NULL),
                    ADD CONSTRAINT ck_order_lines_currency
                        CHECK (currency ~ '^[A-Z]{3}$'),
                    ADD CONSTRAINT ck_order_lines_pax_breakdown_is_object
                        CHECK (jsonb_typeof(pax_breakdown) = 'object');

                ALTER TABLE orders.order_travellers
                    ADD CONSTRAINT ck_order_travellers_type
                        CHECK (traveller_type IN ('Adult', 'Child', 'Infant')),
                    ADD CONSTRAINT ck_order_travellers_nationality
                        CHECK (nationality IS NULL OR nationality ~ '^[A-Z]{2}$');
                ALTER TABLE orders.order_status_history
                    ADD CONSTRAINT ck_order_status_history_to_status
                        CHECK (to_status IN ('PendingPayment', 'Paid', 'PartiallyFulfilled', 'Confirmed',
                                             'PartiallyFailed', 'Cancelled', 'Refunded')),
                    ADD CONSTRAINT ck_order_status_history_from_status
                        CHECK (from_status IS NULL
                            OR from_status IN ('PendingPayment', 'Paid', 'PartiallyFulfilled', 'Confirmed',
                                               'PartiallyFailed', 'Cancelled', 'Refunded')),
                    -- "from_status <> to_status" alone would pass on the first entry, where from_status
                    -- is NULL and a NULL expression passes. Say the NULL case out loud instead.
                    ADD CONSTRAINT ck_order_status_history_actually_changed
                        CHECK (from_status IS NULL OR from_status <> to_status);

                ALTER TABLE orders.carts
                    ADD CONSTRAINT ck_carts_status
                        CHECK (status IN ('Active', 'Converted', 'Abandoned', 'Expired')),
                    ADD CONSTRAINT ck_carts_currency
                        CHECK (currency ~ '^[A-Z]{3}$'),
                    -- A cart nobody can find again is litter. One of the two has to identify it.
                    ADD CONSTRAINT ck_carts_is_identifiable
                        CHECK (customer_id IS NOT NULL OR session_token IS NOT NULL),
                    ADD CONSTRAINT ck_carts_converted_names_its_order
                        CHECK (status <> 'Converted' OR converted_order_id IS NOT NULL);

                ALTER TABLE orders.cart_items
                    ADD CONSTRAINT ck_cart_items_item_type
                        CHECK (item_type IN ('Flight', 'Bus', 'Tour', 'Visa', 'GroupDeparture')),
                    ADD CONSTRAINT ck_cart_items_currency
                        CHECK (currency ~ '^[A-Z]{3}$'),
                    ADD CONSTRAINT ck_cart_items_indicative_gross_not_negative
                        CHECK (indicative_gross_minor >= 0),
                    ADD CONSTRAINT ck_cart_items_pax_breakdown_is_object
                        CHECK (jsonb_typeof(pax_breakdown) = 'object');
                """);

            // -------------------------------------------------------------- the price is final
            //
            // CLAUDE.md rule 5: prices are frozen at purchase, never recalculated. A trigger rather
            // than a REVOKE, because a REVOKE does not bind the schema owner — and placed_at itself is
            // guarded, so the money cannot be reopened by clearing the date that froze it.
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION orders.reject_placed_line_money_change()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $$
                BEGIN
                    IF OLD.placed_at IS NULL THEN
                        RETURN NEW;
                    END IF;

                    IF NEW.net_amount_minor    IS DISTINCT FROM OLD.net_amount_minor
                    OR NEW.markup_amount_minor IS DISTINCT FROM OLD.markup_amount_minor
                    OR NEW.tax_amount_minor    IS DISTINCT FROM OLD.tax_amount_minor
                    OR NEW.platform_fee_minor  IS DISTINCT FROM OLD.platform_fee_minor
                    OR NEW.gross_amount_minor  IS DISTINCT FROM OLD.gross_amount_minor
                    OR NEW.markup_rule_id      IS DISTINCT FROM OLD.markup_rule_id
                    OR NEW.price_quote_id      IS DISTINCT FROM OLD.price_quote_id
                    OR NEW.currency            IS DISTINCT FROM OLD.currency
                    OR NEW.placed_at           IS DISTINCT FROM OLD.placed_at
                    THEN
                        RAISE EXCEPTION
                            'order line % was placed at %; its price is final', OLD.id, OLD.placed_at
                            USING ERRCODE = 'restrict_violation',
                                  HINT = 'Cancel or refund the line and sell again — a price already given never moves.';
                    END IF;

                    RETURN NEW;
                END;
                $$;

                CREATE TRIGGER order_lines_price_final_trg
                    BEFORE UPDATE ON orders.order_lines
                    FOR EACH ROW
                    EXECUTE FUNCTION orders.reject_placed_line_money_change();
                """);


            // ------------------------------------------------------------- the trail is append-only
            //
            // Same reasoning as the price trigger: a REVOKE does not bind the schema owner, and an
            // audit trail its owner can quietly edit is not an audit trail.
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION orders.reject_history_rewrite()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $$
                BEGIN
                    RAISE EXCEPTION 'orders.order_status_history is append-only (attempted %)', TG_OP
                        USING ERRCODE = 'restrict_violation',
                              HINT = 'Record what happened next; never rewrite what happened before.';
                END;
                $$;

                CREATE TRIGGER order_status_history_append_only_trg
                    BEFORE UPDATE OR DELETE ON orders.order_status_history
                    FOR EACH ROW
                    EXECUTE FUNCTION orders.reject_history_rewrite();
                """);

            // ------------------------------------------------- the supplier booking's missing link
            //
            // supplier_bookings.order_line_id has been UNIQUE since #32 but had no foreign key,
            // because order_lines did not exist yet. It does now: one line, at most one booking.
            migrationBuilder.Sql("""
                ALTER TABLE supplier.supplier_bookings
                    ADD CONSTRAINT fk_supplier_bookings_order_lines_order_line_id
                    FOREIGN KEY (order_line_id) REFERENCES orders.order_lines (id) ON DELETE RESTRICT;
                """);

            // ------------------------------------------------------------------ the application role
            //
            // orders is a new schema, so AddRowLevelSecurity's default privileges never covered it.
            // No DELETE on anything: an order is cancelled or refunded, never deleted.
            migrationBuilder.Sql($"""
                GRANT USAGE ON SCHEMA orders TO {AddRowLevelSecurity.ApplicationRole};
                GRANT SELECT, INSERT, UPDATE ON orders.orders TO {AddRowLevelSecurity.ApplicationRole};
                GRANT SELECT, INSERT, UPDATE ON orders.order_lines TO {AddRowLevelSecurity.ApplicationRole};
                GRANT SELECT, INSERT, UPDATE ON orders.order_travellers TO {AddRowLevelSecurity.ApplicationRole};

                -- Append-only: no UPDATE, no DELETE. The trigger above says the same thing to the
                -- owner, who these grants do not bind.
                GRANT SELECT, INSERT ON orders.order_status_history TO {AddRowLevelSecurity.ApplicationRole};

                -- Carts DO get DELETE, unlike everything above. A cart is not a record of anything
                -- that happened: somebody removes an item, the sweeper clears what timed out.
                GRANT SELECT, INSERT, UPDATE, DELETE ON orders.carts TO {AddRowLevelSecurity.ApplicationRole};
                GRANT SELECT, INSERT, UPDATE, DELETE ON orders.cart_items TO {AddRowLevelSecurity.ApplicationRole};
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
                ALTER TABLE supplier.supplier_bookings
                    DROP CONSTRAINT IF EXISTS fk_supplier_bookings_order_lines_order_line_id;
                DROP TRIGGER IF EXISTS order_lines_price_final_trg ON orders.order_lines;
                DROP FUNCTION IF EXISTS orders.reject_placed_line_money_change();
                DROP TRIGGER IF EXISTS order_status_history_append_only_trg ON orders.order_status_history;
                DROP FUNCTION IF EXISTS orders.reject_history_rewrite();
                """);

            migrationBuilder.DropTable(
                name: "cart_items",
                schema: "orders");

            migrationBuilder.DropTable(
                name: "order_status_history",
                schema: "orders");

            migrationBuilder.DropTable(
                name: "order_travellers",
                schema: "orders");

            migrationBuilder.DropTable(
                name: "carts",
                schema: "orders");

            migrationBuilder.DropTable(
                name: "order_lines",
                schema: "orders");

            migrationBuilder.DropTable(
                name: "orders",
                schema: "orders");
        }
    }
}
