using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TripsAgent.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPayoutsDisputesAndReconciliation : Migration
    {
        /// <summary>The tables this migration creates that belong to one agency through agency_id.</summary>
        /// <remarks>
        /// reconciliation_runs is deliberately absent. It is platform-wide, like the exceptions it
        /// produces: a settlement that matches nothing belongs to no agency, and a tenant policy on
        /// it would be the wrong shape of protection rather than a stricter one. Access is by
        /// platform permission and IPlatformScope.
        /// </remarks>
        internal static readonly string[] PolicedTables =
        [
            "payments.agency_bank_accounts",
            "payments.payouts",
            "payments.disputes",
        ];

        // The same InitPlan-wrapped calls AddRowLevelSecurity uses; see the note there.
        private const string PlatformScope = "(SELECT tenancy.platform_scope_active())";
        private const string CurrentAgency = "(SELECT tenancy.current_agency_id())";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "reconciliation_run_id",
                schema: "payments",
                table: "reconciliation_exceptions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "resolved_by_user_id",
                schema: "payments",
                table: "reconciliation_exceptions",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "agency_bank_accounts",
                schema: "payments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    agency_id = table.Column<Guid>(type: "uuid", nullable: false),
                    bank_code = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    bank_name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    account_number = table.Column<string>(type: "character(10)", fixedLength: true, maxLength: 10, nullable: false),
                    account_name_provided = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    account_name_resolved = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                    currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false),
                    status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    gateway_recipient_code = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    verified_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    rejection_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    is_default = table.Column<bool>(type: "boolean", nullable: false),
                    added_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_agency_bank_accounts", x => x.id);
                    table.ForeignKey(
                        name: "fk_agency_bank_accounts_agencies_agency_id",
                        column: x => x.agency_id,
                        principalSchema: "tenancy",
                        principalTable: "agencies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "disputes",
                schema: "payments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    agency_id = table.Column<Guid>(type: "uuid", nullable: false),
                    payment_transaction_id = table.Column<Guid>(type: "uuid", nullable: false),
                    payment_reference = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: true),
                    gateway_dispute_id = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    amount_minor = table.Column<long>(type: "bigint", nullable: false),
                    currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false),
                    category = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    hold_outcome = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    hold_failure_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    opened_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    evidence_due_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    evidence_submitted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    evidence_payload = table.Column<string>(type: "jsonb", nullable: true),
                    evidence_note = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    evidence_asset_ids = table.Column<string>(type: "jsonb", nullable: true),
                    evidence_submitted_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    resolved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    resolution = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    hold_ledger_transaction_group_id = table.Column<Guid>(type: "uuid", nullable: true),
                    resolution_ledger_transaction_group_id = table.Column<Guid>(type: "uuid", nullable: true),
                    last_reminder_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    version = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_disputes", x => x.id);
                    table.ForeignKey(
                        name: "fk_disputes_agencies_agency_id",
                        column: x => x.agency_id,
                        principalSchema: "tenancy",
                        principalTable: "agencies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_disputes_orders_order_id",
                        column: x => x.order_id,
                        principalSchema: "orders",
                        principalTable: "orders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_disputes_payment_transactions_payment_transaction_id",
                        column: x => x.payment_transaction_id,
                        principalSchema: "payments",
                        principalTable: "payment_transactions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "reconciliation_runs",
                schema: "payments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    gateway = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    business_date = table.Column<DateOnly>(type: "date", nullable: false),
                    window_start = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    window_end = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    records_examined = table.Column<int>(type: "integer", nullable: false),
                    records_matched = table.Column<int>(type: "integer", nullable: false),
                    exceptions_raised = table.Column<int>(type: "integer", nullable: false),
                    gateway_gross_minor = table.Column<long>(type: "bigint", nullable: false),
                    gateway_fees_minor = table.Column<long>(type: "bigint", nullable: false),
                    gateway_net_minor = table.Column<long>(type: "bigint", nullable: false),
                    ledger_gross_minor = table.Column<long>(type: "bigint", nullable: false),
                    failure_reason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_reconciliation_runs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "payouts",
                schema: "payments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    agency_id = table.Column<Guid>(type: "uuid", nullable: false),
                    bank_account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    amount_minor = table.Column<long>(type: "bigint", nullable: false),
                    currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false),
                    reference = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    requested_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    requested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    approved_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    approved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    decided_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    rejected_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    rejection_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    sent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    gateway_transfer_code = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    gateway_status = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    status_queries = table.Column<int>(type: "integer", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    failure_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    request_ledger_transaction_group_id = table.Column<Guid>(type: "uuid", nullable: false),
                    settlement_ledger_transaction_group_id = table.Column<Guid>(type: "uuid", nullable: true),
                    return_ledger_transaction_group_id = table.Column<Guid>(type: "uuid", nullable: true),
                    version = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_payouts", x => x.id);
                    table.ForeignKey(
                        name: "fk_payouts_agencies_agency_id",
                        column: x => x.agency_id,
                        principalSchema: "tenancy",
                        principalTable: "agencies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_payouts_agency_bank_accounts_bank_account_id",
                        column: x => x.bank_account_id,
                        principalSchema: "payments",
                        principalTable: "agency_bank_accounts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_reconciliation_exceptions_reconciliation_run_id",
                schema: "payments",
                table: "reconciliation_exceptions",
                column: "reconciliation_run_id");

            migrationBuilder.CreateIndex(
                name: "ix_agency_bank_accounts_agency_bank_number",
                schema: "payments",
                table: "agency_bank_accounts",
                columns: new[] { "agency_id", "bank_code", "account_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_agency_bank_accounts_one_default",
                schema: "payments",
                table: "agency_bank_accounts",
                columns: new[] { "agency_id", "currency" },
                unique: true,
                filter: "is_default");

            migrationBuilder.CreateIndex(
                name: "ix_disputes_agency_id_opened_at",
                schema: "payments",
                table: "disputes",
                columns: new[] { "agency_id", "opened_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_disputes_gateway_dispute_id",
                schema: "payments",
                table: "disputes",
                column: "gateway_dispute_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_disputes_order_id",
                schema: "payments",
                table: "disputes",
                column: "order_id");

            migrationBuilder.CreateIndex(
                name: "ix_disputes_payment_transaction_id",
                schema: "payments",
                table: "disputes",
                column: "payment_transaction_id");

            migrationBuilder.CreateIndex(
                name: "ix_disputes_status_evidence_due_at",
                schema: "payments",
                table: "disputes",
                columns: new[] { "status", "evidence_due_at" });

            migrationBuilder.CreateIndex(
                name: "ix_payouts_agency_id_requested_at",
                schema: "payments",
                table: "payouts",
                columns: new[] { "agency_id", "requested_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_payouts_bank_account_id",
                schema: "payments",
                table: "payouts",
                column: "bank_account_id");

            migrationBuilder.CreateIndex(
                name: "ix_payouts_reference",
                schema: "payments",
                table: "payouts",
                column: "reference",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_payouts_status_requested_at",
                schema: "payments",
                table: "payouts",
                columns: new[] { "status", "requested_at" });

            migrationBuilder.CreateIndex(
                name: "ix_reconciliation_runs_status_business_date",
                schema: "payments",
                table: "reconciliation_runs",
                columns: new[] { "status", "business_date" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_reconciliation_runs_type_gateway_business_date",
                schema: "payments",
                table: "reconciliation_runs",
                columns: new[] { "type", "gateway", "business_date" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "fk_reconciliation_exceptions_reconciliation_runs_reconciliatio",
                schema: "payments",
                table: "reconciliation_exceptions",
                column: "reconciliation_run_id",
                principalSchema: "payments",
                principalTable: "reconciliation_runs",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            // ------------------------------------------------------------------- new ledger accounts
            //
            // Money leaving the platform needs somewhere to sit between the wallet it left and the
            // bank it has not reached. Without these two, a payout would have to credit
            // gateway_clearing directly and a dispute hold would have nowhere at all — and an
            // account type added after entries exist means a migration plus re-deriving every
            // balance posted to the wrong one.
            //
            // Both are liabilities: the money is still somebody's, it is merely spoken for.
            migrationBuilder.Sql("""
                ALTER TABLE payments.ledger_accounts
                    DROP CONSTRAINT ck_ledger_accounts_type;

                ALTER TABLE payments.ledger_accounts
                    ADD CONSTRAINT ck_ledger_accounts_type
                        CHECK (account_type IN ('AgencyWallet', 'PlatformRevenue', 'SupplierPayable',
                                                'CustomerReceivable', 'GatewayClearing', 'TaxPayable', 'Refunds',
                                                'PayoutPayable', 'DisputeHeld'));

                -- Seeded, never created on first use. Two of either would split the platform's
                -- balance between them and every total would depend on remembering to add both.
                INSERT INTO payments.ledger_accounts
                    (id, agency_id, account_type, currency, name, created_at, updated_at)
                SELECT gen_random_uuid(), NULL, t.account_type, 'NGN',
                       t.account_type || ' (NGN)', now(), now()
                  FROM (VALUES ('PayoutPayable'), ('DisputeHeld')) AS t(account_type)
                 WHERE NOT EXISTS (SELECT 1 FROM payments.ledger_accounts l
                                    WHERE l.agency_id IS NULL
                                      AND l.account_type = t.account_type
                                      AND l.currency = 'NGN');
                """);

            // ------------------------------------------------------------------ statement line kinds
            //
            // Five new ways money moves on an agency's statement. The CHECK from AddLedgerAndWallets
            // lists the kinds by name, so it has to learn each one.
            migrationBuilder.Sql("""
                ALTER TABLE payments.wallet_transactions
                    DROP CONSTRAINT ck_wallet_transactions_type;

                ALTER TABLE payments.wallet_transactions
                    ADD CONSTRAINT ck_wallet_transactions_type
                        CHECK (type IN ('TopUp', 'BookingPayment', 'Refund', 'Reversal', 'Adjustment',
                                        'CustomerPayment', 'Payout', 'PayoutReturned', 'DisputeHold',
                                        'DisputeReleased', 'DisputeLost'));
                """);

            // ------------------------------------------------------------------------- shape CHECKs
            //
            // EF Core can express none of these, and they are here rather than left to the
            // application because a hand-typed UPDATE at a psql prompt is not bound by C#.
            migrationBuilder.Sql("""
                -- A NUBAN is ten digits. Not "roughly ten characters" — a transposed pair sends
                -- somebody else's money to a stranger, and a shorter one is a typo caught here
                -- rather than at the bank.
                ALTER TABLE payments.agency_bank_accounts
                    ADD CONSTRAINT ck_agency_bank_accounts_nuban CHECK (account_number ~ '^[0-9]{10}$'),

                    ADD CONSTRAINT ck_agency_bank_accounts_currency CHECK (currency ~ '^[A-Z]{3}$'),

                    -- Verified means the bank answered. A row that claims to be verified without a
                    -- name from the bank is the exact state this table exists to prevent.
                    ADD CONSTRAINT ck_agency_bank_accounts_verified_has_name CHECK (
                        status <> 'Verified' OR (account_name_resolved IS NOT NULL AND verified_at IS NOT NULL)),

                    -- Only an account money may be sent to can be the default destination.
                    ADD CONSTRAINT ck_agency_bank_accounts_default_is_verified CHECK (
                        NOT is_default OR status = 'Verified');

                ALTER TABLE payments.payouts
                    ADD CONSTRAINT ck_payouts_amount_positive CHECK (amount_minor > 0),

                    ADD CONSTRAINT ck_payouts_currency CHECK (currency ~ '^[A-Z]{3}$'),

                    -- Approval is two people. Enforced here as well as in the domain, because this
                    -- is the control that stands between one login and a bank transfer.
                    ADD CONSTRAINT ck_payouts_approver_is_not_requester CHECK (
                        approved_by_user_id IS NULL OR approved_by_user_id <> requested_by_user_id),

                    -- Nothing may be sent without an approval behind it.
                    ADD CONSTRAINT ck_payouts_sent_was_approved CHECK (
                        sent_at IS NULL OR approved_by_user_id IS NOT NULL),

                    -- A payout that has gone back to the wallet says where the reversing entries are.
                    ADD CONSTRAINT ck_payouts_returned_has_entries CHECK (
                        status NOT IN ('Rejected', 'Failed', 'Reversed')
                        OR return_ledger_transaction_group_id IS NOT NULL),

                    -- And a paid one says where the entries that took it out of the platform are.
                    ADD CONSTRAINT ck_payouts_paid_has_entries CHECK (
                        status <> 'Paid' OR settlement_ledger_transaction_group_id IS NOT NULL);

                ALTER TABLE payments.disputes
                    ADD CONSTRAINT ck_disputes_amount_positive CHECK (amount_minor > 0),

                    ADD CONSTRAINT ck_disputes_currency CHECK (currency ~ '^[A-Z]{3}$'),

                    -- The deadline is after the dispute was raised, or the clock was already spent
                    -- when we heard about it and nobody could have met it.
                    ADD CONSTRAINT ck_disputes_due_after_open CHECK (evidence_due_at > opened_at),

                    -- Held money says where it went; money that could not be held says why not.
                    ADD CONSTRAINT ck_disputes_hold_is_explained CHECK (
                        (hold_outcome <> 'Uncovered' OR hold_failure_reason IS NOT NULL)
                        AND (hold_outcome NOT IN ('Held', 'Settled') OR hold_ledger_transaction_group_id IS NOT NULL));

                ALTER TABLE payments.reconciliation_runs
                    ADD CONSTRAINT ck_reconciliation_runs_window CHECK (window_end > window_start),

                    ADD CONSTRAINT ck_reconciliation_runs_currency CHECK (currency ~ '^[A-Z]{3}$'),

                    ADD CONSTRAINT ck_reconciliation_runs_counts_not_negative CHECK (
                        records_examined >= 0 AND records_matched >= 0 AND exceptions_raised >= 0),

                    -- A run that stopped badly says so. An absent reason on a failed run is how a
                    -- reconciliation that never happened comes to look like one that found nothing.
                    ADD CONSTRAINT ck_reconciliation_runs_failure_is_explained CHECK (
                        status <> 'Failed' OR failure_reason IS NOT NULL);

                -- Closing an exception needs a reason, whichever way it is closed.
                ALTER TABLE payments.reconciliation_exceptions
                    DROP CONSTRAINT IF EXISTS ck_reconciliation_exceptions_resolved_has_note;

                ALTER TABLE payments.reconciliation_exceptions
                    ADD CONSTRAINT ck_reconciliation_exceptions_resolved_has_note CHECK (
                        status NOT IN ('Resolved', 'WrittenOff')
                        OR (resolution_note IS NOT NULL AND resolved_at IS NOT NULL));
                """);

            // ------------------------------------------------------------------ the application role
            //
            // payments already has USAGE from AddRowLevelSecurity; the new tables need their own
            // grants, which the blanket grant there could not have covered.
            //
            // No DELETE anywhere. A bank account is retired rather than removed, a payout is a
            // financial record, a dispute is evidence of what a bank decided, and a reconciliation
            // run that can be deleted is a reconciliation that can be made to look clean.
            migrationBuilder.Sql($"""
                GRANT SELECT, INSERT, UPDATE ON payments.agency_bank_accounts TO {AddRowLevelSecurity.ApplicationRole};
                GRANT SELECT, INSERT, UPDATE ON payments.payouts TO {AddRowLevelSecurity.ApplicationRole};
                GRANT SELECT, INSERT, UPDATE ON payments.disputes TO {AddRowLevelSecurity.ApplicationRole};
                GRANT SELECT, INSERT, UPDATE ON payments.reconciliation_runs TO {AddRowLevelSecurity.ApplicationRole};
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
                DELETE FROM payments.ledger_accounts
                 WHERE agency_id IS NULL
                   AND account_type IN ('PayoutPayable', 'DisputeHeld')
                   AND NOT EXISTS (SELECT 1 FROM payments.ledger_entries e
                                    WHERE e.account_id = payments.ledger_accounts.id);

                ALTER TABLE payments.reconciliation_exceptions
                    DROP CONSTRAINT IF EXISTS ck_reconciliation_exceptions_resolved_has_note;

                ALTER TABLE payments.reconciliation_exceptions
                    ADD CONSTRAINT ck_reconciliation_exceptions_resolved_has_note CHECK (
                        status <> 'Resolved' OR (resolution_note IS NOT NULL AND resolved_at IS NOT NULL));

                ALTER TABLE payments.wallet_transactions
                    DROP CONSTRAINT IF EXISTS ck_wallet_transactions_type;

                ALTER TABLE payments.wallet_transactions
                    ADD CONSTRAINT ck_wallet_transactions_type
                        CHECK (type IN ('TopUp', 'BookingPayment', 'Refund', 'Reversal', 'Adjustment',
                                        'CustomerPayment'));

                ALTER TABLE payments.ledger_accounts
                    DROP CONSTRAINT IF EXISTS ck_ledger_accounts_type;

                ALTER TABLE payments.ledger_accounts
                    ADD CONSTRAINT ck_ledger_accounts_type
                        CHECK (account_type IN ('AgencyWallet', 'PlatformRevenue', 'SupplierPayable',
                                                'CustomerReceivable', 'GatewayClearing', 'TaxPayable', 'Refunds'));
                """);

            migrationBuilder.DropForeignKey(
                name: "fk_reconciliation_exceptions_reconciliation_runs_reconciliatio",
                schema: "payments",
                table: "reconciliation_exceptions");

            migrationBuilder.DropTable(
                name: "disputes",
                schema: "payments");

            migrationBuilder.DropTable(
                name: "payouts",
                schema: "payments");

            migrationBuilder.DropTable(
                name: "reconciliation_runs",
                schema: "payments");

            migrationBuilder.DropTable(
                name: "agency_bank_accounts",
                schema: "payments");

            migrationBuilder.DropIndex(
                name: "ix_reconciliation_exceptions_reconciliation_run_id",
                schema: "payments",
                table: "reconciliation_exceptions");

            migrationBuilder.DropColumn(
                name: "reconciliation_run_id",
                schema: "payments",
                table: "reconciliation_exceptions");

            migrationBuilder.DropColumn(
                name: "resolved_by_user_id",
                schema: "payments",
                table: "reconciliation_exceptions");
        }
    }
}
