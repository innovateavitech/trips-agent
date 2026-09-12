using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TripsAgent.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// The sub-agent network: what a sub-agent may sell, what its principal has taken away from
    /// it, and the hard cap on what it may spend. Feature F10, issue 63.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every table here carries <b>two</b> agencies. <c>agency_id</c> is the principal, which owns
    /// and writes the row; <c>sub_agency_id</c> is the sub-agent the row is about, and reads it.
    /// So the write rule stays the plain <c>agency_id = current agency</c> every other table uses,
    /// and only the read is widened — see the policies below and the matching EF filters in
    /// <c>AppDbContext.ApplyTenantQueryFilters</c>. There is no cross-tenant write path.
    /// </para>
    /// <para>
    /// The one exception is a sub-agent reserving against its own allowance at checkout, which has
    /// to add to a row its principal owns. That goes through
    /// <c>payments.reserve_sub_agent_allowance</c> — one SECURITY DEFINER function, one statement,
    /// and it can only ever touch the calling agency's own allowance row.
    /// </para>
    /// </remarks>
    public partial class AddSubAgentNetwork : Migration
    {
        /// <summary>
        /// The tables whose SELECT policy is widened so the sub-agent can read what applies to it.
        /// </summary>
        /// <remarks>
        /// Listed in one place on purpose: a widened read is the one thing in this feature that
        /// could leak across agencies, so the set of tables it applies to should be countable at a
        /// glance and reviewable in a diff. <c>SubAgentNetworkRlsTests</c> checks this list against
        /// the policies actually installed, so a table cannot join it unnoticed.
        /// </remarks>
        internal static readonly string[] PrincipalOwnedSubAgentTables =
        [
            "tenancy.sub_agent_scopes",
            "tenancy.permission_overrides",
            "payments.wallet_allowances",
        ];

        // Wrapped in SELECT so PostgreSQL evaluates each once per query as an InitPlan rather than
        // once per row — the same reason as in AddRowLevelSecurity.
        private const string PlatformScope = "(SELECT tenancy.platform_scope_active())";
        private const string CurrentAgency = "(SELECT tenancy.current_agency_id())";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.CreateTable(
                name: "permission_overrides",
                schema: "tenancy",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    agency_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sub_agency_id = table.Column<Guid>(type: "uuid", nullable: false),
                    permission_code = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_permission_overrides", x => x.id);
                    table.ForeignKey(
                        name: "fk_permission_overrides_agencies_agency_id",
                        column: x => x.agency_id,
                        principalSchema: "tenancy",
                        principalTable: "agencies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_permission_overrides_agencies_sub_agency_id",
                        column: x => x.sub_agency_id,
                        principalSchema: "tenancy",
                        principalTable: "agencies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "sub_agent_scopes",
                schema: "tenancy",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    agency_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sub_agency_id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    supplier_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sub_agent_scopes", x => x.id);
                    table.ForeignKey(
                        name: "fk_sub_agent_scopes_agencies_agency_id",
                        column: x => x.agency_id,
                        principalSchema: "tenancy",
                        principalTable: "agencies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_sub_agent_scopes_agencies_sub_agency_id",
                        column: x => x.sub_agency_id,
                        principalSchema: "tenancy",
                        principalTable: "agencies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_sub_agent_scopes_suppliers_supplier_id",
                        column: x => x.supplier_id,
                        principalSchema: "supplier",
                        principalTable: "suppliers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "wallet_allowances",
                schema: "payments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    agency_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sub_agency_id = table.Column<Guid>(type: "uuid", nullable: false),
                    currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false),
                    limit_minor = table.Column<long>(type: "bigint", nullable: false),
                    spent_minor = table.Column<long>(type: "bigint", nullable: false),
                    period = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    resets_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    version = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_wallet_allowances", x => x.id);
                    table.ForeignKey(
                        name: "fk_wallet_allowances_agencies_agency_id",
                        column: x => x.agency_id,
                        principalSchema: "tenancy",
                        principalTable: "agencies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_wallet_allowances_agencies_sub_agency_id",
                        column: x => x.sub_agency_id,
                        principalSchema: "tenancy",
                        principalTable: "agencies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_permission_overrides_agency_id",
                schema: "tenancy",
                table: "permission_overrides",
                column: "agency_id");

            migrationBuilder.CreateIndex(
                name: "ix_permission_overrides_sub_agency_id_permission_code",
                schema: "tenancy",
                table: "permission_overrides",
                columns: new[] { "sub_agency_id", "permission_code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_sub_agent_scopes_agency_id",
                schema: "tenancy",
                table: "sub_agent_scopes",
                column: "agency_id");

            migrationBuilder.CreateIndex(
                name: "ix_sub_agent_scopes_sub_agency_id_product_type_supplier_id",
                schema: "tenancy",
                table: "sub_agent_scopes",
                columns: new[] { "sub_agency_id", "product_type", "supplier_id" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "ix_sub_agent_scopes_supplier_id",
                schema: "tenancy",
                table: "sub_agent_scopes",
                column: "supplier_id");

            migrationBuilder.CreateIndex(
                name: "ix_wallet_allowances_agency_id",
                schema: "payments",
                table: "wallet_allowances",
                column: "agency_id");

            migrationBuilder.CreateIndex(
                name: "ix_wallet_allowances_resets_at",
                schema: "payments",
                table: "wallet_allowances",
                column: "resets_at");

            migrationBuilder.CreateIndex(
                name: "ix_wallet_allowances_sub_agency_id_currency",
                schema: "payments",
                table: "wallet_allowances",
                columns: new[] { "sub_agency_id", "currency" },
                unique: true);

            // ------------------------------------------------------------------ constraints
            //
            // An agency never scopes, overrides or funds itself: that row would be meaningless and
            // would make every "is this about me or by me?" query ambiguous.
            migrationBuilder.Sql("""
                ALTER TABLE tenancy.sub_agent_scopes
                    ADD CONSTRAINT ck_sub_agent_scopes_two_agencies CHECK (agency_id <> sub_agency_id);

                ALTER TABLE tenancy.permission_overrides
                    ADD CONSTRAINT ck_permission_overrides_two_agencies CHECK (agency_id <> sub_agency_id);

                ALTER TABLE payments.wallet_allowances
                    ADD CONSTRAINT ck_wallet_allowances_two_agencies CHECK (agency_id <> sub_agency_id);
                """);

            // Money, in kobo, and never negative on either column.
            //
            // Deliberately NOT `spent_minor <= limit_minor`. Build-plan decision 8 and the epic
            // both say a principal may lower a limit below what has already been spent — the money
            // is gone, and lowering the cap only stops the next booking. A constraint saying
            // otherwise would refuse that edit. What actually enforces the cap is the conditional
            // UPDATE in reserve_sub_agent_allowance below, which is also the only thing that could
            // enforce it correctly under concurrency.
            migrationBuilder.Sql("""
                ALTER TABLE payments.wallet_allowances
                    ADD CONSTRAINT ck_wallet_allowances_amounts_are_positive
                        CHECK (limit_minor >= 0 AND spent_minor >= 0);
                """);

            // ------------------------------------------------------------------ privileges
            // ALTER DEFAULT PRIVILEGES in the row-level security migration already covers tables
            // created later in these schemas. Said again here so this migration is readable on its
            // own, and so a database whose defaults were changed by hand still works.
            migrationBuilder.Sql($"""
                GRANT SELECT, INSERT, UPDATE, DELETE
                   ON tenancy.sub_agent_scopes,
                      tenancy.permission_overrides,
                      payments.wallet_allowances
                   TO {AddRowLevelSecurity.ApplicationRole};
                """);

            // ------------------------------------------------------------------ row-level security
            //
            // USING (reads) is widened by one clause: the sub-agent the row is about may read it.
            // It has to — the console shows a sub-agent what it may sell and what it may spend.
            //
            // WITH CHECK (writes) is NOT widened. Only the owning principal writes these rows, and
            // PostgreSQL refuses anything else even if an EF filter were removed.
            foreach (var table in PrincipalOwnedSubAgentTables)
            {
                migrationBuilder.Sql($"""
                    ALTER TABLE {table} ENABLE ROW LEVEL SECURITY;

                    -- FORCE: without it the table's owner is exempt, and a deployment connecting as
                    -- the owner would be silently unpoliced.
                    ALTER TABLE {table} FORCE ROW LEVEL SECURITY;

                    CREATE POLICY tenant_isolation ON {table}
                        USING ({PlatformScope}
                               OR agency_id = {CurrentAgency}
                               OR sub_agency_id = {CurrentAgency})
                        WITH CHECK ({PlatformScope} OR agency_id = {CurrentAgency});
                    """);
            }

            // ------------------------------------------------------------------ allowance reservation
            //
            // The one write a sub-agent's own request makes to a row its principal owns.
            //
            // Why a function rather than a policy: widening the UPDATE policy would let a sub-agent
            // write any column of its allowance, including the limit. This can do exactly one
            // thing — add to spent_minor, and only within the limit, and only on its own row.
            //
            // Why it is race-free: a single UPDATE. Under READ COMMITTED, a second transaction
            // updating the same row waits for the first to commit and then re-evaluates its own
            // WHERE against the committed version. Two bookings for 60% of the cap therefore
            // cannot both pass; the loser updates no rows and is told the cap is reached. Reading
            // spent_minor and writing it back — the obvious implementation — loses that.
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION payments.reserve_sub_agent_allowance(
                    p_currency text,
                    p_amount   bigint)
                RETURNS text
                LANGUAGE plpgsql
                SECURITY DEFINER
                SET search_path = payments, tenancy, public
                AS $fn$
                DECLARE
                    v_agency  uuid := tenancy.current_agency_id();
                    v_updated integer;
                    v_status  text;
                BEGIN
                    IF v_agency IS NULL THEN
                        RETURN 'no_tenant';
                    END IF;

                    IF p_amount IS NULL OR p_amount <= 0 THEN
                        RAISE EXCEPTION 'An allowance is reserved in a positive amount of kobo (got %).', p_amount
                            USING ERRCODE = 'invalid_parameter_value';
                    END IF;

                    UPDATE payments.wallet_allowances
                       SET spent_minor = spent_minor + p_amount,
                           version     = version + 1,
                           updated_at  = now()
                     WHERE sub_agency_id = v_agency
                       AND currency      = p_currency
                       AND status        = 'Active'
                       AND spent_minor + p_amount <= limit_minor;

                    GET DIAGNOSTICS v_updated = ROW_COUNT;

                    IF v_updated = 1 THEN
                        RETURN 'ok';
                    END IF;

                    -- Nothing moved. Say which of the three reasons it was, so the console can
                    -- tell an agent whether to ask for more room or to wait for the period to turn.
                    SELECT status INTO v_status
                      FROM payments.wallet_allowances
                     WHERE sub_agency_id = v_agency AND currency = p_currency;

                    IF v_status IS NULL THEN
                        RETURN 'no_allowance';
                    ELSIF v_status <> 'Active' THEN
                        RETURN 'frozen';
                    ELSE
                        RETURN 'exceeded';
                    END IF;
                END;
                $fn$;

                -- Gives a reservation back after a booking that failed, was reversed or lapsed.
                --
                -- Takes the sub-agency explicitly because the caller is often not the sub-agent:
                -- the checkout sweeper and the ticket time limit monitor run as background jobs.
                -- The guard below is what keeps that from being a cross-tenant write: only the
                -- sub-agent itself, its own principal, or an audited platform scope may call it.
                CREATE OR REPLACE FUNCTION payments.release_sub_agent_allowance(
                    p_sub_agency uuid,
                    p_currency   text,
                    p_amount     bigint)
                RETURNS boolean
                LANGUAGE plpgsql
                SECURITY DEFINER
                SET search_path = payments, tenancy, public
                AS $fn$
                DECLARE
                    v_agency  uuid := tenancy.current_agency_id();
                    v_updated integer;
                BEGIN
                    IF p_amount IS NULL OR p_amount <= 0 THEN
                        RAISE EXCEPTION 'An allowance is released in a positive amount of kobo (got %).', p_amount
                            USING ERRCODE = 'invalid_parameter_value';
                    END IF;

                    IF NOT tenancy.platform_scope_active() THEN
                        IF v_agency IS NULL THEN
                            RETURN false;
                        END IF;

                        IF v_agency <> p_sub_agency
                           AND NOT EXISTS (SELECT 1 FROM tenancy.agencies
                                            WHERE id = p_sub_agency AND parent_agency_id = v_agency) THEN
                            RETURN false;
                        END IF;
                    END IF;

                    -- GREATEST, not a bare subtraction: a double release — a reversal racing a
                    -- lapse — must leave the allowance at zero spent, not below it.
                    UPDATE payments.wallet_allowances
                       SET spent_minor = GREATEST(spent_minor - p_amount, 0),
                           version     = version + 1,
                           updated_at  = now()
                     WHERE sub_agency_id = p_sub_agency
                       AND currency      = p_currency;

                    GET DIAGNOSTICS v_updated = ROW_COUNT;
                    RETURN v_updated = 1;
                END;
                $fn$;
                """);

            migrationBuilder.Sql($"""
                GRANT EXECUTE ON FUNCTION payments.reserve_sub_agent_allowance(text, bigint)
                   TO {AddRowLevelSecurity.ApplicationRole};
                GRANT EXECUTE ON FUNCTION payments.release_sub_agent_allowance(uuid, text, bigint)
                   TO {AddRowLevelSecurity.ApplicationRole};
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.Sql("""
                DROP FUNCTION IF EXISTS payments.reserve_sub_agent_allowance(text, bigint);
                DROP FUNCTION IF EXISTS payments.release_sub_agent_allowance(uuid, text, bigint);
                """);

            // The policies go with the tables, but dropping them first keeps the Down readable
            // and works whether or not the table drop below is reached.
            foreach (var table in PrincipalOwnedSubAgentTables)
            {
                migrationBuilder.Sql($"""
                    DROP POLICY IF EXISTS tenant_isolation ON {table};
                    ALTER TABLE {table} NO FORCE ROW LEVEL SECURITY;
                    ALTER TABLE {table} DISABLE ROW LEVEL SECURITY;
                    """);
            }

            migrationBuilder.DropTable(
                name: "permission_overrides",
                schema: "tenancy");

            migrationBuilder.DropTable(
                name: "sub_agent_scopes",
                schema: "tenancy");

            migrationBuilder.DropTable(
                name: "wallet_allowances",
                schema: "payments");
        }
    }
}
