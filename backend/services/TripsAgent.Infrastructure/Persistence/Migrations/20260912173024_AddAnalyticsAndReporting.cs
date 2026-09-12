using System;
using System.Linq;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TripsAgent.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// The <c>analytics</c> schema: the read models the dashboards draw from, and the report tables
    /// (issues 67 and 68).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Nothing here is authoritative.</b> Every table in this schema is derived from
    /// <c>orders</c>, <c>tenancy</c> and <c>supplier</c> by the rollup job, and the job throws its
    /// output away and makes it again from source whenever it runs. That is why there is no
    /// foreign key from <c>fact_bookings</c> to <c>orders.order_lines</c>: a read model that
    /// refuses to be rebuilt because a source row has been purged is a read model that can take
    /// the rollup down with it.
    /// </para>
    /// <para>
    /// <b>Three different tenancy shapes live here</b>, and the policies below are not
    /// interchangeable:
    /// </para>
    /// <list type="number">
    ///   <item>
    ///     <c>fact_bookings</c> and <c>agg_agency_daily</c> belong to one agency, carry a
    ///     non-nullable <c>agency_id</c>, and get the ordinary <c>tenant_isolation</c> policy every
    ///     business table has.
    ///   </item>
    ///   <item>
    ///     <c>agg_platform_daily</c>, <c>agg_supplier_daily</c> and <c>rollup_runs</c> have no
    ///     agency and cannot have one — they ARE the cross-tenant view. There is no column to
    ///     match on, so their policy is <c>platform_scope_active()</c> alone: inside a platform
    ///     scope they are readable, outside one they are empty. An agency cannot infer another
    ///     agency's volume from a row it cannot see.
    ///   </item>
    ///   <item>
    ///     <c>report_jobs</c> and <c>report_exports_audit</c> carry a <i>nullable</i> agency, where
    ///     NULL means "every agency" — a platform report. The policy is the usual
    ///     <c>platform_scope_active() OR agency_id = current_agency_id()</c>, and because NULL is
    ///     never equal to anything, a platform run is invisible to every agency both for reading
    ///     and, through the same WITH CHECK, for writing. The same trick <c>user_roles</c> uses for
    ///     a back-office grant.
    ///   </item>
    /// </list>
    /// <para>
    /// <c>report_definitions</c> is the exception with no policy at all: it is the catalogue of
    /// reports that may be run, like <c>identity.permissions</c>, and holds no agency's data. Who
    /// may run which report is decided by the permission on the endpoint, not by hiding the menu.
    /// It is granted SELECT and nothing else — the seed below is the only thing that writes it.
    /// </para>
    /// <para>
    /// <b>The export log is append-only</b>, like <c>platform.audit_logs</c>. UPDATE and DELETE are
    /// not granted to the application role and a trigger refuses both to anyone the grants do not
    /// bind. It is the only record of who took a large slice of the database out of it, so it must
    /// not be editable by the person who took it (FRD 2.15 UC-1C RS-6).
    /// </para>
    /// <para>
    /// <c>report_schedules</c> is deliberately absent. Scheduled recurring reports are listed under
    /// "what the MVP leaves out" in the build plan, and an empty table nothing reads or writes is
    /// weight without value; it arrives with the feature.
    /// </para>
    /// </remarks>
    public partial class AddAnalyticsAndReporting : Migration
    {
        // The same InitPlan-wrapped calls AddRowLevelSecurity uses; see the note there. Wrapping
        // the function call in a scalar subquery lets PostgreSQL evaluate it once per query rather
        // than once per row.
        private const string PlatformScope = "(SELECT tenancy.platform_scope_active())";
        private const string CurrentAgency = "(SELECT tenancy.current_agency_id())";

        /// <summary>Tables that belong to exactly one agency, policed the ordinary way.</summary>
        private static readonly string[] TenantTables =
        [
            "analytics.fact_bookings",
            "analytics.agg_agency_daily",
        ];

        /// <summary>Tables with no agency at all, readable only inside a platform scope.</summary>
        private static readonly string[] PlatformOnlyTables =
        [
            "analytics.agg_platform_daily",
            "analytics.agg_supplier_daily",
            "analytics.rollup_runs",
        ];

        /// <summary>Tables whose agency is nullable, where NULL means every agency.</summary>
        private static readonly string[] NullableAgencyTables =
        [
            "analytics.report_jobs",
        ];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "analytics");

            migrationBuilder.CreateTable(
                name: "agg_agency_daily",
                schema: "analytics",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    agency_id = table.Column<Guid>(type: "uuid", nullable: false),
                    root_agency_id = table.Column<Guid>(type: "uuid", nullable: false),
                    day = table.Column<DateOnly>(type: "date", nullable: false),
                    currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false),
                    orders_count = table.Column<int>(type: "integer", nullable: false),
                    bookings_count = table.Column<int>(type: "integer", nullable: false),
                    gross_sales_minor = table.Column<long>(type: "bigint", nullable: false),
                    net_cost_minor = table.Column<long>(type: "bigint", nullable: false),
                    markup_minor = table.Column<long>(type: "bigint", nullable: false),
                    tax_minor = table.Column<long>(type: "bigint", nullable: false),
                    platform_fee_minor = table.Column<long>(type: "bigint", nullable: false),
                    refunded_count = table.Column<int>(type: "integer", nullable: false),
                    refunded_gross_minor = table.Column<long>(type: "bigint", nullable: false),
                    cancelled_count = table.Column<int>(type: "integer", nullable: false),
                    failed_count = table.Column<int>(type: "integer", nullable: false),
                    built_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_agg_agency_daily", x => x.id);
                    table.ForeignKey(
                        name: "fk_agg_agency_daily_agencies_agency_id",
                        column: x => x.agency_id,
                        principalSchema: "tenancy",
                        principalTable: "agencies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "agg_platform_daily",
                schema: "analytics",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    day = table.Column<DateOnly>(type: "date", nullable: false),
                    currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false),
                    selling_agencies_count = table.Column<int>(type: "integer", nullable: false),
                    new_agencies_count = table.Column<int>(type: "integer", nullable: false),
                    orders_count = table.Column<int>(type: "integer", nullable: false),
                    bookings_count = table.Column<int>(type: "integer", nullable: false),
                    gmv_minor = table.Column<long>(type: "bigint", nullable: false),
                    net_cost_minor = table.Column<long>(type: "bigint", nullable: false),
                    markup_minor = table.Column<long>(type: "bigint", nullable: false),
                    tax_minor = table.Column<long>(type: "bigint", nullable: false),
                    platform_fee_minor = table.Column<long>(type: "bigint", nullable: false),
                    refunded_count = table.Column<int>(type: "integer", nullable: false),
                    refunded_gross_minor = table.Column<long>(type: "bigint", nullable: false),
                    cancelled_count = table.Column<int>(type: "integer", nullable: false),
                    failed_count = table.Column<int>(type: "integer", nullable: false),
                    built_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_agg_platform_daily", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "agg_supplier_daily",
                schema: "analytics",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    day = table.Column<DateOnly>(type: "date", nullable: false),
                    supplier_id = table.Column<Guid>(type: "uuid", nullable: false),
                    search_count = table.Column<int>(type: "integer", nullable: false),
                    confirm_price_count = table.Column<int>(type: "integer", nullable: false),
                    issue_count = table.Column<int>(type: "integer", nullable: false),
                    status_count = table.Column<int>(type: "integer", nullable: false),
                    other_count = table.Column<int>(type: "integer", nullable: false),
                    error_count = table.Column<int>(type: "integer", nullable: false),
                    timeout_count = table.Column<int>(type: "integer", nullable: false),
                    booked_count = table.Column<int>(type: "integer", nullable: false),
                    average_latency_ms = table.Column<int>(type: "integer", nullable: false),
                    max_latency_ms = table.Column<int>(type: "integer", nullable: false),
                    built_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_agg_supplier_daily", x => x.id);
                    table.ForeignKey(
                        name: "fk_agg_supplier_daily_suppliers_supplier_id",
                        column: x => x.supplier_id,
                        principalSchema: "supplier",
                        principalTable: "suppliers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "fact_bookings",
                schema: "analytics",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_line_id = table.Column<Guid>(type: "uuid", nullable: false),
                    agency_id = table.Column<Guid>(type: "uuid", nullable: false),
                    root_agency_id = table.Column<Guid>(type: "uuid", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    booking_day = table.Column<DateOnly>(type: "date", nullable: false),
                    order_status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    fulfilment_status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    item_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    channel = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    buyer_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    supplier_id = table.Column<Guid>(type: "uuid", nullable: true),
                    currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false),
                    net_amount_minor = table.Column<long>(type: "bigint", nullable: false),
                    markup_amount_minor = table.Column<long>(type: "bigint", nullable: false),
                    tax_amount_minor = table.Column<long>(type: "bigint", nullable: false),
                    platform_fee_minor = table.Column<long>(type: "bigint", nullable: false),
                    gross_amount_minor = table.Column<long>(type: "bigint", nullable: false),
                    is_sale = table.Column<bool>(type: "boolean", nullable: false),
                    is_refunded = table.Column<bool>(type: "boolean", nullable: false),
                    is_cancelled = table.Column<bool>(type: "boolean", nullable: false),
                    is_failed = table.Column<bool>(type: "boolean", nullable: false),
                    source_updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    built_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_fact_bookings", x => x.id);
                    table.ForeignKey(
                        name: "fk_fact_bookings_agencies_agency_id",
                        column: x => x.agency_id,
                        principalSchema: "tenancy",
                        principalTable: "agencies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "report_definitions",
                schema: "analytics",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    scope = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    required_permission = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_report_definitions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "report_jobs",
                schema: "analytics",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    definition_code = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    scope = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    run_mode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    format = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    agency_id = table.Column<Guid>(type: "uuid", nullable: true),
                    requested_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    from_day = table.Column<DateOnly>(type: "date", nullable: false),
                    to_day = table.Column<DateOnly>(type: "date", nullable: false),
                    scope_description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    requested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    row_count = table.Column<int>(type: "integer", nullable: true),
                    result_storage_key = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    result_size_bytes = table.Column<long>(type: "bigint", nullable: true),
                    error_message = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_report_jobs", x => x.id);
                    table.ForeignKey(
                        name: "fk_report_jobs_agencies_agency_id",
                        column: x => x.agency_id,
                        principalSchema: "tenancy",
                        principalTable: "agencies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_report_jobs_users_requested_by_user_id",
                        column: x => x.requested_by_user_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "rollup_runs",
                schema: "analytics",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    watermark_from = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    watermark_to = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    from_day = table.Column<DateOnly>(type: "date", nullable: true),
                    to_day = table.Column<DateOnly>(type: "date", nullable: true),
                    days_rebuilt = table.Column<int>(type: "integer", nullable: false),
                    fact_rows = table.Column<int>(type: "integer", nullable: false),
                    error_message = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_rollup_runs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "report_exports_audit",
                schema: "analytics",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    report_job_id = table.Column<Guid>(type: "uuid", nullable: true),
                    definition_code = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    scope = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    format = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    agency_id = table.Column<Guid>(type: "uuid", nullable: true),
                    actor_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    actor_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    actor_ip_address = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    scope_description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    row_count = table.Column<int>(type: "integer", nullable: false),
                    exported_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    correlation_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_report_exports_audit", x => x.id);
                    table.ForeignKey(
                        name: "fk_report_exports_audit_report_jobs_report_job_id",
                        column: x => x.report_job_id,
                        principalSchema: "analytics",
                        principalTable: "report_jobs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_agg_agency_daily_agency_id_day_currency",
                schema: "analytics",
                table: "agg_agency_daily",
                columns: new[] { "agency_id", "day", "currency" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_agg_agency_daily_day",
                schema: "analytics",
                table: "agg_agency_daily",
                column: "day");

            migrationBuilder.CreateIndex(
                name: "ix_agg_agency_daily_root_agency_id_day",
                schema: "analytics",
                table: "agg_agency_daily",
                columns: new[] { "root_agency_id", "day" });

            migrationBuilder.CreateIndex(
                name: "ix_agg_platform_daily_day_currency",
                schema: "analytics",
                table: "agg_platform_daily",
                columns: new[] { "day", "currency" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_agg_supplier_daily_day_supplier_id",
                schema: "analytics",
                table: "agg_supplier_daily",
                columns: new[] { "day", "supplier_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_agg_supplier_daily_supplier_id",
                schema: "analytics",
                table: "agg_supplier_daily",
                column: "supplier_id");

            migrationBuilder.CreateIndex(
                name: "ix_fact_bookings_agency_id_booking_day",
                schema: "analytics",
                table: "fact_bookings",
                columns: new[] { "agency_id", "booking_day" });

            migrationBuilder.CreateIndex(
                name: "ix_fact_bookings_booking_day",
                schema: "analytics",
                table: "fact_bookings",
                column: "booking_day");

            migrationBuilder.CreateIndex(
                name: "ix_fact_bookings_order_line_id",
                schema: "analytics",
                table: "fact_bookings",
                column: "order_line_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_fact_bookings_root_agency_id_booking_day",
                schema: "analytics",
                table: "fact_bookings",
                columns: new[] { "root_agency_id", "booking_day" });

            migrationBuilder.CreateIndex(
                name: "ix_report_definitions_code",
                schema: "analytics",
                table: "report_definitions",
                column: "code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_report_exports_audit_actor_user_id_exported_at",
                schema: "analytics",
                table: "report_exports_audit",
                columns: new[] { "actor_user_id", "exported_at" });

            migrationBuilder.CreateIndex(
                name: "ix_report_exports_audit_agency_id_exported_at",
                schema: "analytics",
                table: "report_exports_audit",
                columns: new[] { "agency_id", "exported_at" });

            migrationBuilder.CreateIndex(
                name: "ix_report_exports_audit_exported_at",
                schema: "analytics",
                table: "report_exports_audit",
                column: "exported_at");

            migrationBuilder.CreateIndex(
                name: "ix_report_exports_audit_report_job_id",
                schema: "analytics",
                table: "report_exports_audit",
                column: "report_job_id");

            migrationBuilder.CreateIndex(
                name: "ix_report_jobs_agency_id_requested_at",
                schema: "analytics",
                table: "report_jobs",
                columns: new[] { "agency_id", "requested_at" });

            migrationBuilder.CreateIndex(
                name: "ix_report_jobs_requested_by_user_id",
                schema: "analytics",
                table: "report_jobs",
                column: "requested_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_report_jobs_status_requested_at",
                schema: "analytics",
                table: "report_jobs",
                columns: new[] { "status", "requested_at" });

            migrationBuilder.CreateIndex(
                name: "ix_rollup_runs_status_watermark_to",
                schema: "analytics",
                table: "rollup_runs",
                columns: new[] { "status", "watermark_to" });

            // ----------------------------------------------------------------- shape constraints
            //
            // The database's own opinion about what a row in here may say. These are cheap and
            // they catch the class of rollup bug that is otherwise invisible: a fact row whose
            // money does not add up looks perfectly fine on a dashboard.
            migrationBuilder.Sql("""
                ALTER TABLE analytics.fact_bookings
                    ADD CONSTRAINT ck_fact_bookings_amounts_not_negative
                        CHECK (net_amount_minor >= 0 AND markup_amount_minor >= 0
                           AND tax_amount_minor >= 0 AND platform_fee_minor >= 0),
                    -- The same identity orders.order_lines is held to. A fact row is a copy of a
                    -- line; if the copy stops adding up, the copier is broken.
                    ADD CONSTRAINT ck_fact_bookings_gross
                        CHECK (gross_amount_minor = net_amount_minor + markup_amount_minor + tax_amount_minor),
                    ADD CONSTRAINT ck_fact_bookings_currency
                        CHECK (currency ~ '^[A-Z]{3}$');

                ALTER TABLE analytics.agg_agency_daily
                    ADD CONSTRAINT ck_agg_agency_daily_counts_not_negative
                        CHECK (orders_count >= 0 AND bookings_count >= 0 AND refunded_count >= 0
                           AND cancelled_count >= 0 AND failed_count >= 0),
                    ADD CONSTRAINT ck_agg_agency_daily_currency
                        CHECK (currency ~ '^[A-Z]{3}$');

                ALTER TABLE analytics.agg_platform_daily
                    ADD CONSTRAINT ck_agg_platform_daily_counts_not_negative
                        CHECK (selling_agencies_count >= 0 AND new_agencies_count >= 0
                           AND orders_count >= 0 AND bookings_count >= 0),
                    ADD CONSTRAINT ck_agg_platform_daily_currency
                        CHECK (currency ~ '^[A-Z]{3}$');

                ALTER TABLE analytics.agg_supplier_daily
                    ADD CONSTRAINT ck_agg_supplier_daily_counts_not_negative
                        CHECK (search_count >= 0 AND confirm_price_count >= 0 AND issue_count >= 0
                           AND status_count >= 0 AND other_count >= 0 AND error_count >= 0
                           AND timeout_count >= 0 AND booked_count >= 0
                           AND average_latency_ms >= 0 AND max_latency_ms >= 0),
                    -- A ticket cannot be issued more often than it was attempted.
                    ADD CONSTRAINT ck_agg_supplier_daily_booked_within_issues
                        CHECK (booked_count <= issue_count);

                ALTER TABLE analytics.report_jobs
                    -- The scope IS the agency question: an agency report names its agency, a
                    -- platform report must not, because NULL is what makes it invisible to every
                    -- agency under the policy below.
                    ADD CONSTRAINT ck_report_jobs_scope_agency
                        CHECK ((scope = 'Agency' AND agency_id IS NOT NULL)
                            OR (scope = 'Platform' AND agency_id IS NULL)),
                    ADD CONSTRAINT ck_report_jobs_days
                        CHECK (to_day >= from_day),
                    ADD CONSTRAINT ck_report_jobs_failure_is_explained
                        CHECK (status <> 'Failed' OR error_message IS NOT NULL),
                    ADD CONSTRAINT ck_report_jobs_success_has_a_file
                        CHECK (status <> 'Succeeded'
                            OR (result_storage_key IS NOT NULL AND row_count IS NOT NULL));

                ALTER TABLE analytics.report_exports_audit
                    ADD CONSTRAINT ck_report_exports_audit_row_count
                        CHECK (row_count >= 0),
                    ADD CONSTRAINT ck_report_exports_audit_scope_agency
                        CHECK ((scope = 'Agency' AND agency_id IS NOT NULL)
                            OR (scope = 'Platform' AND agency_id IS NULL));
                """);

            // ------------------------------------------------------- the export log is append-only
            //
            // The same guard platform.audit_logs has, and for a stronger reason: this table records
            // who took data out of the system. An actor who can edit the record of their own export
            // has not been audited. The grants below withhold UPDATE and DELETE from the
            // application role; this trigger says the same thing to the table's owner, whom grants
            // do not bind.
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION analytics.reject_export_audit_rewrite()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $$
                BEGIN
                    RAISE EXCEPTION 'analytics.report_exports_audit is append-only (attempted %)', TG_OP
                        USING ERRCODE = 'restrict_violation',
                              HINT = 'An export happened. Record the next one; never rewrite the last.';
                END;
                $$;

                CREATE TRIGGER report_exports_audit_append_only_trg
                    BEFORE UPDATE OR DELETE ON analytics.report_exports_audit
                    FOR EACH ROW
                    EXECUTE FUNCTION analytics.reject_export_audit_rewrite();
                """);

            // ------------------------------------------------------------------ the application role
            //
            // analytics is a new schema, so AddRowLevelSecurity's default privileges never reached
            // it. DELETE is granted on the derived tables and only on those: rebuilding a day means
            // deleting it first, which is the whole mechanism by which a rebuild reproduces
            // identical numbers rather than doubling them.
            migrationBuilder.Sql($"""
                GRANT USAGE ON SCHEMA analytics TO {AddRowLevelSecurity.ApplicationRole};

                -- Derived. Deleted and rebuilt from source on every rollup.
                GRANT SELECT, INSERT, UPDATE, DELETE ON analytics.fact_bookings TO {AddRowLevelSecurity.ApplicationRole};
                GRANT SELECT, INSERT, UPDATE, DELETE ON analytics.agg_agency_daily TO {AddRowLevelSecurity.ApplicationRole};
                GRANT SELECT, INSERT, UPDATE, DELETE ON analytics.agg_platform_daily TO {AddRowLevelSecurity.ApplicationRole};
                GRANT SELECT, INSERT, UPDATE, DELETE ON analytics.agg_supplier_daily TO {AddRowLevelSecurity.ApplicationRole};

                -- A run is opened, then finished. Never deleted: the history is how anyone knows
                -- whether the rollup has been keeping up.
                GRANT SELECT, INSERT, UPDATE ON analytics.rollup_runs TO {AddRowLevelSecurity.ApplicationRole};

                -- Reference data. The migration seeds it; the application only reads it.
                GRANT SELECT ON analytics.report_definitions TO {AddRowLevelSecurity.ApplicationRole};

                -- A job is requested, picked up, and finished or failed. Never deleted.
                GRANT SELECT, INSERT, UPDATE ON analytics.report_jobs TO {AddRowLevelSecurity.ApplicationRole};

                -- Append-only. No UPDATE, no DELETE, and the trigger above for anyone this does
                -- not bind.
                GRANT SELECT, INSERT ON analytics.report_exports_audit TO {AddRowLevelSecurity.ApplicationRole};
                """);

            // ------------------------------------------------------------------ row-level security
            //
            // ADR-0006: the EF query filter is the first line, and this is the backstop underneath
            // it. Three shapes, three policies — see the class remarks for why they differ.
            foreach (var table in TenantTables)
            {
                migrationBuilder.Sql($"""
                    ALTER TABLE {table} ENABLE ROW LEVEL SECURITY;
                    -- FORCE: without it the table's owner is exempt, and a deployment that connects
                    -- as the owner would be silently unpoliced.
                    ALTER TABLE {table} FORCE ROW LEVEL SECURITY;
                    CREATE POLICY tenant_isolation ON {table}
                        USING ({PlatformScope} OR agency_id = {CurrentAgency})
                        WITH CHECK ({PlatformScope} OR agency_id = {CurrentAgency});
                    """);
            }

            foreach (var table in PlatformOnlyTables)
            {
                migrationBuilder.Sql($"""
                    ALTER TABLE {table} ENABLE ROW LEVEL SECURITY;
                    ALTER TABLE {table} FORCE ROW LEVEL SECURITY;
                    -- No agency column to match, so there is nothing an agency could be allowed to
                    -- see. Inside a platform scope, everything; outside one, nothing.
                    CREATE POLICY platform_only ON {table}
                        USING ({PlatformScope})
                        WITH CHECK ({PlatformScope});
                    """);
            }

            foreach (var table in NullableAgencyTables)
            {
                migrationBuilder.Sql($"""
                    ALTER TABLE {table} ENABLE ROW LEVEL SECURITY;
                    ALTER TABLE {table} FORCE ROW LEVEL SECURITY;
                    -- NULL never equals current_agency_id(), so a platform row is invisible to
                    -- every agency for both reading and writing.
                    CREATE POLICY tenant_isolation ON {table}
                        USING ({PlatformScope} OR agency_id = {CurrentAgency})
                        WITH CHECK ({PlatformScope} OR agency_id = {CurrentAgency});
                    """);
            }

            // The export log reads like a tenant table and writes like the audit log: an agency
            // sees its own exports, and a write is never refused. A background job holds no tenant,
            // and a job that could not record an export would fail silently into exactly the blind
            // spot this table exists to remove. UPDATE and DELETE are already gone, so "open" here
            // means INSERT and nothing else.
            migrationBuilder.Sql($"""
                ALTER TABLE analytics.report_exports_audit ENABLE ROW LEVEL SECURITY;
                ALTER TABLE analytics.report_exports_audit FORCE ROW LEVEL SECURITY;

                CREATE POLICY tenant_isolation_read ON analytics.report_exports_audit
                    FOR SELECT
                    USING ({PlatformScope} OR agency_id = {CurrentAgency});

                CREATE POLICY append_only_write ON analytics.report_exports_audit
                    FOR INSERT
                    WITH CHECK (true);
                """);

            // ------------------------------------------------------------------ the report catalogue
            //
            // Seeded here rather than by a runtime seeder so a fresh database can run a report
            // without anything else having started. Ids are fixed in ReportCatalog, so re-running
            // this updates the same rows instead of making more — ON CONFLICT, not INSERT.
            migrationBuilder.Sql(SeedReportDefinitions());
        }

        /// <summary>
        /// The seed for <c>analytics.report_definitions</c>, generated from
        /// <c>TripsAgent.Domain.Analytics.ReportCatalog</c> so the two cannot drift.
        /// </summary>
        /// <remarks>
        /// Written by hand into SQL rather than through <c>InsertData</c> because the upsert is the
        /// point: this migration is the only writer, and it has to be safe to re-run against a
        /// database that already has the rows.
        /// </remarks>
        private static string SeedReportDefinitions()
        {
            var values = string.Join(
                ",\n                ",
                TripsAgent.Domain.Analytics.ReportCatalog.All.Select(definition =>
                    $"('{definition.Id}', '{Escape(definition.Code)}', '{Escape(definition.Name)}', "
                    + $"'{Escape(definition.Description)}', '{definition.Scope}', "
                    + $"'{Escape(definition.RequiredPermission)}', {(definition.IsActive ? "true" : "false")})"));

            return $"""
                INSERT INTO analytics.report_definitions
                    (id, code, name, description, scope, required_permission, is_active)
                VALUES
                {values}
                ON CONFLICT (id) DO UPDATE SET
                    code = EXCLUDED.code,
                    name = EXCLUDED.name,
                    description = EXCLUDED.description,
                    scope = EXCLUDED.scope,
                    required_permission = EXCLUDED.required_permission,
                    is_active = EXCLUDED.is_active;
                """;
        }

        /// <summary>Doubles a single quote, so a report name with an apostrophe cannot end the literal.</summary>
        private static string Escape(string value) => value.Replace("'", "''", StringComparison.Ordinal);

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Policies and the trigger first: DROP TABLE would take them with it, but dropping them
            // explicitly keeps this readable as the exact inverse of Up, and leaves the schema
            // clean if a later edit stops dropping one of these tables.
            foreach (var table in TenantTables)
            {
                migrationBuilder.Sql($"DROP POLICY IF EXISTS tenant_isolation ON {table};");
            }

            foreach (var table in PlatformOnlyTables)
            {
                migrationBuilder.Sql($"DROP POLICY IF EXISTS platform_only ON {table};");
            }

            foreach (var table in NullableAgencyTables)
            {
                migrationBuilder.Sql($"DROP POLICY IF EXISTS tenant_isolation ON {table};");
            }

            migrationBuilder.Sql("""
                DROP POLICY IF EXISTS append_only_write ON analytics.report_exports_audit;
                DROP POLICY IF EXISTS tenant_isolation_read ON analytics.report_exports_audit;
                DROP TRIGGER IF EXISTS report_exports_audit_append_only_trg ON analytics.report_exports_audit;
                DROP FUNCTION IF EXISTS analytics.reject_export_audit_rewrite();
                """);

            migrationBuilder.DropTable(
                name: "agg_agency_daily",
                schema: "analytics");

            migrationBuilder.DropTable(
                name: "agg_platform_daily",
                schema: "analytics");

            migrationBuilder.DropTable(
                name: "agg_supplier_daily",
                schema: "analytics");

            migrationBuilder.DropTable(
                name: "fact_bookings",
                schema: "analytics");

            migrationBuilder.DropTable(
                name: "report_definitions",
                schema: "analytics");

            migrationBuilder.DropTable(
                name: "report_exports_audit",
                schema: "analytics");

            migrationBuilder.DropTable(
                name: "rollup_runs",
                schema: "analytics");

            migrationBuilder.DropTable(
                name: "report_jobs",
                schema: "analytics");
        }
    }
}
