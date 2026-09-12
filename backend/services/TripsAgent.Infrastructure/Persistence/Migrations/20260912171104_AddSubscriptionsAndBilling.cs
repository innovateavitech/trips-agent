using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TripsAgent.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// The <c>billing</c> schema: subscription tiers, what they grant, and what each agency is
    /// charged (issues 64 and 65).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two kinds of table live here and they are policed differently.
    /// </para>
    /// <list type="bullet">
    ///   <item>
    ///     <b>Platform-owned</b> — <c>entitlements</c>, <c>subscription_tiers</c>,
    ///     <c>tier_prices</c>, <c>tier_entitlements</c>, <c>tier_change_log</c>. These belong to
    ///     Trips, not to any agency: there is no <c>agency_id</c> to police by, and the only route
    ///     that reads or writes them demands <c>subscription.manage</c>, which
    ///     <c>PermissionCodes.PlatformOnly</c> forbids an agency role from holding. They are read
    ///     by the entitlement resolver on an agency's behalf, which is why the application role may
    ///     SELECT them; it may not write the two that describe what a tier costs and grants.
    ///   </item>
    ///   <item>
    ///     <b>Tenant-owned</b> — <c>subscriptions</c>, <c>subscription_invoices</c>,
    ///     <c>subscription_invoice_lines</c>, <c>subscription_charge_attempts</c>,
    ///     <c>subscription_migrations</c>, <c>payment_authorizations</c>. Each carries
    ///     <c>agency_id</c>, an EF query filter and the usual <c>tenant_isolation</c> policy.
    ///   </item>
    /// </list>
    /// <para>
    /// Three things the application cannot be trusted to hold on its own are enforced here:
    /// an invoice's total equals the sum of its lines, a charge attempt cannot be rewritten, and
    /// every money column is non-negative.
    /// </para>
    /// </remarks>
    public partial class AddSubscriptionsAndBilling : Migration
    {
        /// <summary>Tables owned by one agency, policed exactly as in <see cref="AddRowLevelSecurity"/>.</summary>
        internal static readonly string[] PolicedTables =
        [
            "billing.subscriptions",
            "billing.subscription_invoices",
            "billing.subscription_invoice_lines",
            "billing.subscription_charge_attempts",
            "billing.subscription_migrations",
            "billing.payment_authorizations",
        ];

        // The same InitPlan-wrapped calls AddRowLevelSecurity uses; see the note there.
        private const string PlatformScope = "(SELECT tenancy.platform_scope_active())";
        private const string CurrentAgency = "(SELECT tenancy.current_agency_id())";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.EnsureSchema(
                name: "billing");

            migrationBuilder.CreateTable(
                name: "entitlements",
                schema: "billing",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    value_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_entitlements", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "payment_authorizations",
                schema: "billing",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    agency_id = table.Column<Guid>(type: "uuid", nullable: false),
                    gateway = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    authorization_code = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    card_brand = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    last4 = table.Column<string>(type: "character varying(4)", maxLength: 4, nullable: true),
                    expiry_month = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: true),
                    expiry_year = table.Column<string>(type: "character varying(4)", maxLength: 4, nullable: true),
                    bank = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    is_default = table.Column<bool>(type: "boolean", nullable: false),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    captured_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_used_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_payment_authorizations", x => x.id);
                    table.ForeignKey(
                        name: "fk_payment_authorizations_agencies_agency_id",
                        column: x => x.agency_id,
                        principalSchema: "tenancy",
                        principalTable: "agencies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "subscription_tiers",
                schema: "billing",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    customer_description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    trial_days = table.Column<int>(type: "integer", nullable: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    is_fallback = table.Column<bool>(type: "boolean", nullable: false),
                    published_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    archived_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_subscription_tiers", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "tier_change_log",
                schema: "billing",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tier_id = table.Column<Guid>(type: "uuid", nullable: false),
                    actor_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    action = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    before = table.Column<string>(type: "jsonb", nullable: true),
                    after = table.Column<string>(type: "jsonb", nullable: true),
                    migration_policy = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    notice_sent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    subscribers_affected = table.Column<int>(type: "integer", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tier_change_log", x => x.id);
                    table.ForeignKey(
                        name: "fk_tier_change_log_subscription_tiers_tier_id",
                        column: x => x.tier_id,
                        principalSchema: "billing",
                        principalTable: "subscription_tiers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "tier_entitlements",
                schema: "billing",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tier_id = table.Column<Guid>(type: "uuid", nullable: false),
                    entitlement_id = table.Column<Guid>(type: "uuid", nullable: false),
                    value_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    value = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tier_entitlements", x => x.id);
                    table.ForeignKey(
                        name: "fk_tier_entitlements_entitlements_entitlement_id",
                        column: x => x.entitlement_id,
                        principalSchema: "billing",
                        principalTable: "entitlements",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_tier_entitlements_subscription_tiers_tier_id",
                        column: x => x.tier_id,
                        principalSchema: "billing",
                        principalTable: "subscription_tiers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "tier_prices",
                schema: "billing",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tier_id = table.Column<Guid>(type: "uuid", nullable: false),
                    currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false),
                    interval = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    amount_minor = table.Column<long>(type: "bigint", nullable: false),
                    is_promotional = table.Column<bool>(type: "boolean", nullable: false),
                    effective_from = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    effective_to = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tier_prices", x => x.id);
                    table.ForeignKey(
                        name: "fk_tier_prices_subscription_tiers_tier_id",
                        column: x => x.tier_id,
                        principalSchema: "billing",
                        principalTable: "subscription_tiers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "subscriptions",
                schema: "billing",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    agency_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tier_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tier_price_id = table.Column<Guid>(type: "uuid", nullable: true),
                    currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    current_period_start = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    current_period_end = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    trial_ends_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    external_ref = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    cancelled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    status_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    dunning_started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    dunning_retries = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_subscriptions", x => x.id);
                    table.ForeignKey(
                        name: "fk_subscriptions_agencies_agency_id",
                        column: x => x.agency_id,
                        principalSchema: "tenancy",
                        principalTable: "agencies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_subscriptions_subscription_tiers_tier_id",
                        column: x => x.tier_id,
                        principalSchema: "billing",
                        principalTable: "subscription_tiers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_subscriptions_tier_prices_tier_price_id",
                        column: x => x.tier_price_id,
                        principalSchema: "billing",
                        principalTable: "tier_prices",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "subscription_invoices",
                schema: "billing",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    agency_id = table.Column<Guid>(type: "uuid", nullable: false),
                    subscription_id = table.Column<Guid>(type: "uuid", nullable: false),
                    invoice_number = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    receipt_number = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    total_minor = table.Column<long>(type: "bigint", nullable: false),
                    period_start = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    period_end = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    issued_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    due_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    paid_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    payment_transaction_id = table.Column<Guid>(type: "uuid", nullable: true),
                    status_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_subscription_invoices", x => x.id);
                    table.ForeignKey(
                        name: "fk_subscription_invoices_agencies_agency_id",
                        column: x => x.agency_id,
                        principalSchema: "tenancy",
                        principalTable: "agencies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_subscription_invoices_subscriptions_subscription_id",
                        column: x => x.subscription_id,
                        principalSchema: "billing",
                        principalTable: "subscriptions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "subscription_migrations",
                schema: "billing",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    agency_id = table.Column<Guid>(type: "uuid", nullable: false),
                    subscription_id = table.Column<Guid>(type: "uuid", nullable: false),
                    from_tier_id = table.Column<Guid>(type: "uuid", nullable: false),
                    to_tier_id = table.Column<Guid>(type: "uuid", nullable: false),
                    change_reason = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    scheduled_for = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    notified_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    applied_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    cancelled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_subscription_migrations", x => x.id);
                    table.ForeignKey(
                        name: "fk_subscription_migrations_subscriptions_subscription_id",
                        column: x => x.subscription_id,
                        principalSchema: "billing",
                        principalTable: "subscriptions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "subscription_charge_attempts",
                schema: "billing",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    agency_id = table.Column<Guid>(type: "uuid", nullable: false),
                    invoice_id = table.Column<Guid>(type: "uuid", nullable: false),
                    attempt_number = table.Column<int>(type: "integer", nullable: false),
                    amount_minor = table.Column<long>(type: "bigint", nullable: false),
                    reference = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    outcome = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    attempted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    failure_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    next_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_subscription_charge_attempts", x => x.id);
                    table.ForeignKey(
                        name: "fk_subscription_charge_attempts_subscription_invoices_invoice_",
                        column: x => x.invoice_id,
                        principalSchema: "billing",
                        principalTable: "subscription_invoices",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "subscription_invoice_lines",
                schema: "billing",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    agency_id = table.Column<Guid>(type: "uuid", nullable: false),
                    invoice_id = table.Column<Guid>(type: "uuid", nullable: false),
                    description = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    quantity = table.Column<int>(type: "integer", nullable: false),
                    unit_amount_minor = table.Column<long>(type: "bigint", nullable: false),
                    amount_minor = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_subscription_invoice_lines", x => x.id);
                    table.ForeignKey(
                        name: "fk_subscription_invoice_lines_subscription_invoices_invoice_id",
                        column: x => x.invoice_id,
                        principalSchema: "billing",
                        principalTable: "subscription_invoices",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_entitlements_code",
                schema: "billing",
                table: "entitlements",
                column: "code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_payment_authorizations_agency_id_authorization_code",
                schema: "billing",
                table: "payment_authorizations",
                columns: new[] { "agency_id", "authorization_code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_payment_authorizations_agency_id_default",
                schema: "billing",
                table: "payment_authorizations",
                column: "agency_id",
                unique: true,
                filter: "is_default AND revoked_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_subscription_charge_attempts_agency_id_attempted_at",
                schema: "billing",
                table: "subscription_charge_attempts",
                columns: new[] { "agency_id", "attempted_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_subscription_charge_attempts_invoice_id_attempt_number",
                schema: "billing",
                table: "subscription_charge_attempts",
                columns: new[] { "invoice_id", "attempt_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_subscription_charge_attempts_reference",
                schema: "billing",
                table: "subscription_charge_attempts",
                column: "reference",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_subscription_invoice_lines_agency_id_invoice_id",
                schema: "billing",
                table: "subscription_invoice_lines",
                columns: new[] { "agency_id", "invoice_id" });

            migrationBuilder.CreateIndex(
                name: "ix_subscription_invoice_lines_invoice_id",
                schema: "billing",
                table: "subscription_invoice_lines",
                column: "invoice_id");

            migrationBuilder.CreateIndex(
                name: "ix_subscription_invoices_agency_id_issued_at",
                schema: "billing",
                table: "subscription_invoices",
                columns: new[] { "agency_id", "issued_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_subscription_invoices_invoice_number",
                schema: "billing",
                table: "subscription_invoices",
                column: "invoice_number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_subscription_invoices_receipt_number",
                schema: "billing",
                table: "subscription_invoices",
                column: "receipt_number",
                unique: true,
                filter: "receipt_number IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_subscription_invoices_status_due_at",
                schema: "billing",
                table: "subscription_invoices",
                columns: new[] { "status", "due_at" });

            migrationBuilder.CreateIndex(
                name: "ix_subscription_invoices_subscription_id",
                schema: "billing",
                table: "subscription_invoices",
                column: "subscription_id");

            migrationBuilder.CreateIndex(
                name: "ix_subscription_migrations_agency_id",
                schema: "billing",
                table: "subscription_migrations",
                column: "agency_id");

            migrationBuilder.CreateIndex(
                name: "ix_subscription_migrations_scheduled_for_applied_at",
                schema: "billing",
                table: "subscription_migrations",
                columns: new[] { "scheduled_for", "applied_at" });

            migrationBuilder.CreateIndex(
                name: "ix_subscription_migrations_subscription_id",
                schema: "billing",
                table: "subscription_migrations",
                column: "subscription_id");

            migrationBuilder.CreateIndex(
                name: "ix_subscription_tiers_code",
                schema: "billing",
                table: "subscription_tiers",
                column: "code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_subscription_tiers_fallback",
                schema: "billing",
                table: "subscription_tiers",
                column: "is_fallback",
                unique: true,
                filter: "is_fallback");

            migrationBuilder.CreateIndex(
                name: "ix_subscription_tiers_status_sort_order",
                schema: "billing",
                table: "subscription_tiers",
                columns: new[] { "status", "sort_order" });

            migrationBuilder.CreateIndex(
                name: "ix_subscriptions_agency_id_live",
                schema: "billing",
                table: "subscriptions",
                column: "agency_id",
                unique: true,
                filter: "status IN ('Trialing', 'Active', 'PastDue')");

            migrationBuilder.CreateIndex(
                name: "ix_subscriptions_status_current_period_end",
                schema: "billing",
                table: "subscriptions",
                columns: new[] { "status", "current_period_end" });

            migrationBuilder.CreateIndex(
                name: "ix_subscriptions_tier_id",
                schema: "billing",
                table: "subscriptions",
                column: "tier_id");

            migrationBuilder.CreateIndex(
                name: "ix_subscriptions_tier_price_id",
                schema: "billing",
                table: "subscriptions",
                column: "tier_price_id");

            migrationBuilder.CreateIndex(
                name: "ix_tier_change_log_tier_id_occurred_at",
                schema: "billing",
                table: "tier_change_log",
                columns: new[] { "tier_id", "occurred_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_tier_entitlements_entitlement_id",
                schema: "billing",
                table: "tier_entitlements",
                column: "entitlement_id");

            migrationBuilder.CreateIndex(
                name: "ix_tier_entitlements_tier_id_entitlement_id",
                schema: "billing",
                table: "tier_entitlements",
                columns: new[] { "tier_id", "entitlement_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_tier_prices_tier_id_currency_interval_effective_to",
                schema: "billing",
                table: "tier_prices",
                columns: new[] { "tier_id", "currency", "interval", "effective_to" });

            // ------------------------------------------------------------------ the application role
            //
            // `billing` is a new schema, so AddRowLevelSecurity's ALTER DEFAULT PRIVILEGES never
            // covered it. Every grant below is deliberate; the shape of them is the access-control
            // rule for this module, so read them as one list rather than as boilerplate.
            migrationBuilder.Sql($"""
                GRANT USAGE ON SCHEMA billing TO {AddRowLevelSecurity.ApplicationRole};

                -- Platform-owned, read-only to the application. The entitlement resolver reads all
                -- three on an agency's behalf on a hot path, so SELECT is needed; nothing an agency
                -- can reach may change what a plan costs or what it grants, so INSERT, UPDATE and
                -- DELETE are not. The back office writes them through the same role, which is why
                -- subscription_tiers is the one exception — see below.
                GRANT SELECT ON billing.entitlements TO {AddRowLevelSecurity.ApplicationRole};
                GRANT SELECT, INSERT, UPDATE ON billing.subscription_tiers TO {AddRowLevelSecurity.ApplicationRole};
                GRANT SELECT, INSERT, UPDATE ON billing.tier_prices TO {AddRowLevelSecurity.ApplicationRole};
                GRANT SELECT, INSERT, UPDATE, DELETE ON billing.tier_entitlements TO {AddRowLevelSecurity.ApplicationRole};

                -- No DELETE on a tier anywhere, at any level. A tier is what an invoice says the
                -- agency was charged for; archiving is the operation, and the absence of the grant
                -- is what makes that true rather than merely intended.
                REVOKE DELETE ON billing.subscription_tiers FROM {AddRowLevelSecurity.ApplicationRole};

                -- Append-only: the change log records what an admin did.
                GRANT SELECT, INSERT ON billing.tier_change_log TO {AddRowLevelSecurity.ApplicationRole};

                -- Tenant-owned.
                GRANT SELECT, INSERT, UPDATE ON billing.subscriptions TO {AddRowLevelSecurity.ApplicationRole};
                GRANT SELECT, INSERT, UPDATE ON billing.subscription_invoices TO {AddRowLevelSecurity.ApplicationRole};
                GRANT SELECT, INSERT ON billing.subscription_invoice_lines TO {AddRowLevelSecurity.ApplicationRole};
                GRANT SELECT, INSERT, UPDATE ON billing.subscription_migrations TO {AddRowLevelSecurity.ApplicationRole};
                GRANT SELECT, INSERT, UPDATE ON billing.payment_authorizations TO {AddRowLevelSecurity.ApplicationRole};

                -- Append-only: an attempt to take money is a record of something that happened.
                GRANT SELECT, INSERT ON billing.subscription_charge_attempts TO {AddRowLevelSecurity.ApplicationRole};

                GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA billing TO {AddRowLevelSecurity.ApplicationRole};
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

            // ------------------------------------------------------------------ shape constraints
            //
            // The same rules the domain enforces, said again where nothing can get round them. Every
            // one of these is about money, and money that is wrong in the database is wrong for good.
            migrationBuilder.Sql("""
                ALTER TABLE billing.tier_prices
                    ADD CONSTRAINT ck_tier_prices_amount_not_negative
                        CHECK (amount_minor >= 0),
                    ADD CONSTRAINT ck_tier_prices_currency
                        CHECK (currency ~ '^[A-Z]{3}$'),
                    -- "interval" is quoted: unquoted, PostgreSQL reads it as the type name.
                    ADD CONSTRAINT ck_tier_prices_interval
                        CHECK ("interval" IN ('Monthly', 'Annual')),
                    ADD CONSTRAINT ck_tier_prices_period_ordered
                        CHECK (effective_to IS NULL OR effective_to > effective_from);

                ALTER TABLE billing.subscription_tiers
                    ADD CONSTRAINT ck_subscription_tiers_status
                        CHECK (status IN ('Draft', 'Published', 'Archived')),
                    ADD CONSTRAINT ck_subscription_tiers_trial_days
                        CHECK (trial_days BETWEEN 0 AND 90),
                    ADD CONSTRAINT ck_subscription_tiers_code
                        CHECK (code ~ '^[a-z0-9_-]+$');

                ALTER TABLE billing.entitlements
                    ADD CONSTRAINT ck_entitlements_value_type
                        CHECK (value_type IN ('Flag', 'Limit', 'Rate'));

                ALTER TABLE billing.tier_entitlements
                    ADD CONSTRAINT ck_tier_entitlements_value_type
                        CHECK (value_type IN ('Flag', 'Limit', 'Rate')),

                    -- A flag is a JSON boolean; a limit is -1 or a whole number; a rate is 0 to
                    -- 10,000 basis points. Without this a typo in a seed leaves a tier granting
                    -- "true" where a ceiling belongs, and the resolver throws on every request the
                    -- agency makes.
                    ADD CONSTRAINT ck_tier_entitlements_value_shape
                        CHECK (
                            (value_type = 'Flag'  AND jsonb_typeof(value) = 'boolean')
                         OR (value_type = 'Limit' AND jsonb_typeof(value) = 'number'
                                                  AND (value)::numeric >= -1
                                                  AND (value)::numeric = trunc((value)::numeric))
                         OR (value_type = 'Rate'  AND jsonb_typeof(value) = 'number'
                                                  AND (value)::numeric BETWEEN 0 AND 10000
                                                  AND (value)::numeric = trunc((value)::numeric))
                        );

                ALTER TABLE billing.subscriptions
                    ADD CONSTRAINT ck_subscriptions_status
                        CHECK (status IN ('Trialing', 'Active', 'PastDue', 'Cancelled', 'Expired')),
                    ADD CONSTRAINT ck_subscriptions_currency
                        CHECK (currency ~ '^[A-Z]{3}$'),
                    ADD CONSTRAINT ck_subscriptions_period_ordered
                        CHECK (current_period_end >= current_period_start),
                    ADD CONSTRAINT ck_subscriptions_dunning_counted
                        CHECK (dunning_retries >= 0 AND (dunning_started_at IS NOT NULL OR dunning_retries = 0));

                ALTER TABLE billing.subscription_invoices
                    ADD CONSTRAINT ck_subscription_invoices_status
                        CHECK (status IN ('Open', 'Paid', 'PastDue', 'Uncollectible', 'Void')),
                    ADD CONSTRAINT ck_subscription_invoices_total_not_negative
                        CHECK (total_minor >= 0),
                    ADD CONSTRAINT ck_subscription_invoices_currency
                        CHECK (currency ~ '^[A-Z]{3}$'),
                    ADD CONSTRAINT ck_subscription_invoices_period_ordered
                        CHECK (period_end >= period_start),

                    -- Paid means all three: the timestamp, the receipt number and the status. A
                    -- receipt with no payment behind it is the thing an auditor asks about.
                    ADD CONSTRAINT ck_subscription_invoices_paid_is_receipted
                        CHECK ((status = 'Paid') = (paid_at IS NOT NULL AND receipt_number IS NOT NULL));

                ALTER TABLE billing.subscription_invoice_lines
                    ADD CONSTRAINT ck_subscription_invoice_lines_quantity
                        CHECK (quantity >= 1),
                    ADD CONSTRAINT ck_subscription_invoice_lines_amounts_not_negative
                        CHECK (unit_amount_minor >= 0 AND amount_minor >= 0),

                    -- The line's own arithmetic. Two places computing quantity times unit price is
                    -- how they end up disagreeing by a kobo.
                    ADD CONSTRAINT ck_subscription_invoice_lines_amount_is_product
                        CHECK (amount_minor = unit_amount_minor * quantity);

                ALTER TABLE billing.subscription_charge_attempts
                    ADD CONSTRAINT ck_subscription_charge_attempts_outcome
                        CHECK (outcome IN ('Succeeded', 'Failed', 'Unknown', 'NoAuthorization')),
                    ADD CONSTRAINT ck_subscription_charge_attempts_amount_not_negative
                        CHECK (amount_minor >= 0),
                    ADD CONSTRAINT ck_subscription_charge_attempts_number
                        CHECK (attempt_number BETWEEN 0 AND 4);

                ALTER TABLE billing.subscription_migrations
                    ADD CONSTRAINT ck_subscription_migrations_change_reason
                        CHECK (change_reason IN ('Upgrade', 'Downgrade', 'AdminMigration', 'DunningFallback')),
                    ADD CONSTRAINT ck_subscription_migrations_not_both
                        CHECK (applied_at IS NULL OR cancelled_at IS NULL);

                ALTER TABLE billing.payment_authorizations
                    ADD CONSTRAINT ck_payment_authorizations_last4
                        CHECK (last4 IS NULL OR last4 ~ '^[0-9]{4}$'),
                    ADD CONSTRAINT ck_payment_authorizations_expiry
                        CHECK ((expiry_month IS NULL OR expiry_month ~ '^[0-9]{2}$')
                           AND (expiry_year IS NULL OR expiry_year ~ '^[0-9]{4}$')),

                    -- A revoked authorisation is never the default one. The application says the
                    -- same thing; this is what stops a half-applied update leaving a revoked card as
                    -- the one every renewal charges.
                    ADD CONSTRAINT ck_payment_authorizations_revoked_is_not_default
                        CHECK (revoked_at IS NULL OR NOT is_default);

                ALTER TABLE billing.tier_change_log
                    ADD CONSTRAINT ck_tier_change_log_migration_policy
                        CHECK (migration_policy IN ('NewOnly', 'MigrateExisting')),
                    ADD CONSTRAINT ck_tier_change_log_subscribers_affected
                        CHECK (subscribers_affected >= 0);
                """);

            // ------------------------------------------------- an invoice's lines equal its total
            //
            // The single most important rule in this migration, and the one an application can most
            // easily get wrong: add a line, forget to move the total, and the agency is billed an
            // amount its own invoice does not explain.
            //
            // A constraint trigger deferred to the end of the transaction, not a CHECK: the total
            // and the lines are different tables, and the invoice row is necessarily written before
            // its lines. Checking immediately would reject every correct insert.
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION billing.assert_invoice_adds_up()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $$
                DECLARE
                    target      uuid;
                    line_total  bigint;
                    stated      bigint;
                BEGIN
                    -- One function, two tables, so the invoice's id is read from whichever row
                    -- fired it. The surviving row on a DELETE is OLD and on an INSERT is NEW;
                    -- reading the wrong one is a null reference, not a failed check.
                    IF TG_TABLE_NAME = 'subscription_invoices' THEN
                        target := CASE WHEN TG_OP = 'DELETE' THEN OLD.id ELSE NEW.id END;
                    ELSE
                        target := CASE WHEN TG_OP = 'DELETE' THEN OLD.invoice_id ELSE NEW.invoice_id END;
                    END IF;

                    SELECT i.total_minor INTO stated
                      FROM billing.subscription_invoices AS i
                     WHERE i.id = target;

                    -- The invoice went away in this transaction; nothing left to check.
                    IF NOT FOUND THEN
                        RETURN NULL;
                    END IF;

                    SELECT coalesce(sum(l.amount_minor), 0) INTO line_total
                      FROM billing.subscription_invoice_lines AS l
                     WHERE l.invoice_id = target;

                    IF stated <> line_total THEN
                        RAISE EXCEPTION
                            'Subscription invoice % states %, but its lines come to %',
                            target, stated, line_total
                            USING ERRCODE = 'check_violation',
                                  HINT = 'An invoice total is the sum of its lines. Add lines through SubscriptionInvoice.AddLine, which recomputes the total.';
                    END IF;

                    RETURN NULL;
                END;
                $$;

                CREATE CONSTRAINT TRIGGER subscription_invoices_add_up_trg
                    AFTER INSERT OR UPDATE ON billing.subscription_invoices
                    DEFERRABLE INITIALLY DEFERRED
                    FOR EACH ROW
                    EXECUTE FUNCTION billing.assert_invoice_adds_up();

                CREATE CONSTRAINT TRIGGER subscription_invoice_lines_add_up_trg
                    AFTER INSERT OR UPDATE OR DELETE ON billing.subscription_invoice_lines
                    DEFERRABLE INITIALLY DEFERRED
                    FOR EACH ROW
                    EXECUTE FUNCTION billing.assert_invoice_adds_up();
                """);

            // ------------------------------------------------------------------ append-only tables
            //
            // A trigger as well as the missing grant, because a REVOKE does not bind the table's
            // owner and a deployment that connects as the owner would otherwise be unpoliced.
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION billing.reject_history_rewrite()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $$
                BEGIN
                    RAISE EXCEPTION '%.% is append-only (attempted %)', TG_TABLE_SCHEMA, TG_TABLE_NAME, TG_OP
                        USING ERRCODE = 'restrict_violation',
                              HINT = 'Record what happened next; never rewrite what happened before.';
                END;
                $$;

                CREATE TRIGGER subscription_charge_attempts_append_only_trg
                    BEFORE UPDATE OR DELETE ON billing.subscription_charge_attempts
                    FOR EACH ROW
                    EXECUTE FUNCTION billing.reject_history_rewrite();

                CREATE TRIGGER tier_change_log_append_only_trg
                    BEFORE DELETE ON billing.tier_change_log
                    FOR EACH ROW
                    EXECUTE FUNCTION billing.reject_history_rewrite();
                """);

            // ------------------------------------------------------------------ a tier is never deleted
            //
            // The grant above already withholds DELETE from the application role, and this says the
            // same thing to the owner. FRD RS-6: a tier with subscribers may not be deleted, and the
            // safe reading of that is that no tier is ever deleted at all — an invoice from six
            // months ago names the plan it billed for, and a dangling name is not an answer anyone
            // can give a customer.
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION billing.reject_tier_delete()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $$
                BEGIN
                    RAISE EXCEPTION 'Subscription tier % (%) cannot be deleted', OLD.code, OLD.id
                        USING ERRCODE = 'restrict_violation',
                              HINT = 'Archive it instead. Invoices name the tier they billed for, and archiving takes it out of the picker while leaving them readable.';
                END;
                $$;

                CREATE TRIGGER subscription_tiers_no_delete_trg
                    BEFORE DELETE ON billing.subscription_tiers
                    FOR EACH ROW
                    EXECUTE FUNCTION billing.reject_tier_delete();
                """);

            // ------------------------------------------------------- the subscription invoice number
            //
            // Ours, not an agency's, so it does not go through DocumentIssuer — that allocator is
            // gapless per agency, and these are gapless per platform. A sequence rather than a
            // counter row: allocation must not block one agency's renewal behind another's.
            migrationBuilder.Sql("""
                CREATE SEQUENCE billing.subscription_invoice_number_seq AS bigint START WITH 1 INCREMENT BY 1;
                CREATE SEQUENCE billing.subscription_receipt_number_seq AS bigint START WITH 1 INCREMENT BY 1;
                """);

            migrationBuilder.Sql($"""
                GRANT USAGE, SELECT ON SEQUENCE billing.subscription_invoice_number_seq
                    TO {AddRowLevelSecurity.ApplicationRole};
                GRANT USAGE, SELECT ON SEQUENCE billing.subscription_receipt_number_seq
                    TO {AddRowLevelSecurity.ApplicationRole};
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            foreach (var table in PolicedTables)
            {
                migrationBuilder.Sql($"DROP POLICY IF EXISTS tenant_isolation ON {table};");
            }

            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS subscription_tiers_no_delete_trg ON billing.subscription_tiers;
                DROP TRIGGER IF EXISTS tier_change_log_append_only_trg ON billing.tier_change_log;
                DROP TRIGGER IF EXISTS subscription_charge_attempts_append_only_trg
                    ON billing.subscription_charge_attempts;
                DROP TRIGGER IF EXISTS subscription_invoice_lines_add_up_trg ON billing.subscription_invoice_lines;
                DROP TRIGGER IF EXISTS subscription_invoices_add_up_trg ON billing.subscription_invoices;

                DROP FUNCTION IF EXISTS billing.reject_tier_delete();
                DROP FUNCTION IF EXISTS billing.reject_history_rewrite();
                DROP FUNCTION IF EXISTS billing.assert_invoice_adds_up();

                DROP SEQUENCE IF EXISTS billing.subscription_receipt_number_seq;
                DROP SEQUENCE IF EXISTS billing.subscription_invoice_number_seq;
                """);

            migrationBuilder.DropTable(
                name: "payment_authorizations",
                schema: "billing");

            migrationBuilder.DropTable(
                name: "subscription_charge_attempts",
                schema: "billing");

            migrationBuilder.DropTable(
                name: "subscription_invoice_lines",
                schema: "billing");

            migrationBuilder.DropTable(
                name: "subscription_migrations",
                schema: "billing");

            migrationBuilder.DropTable(
                name: "tier_change_log",
                schema: "billing");

            migrationBuilder.DropTable(
                name: "tier_entitlements",
                schema: "billing");

            migrationBuilder.DropTable(
                name: "subscription_invoices",
                schema: "billing");

            migrationBuilder.DropTable(
                name: "entitlements",
                schema: "billing");

            migrationBuilder.DropTable(
                name: "subscriptions",
                schema: "billing");

            migrationBuilder.DropTable(
                name: "tier_prices",
                schema: "billing");

            migrationBuilder.DropTable(
                name: "subscription_tiers",
                schema: "billing");
        }
    }
}
