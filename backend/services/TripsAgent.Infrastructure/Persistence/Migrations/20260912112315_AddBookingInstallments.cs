using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TripsAgent.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// What one booking on a departure pays, and when: the departure's terms snapshotted on the day
    /// it was booked, and the payments it is split into. Issue #57, plan §3 job 11.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Everything below the generated tables is hand-written, because EF Core can express none of
    /// it: the CHECK constraints, the grants to the application role and row-level security.
    /// <b>Regenerating this migration drops those blocks</b> — carry them across, and grep the new
    /// file for <c>tenant_isolation</c>, <c>FORCE ROW LEVEL SECURITY</c> and <c>GRANT</c> to prove it.
    /// </para>
    /// <para>
    /// A schedule is a bill, not a view of the departure. A later edit to the departure's terms must
    /// never move a payment somebody has already been told about — CLAUDE.md rule 5 — which is why
    /// the amounts and dates are stored here rather than recomputed.
    /// </para>
    /// </remarks>
    public partial class AddBookingInstallments : Migration
    {
        /// <summary>
        /// The tables this migration creates. Both carry their own agency_id — the installment too,
        /// rather than trusting that it is reachable through schedule_id — so each is policed
        /// directly (ADR-0006).
        /// </summary>
        internal static readonly string[] PolicedTables =
        [
            "catalog.booking_payment_schedules",
            "catalog.booking_installments",
        ];

        // The same InitPlan-wrapped calls AddRowLevelSecurity uses; see the note there.
        private const string PlatformScope = "(SELECT tenancy.platform_scope_active())";
        private const string CurrentAgency = "(SELECT tenancy.current_agency_id())";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.CreateTable(
                name: "booking_payment_schedules",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    agency_id = table.Column<Guid>(type: "uuid", nullable: false),
                    departure_id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_line_id = table.Column<Guid>(type: "uuid", nullable: false),
                    pax_count = table.Column<int>(type: "integer", nullable: false),
                    currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false),
                    price_per_pax_minor = table.Column<long>(type: "bigint", nullable: false),
                    contact_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    contact_email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    booked_on = table.Column<DateOnly>(type: "date", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_booking_payment_schedules", x => x.id);
                    table.ForeignKey(
                        name: "fk_booking_payment_schedules_agencies_agency_id",
                        column: x => x.agency_id,
                        principalSchema: "tenancy",
                        principalTable: "agencies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_booking_payment_schedules_departures_departure_id",
                        columns: x => new { x.agency_id, x.departure_id },
                        principalSchema: "catalog",
                        principalTable: "departures",
                        principalColumns: new[] { "agency_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_booking_payment_schedules_order_lines_order_line_id",
                        columns: x => new { x.agency_id, x.order_line_id },
                        principalSchema: "orders",
                        principalTable: "order_lines",
                        principalColumns: new[] { "agency_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "booking_installments",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    agency_id = table.Column<Guid>(type: "uuid", nullable: false),
                    schedule_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sequence = table.Column<int>(type: "integer", nullable: false),
                    label = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    due_date = table.Column<DateOnly>(type: "date", nullable: false),
                    amount_minor = table.Column<long>(type: "bigint", nullable: false),
                    state = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    paid_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_reminder_stage = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    last_reminder_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    flagged_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_booking_installments", x => x.id);
                    table.ForeignKey(
                        name: "fk_booking_installments_agencies_agency_id",
                        column: x => x.agency_id,
                        principalSchema: "tenancy",
                        principalTable: "agencies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_booking_installments_booking_payment_schedules_schedule_id",
                        column: x => x.schedule_id,
                        principalSchema: "catalog",
                        principalTable: "booking_payment_schedules",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_booking_installments_agency_id",
                schema: "catalog",
                table: "booking_installments",
                column: "agency_id");

            migrationBuilder.CreateIndex(
                name: "ix_booking_installments_schedule_id",
                schema: "catalog",
                table: "booking_installments",
                column: "schedule_id");

            migrationBuilder.CreateIndex(
                name: "ix_booking_installments_state_due_date",
                schema: "catalog",
                table: "booking_installments",
                columns: new[] { "state", "due_date" });

            migrationBuilder.CreateIndex(
                name: "ix_booking_payment_schedules_agency_id_departure_id",
                schema: "catalog",
                table: "booking_payment_schedules",
                columns: new[] { "agency_id", "departure_id" });

            migrationBuilder.CreateIndex(
                name: "ix_booking_payment_schedules_agency_id_order_line_id",
                schema: "catalog",
                table: "booking_payment_schedules",
                columns: new[] { "agency_id", "order_line_id" });

            migrationBuilder.CreateIndex(
                name: "ix_booking_payment_schedules_order_line_id",
                schema: "catalog",
                table: "booking_payment_schedules",
                column: "order_line_id",
                unique: true);

            // ------------------------------------------------------------------------ shape
            //
            // Every IS NOT NULL below is load-bearing: a CHECK constraint PASSES when its expression
            // is NULL, not only when it is true. "A paid installment says when it was paid" has to
            // say so explicitly or a NULL slips straight through.
            migrationBuilder.Sql("""
                ALTER TABLE catalog.booking_payment_schedules
                    ADD CONSTRAINT ck_booking_payment_schedules_pax_count
                        CHECK (pax_count >= 1),
                    ADD CONSTRAINT ck_booking_payment_schedules_price
                        CHECK (price_per_pax_minor > 0),
                    ADD CONSTRAINT ck_booking_payment_schedules_contact_name
                        CHECK (btrim(contact_name) <> ''),
                    -- Null means the booking has no address, and nothing is emailed. An empty string
                    -- would mean the same thing while looking like an address, so it is refused.
                    ADD CONSTRAINT ck_booking_payment_schedules_contact_email
                        CHECK (contact_email IS NULL OR btrim(contact_email) <> ''),
                    ADD CONSTRAINT ck_booking_payment_schedules_currency
                        CHECK (currency ~ '^[A-Z]{3}$');

                ALTER TABLE catalog.booking_installments
                    ADD CONSTRAINT ck_booking_installments_state
                        CHECK (state IN ('Pending', 'Paid', 'Cancelled')),
                    ADD CONSTRAINT ck_booking_installments_sequence
                        CHECK (sequence >= 1),
                    ADD CONSTRAINT ck_booking_installments_label
                        CHECK (btrim(label) <> ''),
                    -- A payment of nothing is not a payment. Rounding gives the last line the
                    -- remainder, so every line the schedule builds is above zero.
                    ADD CONSTRAINT ck_booking_installments_amount
                        CHECK (amount_minor > 0),
                    ADD CONSTRAINT ck_booking_installments_paid_at
                        CHECK ((state = 'Paid' AND paid_at IS NOT NULL)
                            OR (state <> 'Paid' AND paid_at IS NULL)),
                    ADD CONSTRAINT ck_booking_installments_reminder_stage
                        CHECK (last_reminder_stage IS NULL
                            OR last_reminder_stage IN ('Overdue', 'OneDay', 'ThreeDays', 'SevenDays')),
                    -- A recorded reminder has a time. Without this half, a stage could be set by a
                    -- job that never actually sent anything and nobody could tell.
                    ADD CONSTRAINT ck_booking_installments_reminder_at
                        CHECK ((last_reminder_stage IS NULL AND last_reminder_at IS NULL)
                            OR (last_reminder_stage IS NOT NULL AND last_reminder_at IS NOT NULL));
                """);

            // ------------------------------------------------------------------ the application role
            //
            // catalog already has USAGE from AddProductCatalog; these are the new tables only.
            migrationBuilder.Sql($"""
                -- No DELETE on a schedule: it is what a traveller was told they owe, and the
                -- booking it belongs to is refunded rather than erased.
                GRANT SELECT, INSERT, UPDATE ON catalog.booking_payment_schedules TO {AddRowLevelSecurity.ApplicationRole};

                -- The installments go with their schedule when one is deleted by retention, which is
                -- the one path that ever removes either.
                GRANT SELECT, INSERT, UPDATE, DELETE ON catalog.booking_installments TO {AddRowLevelSecurity.ApplicationRole};
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
                name: "booking_installments",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "booking_payment_schedules",
                schema: "catalog");
        }
    }
}
