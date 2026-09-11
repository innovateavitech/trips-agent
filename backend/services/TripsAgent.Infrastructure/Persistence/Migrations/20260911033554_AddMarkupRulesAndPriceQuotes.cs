using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TripsAgent.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMarkupRulesAndPriceQuotes : Migration
    {
        /// <summary>Tables owned by one agency, policed exactly as in <see cref="AddRowLevelSecurity"/>.</summary>
        internal static readonly string[] PolicedTables =
        [
            "pricing.markup_rules",
            "pricing.price_quotes",
        ];

        // The same InitPlan-wrapped calls AddRowLevelSecurity uses; see the note there.
        private const string PlatformScope = "(SELECT tenancy.platform_scope_active())";
        private const string CurrentAgency = "(SELECT tenancy.current_agency_id())";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "pricing");

            migrationBuilder.CreateTable(
                name: "markup_rules",
                schema: "pricing",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    agency_id = table.Column<Guid>(type: "uuid", nullable: false),
                    scope = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    product_type = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    product_id = table.Column<Guid>(type: "uuid", nullable: true),
                    supplier_code = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false),
                    calculation_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    percent_basis_points = table.Column<int>(type: "integer", nullable: true),
                    value_minor = table.Column<long>(type: "bigint", nullable: true),
                    min_markup_minor = table.Column<long>(type: "bigint", nullable: true),
                    max_markup_minor = table.Column<long>(type: "bigint", nullable: true),
                    priority = table.Column<int>(type: "integer", nullable: false),
                    applies_to_sub_agents = table.Column<bool>(type: "boolean", nullable: false),
                    effective_from = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    effective_to = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    superseded_by_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_markup_rules", x => x.id);
                    table.ForeignKey(
                        name: "fk_markup_rules_agencies_agency_id",
                        column: x => x.agency_id,
                        principalSchema: "tenancy",
                        principalTable: "agencies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_markup_rules_markup_rules_superseded_by_id",
                        column: x => x.superseded_by_id,
                        principalSchema: "pricing",
                        principalTable: "markup_rules",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "price_quotes",
                schema: "pricing",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    agency_id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_type = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: true),
                    supplier_code = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false),
                    net_amount_minor = table.Column<long>(type: "bigint", nullable: false),
                    markup_amount_minor = table.Column<long>(type: "bigint", nullable: false),
                    gross_amount_minor = table.Column<long>(type: "bigint", nullable: false),
                    markup_rule_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_price_quotes", x => x.id);
                    table.ForeignKey(
                        name: "fk_price_quotes_agencies_agency_id",
                        column: x => x.agency_id,
                        principalSchema: "tenancy",
                        principalTable: "agencies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_price_quotes_markup_rules_markup_rule_id",
                        column: x => x.markup_rule_id,
                        principalSchema: "pricing",
                        principalTable: "markup_rules",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_markup_rules_agency_id_effective_to",
                schema: "pricing",
                table: "markup_rules",
                columns: new[] { "agency_id", "effective_to" });

            migrationBuilder.CreateIndex(
                name: "ix_markup_rules_superseded_by_id",
                schema: "pricing",
                table: "markup_rules",
                column: "superseded_by_id");

            migrationBuilder.CreateIndex(
                name: "ix_price_quotes_agency_id_created_at",
                schema: "pricing",
                table: "price_quotes",
                columns: new[] { "agency_id", "created_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_price_quotes_markup_rule_id",
                schema: "pricing",
                table: "price_quotes",
                column: "markup_rule_id");

            // ------------------------------------------------------------------- rule shape
            //
            // The same rules MarkupRuleTerms.Validated() enforces, repeated here so a row written by
            // hand, by a script or by a future bug still cannot be half a rule. Enum columns are
            // stored by name, so the allowed names are listed explicitly.
            migrationBuilder.Sql("""
                ALTER TABLE pricing.markup_rules
                    ADD CONSTRAINT ck_markup_rules_scope
                        CHECK (scope IN ('Global', 'Supplier', 'ProductType', 'Product')),

                    ADD CONSTRAINT ck_markup_rules_calculation_type
                        CHECK (calculation_type IN ('Percentage', 'Fixed')),

                    ADD CONSTRAINT ck_markup_rules_product_type
                        CHECK (product_type IS NULL
                            OR product_type IN ('Flight', 'Bus', 'Tour', 'Visa', 'GroupDeparture')),

                    -- Each scope names exactly what it targets, and nothing else.
                    ADD CONSTRAINT ck_markup_rules_scope_shape
                        CHECK (
                            (scope = 'Global'      AND product_type IS NULL     AND product_id IS NULL     AND supplier_code IS NULL)
                         OR (scope = 'Supplier'    AND product_type IS NULL     AND product_id IS NULL     AND supplier_code IS NOT NULL)
                         OR (scope = 'ProductType' AND product_type IS NOT NULL AND product_id IS NULL     AND supplier_code IS NULL)
                         OR (scope = 'Product'     AND product_type IS NOT NULL AND product_id IS NOT NULL AND supplier_code IS NULL)
                        ),

                    -- A percentage rule has a percentage (basis points, 0 to 1000%); a fixed rule has an
                    -- amount and no caps, because a cap on a number that never moves does nothing.
                    ADD CONSTRAINT ck_markup_rules_calculation_shape
                        CHECK (
                            (calculation_type = 'Percentage'
                                AND percent_basis_points BETWEEN 0 AND 100000
                                AND value_minor IS NULL)
                         OR (calculation_type = 'Fixed'
                                AND value_minor >= 0
                                AND percent_basis_points IS NULL
                                AND min_markup_minor IS NULL
                                AND max_markup_minor IS NULL)
                        ),

                    ADD CONSTRAINT ck_markup_rules_caps
                        CHECK ((min_markup_minor IS NULL OR min_markup_minor >= 0)
                           AND (max_markup_minor IS NULL OR max_markup_minor >= 0)
                           AND (min_markup_minor IS NULL OR max_markup_minor IS NULL
                                OR min_markup_minor <= max_markup_minor)),

                    ADD CONSTRAINT ck_markup_rules_currency
                        CHECK (currency ~ '^[A-Z]{3}$'),

                    ADD CONSTRAINT ck_markup_rules_supplier_code
                        CHECK (supplier_code IS NULL OR supplier_code ~ '^[a-z0-9_]+$'),

                    -- ">=", not ">": retiring a rule that had not started yet closes it at its own
                    -- start, an empty window that records it never applied.
                    ADD CONSTRAINT ck_markup_rules_window
                        CHECK (effective_to IS NULL OR effective_to >= effective_from);
                """);

            // ------------------------------------------------------------------- terms never change
            //
            // A quote stores the id of the rule that priced it. That id only explains the margin if
            // the rule still says what it said then — so a rule's terms can never be updated, and a
            // rule can never be deleted. "Editing" retires the rule and inserts a replacement.
            //
            // The only writes allowed: closing the window (effective_to may be set, or moved
            // earlier, never later or back to open), recording the replacement once, and the
            // updated_at stamp that goes with both.
            //
            // A trigger rather than only a REVOKE, for the reason given on the ledger's: a REVOKE does
            // not bind superusers or the migration role.
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION pricing.reject_markup_rule_rewrite()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $$
                BEGIN
                    IF TG_OP = 'DELETE' THEN
                        RAISE EXCEPTION
                            'markup rule % cannot be deleted; retire it instead', OLD.id
                            USING ERRCODE = 'restrict_violation',
                                  HINT = 'Quotes name this rule to explain their margin. Retiring it keeps that true.';
                    END IF;

                    IF (NEW.id, NEW.agency_id, NEW.scope, NEW.product_type, NEW.product_id, NEW.supplier_code,
                        NEW.currency, NEW.calculation_type, NEW.percent_basis_points, NEW.value_minor,
                        NEW.min_markup_minor, NEW.max_markup_minor, NEW.priority, NEW.applies_to_sub_agents,
                        NEW.effective_from, NEW.created_at)
                       IS DISTINCT FROM
                       (OLD.id, OLD.agency_id, OLD.scope, OLD.product_type, OLD.product_id, OLD.supplier_code,
                        OLD.currency, OLD.calculation_type, OLD.percent_basis_points, OLD.value_minor,
                        OLD.min_markup_minor, OLD.max_markup_minor, OLD.priority, OLD.applies_to_sub_agents,
                        OLD.effective_from, OLD.created_at)
                    THEN
                        RAISE EXCEPTION
                            'markup rule % cannot be edited in place', OLD.id
                            USING ERRCODE = 'restrict_violation',
                                  HINT = 'Retire it and insert a replacement, so quotes priced by it stay explainable.';
                    END IF;

                    IF OLD.effective_to IS NOT NULL
                       AND (NEW.effective_to IS NULL OR NEW.effective_to > OLD.effective_to) THEN
                        RAISE EXCEPTION
                            'markup rule % has ended and cannot be reopened or extended', OLD.id
                            USING ERRCODE = 'restrict_violation',
                                  HINT = 'Create a new rule instead.';
                    END IF;

                    IF OLD.superseded_by_id IS NOT NULL
                       AND NEW.superseded_by_id IS DISTINCT FROM OLD.superseded_by_id THEN
                        RAISE EXCEPTION
                            'markup rule % has already been replaced by %', OLD.id, OLD.superseded_by_id
                            USING ERRCODE = 'restrict_violation',
                                  HINT = 'Edit the rule that replaced it instead.';
                    END IF;

                    RETURN NEW;
                END;
                $$;
                """);

            migrationBuilder.Sql("""
                CREATE TRIGGER markup_rules_terms_immutable_trg
                    BEFORE UPDATE OR DELETE ON pricing.markup_rules
                    FOR EACH ROW
                    EXECUTE FUNCTION pricing.reject_markup_rule_rewrite();
                """);


            // ------------------------------------------------------------------- quote arithmetic
            //
            // No margin without an explanation: a non-zero markup must name the rule behind it.
            // Tax and the platform fee join the gross in #29, which will widen ck_price_quotes_gross.
            migrationBuilder.Sql("""
                ALTER TABLE pricing.price_quotes
                    ADD CONSTRAINT ck_price_quotes_amounts_not_negative
                        CHECK (net_amount_minor >= 0 AND markup_amount_minor >= 0),

                    ADD CONSTRAINT ck_price_quotes_gross
                        CHECK (gross_amount_minor = net_amount_minor + markup_amount_minor),

                    ADD CONSTRAINT ck_price_quotes_markup_explained
                        CHECK (markup_amount_minor = 0 OR markup_rule_id IS NOT NULL),

                    ADD CONSTRAINT ck_price_quotes_product_type
                        CHECK (product_type IN ('Flight', 'Bus', 'Tour', 'Visa', 'GroupDeparture')),

                    ADD CONSTRAINT ck_price_quotes_currency
                        CHECK (currency ~ '^[A-Z]{3}$');
                """);

            // ------------------------------------------------------------------ quotes never change
            //
            // A quote is the record of what was charged and why. Rewriting one would rewrite a margin
            // report after the fact, which is exactly what CLAUDE.md rule 5 forbids. A trigger for the
            // same reason as the rules': a REVOKE alone does not bind the schema owner.
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION pricing.reject_price_quote_rewrite()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $$
                BEGIN
                    RAISE EXCEPTION
                        'price quote % cannot be % — quotes are a permanent record', OLD.id, lower(TG_OP)
                        USING ERRCODE = 'restrict_violation',
                              HINT = 'Price again to get a new quote; the old one stays as it was.';
                END;
                $$;

                CREATE TRIGGER price_quotes_immutable_trg
                    BEFORE UPDATE OR DELETE ON pricing.price_quotes
                    FOR EACH ROW
                    EXECUTE FUNCTION pricing.reject_price_quote_rewrite();
                """);

            // ------------------------------------------------------------------ the application role
            //
            // AddRowLevelSecurity granted the runtime role its tables schema by schema, and its default
            // privileges only cover the schemas that existed then. pricing is new, so it is granted
            // here — and narrower than the blanket grant: no DELETE on either table, and no UPDATE on
            // quotes, because neither is ever deleted and a quote is never changed.
            migrationBuilder.Sql($"""
                GRANT USAGE ON SCHEMA pricing TO {AddRowLevelSecurity.ApplicationRole};
                GRANT SELECT, INSERT, UPDATE ON pricing.markup_rules TO {AddRowLevelSecurity.ApplicationRole};
                GRANT SELECT, INSERT ON pricing.price_quotes TO {AddRowLevelSecurity.ApplicationRole};
                """);

            // ------------------------------------------------------------------ row-level security
            //
            // Both tables are owned by one agency through agency_id, so they get the same policy as
            // every such table in AddRowLevelSecurity: the caller's own agency, or the platform scope.
            // A sub-agent inheriting its principal's rules reads them inside the platform scope
            // (PricingService.LoadParentAsync), so the policy needs no special case for inheritance.
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

            migrationBuilder.DropTable(
                name: "price_quotes",
                schema: "pricing");

            migrationBuilder.DropTable(
                name: "markup_rules",
                schema: "pricing");

            // Dropping the table dropped its trigger; the function it called is left behind otherwise.
            migrationBuilder.Sql("""
                DROP FUNCTION IF EXISTS pricing.reject_markup_rule_rewrite();
                DROP FUNCTION IF EXISTS pricing.reject_price_quote_rewrite();
                """);
        }
    }
}
