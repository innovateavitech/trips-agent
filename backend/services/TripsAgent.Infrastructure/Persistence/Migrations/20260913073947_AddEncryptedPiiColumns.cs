using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TripsAgent.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddEncryptedPiiColumns : Migration
    {
        /// <summary>
        /// Step one of three for issue 104: the ciphertext columns arrive beside the plaintext ones.
        /// </summary>
        /// <remarks>
        /// SQL cannot encrypt with AES-GCM under a key that lives in configuration, so the rows that already
        /// exist are encrypted by <c>FieldEncryptionBackfill</c>, which <c>migrate</c> runs straight after this
        /// migration. The next migration, <c>DropPlaintextPiiColumns</c>, refuses to drop a plaintext column
        /// while any row still holds a value the backfill has not encrypted — nothing is left behind and
        /// nothing is dropped with data in it.
        /// <para>
        /// The unique index on the bank account number goes: a column encrypted with a fresh nonce every time
        /// cannot be compared by the database. <c>BankAccountService</c> checks for a duplicate instead.
        /// </para>
        /// </remarks>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_agency_bank_accounts_agency_bank_number",
                schema: "payments",
                table: "agency_bank_accounts");

            migrationBuilder.AddColumn<byte[]>(
                name: "expires_on_encrypted",
                schema: "supplier",
                table: "passenger_documents",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "passport_expiry_encrypted",
                schema: "orders",
                table: "order_travellers",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "account_number_encrypted",
                schema: "payments",
                table: "agency_bank_accounts",
                type: "bytea",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "expires_on_encrypted",
                schema: "supplier",
                table: "passenger_documents");

            migrationBuilder.DropColumn(
                name: "passport_expiry_encrypted",
                schema: "orders",
                table: "order_travellers");

            migrationBuilder.DropColumn(
                name: "account_number_encrypted",
                schema: "payments",
                table: "agency_bank_accounts");

            migrationBuilder.CreateIndex(
                name: "ix_agency_bank_accounts_agency_bank_number",
                schema: "payments",
                table: "agency_bank_accounts",
                columns: new[] { "agency_id", "bank_code", "account_number" },
                unique: true);
        }
    }
}
