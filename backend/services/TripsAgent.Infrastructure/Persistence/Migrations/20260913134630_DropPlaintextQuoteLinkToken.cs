using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TripsAgent.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DropPlaintextQuoteLinkToken : Migration
    {
        /// <summary>
        /// Step two of two for issue 175: the quote link in clear goes, once every token in it is hashed.
        /// </summary>
        /// <remarks>
        /// Refuses — and changes nothing — while any quote still holds a token <c>QuoteLinkTokenBackfill</c>
        /// has not hashed. <c>migrate</c> runs that backfill between the two migrations; <c>dotnet ef
        /// database update</c> does not, which is exactly the case this guard is for.
        /// </remarks>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.Sql(UnhashedTokensRemainGuard);

            migrationBuilder.DropIndex(
                name: "ix_quotes_public_token",
                schema: "crm",
                table: "quotes");

            migrationBuilder.DropColumn(
                name: "public_token",
                schema: "crm",
                table: "quotes");
        }

        /// <inheritdoc />
        /// <remarks>
        /// The column comes back empty, because a hash cannot be turned back into the token it was made
        /// from. The links customers hold go on working — they are looked up by that hash — but rolling
        /// back the migration before this one as well would drop the hashes, and with them every link.
        /// </remarks>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.AddColumn<string>(
                name: "public_token",
                schema: "crm",
                table: "quotes",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_quotes_public_token",
                schema: "crm",
                table: "quotes",
                column: "public_token",
                unique: true,
                filter: "public_token IS NOT NULL");
        }

        /// <summary>Raises unless every token the plaintext column held has been hashed.</summary>
        internal const string UnhashedTokensRemainGuard = """
            DO $$
            BEGIN
                -- The owner connection normally bypasses row-level security; this makes sure every
                -- agency's rows are counted even where it does not. Local to this transaction.
                PERFORM set_config('app.platform_scope', 'on', true);

                IF EXISTS (SELECT 1 FROM crm.quotes
                            WHERE public_token IS NOT NULL AND public_token_hash IS NULL) THEN
                    RAISE EXCEPTION 'Quote links are still stored in clear'
                        USING ERRCODE = 'object_not_in_prerequisite_state',
                              HINT = 'Run the Api with the migrate command, which hashes them between AddQuoteLinkTokenHash and this migration.';
                END IF;
            END
            $$;
            """;
    }
}
