using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TripsAgent.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class HardenMoneyPath : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_payment_webhook_events_pending",
                schema: "payments",
                table: "payment_webhook_events");

            migrationBuilder.DropIndex(
                name: "ix_ledger_accounts_agency_id_account_type_currency",
                schema: "payments",
                table: "ledger_accounts");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "claim_expires_at",
                schema: "payments",
                table: "payment_webhook_events",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "next_attempt_at",
                schema: "payments",
                table: "payment_webhook_events",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "transient_failures",
                schema: "payments",
                table: "payment_webhook_events",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "ix_payment_webhook_events_pending",
                schema: "payments",
                table: "payment_webhook_events",
                column: "created_at",
                filter: "processing_status IN ('Pending', 'Processing')");

            migrationBuilder.CreateIndex(
                name: "ix_ledger_entries_payment_posting",
                schema: "payments",
                table: "ledger_entries",
                columns: new[] { "reference_type", "reference_id", "account_id" },
                unique: true,
                filter: "reference_type = 'PaymentTransaction'");

            migrationBuilder.CreateIndex(
                name: "ix_ledger_accounts_agency_id_account_type_currency",
                schema: "payments",
                table: "ledger_accounts",
                columns: new[] { "agency_id", "account_type", "currency" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            // The platform's own accounts, one per type and currency. WalletTopUpService refuses
            // to create these on first use: two first-ever top-ups each created a clearing
            // account, and a split clearing balance is not something the ledger can tell you
            // about. Seeded here so there is exactly one, forever.
            migrationBuilder.Sql(
                """
                INSERT INTO payments.ledger_accounts
                    (id, agency_id, account_type, currency, name, created_at, updated_at)
                SELECT gen_random_uuid(), NULL, t.account_type, 'NGN',
                       t.account_type || ' (NGN)', now(), now()
                  FROM (VALUES ('GatewayClearing'), ('PlatformRevenue'), ('SupplierPayable'),
                               ('TaxPayable'), ('Refunds')) AS t(account_type)
                 WHERE NOT EXISTS (SELECT 1 FROM payments.ledger_accounts l
                                    WHERE l.agency_id IS NULL
                                      AND l.account_type = t.account_type
                                      AND l.currency = 'NGN');
                """);

            // Every agency verified before KYB approval learned to open a wallet has none, and
            // nothing else would ever create one — so a real top-up for them fails to post and
            // dead-letters. Opened here, with its ledger account, exactly as approval now does.
            migrationBuilder.Sql(
                """
                INSERT INTO payments.wallets
                    (id, agency_id, currency, balance_minor, reserved_minor, status, version, created_at, updated_at)
                SELECT gen_random_uuid(), a.id, a.base_currency, 0, 0, 'Active', 0, now(), now()
                  FROM tenancy.agencies a
                 WHERE a.status = 'Verified'
                   AND NOT EXISTS (SELECT 1 FROM payments.wallets w
                                    WHERE w.agency_id = a.id AND w.currency = a.base_currency);

                INSERT INTO payments.ledger_accounts
                    (id, agency_id, account_type, currency, name, created_at, updated_at)
                SELECT gen_random_uuid(), a.id, 'AgencyWallet', a.base_currency,
                       'AgencyWallet (' || a.base_currency || ')', now(), now()
                  FROM tenancy.agencies a
                 WHERE a.status = 'Verified'
                   AND NOT EXISTS (SELECT 1 FROM payments.ledger_accounts l
                                    WHERE l.agency_id = a.id
                                      AND l.account_type = 'AgencyWallet'
                                      AND l.currency = a.base_currency);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_payment_webhook_events_pending",
                schema: "payments",
                table: "payment_webhook_events");

            migrationBuilder.DropIndex(
                name: "ix_ledger_entries_payment_posting",
                schema: "payments",
                table: "ledger_entries");

            migrationBuilder.DropIndex(
                name: "ix_ledger_accounts_agency_id_account_type_currency",
                schema: "payments",
                table: "ledger_accounts");

            migrationBuilder.DropColumn(
                name: "claim_expires_at",
                schema: "payments",
                table: "payment_webhook_events");

            migrationBuilder.DropColumn(
                name: "next_attempt_at",
                schema: "payments",
                table: "payment_webhook_events");

            migrationBuilder.DropColumn(
                name: "transient_failures",
                schema: "payments",
                table: "payment_webhook_events");

            migrationBuilder.CreateIndex(
                name: "ix_payment_webhook_events_pending",
                schema: "payments",
                table: "payment_webhook_events",
                column: "created_at",
                filter: "processing_status = 'Pending'");

            migrationBuilder.CreateIndex(
                name: "ix_ledger_accounts_agency_id_account_type_currency",
                schema: "payments",
                table: "ledger_accounts",
                columns: new[] { "agency_id", "account_type", "currency" },
                unique: true);
        }
    }
}
