using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TripsAgent.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCrm : Migration
    {
        /// <summary>The tables this migration creates, each owned by one agency through agency_id.</summary>
        internal static readonly string[] PolicedTables =
        [
            "crm.customers",
            "crm.leads",
            "crm.lead_stage_history",
            "crm.quotes",
            "crm.quote_items",
            "crm.quote_itinerary_days",
            "crm.tasks",
            "crm.communications",
        ];

        // The same InitPlan-wrapped calls AddRowLevelSecurity uses; see the note there.
        private const string PlatformScope = "(SELECT tenancy.platform_scope_active())";
        private const string CurrentAgency = "(SELECT tenancy.current_agency_id())";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "crm");

            migrationBuilder.AddUniqueConstraint(
                name: "ak_products_agency_id_id",
                schema: "catalog",
                table: "products",
                columns: new[] { "agency_id", "id" });

            migrationBuilder.CreateTable(
                name: "customers",
                schema: "crm",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    agency_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    email = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: true),
                    phone = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    phone_key = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    last_activity_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_customers", x => x.id);
                    table.UniqueConstraint("ak_customers_agency_id_id", x => new { x.agency_id, x.id });
                    table.ForeignKey(
                        name: "fk_customers_agencies_agency_id",
                        column: x => x.agency_id,
                        principalSchema: "tenancy",
                        principalTable: "agencies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "leads",
                schema: "crm",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    agency_id = table.Column<Guid>(type: "uuid", nullable: false),
                    customer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    destination = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    travel_from = table.Column<DateOnly>(type: "date", nullable: true),
                    travel_to = table.Column<DateOnly>(type: "date", nullable: true),
                    adults = table.Column<int>(type: "integer", nullable: false),
                    children = table.Column<int>(type: "integer", nullable: false),
                    budget_min_minor = table.Column<long>(type: "bigint", nullable: true),
                    budget_max_minor = table.Column<long>(type: "bigint", nullable: true),
                    currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false),
                    message = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    stage = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    lost_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    owner_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_leads", x => x.id);
                    table.UniqueConstraint("ak_leads_agency_id_id", x => new { x.agency_id, x.id });
                    table.ForeignKey(
                        name: "fk_leads_agencies_agency_id",
                        column: x => x.agency_id,
                        principalSchema: "tenancy",
                        principalTable: "agencies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_leads_customers_agency_id_customer_id",
                        columns: x => new { x.agency_id, x.customer_id },
                        principalSchema: "crm",
                        principalTable: "customers",
                        principalColumns: new[] { "agency_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_leads_users_owner_user_id",
                        column: x => x.owner_user_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "communications",
                schema: "crm",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    agency_id = table.Column<Guid>(type: "uuid", nullable: false),
                    channel = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    direction = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    summary = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    related_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    related_id = table.Column<Guid>(type: "uuid", nullable: false),
                    customer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    lead_id = table.Column<Guid>(type: "uuid", nullable: true),
                    by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    by_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_communications", x => x.id);
                    table.ForeignKey(
                        name: "fk_communications_agencies_agency_id",
                        column: x => x.agency_id,
                        principalSchema: "tenancy",
                        principalTable: "agencies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_communications_customers_agency_id_customer_id",
                        columns: x => new { x.agency_id, x.customer_id },
                        principalSchema: "crm",
                        principalTable: "customers",
                        principalColumns: new[] { "agency_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_communications_leads_agency_id_lead_id",
                        columns: x => new { x.agency_id, x.lead_id },
                        principalSchema: "crm",
                        principalTable: "leads",
                        principalColumns: new[] { "agency_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "lead_stage_history",
                schema: "crm",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    agency_id = table.Column<Guid>(type: "uuid", nullable: false),
                    lead_id = table.Column<Guid>(type: "uuid", nullable: false),
                    stage = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    by_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_lead_stage_history", x => x.id);
                    table.ForeignKey(
                        name: "fk_lead_stage_history_agencies_agency_id",
                        column: x => x.agency_id,
                        principalSchema: "tenancy",
                        principalTable: "agencies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_lead_stage_history_leads_lead_id",
                        column: x => x.lead_id,
                        principalSchema: "crm",
                        principalTable: "leads",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "quotes",
                schema: "crm",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    agency_id = table.Column<Guid>(type: "uuid", nullable: false),
                    lead_id = table.Column<Guid>(type: "uuid", nullable: false),
                    number = table.Column<int>(type: "integer", nullable: false),
                    quote_number = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    valid_until = table.Column<DateOnly>(type: "date", nullable: false),
                    currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false),
                    notes = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    total_minor = table.Column<long>(type: "bigint", nullable: false),
                    public_token = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    sent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    viewed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    responded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    version = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_quotes", x => x.id);
                    table.ForeignKey(
                        name: "fk_quotes_agencies_agency_id",
                        column: x => x.agency_id,
                        principalSchema: "tenancy",
                        principalTable: "agencies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_quotes_leads_agency_id_lead_id",
                        columns: x => new { x.agency_id, x.lead_id },
                        principalSchema: "crm",
                        principalTable: "leads",
                        principalColumns: new[] { "agency_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "tasks",
                schema: "crm",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    agency_id = table.Column<Guid>(type: "uuid", nullable: false),
                    title = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    due_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    related_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    related_id = table.Column<Guid>(type: "uuid", nullable: false),
                    customer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    lead_id = table.Column<Guid>(type: "uuid", nullable: true),
                    owner_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    reminder_sent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tasks", x => x.id);
                    table.ForeignKey(
                        name: "fk_tasks_agencies_agency_id",
                        column: x => x.agency_id,
                        principalSchema: "tenancy",
                        principalTable: "agencies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_tasks_customers_agency_id_customer_id",
                        columns: x => new { x.agency_id, x.customer_id },
                        principalSchema: "crm",
                        principalTable: "customers",
                        principalColumns: new[] { "agency_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_tasks_leads_agency_id_lead_id",
                        columns: x => new { x.agency_id, x.lead_id },
                        principalSchema: "crm",
                        principalTable: "leads",
                        principalColumns: new[] { "agency_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_tasks_users_owner_user_id",
                        column: x => x.owner_user_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "quote_items",
                schema: "crm",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    agency_id = table.Column<Guid>(type: "uuid", nullable: false),
                    quote_id = table.Column<Guid>(type: "uuid", nullable: false),
                    position = table.Column<int>(type: "integer", nullable: false),
                    description = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    quantity = table.Column<int>(type: "integer", nullable: false),
                    unit_price_minor = table.Column<long>(type: "bigint", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_quote_items", x => x.id);
                    table.ForeignKey(
                        name: "fk_quote_items_agencies_agency_id",
                        column: x => x.agency_id,
                        principalSchema: "tenancy",
                        principalTable: "agencies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_quote_items_products_agency_id_product_id",
                        columns: x => new { x.agency_id, x.product_id },
                        principalSchema: "catalog",
                        principalTable: "products",
                        principalColumns: new[] { "agency_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_quote_items_quotes_quote_id",
                        column: x => x.quote_id,
                        principalSchema: "crm",
                        principalTable: "quotes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "quote_itinerary_days",
                schema: "crm",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    agency_id = table.Column<Guid>(type: "uuid", nullable: false),
                    quote_id = table.Column<Guid>(type: "uuid", nullable: false),
                    day_number = table.Column<int>(type: "integer", nullable: false),
                    title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_quote_itinerary_days", x => x.id);
                    table.ForeignKey(
                        name: "fk_quote_itinerary_days_agencies_agency_id",
                        column: x => x.agency_id,
                        principalSchema: "tenancy",
                        principalTable: "agencies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_quote_itinerary_days_quotes_quote_id",
                        column: x => x.quote_id,
                        principalSchema: "crm",
                        principalTable: "quotes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_orders_agency_id_customer_id",
                schema: "orders",
                table: "orders",
                columns: new[] { "agency_id", "customer_id" });

            migrationBuilder.CreateIndex(
                name: "ix_communications_agency_id_customer_id",
                schema: "crm",
                table: "communications",
                columns: new[] { "agency_id", "customer_id" });

            migrationBuilder.CreateIndex(
                name: "ix_communications_agency_id_lead_id",
                schema: "crm",
                table: "communications",
                columns: new[] { "agency_id", "lead_id" });

            migrationBuilder.CreateIndex(
                name: "ix_customers_agency_id_email",
                schema: "crm",
                table: "customers",
                columns: new[] { "agency_id", "email" },
                unique: true,
                filter: "email IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_customers_agency_id_last_activity_at",
                schema: "crm",
                table: "customers",
                columns: new[] { "agency_id", "last_activity_at" });

            migrationBuilder.CreateIndex(
                name: "ix_customers_agency_id_phone_key",
                schema: "crm",
                table: "customers",
                columns: new[] { "agency_id", "phone_key" },
                filter: "phone_key IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_lead_stage_history_agency_id_lead_id_at",
                schema: "crm",
                table: "lead_stage_history",
                columns: new[] { "agency_id", "lead_id", "at" });

            migrationBuilder.CreateIndex(
                name: "ix_lead_stage_history_lead_id",
                schema: "crm",
                table: "lead_stage_history",
                column: "lead_id");

            migrationBuilder.CreateIndex(
                name: "ix_leads_agency_id_created_at",
                schema: "crm",
                table: "leads",
                columns: new[] { "agency_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_leads_agency_id_customer_id",
                schema: "crm",
                table: "leads",
                columns: new[] { "agency_id", "customer_id" });

            migrationBuilder.CreateIndex(
                name: "ix_leads_owner_user_id",
                schema: "crm",
                table: "leads",
                column: "owner_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_quote_items_agency_id_product_id",
                schema: "crm",
                table: "quote_items",
                columns: new[] { "agency_id", "product_id" });

            migrationBuilder.CreateIndex(
                name: "ix_quote_items_quote_id_position",
                schema: "crm",
                table: "quote_items",
                columns: new[] { "quote_id", "position" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_quote_itinerary_days_agency_id",
                schema: "crm",
                table: "quote_itinerary_days",
                column: "agency_id");

            migrationBuilder.CreateIndex(
                name: "ix_quote_itinerary_days_quote_id_day_number",
                schema: "crm",
                table: "quote_itinerary_days",
                columns: new[] { "quote_id", "day_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_quotes_agency_id_lead_id",
                schema: "crm",
                table: "quotes",
                columns: new[] { "agency_id", "lead_id" });

            migrationBuilder.CreateIndex(
                name: "ix_quotes_agency_id_number",
                schema: "crm",
                table: "quotes",
                columns: new[] { "agency_id", "number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_quotes_public_token",
                schema: "crm",
                table: "quotes",
                column: "public_token",
                unique: true,
                filter: "public_token IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_tasks_agency_id_customer_id",
                schema: "crm",
                table: "tasks",
                columns: new[] { "agency_id", "customer_id" });

            migrationBuilder.CreateIndex(
                name: "ix_tasks_agency_id_due_at_open",
                schema: "crm",
                table: "tasks",
                columns: new[] { "agency_id", "due_at" },
                filter: "completed_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_tasks_agency_id_lead_id",
                schema: "crm",
                table: "tasks",
                columns: new[] { "agency_id", "lead_id" });

            migrationBuilder.CreateIndex(
                name: "ix_tasks_due_at_awaiting_reminder",
                schema: "crm",
                table: "tasks",
                column: "due_at",
                filter: "completed_at IS NULL AND reminder_sent_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_tasks_owner_user_id",
                schema: "crm",
                table: "tasks",
                column: "owner_user_id");

            migrationBuilder.AddForeignKey(
                name: "fk_orders_customers_customer",
                schema: "orders",
                table: "orders",
                columns: new[] { "agency_id", "customer_id" },
                principalSchema: "crm",
                principalTable: "customers",
                principalColumns: new[] { "agency_id", "id" },
                onDelete: ReferentialAction.Restrict);

            // ------------------------------------------------------------------ what a row may say
            //
            // Every enum is stored by name, so the CHECK is the list of names. A value the code can
            // never produce is a value the database refuses, whoever is holding the connection.
            migrationBuilder.Sql("""
                ALTER TABLE crm.customers
                    ADD CONSTRAINT ck_customers_name_not_blank
                        CHECK (btrim(name) <> ''),
                    -- Stored lower-cased, because that is how the unique index and every lookup
                    -- match it. A mixed-case address here would be a second record for one inbox.
                    ADD CONSTRAINT ck_customers_email_is_lower
                        CHECK (email IS NULL OR email = lower(email)),
                    ADD CONSTRAINT ck_customers_email_shape
                        CHECK (email IS NULL OR email ~ '^[^@[:space:]]+@[^@[:space:]]+\.[^@[:space:]]+$'),
                    -- A phone is kept with the digits that identify it, or not at all: one without
                    -- the other is a number nothing can match against.
                    ADD CONSTRAINT ck_customers_phone_has_key
                        CHECK ((phone IS NULL) = (phone_key IS NULL)),
                    ADD CONSTRAINT ck_customers_phone_key_is_digits
                        CHECK (phone_key IS NULL OR phone_key ~ '^[0-9]{7,15}$'),
                    -- Someone with neither cannot be reached and cannot be recognised again.
                    ADD CONSTRAINT ck_customers_is_contactable
                        CHECK (email IS NOT NULL OR phone IS NOT NULL);

                ALTER TABLE crm.leads
                    ADD CONSTRAINT ck_leads_source
                        CHECK (source IN ('TripRequestWidget', 'ContactForm', 'Manual')),
                    ADD CONSTRAINT ck_leads_stage
                        CHECK (stage IN ('New', 'Quoted', 'Negotiating', 'Won', 'Lost')),
                    -- A lost lead is what the agency learns from, so it always says why — and a lead
                    -- that moved on again no longer carries the reason it was lost.
                    ADD CONSTRAINT ck_leads_lost_says_why
                        CHECK ((stage = 'Lost') = (lost_reason IS NOT NULL)),
                    ADD CONSTRAINT ck_leads_destination_not_blank
                        CHECK (btrim(destination) <> ''),
                    ADD CONSTRAINT ck_leads_currency
                        CHECK (currency ~ '^[A-Z]{3}$'),
                    ADD CONSTRAINT ck_leads_party_size
                        CHECK (adults BETWEEN 1 AND 99 AND children BETWEEN 0 AND 99),
                    ADD CONSTRAINT ck_leads_dates_run_forwards
                        CHECK (travel_from IS NULL OR travel_to IS NULL OR travel_to >= travel_from),
                    ADD CONSTRAINT ck_leads_budget_not_negative
                        CHECK ((budget_min_minor IS NULL OR budget_min_minor >= 0)
                           AND (budget_max_minor IS NULL OR budget_max_minor >= 0)),
                    ADD CONSTRAINT ck_leads_budget_range
                        CHECK (budget_min_minor IS NULL OR budget_max_minor IS NULL
                            OR budget_max_minor >= budget_min_minor);

                ALTER TABLE crm.lead_stage_history
                    ADD CONSTRAINT ck_lead_stage_history_stage
                        CHECK (stage IN ('New', 'Quoted', 'Negotiating', 'Won', 'Lost')),
                    ADD CONSTRAINT ck_lead_stage_history_by_name_not_blank
                        CHECK (btrim(by_name) <> '');

                ALTER TABLE crm.quotes
                    ADD CONSTRAINT ck_quotes_status
                        CHECK (status IN ('Draft', 'Sent', 'Viewed', 'Accepted', 'Declined')),
                    ADD CONSTRAINT ck_quotes_number_positive
                        CHECK (number >= 1),
                    ADD CONSTRAINT ck_quotes_currency
                        CHECK (currency ~ '^[A-Z]{3}$'),
                    ADD CONSTRAINT ck_quotes_total_not_negative
                        CHECK (total_minor >= 0),
                    -- A draft has no link and has not been sent; anything further along has both.
                    ADD CONSTRAINT ck_quotes_sent_has_link
                        CHECK ((status = 'Draft') = (public_token IS NULL)
                           AND (status = 'Draft') = (sent_at IS NULL)),
                    ADD CONSTRAINT ck_quotes_answer_has_a_time
                        CHECK ((status IN ('Accepted', 'Declined')) = (responded_at IS NOT NULL)),
                    -- Answering is proof of having looked, so an answered quote has been viewed.
                    ADD CONSTRAINT ck_quotes_answered_was_viewed
                        CHECK (responded_at IS NULL OR viewed_at IS NOT NULL);

                ALTER TABLE crm.quote_items
                    ADD CONSTRAINT ck_quote_items_position_not_negative
                        CHECK (position >= 0),
                    ADD CONSTRAINT ck_quote_items_quantity
                        CHECK (quantity BETWEEN 1 AND 999),
                    ADD CONSTRAINT ck_quote_items_unit_price_not_negative
                        CHECK (unit_price_minor >= 0),
                    ADD CONSTRAINT ck_quote_items_description_not_blank
                        CHECK (btrim(description) <> '');

                ALTER TABLE crm.quote_itinerary_days
                    ADD CONSTRAINT ck_quote_itinerary_days_day_number
                        CHECK (day_number BETWEEN 1 AND 60),
                    ADD CONSTRAINT ck_quote_itinerary_days_title_not_blank
                        CHECK (btrim(title) <> '');

                ALTER TABLE crm.tasks
                    ADD CONSTRAINT ck_tasks_related_type
                        CHECK (related_type IN ('Lead', 'Customer', 'Quote')),
                    ADD CONSTRAINT ck_tasks_title_not_blank
                        CHECK (btrim(title) <> ''),
                    -- A task about a lead, or about a quote, names that lead. Only a customer's own
                    -- task has none, and that is what makes a customer's timeline readable.
                    ADD CONSTRAINT ck_tasks_lead_work_names_its_lead
                        CHECK (related_type = 'Customer' OR lead_id IS NOT NULL);

                ALTER TABLE crm.communications
                    ADD CONSTRAINT ck_communications_channel
                        CHECK (channel IN ('Email', 'Sms', 'Whatsapp', 'Call', 'Note')),
                    ADD CONSTRAINT ck_communications_direction
                        CHECK (direction IN ('Inbound', 'Outbound')),
                    ADD CONSTRAINT ck_communications_related_type
                        CHECK (related_type IN ('Lead', 'Customer', 'Quote')),
                    ADD CONSTRAINT ck_communications_summary_not_blank
                        CHECK (btrim(summary) <> ''),
                    ADD CONSTRAINT ck_communications_by_name_not_blank
                        CHECK (btrim(by_name) <> '');
                """);

            // ------------------------------------------------------- the record of what happened
            //
            // A lead's history and a customer's timeline are records, not working data. The grants
            // below withhold UPDATE and DELETE from the application role; these triggers say the
            // same thing to the schema owner, whom a grant does not bind — the same reasoning as
            // orders.order_status_history.
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION crm.reject_rewrite()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $$
                BEGIN
                    RAISE EXCEPTION '%.% is append-only (attempted %)', TG_TABLE_SCHEMA, TG_TABLE_NAME, TG_OP
                        USING ERRCODE = 'restrict_violation',
                              HINT = 'Record what happened next; never rewrite what happened before.';
                END;
                $$;

                CREATE TRIGGER lead_stage_history_append_only_trg
                    BEFORE UPDATE OR DELETE ON crm.lead_stage_history
                    FOR EACH ROW
                    EXECUTE FUNCTION crm.reject_rewrite();

                CREATE TRIGGER communications_append_only_trg
                    BEFORE UPDATE OR DELETE ON crm.communications
                    FOR EACH ROW
                    EXECUTE FUNCTION crm.reject_rewrite();
                """);

            // ------------------------------------------------------------- a sent quote is final
            //
            // CLAUDE.md rule 5's reasoning applied to a quote: the customer holds what was sent, so
            // once it is out its wording and its prices never move. The application refuses it with
            // a 409; this refuses it to anything else that reaches the table. The status, the view,
            // the answer and the version still change — that is the quote being answered, not rewritten.
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION crm.reject_sent_quote_change()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $$
                BEGIN
                    IF OLD.status = 'Draft' THEN
                        RETURN NEW;
                    END IF;

                    IF NEW.title        IS DISTINCT FROM OLD.title
                    OR NEW.valid_until  IS DISTINCT FROM OLD.valid_until
                    OR NEW.notes        IS DISTINCT FROM OLD.notes
                    OR NEW.total_minor  IS DISTINCT FROM OLD.total_minor
                    OR NEW.currency     IS DISTINCT FROM OLD.currency
                    OR NEW.lead_id      IS DISTINCT FROM OLD.lead_id
                    OR NEW.quote_number IS DISTINCT FROM OLD.quote_number
                    OR NEW.public_token IS DISTINCT FROM OLD.public_token
                    OR NEW.sent_at      IS DISTINCT FROM OLD.sent_at
                    THEN
                        RAISE EXCEPTION 'quote % was sent at %; what it says is final', OLD.quote_number, OLD.sent_at
                            USING ERRCODE = 'restrict_violation',
                                  HINT = 'New terms are a new quote. The customer holds this one as it was sent.';
                    END IF;

                    RETURN NEW;
                END;
                $$;

                CREATE TRIGGER quotes_sent_is_final_trg
                    BEFORE UPDATE ON crm.quotes
                    FOR EACH ROW
                    EXECUTE FUNCTION crm.reject_sent_quote_change();

                CREATE OR REPLACE FUNCTION crm.reject_sent_quote_content_change()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $$
                DECLARE
                    quote_status text;
                    quote_id uuid := COALESCE(NEW.quote_id, OLD.quote_id);
                BEGIN
                    SELECT status INTO quote_status FROM crm.quotes WHERE id = quote_id;

                    IF quote_status IS NOT NULL AND quote_status <> 'Draft' THEN
                        RAISE EXCEPTION 'quote % has been sent; its % are final', quote_id, TG_TABLE_NAME
                            USING ERRCODE = 'restrict_violation',
                                  HINT = 'New terms are a new quote.';
                    END IF;

                    RETURN COALESCE(NEW, OLD);
                END;
                $$;

                CREATE TRIGGER quote_items_sent_is_final_trg
                    BEFORE INSERT OR UPDATE OR DELETE ON crm.quote_items
                    FOR EACH ROW
                    EXECUTE FUNCTION crm.reject_sent_quote_content_change();

                CREATE TRIGGER quote_itinerary_days_sent_is_final_trg
                    BEFORE INSERT OR UPDATE OR DELETE ON crm.quote_itinerary_days
                    FOR EACH ROW
                    EXECUTE FUNCTION crm.reject_sent_quote_content_change();
                """);

            // ------------------------------------------------------------------ the application role
            //
            // crm is a new schema, so AddRowLevelSecurity's default privileges never covered it.
            // No DELETE on anything a person is in: erasing a customer (#106) anonymises the row.
            migrationBuilder.Sql($"""
                GRANT USAGE ON SCHEMA crm TO {AddRowLevelSecurity.ApplicationRole};
                GRANT SELECT, INSERT, UPDATE ON crm.customers TO {AddRowLevelSecurity.ApplicationRole};
                GRANT SELECT, INSERT, UPDATE ON crm.leads TO {AddRowLevelSecurity.ApplicationRole};
                GRANT SELECT, INSERT, UPDATE ON crm.quotes TO {AddRowLevelSecurity.ApplicationRole};
                GRANT SELECT, INSERT, UPDATE ON crm.tasks TO {AddRowLevelSecurity.ApplicationRole};

                -- Append-only: no UPDATE, no DELETE. The triggers above say the same thing to the
                -- owner, whom these grants do not bind.
                GRANT SELECT, INSERT ON crm.lead_stage_history TO {AddRowLevelSecurity.ApplicationRole};
                GRANT SELECT, INSERT ON crm.communications TO {AddRowLevelSecurity.ApplicationRole};

                -- A draft's items and days DO get DELETE: saving a draft replaces them both, and
                -- until it is sent they are working notes rather than a record of anything.
                GRANT SELECT, INSERT, UPDATE, DELETE ON crm.quote_items TO {AddRowLevelSecurity.ApplicationRole};
                GRANT SELECT, INSERT, UPDATE, DELETE ON crm.quote_itinerary_days TO {AddRowLevelSecurity.ApplicationRole};
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
                DROP TRIGGER IF EXISTS quote_itinerary_days_sent_is_final_trg ON crm.quote_itinerary_days;
                DROP TRIGGER IF EXISTS quote_items_sent_is_final_trg ON crm.quote_items;
                DROP FUNCTION IF EXISTS crm.reject_sent_quote_content_change();
                DROP TRIGGER IF EXISTS quotes_sent_is_final_trg ON crm.quotes;
                DROP FUNCTION IF EXISTS crm.reject_sent_quote_change();
                DROP TRIGGER IF EXISTS communications_append_only_trg ON crm.communications;
                DROP TRIGGER IF EXISTS lead_stage_history_append_only_trg ON crm.lead_stage_history;
                DROP FUNCTION IF EXISTS crm.reject_rewrite();
                """);

            migrationBuilder.DropForeignKey(
                name: "fk_orders_customers_customer",
                schema: "orders",
                table: "orders");

            migrationBuilder.DropTable(
                name: "communications",
                schema: "crm");

            migrationBuilder.DropTable(
                name: "lead_stage_history",
                schema: "crm");

            migrationBuilder.DropTable(
                name: "quote_items",
                schema: "crm");

            migrationBuilder.DropTable(
                name: "quote_itinerary_days",
                schema: "crm");

            migrationBuilder.DropTable(
                name: "tasks",
                schema: "crm");

            migrationBuilder.DropTable(
                name: "quotes",
                schema: "crm");

            migrationBuilder.DropTable(
                name: "leads",
                schema: "crm");

            migrationBuilder.DropTable(
                name: "customers",
                schema: "crm");

            migrationBuilder.DropUniqueConstraint(
                name: "ak_products_agency_id_id",
                schema: "catalog",
                table: "products");

            migrationBuilder.DropIndex(
                name: "ix_orders_agency_id_customer_id",
                schema: "orders",
                table: "orders");
        }
    }
}
