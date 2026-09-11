using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TripsAgent.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// The <c>supplier</c> schema — the Trips Africa abstraction (plan §2.7, issue #32).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Mostly scaffolded. Written by hand: <c>supplier_api_calls</c> as a monthly range-partitioned
    /// table and the functions that maintain it, the grants the application role needs on a schema
    /// that did not exist when AddRowLevelSecurity ran, and a row-level security policy on every
    /// table an agency owns, written exactly as AddRowLevelSecurity writes them.
    /// </para>
    /// <para>
    /// <c>suppliers</c> has no agency and no policy: it is platform reference data every agency reads.
    /// </para>
    /// </remarks>
    public partial class AddSupplierSchema : Migration
    {
        /// <summary>
        /// Tables owned by one agency through a non-null <c>agency_id</c> — plus the call log, whose
        /// agency is null for platform work: a null-agency row is then visible only inside a platform
        /// scope, as with the ledger's platform accounts.
        /// </summary>
        internal static readonly string[] AgencyOwnedTables =
        [
            "supplier.search_requests",
            "supplier.search_sessions",
            "supplier.supplier_offers",
            "supplier.flight_segments",
            "supplier.bus_segments",
            "supplier.supplier_fare_rules",
            "supplier.supplier_bookings",
            "supplier.supplier_booking_confirmations",
            "supplier.supplier_booking_passengers",
            "supplier.passenger_documents",
            "supplier.supplier_status_polls",
            "supplier.supplier_api_calls",
        ];

        // Wrapped in SELECT so PostgreSQL evaluates each once per query, as AddRowLevelSecurity does.
        private const string PlatformScope = "(SELECT tenancy.platform_scope_active())";
        private const string CurrentAgency = "(SELECT tenancy.current_agency_id())";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "supplier");

            migrationBuilder.CreateTable(
                name: "search_requests",
                schema: "supplier",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    agency_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    product_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    criteria_hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    criteria = table.Column<string>(type: "jsonb", nullable: false),
                    trip_type = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    result_count = table.Column<int>(type: "integer", nullable: true),
                    latency_ms = table.Column<int>(type: "integer", nullable: true),
                    error_code = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    requested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_search_requests", x => x.id);
                    table.ForeignKey(
                        name: "fk_search_requests_agencies_agency_id",
                        column: x => x.agency_id,
                        principalSchema: "tenancy",
                        principalTable: "agencies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "suppliers",
                schema: "supplier",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    base_url = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    config = table.Column<string>(type: "jsonb", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_suppliers", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "search_sessions",
                schema: "supplier",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    agency_id = table.Column<Guid>(type: "uuid", nullable: false),
                    search_request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    supplier_id = table.Column<Guid>(type: "uuid", nullable: false),
                    supplier_session_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    gds_session_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_search_sessions", x => x.id);
                    table.ForeignKey(
                        name: "fk_search_sessions_search_requests_search_request_id",
                        column: x => x.search_request_id,
                        principalSchema: "supplier",
                        principalTable: "search_requests",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_search_sessions_suppliers_supplier_id",
                        column: x => x.supplier_id,
                        principalSchema: "supplier",
                        principalTable: "suppliers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            // supplier_api_calls: PARTITION BY RANGE on occurred_at, which EF cannot express. The
            // columns, key and foreign key are exactly what the model snapshot describes, so
            // has-pending-model-changes stays honest; the indexes below are created on the parent and
            // PostgreSQL cascades them to every partition.
            migrationBuilder.Sql(
                """
                CREATE TABLE supplier.supplier_api_calls (
                    id                   uuid                     NOT NULL,
                    occurred_at          timestamp with time zone NOT NULL,
                    agency_id            uuid                     NULL,
                    supplier_id          uuid                     NOT NULL,
                    supplier_booking_id  uuid                     NULL,
                    operation            character varying(20)    NOT NULL,
                    http_method          character varying(10)    NOT NULL,
                    endpoint             character varying(500)   NOT NULL,
                    request_headers      jsonb                    NOT NULL,
                    request_body         text                     NULL,
                    response_status_code integer                  NULL,
                    response_body        text                     NULL,
                    latency_ms           integer                  NOT NULL,
                    outcome              character varying(20)    NOT NULL,
                    error_message        character varying(2000)  NULL,
                    correlation_id       character varying(100)   NULL,
                    CONSTRAINT pk_supplier_api_calls PRIMARY KEY (id, occurred_at),
                    CONSTRAINT fk_supplier_api_calls_suppliers_supplier_id FOREIGN KEY (supplier_id)
                        REFERENCES supplier.suppliers (id) ON DELETE RESTRICT
                ) PARTITION BY RANGE (occurred_at);
                """);

            migrationBuilder.CreateTable(
                name: "supplier_credentials",
                schema: "supplier",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    supplier_id = table.Column<Guid>(type: "uuid", nullable: false),
                    agency_id = table.Column<Guid>(type: "uuid", nullable: true),
                    environment = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    merchant_code = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    merchant_key_encrypted = table.Column<byte[]>(type: "bytea", nullable: false),
                    bearer_token_encrypted = table.Column<byte[]>(type: "bytea", nullable: true),
                    rotated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_supplier_credentials", x => x.id);
                    table.ForeignKey(
                        name: "fk_supplier_credentials_agencies_agency_id",
                        column: x => x.agency_id,
                        principalSchema: "tenancy",
                        principalTable: "agencies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_supplier_credentials_suppliers_supplier_id",
                        column: x => x.supplier_id,
                        principalSchema: "supplier",
                        principalTable: "suppliers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "supplier_offers",
                schema: "supplier",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    agency_id = table.Column<Guid>(type: "uuid", nullable: false),
                    search_session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    supplier_id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    offer_ref = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    agent_id_ext = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    gds_id_ext = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    combination_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    recommendation_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    flight_route_index = table.Column<int>(type: "integer", nullable: true),
                    currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false),
                    base_fare_minor = table.Column<long>(type: "bigint", nullable: false),
                    total_fare_minor = table.Column<long>(type: "bigint", nullable: false),
                    raw_payload = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_supplier_offers", x => x.id);
                    table.ForeignKey(
                        name: "fk_supplier_offers_search_sessions_search_session_id",
                        column: x => x.search_session_id,
                        principalSchema: "supplier",
                        principalTable: "search_sessions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_supplier_offers_suppliers_supplier_id",
                        column: x => x.supplier_id,
                        principalSchema: "supplier",
                        principalTable: "suppliers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "bus_segments",
                schema: "supplier",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    agency_id = table.Column<Guid>(type: "uuid", nullable: false),
                    supplier_offer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    operator_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    departure_terminal_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    arrival_terminal_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    departure_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    arrival_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    available_seats = table.Column<int>(type: "integer", nullable: true),
                    seat_numbers = table.Column<string>(type: "jsonb", nullable: true),
                    reservation_id_ext = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_bus_segments", x => x.id);
                    table.ForeignKey(
                        name: "fk_bus_segments_supplier_offers_supplier_offer_id",
                        column: x => x.supplier_offer_id,
                        principalSchema: "supplier",
                        principalTable: "supplier_offers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "flight_segments",
                schema: "supplier",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    agency_id = table.Column<Guid>(type: "uuid", nullable: false),
                    supplier_offer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    leg_index = table.Column<int>(type: "integer", nullable: false),
                    segment_index = table.Column<int>(type: "integer", nullable: false),
                    marketing_carrier = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    operating_carrier = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: true),
                    flight_number = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    origin_iata = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false),
                    destination_iata = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false),
                    departure_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    arrival_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    cabin = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    baggage_allowance = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    fare_basis = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_flight_segments", x => x.id);
                    table.ForeignKey(
                        name: "fk_flight_segments_supplier_offers_supplier_offer_id",
                        column: x => x.supplier_offer_id,
                        principalSchema: "supplier",
                        principalTable: "supplier_offers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "supplier_bookings",
                schema: "supplier",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    agency_id = table.Column<Guid>(type: "uuid", nullable: false),
                    supplier_id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_line_id = table.Column<Guid>(type: "uuid", nullable: false),
                    supplier_offer_id = table.Column<Guid>(type: "uuid", nullable: true),
                    product_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    trip_type = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    trip_mode = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    supplier_session_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    confirmation_code = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    pnr = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    ticket_time_limit = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    supplier_status_code = table.Column<int>(type: "integer", nullable: true),
                    currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false),
                    old_price_minor = table.Column<long>(type: "bigint", nullable: true),
                    new_price_minor = table.Column<long>(type: "bigint", nullable: true),
                    price_changed = table.Column<bool>(type: "boolean", nullable: false),
                    hash_verified = table.Column<bool>(type: "boolean", nullable: false),
                    idempotency_key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    issue_started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_polled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    poll_attempts = table.Column<int>(type: "integer", nullable: false),
                    next_poll_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    failure_reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_supplier_bookings", x => x.id);
                    table.ForeignKey(
                        name: "fk_supplier_bookings_agencies_agency_id",
                        column: x => x.agency_id,
                        principalSchema: "tenancy",
                        principalTable: "agencies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_supplier_bookings_supplier_offers_supplier_offer_id",
                        column: x => x.supplier_offer_id,
                        principalSchema: "supplier",
                        principalTable: "supplier_offers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_supplier_bookings_suppliers_supplier_id",
                        column: x => x.supplier_id,
                        principalSchema: "supplier",
                        principalTable: "suppliers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "supplier_booking_confirmations",
                schema: "supplier",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    agency_id = table.Column<Guid>(type: "uuid", nullable: false),
                    supplier_booking_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sequence = table.Column<int>(type: "integer", nullable: false),
                    confirmation_code = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    old_price_minor = table.Column<long>(type: "bigint", nullable: false),
                    new_price_minor = table.Column<long>(type: "bigint", nullable: false),
                    ticket_time_limit = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    hash_expected = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    hash_received = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    hash_verified = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_supplier_booking_confirmations", x => x.id);
                    table.ForeignKey(
                        name: "fk_supplier_booking_confirmations_supplier_bookings_supplier_b",
                        column: x => x.supplier_booking_id,
                        principalSchema: "supplier",
                        principalTable: "supplier_bookings",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "supplier_booking_passengers",
                schema: "supplier",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    agency_id = table.Column<Guid>(type: "uuid", nullable: false),
                    supplier_booking_id = table.Column<Guid>(type: "uuid", nullable: false),
                    passenger_type = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    title = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    first_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    middle_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    last_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    birth_date = table.Column<DateOnly>(type: "date", nullable: true),
                    gender = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    phone_number = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    seat_numbers = table.Column<string>(type: "jsonb", nullable: true),
                    ticket_number = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_supplier_booking_passengers", x => x.id);
                    table.ForeignKey(
                        name: "fk_supplier_booking_passengers_supplier_bookings_supplier_book",
                        column: x => x.supplier_booking_id,
                        principalSchema: "supplier",
                        principalTable: "supplier_bookings",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "supplier_fare_rules",
                schema: "supplier",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    agency_id = table.Column<Guid>(type: "uuid", nullable: false),
                    supplier_offer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    supplier_booking_id = table.Column<Guid>(type: "uuid", nullable: true),
                    rules_html = table.Column<string>(type: "text", nullable: true),
                    penalties = table.Column<string>(type: "jsonb", nullable: true),
                    fetched_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_supplier_fare_rules", x => x.id);
                    table.ForeignKey(
                        name: "fk_supplier_fare_rules_supplier_bookings_supplier_booking_id",
                        column: x => x.supplier_booking_id,
                        principalSchema: "supplier",
                        principalTable: "supplier_bookings",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_supplier_fare_rules_supplier_offers_supplier_offer_id",
                        column: x => x.supplier_offer_id,
                        principalSchema: "supplier",
                        principalTable: "supplier_offers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "supplier_status_polls",
                schema: "supplier",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    agency_id = table.Column<Guid>(type: "uuid", nullable: false),
                    supplier_booking_id = table.Column<Guid>(type: "uuid", nullable: false),
                    polled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    http_status_code = table.Column<int>(type: "integer", nullable: true),
                    supplier_status_code = table.Column<int>(type: "integer", nullable: true),
                    outcome = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    action_taken = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    supplier_api_call_id = table.Column<Guid>(type: "uuid", nullable: true),
                    note = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_supplier_status_polls", x => x.id);
                    table.ForeignKey(
                        name: "fk_supplier_status_polls_supplier_bookings_supplier_booking_id",
                        column: x => x.supplier_booking_id,
                        principalSchema: "supplier",
                        principalTable: "supplier_bookings",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "passenger_documents",
                schema: "supplier",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    agency_id = table.Column<Guid>(type: "uuid", nullable: false),
                    passenger_id = table.Column<Guid>(type: "uuid", nullable: false),
                    doc_type = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    inner_doc_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    doc_number_encrypted = table.Column<byte[]>(type: "bytea", nullable: false),
                    issuing_country = table.Column<string>(type: "character(2)", fixedLength: true, maxLength: 2, nullable: false),
                    nationality_country = table.Column<string>(type: "character(2)", fixedLength: true, maxLength: 2, nullable: true),
                    issued_on = table.Column<DateOnly>(type: "date", nullable: true),
                    expires_on = table.Column<DateOnly>(type: "date", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_passenger_documents", x => x.id);
                    table.ForeignKey(
                        name: "fk_passenger_documents_supplier_booking_passengers_passenger_id",
                        column: x => x.passenger_id,
                        principalSchema: "supplier",
                        principalTable: "supplier_booking_passengers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_bus_segments_agency_id_supplier_offer_id",
                schema: "supplier",
                table: "bus_segments",
                columns: new[] { "agency_id", "supplier_offer_id" });

            migrationBuilder.CreateIndex(
                name: "ix_bus_segments_supplier_offer_id",
                schema: "supplier",
                table: "bus_segments",
                column: "supplier_offer_id");

            migrationBuilder.CreateIndex(
                name: "ix_flight_segments_agency_id_supplier_offer_id",
                schema: "supplier",
                table: "flight_segments",
                columns: new[] { "agency_id", "supplier_offer_id" });

            migrationBuilder.CreateIndex(
                name: "ix_flight_segments_offer_leg_segment",
                schema: "supplier",
                table: "flight_segments",
                columns: new[] { "supplier_offer_id", "leg_index", "segment_index" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_passenger_documents_agency_id_passenger_id",
                schema: "supplier",
                table: "passenger_documents",
                columns: new[] { "agency_id", "passenger_id" });

            migrationBuilder.CreateIndex(
                name: "ix_passenger_documents_passenger_id",
                schema: "supplier",
                table: "passenger_documents",
                column: "passenger_id");

            migrationBuilder.CreateIndex(
                name: "ix_search_requests_agency_id_criteria_hash",
                schema: "supplier",
                table: "search_requests",
                columns: new[] { "agency_id", "criteria_hash" });

            migrationBuilder.CreateIndex(
                name: "ix_search_requests_agency_id_requested_at",
                schema: "supplier",
                table: "search_requests",
                columns: new[] { "agency_id", "requested_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_search_sessions_agency_id_search_request_id",
                schema: "supplier",
                table: "search_sessions",
                columns: new[] { "agency_id", "search_request_id" });

            migrationBuilder.CreateIndex(
                name: "ix_search_sessions_search_request_id",
                schema: "supplier",
                table: "search_sessions",
                column: "search_request_id");

            migrationBuilder.CreateIndex(
                name: "ix_search_sessions_supplier_id_supplier_session_id",
                schema: "supplier",
                table: "search_sessions",
                columns: new[] { "supplier_id", "supplier_session_id" });

            migrationBuilder.CreateIndex(
                name: "ix_supplier_api_calls_agency_id_occurred_at",
                schema: "supplier",
                table: "supplier_api_calls",
                columns: new[] { "agency_id", "occurred_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_supplier_api_calls_supplier_booking_id_occurred_at",
                schema: "supplier",
                table: "supplier_api_calls",
                columns: new[] { "supplier_booking_id", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "ix_supplier_api_calls_supplier_operation_occurred_at",
                schema: "supplier",
                table: "supplier_api_calls",
                columns: new[] { "supplier_id", "operation", "occurred_at" },
                descending: new[] { false, false, true });

            migrationBuilder.CreateIndex(
                name: "ix_supplier_booking_confirmations_agency_id_booking_id",
                schema: "supplier",
                table: "supplier_booking_confirmations",
                columns: new[] { "agency_id", "supplier_booking_id" });

            migrationBuilder.CreateIndex(
                name: "ix_supplier_booking_confirmations_booking_id_sequence",
                schema: "supplier",
                table: "supplier_booking_confirmations",
                columns: new[] { "supplier_booking_id", "sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_supplier_booking_passengers_agency_id_booking_id",
                schema: "supplier",
                table: "supplier_booking_passengers",
                columns: new[] { "agency_id", "supplier_booking_id" });

            migrationBuilder.CreateIndex(
                name: "ix_supplier_booking_passengers_supplier_booking_id",
                schema: "supplier",
                table: "supplier_booking_passengers",
                column: "supplier_booking_id");

            migrationBuilder.CreateIndex(
                name: "ix_supplier_bookings_agency_id_created_at",
                schema: "supplier",
                table: "supplier_bookings",
                columns: new[] { "agency_id", "created_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_supplier_bookings_idempotency_key",
                schema: "supplier",
                table: "supplier_bookings",
                column: "idempotency_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_supplier_bookings_order_line_id",
                schema: "supplier",
                table: "supplier_bookings",
                column: "order_line_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_supplier_bookings_status_next_poll_at",
                schema: "supplier",
                table: "supplier_bookings",
                columns: new[] { "status", "next_poll_at" });

            migrationBuilder.CreateIndex(
                name: "ix_supplier_bookings_supplier_id",
                schema: "supplier",
                table: "supplier_bookings",
                column: "supplier_id");

            migrationBuilder.CreateIndex(
                name: "ix_supplier_bookings_supplier_offer_id",
                schema: "supplier",
                table: "supplier_bookings",
                column: "supplier_offer_id");

            migrationBuilder.CreateIndex(
                name: "ix_supplier_credentials_owner_supplier_environment",
                schema: "supplier",
                table: "supplier_credentials",
                columns: new[] { "agency_id", "supplier_id", "environment" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "ix_supplier_credentials_supplier_id",
                schema: "supplier",
                table: "supplier_credentials",
                column: "supplier_id");

            migrationBuilder.CreateIndex(
                name: "ix_supplier_fare_rules_agency_id_supplier_offer_id",
                schema: "supplier",
                table: "supplier_fare_rules",
                columns: new[] { "agency_id", "supplier_offer_id" });

            migrationBuilder.CreateIndex(
                name: "ix_supplier_fare_rules_supplier_booking_id",
                schema: "supplier",
                table: "supplier_fare_rules",
                column: "supplier_booking_id");

            migrationBuilder.CreateIndex(
                name: "ix_supplier_fare_rules_supplier_offer_id",
                schema: "supplier",
                table: "supplier_fare_rules",
                column: "supplier_offer_id");

            migrationBuilder.CreateIndex(
                name: "ix_supplier_offers_agency_id_search_session_id",
                schema: "supplier",
                table: "supplier_offers",
                columns: new[] { "agency_id", "search_session_id" });

            migrationBuilder.CreateIndex(
                name: "ix_supplier_offers_search_session_id",
                schema: "supplier",
                table: "supplier_offers",
                column: "search_session_id");

            migrationBuilder.CreateIndex(
                name: "ix_supplier_offers_supplier_id",
                schema: "supplier",
                table: "supplier_offers",
                column: "supplier_id");

            migrationBuilder.CreateIndex(
                name: "ix_supplier_status_polls_agency_id_booking_id_polled_at",
                schema: "supplier",
                table: "supplier_status_polls",
                columns: new[] { "agency_id", "supplier_booking_id", "polled_at" },
                descending: new[] { false, false, true });

            migrationBuilder.CreateIndex(
                name: "ix_supplier_status_polls_supplier_booking_id",
                schema: "supplier",
                table: "supplier_status_polls",
                column: "supplier_booking_id");

            migrationBuilder.CreateIndex(
                name: "ix_suppliers_code",
                schema: "supplier",
                table: "suppliers",
                column: "code",
                unique: true);

            CreatePartitionFunctions(migrationBuilder);
            GrantToApplicationRole(migrationBuilder);
            EnableRowLevelSecurity(migrationBuilder);

            // Last month through three months ahead, so a deploy on the 31st does not break at
            // midnight. The maintenance job keeps the runway from then on.
            migrationBuilder.Sql(
                """
                SELECT supplier.create_supplier_api_call_partition(
                    (date_trunc('month', now()) + make_interval(months => n))::date)
                FROM generate_series(-1, 3) AS n;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "bus_segments",
                schema: "supplier");

            migrationBuilder.DropTable(
                name: "flight_segments",
                schema: "supplier");

            migrationBuilder.DropTable(
                name: "passenger_documents",
                schema: "supplier");

            // Dropping the partitioned parent takes every partition with it.
            migrationBuilder.Sql("DROP TABLE IF EXISTS supplier.supplier_api_calls CASCADE;");

            migrationBuilder.DropTable(
                name: "supplier_booking_confirmations",
                schema: "supplier");

            migrationBuilder.DropTable(
                name: "supplier_credentials",
                schema: "supplier");

            migrationBuilder.DropTable(
                name: "supplier_fare_rules",
                schema: "supplier");

            migrationBuilder.DropTable(
                name: "supplier_status_polls",
                schema: "supplier");

            migrationBuilder.DropTable(
                name: "supplier_booking_passengers",
                schema: "supplier");

            migrationBuilder.DropTable(
                name: "supplier_bookings",
                schema: "supplier");

            migrationBuilder.DropTable(
                name: "supplier_offers",
                schema: "supplier");

            migrationBuilder.DropTable(
                name: "search_sessions",
                schema: "supplier");

            migrationBuilder.DropTable(
                name: "search_requests",
                schema: "supplier");

            migrationBuilder.DropTable(
                name: "suppliers",
                schema: "supplier");

            migrationBuilder.Sql("DROP FUNCTION IF EXISTS supplier.drop_expired_supplier_api_call_partitions(integer);");
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS supplier.create_supplier_api_call_partition(date);");
            migrationBuilder.Sql("ALTER DEFAULT PRIVILEGES IN SCHEMA supplier REVOKE SELECT, INSERT, UPDATE, DELETE ON TABLES FROM tripsagent_app;");
        }

        /// <summary>
        /// The two functions the maintenance job drives: create one month, drop expired months.
        /// </summary>
        /// <remarks>
        /// The same design as the audit log's (AddPlatformAuditLog): DDL that names a table computed
        /// from a date lives in a function taking a parameter, so no caller ever builds that name as a
        /// string. Both are idempotent, so the job is safe to run as often as anyone likes.
        /// </remarks>
        private static void CreatePartitionFunctions(MigrationBuilder migrationBuilder)
        {
            // A partition is a table in its own right, and the parent's row-level security does not
            // apply to it when it is queried directly. The application only ever goes through the
            // parent — where PostgreSQL checks privileges on the parent alone — so it is given none on
            // the partition, and cannot reach round the policy by naming one.
            migrationBuilder.Sql(
                """
                CREATE OR REPLACE FUNCTION supplier.create_supplier_api_call_partition(month date)
                RETURNS text
                LANGUAGE plpgsql
                AS $function$
                DECLARE
                    starts_on      date := date_trunc('month', month)::date;
                    ends_on        date := (date_trunc('month', month) + interval '1 month')::date;
                    partition_name text := 'supplier_api_calls_' || to_char(starts_on, 'YYYY_MM');
                BEGIN
                    IF EXISTS (
                        SELECT 1
                        FROM pg_class c
                        JOIN pg_namespace n ON n.oid = c.relnamespace
                        WHERE n.nspname = 'supplier' AND c.relname = partition_name
                    ) THEN
                        RETURN partition_name;
                    END IF;

                    EXECUTE format(
                        'CREATE TABLE supplier.%I PARTITION OF supplier.supplier_api_calls FOR VALUES FROM (%L) TO (%L)',
                        partition_name, starts_on, ends_on);

                    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'tripsagent_app') THEN
                        EXECUTE format('REVOKE ALL ON supplier.%I FROM tripsagent_app', partition_name);
                    END IF;

                    RETURN partition_name;
                END;
                $function$;
                """);

            // Retention takes the window as an argument, so changing it is configuration, not a
            // migration. Whole months only: dropping a partition is instant and leaves no bloat.
            migrationBuilder.Sql(
                """
                CREATE OR REPLACE FUNCTION supplier.drop_expired_supplier_api_call_partitions(
                    retain_months integer)
                RETURNS integer
                LANGUAGE plpgsql
                AS $function$
                DECLARE
                    cutoff    date := (date_trunc('month', now()) - make_interval(months => retain_months))::date;
                    expired   record;
                    dropped   integer := 0;
                BEGIN
                    IF retain_months IS NULL OR retain_months < 1 THEN
                        RAISE EXCEPTION
                            'retain_months must be at least 1, got %', retain_months
                            USING HINT = 'Zero would drop the month still being written to.';
                    END IF;

                    FOR expired IN
                        SELECT child.relname AS name
                        FROM pg_inherits i
                        JOIN pg_class  child  ON child.oid  = i.inhrelid
                        JOIN pg_class  parent ON parent.oid = i.inhparent
                        JOIN pg_namespace pn  ON pn.oid     = parent.relnamespace
                        WHERE pn.nspname = 'supplier'
                          AND parent.relname = 'supplier_api_calls'
                          AND child.relname ~ '^supplier_api_calls_[0-9]{4}_[0-9]{2}$'
                          AND to_date(right(child.relname, 7), 'YYYY_MM') < cutoff
                    LOOP
                        EXECUTE format('DROP TABLE supplier.%I', expired.name);
                        dropped := dropped + 1;
                    END LOOP;

                    RETURN dropped;
                END;
                $function$;
                """);
        }

        /// <summary>
        /// Gives the policed application role the same access to this schema that AddRowLevelSecurity
        /// gave it to the others. Its default privileges named only the schemas that existed then.
        /// </summary>
        private static void GrantToApplicationRole(MigrationBuilder migrationBuilder) =>
            migrationBuilder.Sql(
                """
                GRANT USAGE ON SCHEMA supplier TO tripsagent_app;

                GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA supplier TO tripsagent_app;

                ALTER DEFAULT PRIVILEGES IN SCHEMA supplier
                    GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO tripsagent_app;
                """);

        /// <summary>
        /// Row-level security for every table here that an agency owns, exactly as AddRowLevelSecurity
        /// polices the others (ADR-0006). The EF filters are the first line; this is the backstop.
        /// </summary>
        private static void EnableRowLevelSecurity(MigrationBuilder migrationBuilder)
        {
            foreach (var table in AgencyOwnedTables)
            {
                EnableFor(migrationBuilder, table,
                    @using: $"{PlatformScope} OR agency_id = {CurrentAgency}",
                    check: $"{PlatformScope} OR agency_id = {CurrentAgency}");
            }

            // The platform's credential (null agency) is readable by every agency — their calls are
            // made with it — exactly like the system roles in identity.roles. Only the platform may
            // write it; an agency may write only its own.
            EnableFor(migrationBuilder, "supplier.supplier_credentials",
                @using: $"{PlatformScope} OR agency_id = {CurrentAgency} OR agency_id IS NULL",
                check: $"{PlatformScope} OR agency_id = {CurrentAgency}");
        }

        private static void EnableFor(MigrationBuilder migrationBuilder, string table, string @using, string check) =>
            migrationBuilder.Sql(
                $"""
                 ALTER TABLE {table} ENABLE ROW LEVEL SECURITY;

                 -- FORCE: without it the table's owner is exempt, and a deployment that connects as
                 -- the owner would be silently unpoliced.
                 ALTER TABLE {table} FORCE ROW LEVEL SECURITY;

                 CREATE POLICY tenant_isolation ON {table}
                     USING ({@using})
                     WITH CHECK ({check});
                 """);
    }
}
