using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TripsAgent.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DropPlaintextPiiColumns : Migration
    {
        /// <summary>
        /// Step three of three for issue 104: the plaintext columns go, once nothing in them is left unencrypted.
        /// </summary>
        /// <remarks>
        /// Refuses — and changes nothing — while any row holds a value <c>FieldEncryptionBackfill</c> has not
        /// yet encrypted, or a passport number still in the format it had before issue 104. <c>migrate</c>
        /// runs the backfill between the two migrations; <c>dotnet ef database update</c> does not, which is
        /// exactly the case this guard is for. See docs/runbooks/field-encryption.md.
        /// </remarks>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.Sql(PlaintextRemainsGuard);

            migrationBuilder.DropColumn(
                name: "expires_on",
                schema: "supplier",
                table: "passenger_documents");

            migrationBuilder.DropColumn(
                name: "passport_expiry",
                schema: "orders",
                table: "order_travellers");

            migrationBuilder.DropColumn(
                name: "account_number",
                schema: "payments",
                table: "agency_bank_accounts");

            migrationBuilder.AlterColumn<byte[]>(
                name: "account_number_encrypted",
                schema: "payments",
                table: "agency_bank_accounts",
                type: "bytea",
                nullable: false,
                oldClrType: typeof(byte[]),
                oldType: "bytea",
                oldNullable: true);
        }

        /// <inheritdoc />
        /// <remarks>
        /// The plaintext cannot come back: the key is not in the database. So Down only runs on a database
        /// with no encrypted values to lose, and refuses otherwise.
        /// </remarks>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    PERFORM set_config('app.platform_scope', 'on', true);

                    IF EXISTS (SELECT 1 FROM payments.agency_bank_accounts)
                    OR EXISTS (SELECT 1 FROM orders.order_travellers WHERE passport_expiry_encrypted IS NOT NULL)
                    OR EXISTS (SELECT 1 FROM supplier.passenger_documents WHERE expires_on_encrypted IS NOT NULL) THEN
                        RAISE EXCEPTION 'DropPlaintextPiiColumns cannot be reverted on a database holding encrypted values'
                            USING HINT = 'The plaintext columns would come back empty, and a bank account number cannot be empty.';
                    END IF;
                END
                $$;
                """);

            migrationBuilder.AddColumn<DateOnly>(
                name: "expires_on",
                schema: "supplier",
                table: "passenger_documents",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "passport_expiry",
                schema: "orders",
                table: "order_travellers",
                type: "date",
                nullable: true);

            migrationBuilder.AlterColumn<byte[]>(
                name: "account_number_encrypted",
                schema: "payments",
                table: "agency_bank_accounts",
                type: "bytea",
                nullable: true,
                oldClrType: typeof(byte[]),
                oldType: "bytea");

            migrationBuilder.AddColumn<string>(
                name: "account_number",
                schema: "payments",
                table: "agency_bank_accounts",
                type: "character(10)",
                fixedLength: true,
                maxLength: 10,
                nullable: false,
                defaultValue: "");
        }

        /// <summary>Raises unless every value the plaintext columns held has been encrypted.</summary>
        internal const string PlaintextRemainsGuard = """
            DO $$
            BEGIN
                -- The owner connection normally bypasses row-level security; this makes sure every agency's
                -- rows are counted even where it does not. Local to this transaction.
                PERFORM set_config('app.platform_scope', 'on', true);

                IF EXISTS (SELECT 1 FROM orders.order_travellers
                            WHERE passport_expiry IS NOT NULL AND passport_expiry_encrypted IS NULL)
                OR EXISTS (SELECT 1 FROM orders.order_travellers
                            WHERE passport_number_encrypted IS NOT NULL
                              AND (length(passport_number_encrypted) < 1 OR get_byte(passport_number_encrypted, 0) <> 2))
                OR EXISTS (SELECT 1 FROM supplier.passenger_documents
                            WHERE expires_on IS NOT NULL AND expires_on_encrypted IS NULL)
                OR EXISTS (SELECT 1 FROM supplier.passenger_documents
                            WHERE length(doc_number_encrypted) < 1 OR get_byte(doc_number_encrypted, 0) <> 2)
                OR EXISTS (SELECT 1 FROM payments.agency_bank_accounts
                            WHERE account_number_encrypted IS NULL) THEN
                    RAISE EXCEPTION 'Traveller documents or bank account numbers are still stored unencrypted'
                        USING ERRCODE = 'object_not_in_prerequisite_state',
                              HINT = 'Run the Api with the migrate command, which encrypts them between AddEncryptedPiiColumns and this migration. See docs/runbooks/field-encryption.md.';
                END IF;
            END
            $$;
            """;
    }
}
