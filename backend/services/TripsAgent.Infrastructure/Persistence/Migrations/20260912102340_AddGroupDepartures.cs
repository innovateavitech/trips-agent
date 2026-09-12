using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TripsAgent.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Group departures: dated runs of a tour or package sold by the seat, with tiered prices, a
    /// deposit, an installment plan, seat holds, a waitlist and a manifest. Issue #57, plan §2.5.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Everything below the generated tables is hand-written, because EF Core can express none of
    /// it: the CHECK constraints, the grants to the application role and row-level security.
    /// <b>Regenerating this migration drops those blocks</b> — carry them across, and grep the new
    /// file for <c>tenant_isolation</c>, <c>FORCE ROW LEVEL SECURITY</c> and <c>GRANT</c> to prove it.
    /// </para>
    /// <para>
    /// The one constraint to read before anything else is <c>ck_departures_no_oversell</c>. It is
    /// what makes selling the same last seat twice impossible, in the database rather than in
    /// hopeful application code, however many API instances are running.
    /// </para>
    /// </remarks>
    public partial class AddGroupDepartures : Migration
    {
        /// <summary>
        /// The tables this migration creates. Every one carries its own agency_id — the child tables
        /// too, rather than trusting that they are reachable through departure_id — so each is
        /// policed directly (ADR-0006).
        /// </summary>
        internal static readonly string[] PolicedTables =
        [
            "catalog.departures",
            "catalog.departure_price_tiers",
            "catalog.installment_plans",
            "catalog.installment_schedule_items",
            "catalog.departure_holds",
            "catalog.departure_waitlist",
            "catalog.pax_manifests",
        ];

        // The same InitPlan-wrapped calls AddRowLevelSecurity uses; see the note there.
        private const string PlatformScope = "(SELECT tenancy.platform_scope_active())";
        private const string CurrentAgency = "(SELECT tenancy.current_agency_id())";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.DropIndex(
                name: "ix_order_travellers_agency_id",
                schema: "orders",
                table: "order_travellers");

            migrationBuilder.AddUniqueConstraint(
                name: "ak_products_agency_id_id",
                schema: "catalog",
                table: "products",
                columns: new[] { "agency_id", "id" });

            migrationBuilder.AddUniqueConstraint(
                name: "ak_order_travellers_agency_id_id",
                schema: "orders",
                table: "order_travellers",
                columns: new[] { "agency_id", "id" });

            migrationBuilder.AddUniqueConstraint(
                name: "ak_order_lines_agency_id_id",
                schema: "orders",
                table: "order_lines",
                columns: new[] { "agency_id", "id" });

            migrationBuilder.AddUniqueConstraint(
                name: "ak_carts_agency_id_id",
                schema: "orders",
                table: "carts",
                columns: new[] { "agency_id", "id" });

            migrationBuilder.CreateTable(
                name: "departures",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    agency_id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    departure_date = table.Column<DateOnly>(type: "date", nullable: false),
                    is_group_departure = table.Column<bool>(type: "boolean", nullable: false),
                    min_pax = table.Column<int>(type: "integer", nullable: false),
                    max_pax = table.Column<int>(type: "integer", nullable: false),
                    capacity_total = table.Column<int>(type: "integer", nullable: false),
                    capacity_reserved = table.Column<int>(type: "integer", nullable: false),
                    capacity_confirmed = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    deposit_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    deposit_amount_minor = table.Column<long>(type: "bigint", nullable: true),
                    cutoff_days_before = table.Column<int>(type: "integer", nullable: false),
                    cutoff_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_departures", x => x.id);
                    table.UniqueConstraint("ak_departures_agency_id_id", x => new { x.agency_id, x.id });
                    table.ForeignKey(
                        name: "fk_departures_agencies_agency_id",
                        column: x => x.agency_id,
                        principalSchema: "tenancy",
                        principalTable: "agencies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_departures_products_product_id",
                        columns: x => new { x.agency_id, x.product_id },
                        principalSchema: "catalog",
                        principalTable: "products",
                        principalColumns: new[] { "agency_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "departure_holds",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    agency_id = table.Column<Guid>(type: "uuid", nullable: false),
                    departure_id = table.Column<Guid>(type: "uuid", nullable: false),
                    cart_id = table.Column<Guid>(type: "uuid", nullable: false),
                    pax_count = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    settled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_departure_holds", x => x.id);
                    table.ForeignKey(
                        name: "fk_departure_holds_agencies_agency_id",
                        column: x => x.agency_id,
                        principalSchema: "tenancy",
                        principalTable: "agencies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_departure_holds_carts_cart_id",
                        columns: x => new { x.agency_id, x.cart_id },
                        principalSchema: "orders",
                        principalTable: "carts",
                        principalColumns: new[] { "agency_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_departure_holds_departures_departure_id",
                        columns: x => new { x.agency_id, x.departure_id },
                        principalSchema: "catalog",
                        principalTable: "departures",
                        principalColumns: new[] { "agency_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "departure_price_tiers",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    agency_id = table.Column<Guid>(type: "uuid", nullable: false),
                    departure_id = table.Column<Guid>(type: "uuid", nullable: false),
                    min_pax = table.Column<int>(type: "integer", nullable: false),
                    max_pax = table.Column<int>(type: "integer", nullable: true),
                    price_per_pax_minor = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_departure_price_tiers", x => x.id);
                    table.ForeignKey(
                        name: "fk_departure_price_tiers_agencies_agency_id",
                        column: x => x.agency_id,
                        principalSchema: "tenancy",
                        principalTable: "agencies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_departure_price_tiers_departures_departure_id",
                        column: x => x.departure_id,
                        principalSchema: "catalog",
                        principalTable: "departures",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "departure_waitlist",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    agency_id = table.Column<Guid>(type: "uuid", nullable: false),
                    departure_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    email = table.Column<string>(type: "citext", maxLength: 320, nullable: false),
                    pax_count = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    joined_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    offered_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_departure_waitlist", x => x.id);
                    table.ForeignKey(
                        name: "fk_departure_waitlist_agencies_agency_id",
                        column: x => x.agency_id,
                        principalSchema: "tenancy",
                        principalTable: "agencies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_departure_waitlist_departures_departure_id",
                        columns: x => new { x.agency_id, x.departure_id },
                        principalSchema: "catalog",
                        principalTable: "departures",
                        principalColumns: new[] { "agency_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "installment_plans",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    agency_id = table.Column<Guid>(type: "uuid", nullable: false),
                    departure_id = table.Column<Guid>(type: "uuid", nullable: false),
                    deposit_percent_basis_points = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_installment_plans", x => x.id);
                    table.ForeignKey(
                        name: "fk_installment_plans_agencies_agency_id",
                        column: x => x.agency_id,
                        principalSchema: "tenancy",
                        principalTable: "agencies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_installment_plans_departures_departure_id",
                        column: x => x.departure_id,
                        principalSchema: "catalog",
                        principalTable: "departures",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "pax_manifests",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    agency_id = table.Column<Guid>(type: "uuid", nullable: false),
                    departure_id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_line_id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_traveller_id = table.Column<Guid>(type: "uuid", nullable: false),
                    room_assignment = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_pax_manifests", x => x.id);
                    table.ForeignKey(
                        name: "fk_pax_manifests_agencies_agency_id",
                        column: x => x.agency_id,
                        principalSchema: "tenancy",
                        principalTable: "agencies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_pax_manifests_departures_departure_id",
                        columns: x => new { x.agency_id, x.departure_id },
                        principalSchema: "catalog",
                        principalTable: "departures",
                        principalColumns: new[] { "agency_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_pax_manifests_order_lines_order_line_id",
                        columns: x => new { x.agency_id, x.order_line_id },
                        principalSchema: "orders",
                        principalTable: "order_lines",
                        principalColumns: new[] { "agency_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_pax_manifests_order_travellers_order_traveller_id",
                        columns: x => new { x.agency_id, x.order_traveller_id },
                        principalSchema: "orders",
                        principalTable: "order_travellers",
                        principalColumns: new[] { "agency_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "installment_schedule_items",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    agency_id = table.Column<Guid>(type: "uuid", nullable: false),
                    installment_plan_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sequence = table.Column<int>(type: "integer", nullable: false),
                    due_basis = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    due_offset_days = table.Column<int>(type: "integer", nullable: false),
                    percent_of_balance_basis_points = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_installment_schedule_items", x => x.id);
                    table.ForeignKey(
                        name: "fk_installment_schedule_items_agencies_agency_id",
                        column: x => x.agency_id,
                        principalSchema: "tenancy",
                        principalTable: "agencies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_installment_schedule_items_installment_plans_installment_pl",
                        column: x => x.installment_plan_id,
                        principalSchema: "catalog",
                        principalTable: "installment_plans",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_order_travellers_agency_id_order_line_id",
                schema: "orders",
                table: "order_travellers",
                columns: new[] { "agency_id", "order_line_id" });

            migrationBuilder.CreateIndex(
                name: "ix_departure_holds_agency_id_cart_id",
                schema: "catalog",
                table: "departure_holds",
                columns: new[] { "agency_id", "cart_id" });

            migrationBuilder.CreateIndex(
                name: "ix_departure_holds_agency_id_departure_id",
                schema: "catalog",
                table: "departure_holds",
                columns: new[] { "agency_id", "departure_id" });

            migrationBuilder.CreateIndex(
                name: "ix_departure_holds_departure_id",
                schema: "catalog",
                table: "departure_holds",
                column: "departure_id");

            migrationBuilder.CreateIndex(
                name: "ix_departure_holds_status_expires_at",
                schema: "catalog",
                table: "departure_holds",
                columns: new[] { "status", "expires_at" });

            migrationBuilder.CreateIndex(
                name: "ix_departure_price_tiers_agency_id",
                schema: "catalog",
                table: "departure_price_tiers",
                column: "agency_id");

            migrationBuilder.CreateIndex(
                name: "ix_departure_price_tiers_departure_id",
                schema: "catalog",
                table: "departure_price_tiers",
                column: "departure_id");

            migrationBuilder.CreateIndex(
                name: "ix_departure_waitlist_agency_id_departure_id",
                schema: "catalog",
                table: "departure_waitlist",
                columns: new[] { "agency_id", "departure_id" });

            migrationBuilder.CreateIndex(
                name: "ix_departure_waitlist_departure_id_email",
                schema: "catalog",
                table: "departure_waitlist",
                columns: new[] { "departure_id", "email" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_departure_waitlist_departure_id_status_joined_at",
                schema: "catalog",
                table: "departure_waitlist",
                columns: new[] { "departure_id", "status", "joined_at" });

            migrationBuilder.CreateIndex(
                name: "ix_departure_waitlist_status_expires_at",
                schema: "catalog",
                table: "departure_waitlist",
                columns: new[] { "status", "expires_at" });

            migrationBuilder.CreateIndex(
                name: "ix_departures_agency_id_product_id_departure_date",
                schema: "catalog",
                table: "departures",
                columns: new[] { "agency_id", "product_id", "departure_date" });

            migrationBuilder.CreateIndex(
                name: "ix_departures_status_departure_date",
                schema: "catalog",
                table: "departures",
                columns: new[] { "status", "departure_date" });

            migrationBuilder.CreateIndex(
                name: "ix_installment_plans_agency_id",
                schema: "catalog",
                table: "installment_plans",
                column: "agency_id");

            migrationBuilder.CreateIndex(
                name: "ix_installment_plans_departure_id",
                schema: "catalog",
                table: "installment_plans",
                column: "departure_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_installment_schedule_items_agency_id",
                schema: "catalog",
                table: "installment_schedule_items",
                column: "agency_id");

            migrationBuilder.CreateIndex(
                name: "ix_installment_schedule_items_installment_plan_id",
                schema: "catalog",
                table: "installment_schedule_items",
                column: "installment_plan_id");

            migrationBuilder.CreateIndex(
                name: "ix_pax_manifests_agency_id_departure_id",
                schema: "catalog",
                table: "pax_manifests",
                columns: new[] { "agency_id", "departure_id" });

            migrationBuilder.CreateIndex(
                name: "ix_pax_manifests_agency_id_order_line_id",
                schema: "catalog",
                table: "pax_manifests",
                columns: new[] { "agency_id", "order_line_id" });

            migrationBuilder.CreateIndex(
                name: "ix_pax_manifests_agency_id_order_traveller_id",
                schema: "catalog",
                table: "pax_manifests",
                columns: new[] { "agency_id", "order_traveller_id" });

            migrationBuilder.CreateIndex(
                name: "ix_pax_manifests_departure_id_order_traveller_id",
                schema: "catalog",
                table: "pax_manifests",
                columns: new[] { "departure_id", "order_traveller_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_pax_manifests_order_line_id",
                schema: "catalog",
                table: "pax_manifests",
                column: "order_line_id");

            // ------------------------------------------------------------------------ shape
            //
            // Every IS NOT NULL below is load-bearing: a CHECK constraint PASSES when its expression
            // is NULL, not only when it is true, so "a fixed deposit has an amount" has to say so
            // explicitly or a NULL slips straight through. The same reasoning as orders.
            migrationBuilder.Sql("""
                ALTER TABLE catalog.departures
                    ADD CONSTRAINT ck_departures_status
                        CHECK (status IN ('Open', 'Guaranteed', 'NearlyFull', 'SoldOut', 'Closed', 'Cancelled')),
                    ADD CONSTRAINT ck_departures_deposit_type
                        CHECK (deposit_type IN ('None', 'Percent', 'Fixed')),

                    -- THE constraint. Two checkouts racing for the last seat cannot both win: the
                    -- second UPDATE fails here, whatever the application believed it had read.
                    ADD CONSTRAINT ck_departures_no_oversell
                        CHECK (capacity_reserved + capacity_confirmed <= capacity_total),

                    ADD CONSTRAINT ck_departures_capacity_total
                        CHECK (capacity_total >= 1 AND capacity_total <= 5000),
                    ADD CONSTRAINT ck_departures_capacity_not_negative
                        CHECK (capacity_reserved >= 0 AND capacity_confirmed >= 0),
                    ADD CONSTRAINT ck_departures_min_pax
                        CHECK (min_pax >= 1 AND min_pax <= capacity_total),
                    ADD CONSTRAINT ck_departures_max_pax
                        CHECK (max_pax >= min_pax AND max_pax <= capacity_total),
                    ADD CONSTRAINT ck_departures_cutoff_days_before
                        CHECK (cutoff_days_before >= 0 AND cutoff_days_before <= 3650),
                    ADD CONSTRAINT ck_departures_version
                        CHECK (version >= 1),

                    -- A fixed deposit has an amount and nothing else does, so a deposit can never be
                    -- read from a column that was left over from a different kind of deposit.
                    ADD CONSTRAINT ck_departures_deposit_amount
                        CHECK ((deposit_type = 'Fixed' AND deposit_amount_minor IS NOT NULL AND deposit_amount_minor > 0)
                            OR (deposit_type <> 'Fixed' AND deposit_amount_minor IS NULL));

                ALTER TABLE catalog.departure_price_tiers
                    ADD CONSTRAINT ck_departure_price_tiers_min_pax
                        CHECK (min_pax >= 1),
                    ADD CONSTRAINT ck_departure_price_tiers_max_pax
                        CHECK (max_pax IS NULL OR max_pax >= min_pax),
                    ADD CONSTRAINT ck_departure_price_tiers_price
                        CHECK (price_per_pax_minor > 0);

                ALTER TABLE catalog.installment_plans
                    ADD CONSTRAINT ck_installment_plans_deposit_percent
                        CHECK (deposit_percent_basis_points IS NULL
                            OR (deposit_percent_basis_points > 0 AND deposit_percent_basis_points <= 10000));

                ALTER TABLE catalog.installment_schedule_items
                    ADD CONSTRAINT ck_installment_schedule_items_due_basis
                        CHECK (due_basis IN ('FromBooking', 'BeforeDeparture')),
                    ADD CONSTRAINT ck_installment_schedule_items_sequence
                        CHECK (sequence >= 1),
                    ADD CONSTRAINT ck_installment_schedule_items_offset
                        CHECK (due_offset_days >= 0 AND due_offset_days <= 3650),
                    ADD CONSTRAINT ck_installment_schedule_items_share
                        CHECK (percent_of_balance_basis_points > 0
                           AND percent_of_balance_basis_points <= 10000);

                ALTER TABLE catalog.departure_holds
                    ADD CONSTRAINT ck_departure_holds_status
                        CHECK (status IN ('Held', 'Released', 'Converted')),
                    ADD CONSTRAINT ck_departure_holds_pax_count
                        CHECK (pax_count >= 1),
                    -- A hold that is no longer held says when it stopped being held; one that still
                    -- is says nothing. Without the second half a settled hold could have no date.
                    ADD CONSTRAINT ck_departure_holds_settled
                        CHECK ((status = 'Held' AND settled_at IS NULL)
                            OR (status <> 'Held' AND settled_at IS NOT NULL));

                ALTER TABLE catalog.departure_waitlist
                    ADD CONSTRAINT ck_departure_waitlist_status
                        CHECK (status IN ('Waiting', 'Offered', 'Converted', 'Expired')),
                    ADD CONSTRAINT ck_departure_waitlist_pax_count
                        CHECK (pax_count >= 1),
                    ADD CONSTRAINT ck_departure_waitlist_name
                        CHECK (btrim(name) <> ''),
                    -- An open offer has a deadline. Without one, job 10 would never roll it on and
                    -- a seat would sit unsold behind somebody who stopped answering.
                    ADD CONSTRAINT ck_departure_waitlist_offer_is_dated
                        CHECK (status <> 'Offered'
                            OR (offered_at IS NOT NULL AND expires_at IS NOT NULL));

                ALTER TABLE catalog.pax_manifests
                    ADD CONSTRAINT ck_pax_manifests_room_assignment
                        CHECK (room_assignment IS NULL OR btrim(room_assignment) <> '');
                """);

            // ------------------------------------------------------------------ the application role
            //
            // catalog already has USAGE from AddProductCatalog; these are the new tables only.
            migrationBuilder.Sql($"""
                -- No DELETE: a departure is closed or cancelled, never deleted, because order lines
                -- and manifest rows point at it.
                GRANT SELECT, INSERT, UPDATE ON catalog.departures TO {AddRowLevelSecurity.ApplicationRole};

                -- The rows a departure is made of are replaced when it is saved, so these do get DELETE.
                GRANT SELECT, INSERT, UPDATE, DELETE ON
                    catalog.departure_price_tiers,
                    catalog.installment_plans,
                    catalog.installment_schedule_items
                    TO {AddRowLevelSecurity.ApplicationRole};

                -- Records of things that happened. A hold is released rather than deleted, a waitlist
                -- entry expires rather than vanishing, and a manifest row is the proof somebody
                -- travelled. Deleting a cart does cascade its holds away, which is the one exception
                -- the foreign key makes, and the cart's own grants already allow it.
                GRANT SELECT, INSERT, UPDATE ON
                    catalog.departure_holds,
                    catalog.departure_waitlist,
                    catalog.pax_manifests
                    TO {AddRowLevelSecurity.ApplicationRole};
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
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            // The CHECKs and grants go with their tables; the policies are dropped by name first so
            // the Down reads as the exact reverse of the Up.
            foreach (var table in PolicedTables)
            {
                migrationBuilder.Sql($"DROP POLICY IF EXISTS tenant_isolation ON {table};");
            }

            migrationBuilder.DropTable(
                name: "departure_holds",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "departure_price_tiers",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "departure_waitlist",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "installment_schedule_items",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "pax_manifests",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "installment_plans",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "departures",
                schema: "catalog");

            migrationBuilder.DropUniqueConstraint(
                name: "ak_products_agency_id_id",
                schema: "catalog",
                table: "products");

            migrationBuilder.DropUniqueConstraint(
                name: "ak_order_travellers_agency_id_id",
                schema: "orders",
                table: "order_travellers");

            migrationBuilder.DropIndex(
                name: "ix_order_travellers_agency_id_order_line_id",
                schema: "orders",
                table: "order_travellers");

            migrationBuilder.DropUniqueConstraint(
                name: "ak_order_lines_agency_id_id",
                schema: "orders",
                table: "order_lines");

            migrationBuilder.DropUniqueConstraint(
                name: "ak_carts_agency_id_id",
                schema: "orders",
                table: "carts");

            migrationBuilder.CreateIndex(
                name: "ix_order_travellers_agency_id",
                schema: "orders",
                table: "order_travellers",
                column: "agency_id");
        }
    }
}
