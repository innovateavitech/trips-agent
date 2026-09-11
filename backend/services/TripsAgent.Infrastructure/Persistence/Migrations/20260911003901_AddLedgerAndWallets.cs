using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TripsAgent.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddLedgerAndWallets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "payments");

            migrationBuilder.CreateTable(
                name: "ledger_accounts",
                schema: "payments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    agency_id = table.Column<Guid>(type: "uuid", nullable: true),
                    account_type = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false),
                    name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ledger_accounts", x => x.id);
                    table.ForeignKey(
                        name: "fk_ledger_accounts_agencies_agency_id",
                        column: x => x.agency_id,
                        principalSchema: "tenancy",
                        principalTable: "agencies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "wallets",
                schema: "payments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    agency_id = table.Column<Guid>(type: "uuid", nullable: false),
                    currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false),
                    balance_minor = table.Column<long>(type: "bigint", nullable: false),
                    reserved_minor = table.Column<long>(type: "bigint", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_wallets", x => x.id);
                    table.ForeignKey(
                        name: "fk_wallets_agencies_agency_id",
                        column: x => x.agency_id,
                        principalSchema: "tenancy",
                        principalTable: "agencies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ledger_entries",
                schema: "payments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    transaction_group_id = table.Column<Guid>(type: "uuid", nullable: false),
                    account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    agency_id = table.Column<Guid>(type: "uuid", nullable: true),
                    direction = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    amount_minor = table.Column<long>(type: "bigint", nullable: false),
                    reference_type = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    reference_id = table.Column<Guid>(type: "uuid", nullable: true),
                    description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ledger_entries", x => x.id);
                    table.ForeignKey(
                        name: "fk_ledger_entries_ledger_accounts_account_id",
                        column: x => x.account_id,
                        principalSchema: "payments",
                        principalTable: "ledger_accounts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "wallet_holds",
                schema: "payments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    wallet_id = table.Column<Guid>(type: "uuid", nullable: false),
                    agency_id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: true),
                    amount_minor = table.Column<long>(type: "bigint", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    settled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_wallet_holds", x => x.id);
                    table.ForeignKey(
                        name: "fk_wallet_holds_wallets_wallet_id",
                        column: x => x.wallet_id,
                        principalSchema: "payments",
                        principalTable: "wallets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "wallet_transactions",
                schema: "payments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    wallet_id = table.Column<Guid>(type: "uuid", nullable: false),
                    agency_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    amount_minor = table.Column<long>(type: "bigint", nullable: false),
                    balance_before_minor = table.Column<long>(type: "bigint", nullable: false),
                    balance_after_minor = table.Column<long>(type: "bigint", nullable: false),
                    description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    transaction_group_id = table.Column<Guid>(type: "uuid", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_wallet_transactions", x => x.id);
                    table.ForeignKey(
                        name: "fk_wallet_transactions_wallets_wallet_id",
                        column: x => x.wallet_id,
                        principalSchema: "payments",
                        principalTable: "wallets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_ledger_accounts_agency_id_account_type_currency",
                schema: "payments",
                table: "ledger_accounts",
                columns: new[] { "agency_id", "account_type", "currency" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_ledger_entries_account_id_occurred_at",
                schema: "payments",
                table: "ledger_entries",
                columns: new[] { "account_id", "occurred_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_ledger_entries_reference",
                schema: "payments",
                table: "ledger_entries",
                columns: new[] { "reference_type", "reference_id" });

            migrationBuilder.CreateIndex(
                name: "ix_ledger_entries_transaction_group_id",
                schema: "payments",
                table: "ledger_entries",
                column: "transaction_group_id");

            migrationBuilder.CreateIndex(
                name: "ix_wallet_holds_agency_id_status",
                schema: "payments",
                table: "wallet_holds",
                columns: new[] { "agency_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_wallet_holds_status_expires_at",
                schema: "payments",
                table: "wallet_holds",
                columns: new[] { "status", "expires_at" });

            migrationBuilder.CreateIndex(
                name: "ix_wallet_holds_wallet_id",
                schema: "payments",
                table: "wallet_holds",
                column: "wallet_id");

            migrationBuilder.CreateIndex(
                name: "ix_wallet_transactions_agency_id_occurred_at",
                schema: "payments",
                table: "wallet_transactions",
                columns: new[] { "agency_id", "occurred_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_wallet_transactions_transaction_group_id",
                schema: "payments",
                table: "wallet_transactions",
                column: "transaction_group_id");

            migrationBuilder.CreateIndex(
                name: "ix_wallet_transactions_wallet_id",
                schema: "payments",
                table: "wallet_transactions",
                column: "wallet_id");

            migrationBuilder.CreateIndex(
                name: "ix_wallets_agency_id_currency",
                schema: "payments",
                table: "wallets",
                columns: new[] { "agency_id", "currency" },
                unique: true);

            // ---------------------------------------------------------------- the balance rule
            //
            // Debits must equal credits within a transaction group. This is the one invariant the
            // whole system rests on, so it is enforced by the database rather than by every
            // caller remembering.
            //
            // DEFERRABLE INITIALLY DEFERRED is what makes it usable: the sides of a transaction
            // are inserted as separate rows, so a check that ran per statement would fail on the
            // first one. Deferred, it runs once at COMMIT, when the group is complete.
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION payments.assert_ledger_group_balanced()
                RETURNS trigger
                LANGUAGE plpgsql
                SET search_path = payments, public
                AS $$
                DECLARE
                    debits  bigint;
                    credits bigint;
                BEGIN
                    SELECT
                        COALESCE(SUM(amount_minor) FILTER (WHERE direction = 'Debit'), 0),
                        COALESCE(SUM(amount_minor) FILTER (WHERE direction = 'Credit'), 0)
                      INTO debits, credits
                      FROM payments.ledger_entries
                     WHERE transaction_group_id = NEW.transaction_group_id;

                    IF debits <> credits THEN
                        RAISE EXCEPTION
                            'Ledger transaction % does not balance: debits %, credits % (out by %)',
                            NEW.transaction_group_id, debits, credits, debits - credits
                            USING ERRCODE = 'check_violation',
                                  HINT = 'Every financial event moves value from somewhere to somewhere else.';
                    END IF;

                    RETURN NULL;
                END;
                $$;
                """);

            migrationBuilder.Sql("""
                CREATE CONSTRAINT TRIGGER ledger_entries_balanced_trg
                    AFTER INSERT ON payments.ledger_entries
                    DEFERRABLE INITIALLY DEFERRED
                    FOR EACH ROW
                    EXECUTE FUNCTION payments.assert_ledger_group_balanced();
                """);

            // ------------------------------------------------------------------- append-only
            //
            // A correction is a new reversing entry, never an edit. An edited ledger cannot be
            // audited, because nothing records what it said before.
            //
            // A trigger rather than only a REVOKE, because a REVOKE does not apply to superusers
            // or to the migration role — and "nobody runs as superuser in production" is exactly
            // the assumption that fails at 3am.
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION payments.reject_ledger_mutation()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $$
                BEGIN
                    RAISE EXCEPTION
                        'ledger_entries is append-only; use a reversing entry instead of %', TG_OP
                        USING ERRCODE = 'restrict_violation',
                              HINT = 'Post a new entry that reverses the original. The history has to stay true.';
                END;
                $$;
                """);

            migrationBuilder.Sql("""
                CREATE TRIGGER ledger_entries_append_only_trg
                    BEFORE UPDATE OR DELETE ON payments.ledger_entries
                    FOR EACH ROW
                    EXECUTE FUNCTION payments.reject_ledger_mutation();
                """);

            // The REVOKE the issue asks for, applied to the application role when one exists.
            // Defence in depth behind the trigger: it stops the statement ever reaching the
            // table, which is cheaper and clearer in a permissions audit.
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'tripsagent_app') THEN
                        REVOKE UPDATE, DELETE ON payments.ledger_entries FROM tripsagent_app;
                    END IF;
                END;
                $$;
                """);

            // ------------------------------------------------------------------- constraints
            migrationBuilder.Sql("""
                ALTER TABLE payments.ledger_entries
                    -- Direction carries the sign. Allowing negative amounts would mean a debit of
                    -- -100 and a credit of 100 are the same thing spelled two ways, and every
                    -- balance query would have to handle both.
                    ADD CONSTRAINT ck_ledger_entries_amount_positive
                        CHECK (amount_minor > 0),

                    ADD CONSTRAINT ck_ledger_entries_direction
                        CHECK (direction IN ('Debit', 'Credit'));
                """);

            migrationBuilder.Sql("""
                ALTER TABLE payments.ledger_accounts
                    ADD CONSTRAINT ck_ledger_accounts_type
                        CHECK (account_type IN ('AgencyWallet', 'PlatformRevenue', 'SupplierPayable',
                                                'CustomerReceivable', 'GatewayClearing', 'TaxPayable', 'Refunds')),

                    ADD CONSTRAINT ck_ledger_accounts_currency
                        CHECK (currency ~ '^[A-Z]{3}$'),

                    -- Trips' own accounts belong to no agency; an agency's wallet and receivables
                    -- must belong to one.
                    ADD CONSTRAINT ck_ledger_accounts_ownership
                        CHECK (
                            (account_type IN ('AgencyWallet', 'CustomerReceivable') AND agency_id IS NOT NULL)
                         OR (account_type NOT IN ('AgencyWallet', 'CustomerReceivable'))
                        );
                """);

            migrationBuilder.Sql("""
                ALTER TABLE payments.wallets
                    -- A wallet cannot go negative, and cannot reserve more than it holds. These
                    -- are the invariants the domain maintains; the database keeps them true for
                    -- anything that does not go through the domain.
                    ADD CONSTRAINT ck_wallets_balance_not_negative
                        CHECK (balance_minor >= 0),

                    ADD CONSTRAINT ck_wallets_reserved_not_negative
                        CHECK (reserved_minor >= 0),

                    ADD CONSTRAINT ck_wallets_reserved_within_balance
                        CHECK (reserved_minor <= balance_minor),

                    ADD CONSTRAINT ck_wallets_status
                        CHECK (status IN ('Active', 'Frozen')),

                    ADD CONSTRAINT ck_wallets_currency
                        CHECK (currency ~ '^[A-Z]{3}$');
                """);

            migrationBuilder.Sql("""
                ALTER TABLE payments.wallet_holds
                    ADD CONSTRAINT ck_wallet_holds_amount_positive
                        CHECK (amount_minor > 0),

                    ADD CONSTRAINT ck_wallet_holds_status
                        CHECK (status IN ('Held', 'Captured', 'Released')),

                    -- A settled hold records when. An outstanding one has not settled.
                    ADD CONSTRAINT ck_wallet_holds_settlement
                        CHECK ((status = 'Held' AND settled_at IS NULL)
                            OR (status <> 'Held' AND settled_at IS NOT NULL));
                """);

            migrationBuilder.Sql("""
                ALTER TABLE payments.wallet_transactions
                    ADD CONSTRAINT ck_wallet_transactions_type
                        CHECK (type IN ('TopUp', 'BookingPayment', 'Refund', 'Reversal', 'Adjustment')),

                    -- The running balance has to add up, or the statement is fiction.
                    ADD CONSTRAINT ck_wallet_transactions_running_balance
                        CHECK (balance_after_minor = balance_before_minor + amount_minor);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS payments.assert_ledger_group_balanced() CASCADE;");
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS payments.reject_ledger_mutation() CASCADE;");

            migrationBuilder.DropTable(
                name: "ledger_entries",
                schema: "payments");

            migrationBuilder.DropTable(
                name: "wallet_holds",
                schema: "payments");

            migrationBuilder.DropTable(
                name: "wallet_transactions",
                schema: "payments");

            migrationBuilder.DropTable(
                name: "ledger_accounts",
                schema: "payments");

            migrationBuilder.DropTable(
                name: "wallets",
                schema: "payments");
        }
    }
}
