using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TripsAgent.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddQuoteLinkTokenHash : Migration
    {
        /// <summary>
        /// Step one of two for issue 175: the column that keeps the keyed hash of a quote's link.
        /// </summary>
        /// <remarks>
        /// The plaintext column stays until <c>QuoteLinkTokenBackfill</c> has hashed what is in it, which
        /// <c>migrate</c> runs straight after this. Until then a sent quote's link may be in either column,
        /// so the CHECK <c>AddCrm</c> wrote is widened to say exactly that — and the trigger that makes a
        /// sent quote final stops guarding the token, because moving it is the backfill's whole job.
        /// </remarks>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.AddColumn<string>(
                name: "public_token_hash",
                schema: "crm",
                table: "quotes",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_quotes_public_token_hash",
                schema: "crm",
                table: "quotes",
                column: "public_token_hash",
                unique: true,
                filter: "public_token_hash IS NOT NULL");

            // Carried across by hand: EF scaffolds columns and indexes, and knows nothing about the
            // CHECK constraint and the trigger AddCrm wrote against the column being replaced.
            migrationBuilder.Sql(TransitionalSentQuoteInvariants);
        }

        /// <inheritdoc />
        /// <remarks>
        /// Puts the pair back the way <c>AddCrm</c> left them. On a database where quotes have been sent
        /// since, the restored CHECK will refuse: their link is in the column this drops, and there is
        /// nowhere else for it to be.
        /// </remarks>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.Sql(OriginalSentQuoteInvariants);

            migrationBuilder.DropIndex(
                name: "ix_quotes_public_token_hash",
                schema: "crm",
                table: "quotes");

            migrationBuilder.DropColumn(
                name: "public_token_hash",
                schema: "crm",
                table: "quotes");
        }

        /// <summary>The sent-quote rules while a link may be in either column.</summary>
        internal const string TransitionalSentQuoteInvariants = """
            ALTER TABLE crm.quotes DROP CONSTRAINT ck_quotes_sent_has_link;

            -- A draft has no link and has not been sent; anything further along has both. While the two
            -- columns exist the link may be in either: rows written before this carry theirs in clear,
            -- and the backfill moves each one over.
            ALTER TABLE crm.quotes
                ADD CONSTRAINT ck_quotes_sent_has_link
                    CHECK ((status = 'Draft') = (sent_at IS NULL)
                       AND (status = 'Draft') = (public_token IS NULL AND public_token_hash IS NULL));

            -- AddCrm's trigger, less the token: moving a link from one column to the other is what the
            -- backfill does, and it is not a change to what the customer was sent.
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
                OR NEW.sent_at      IS DISTINCT FROM OLD.sent_at
                THEN
                    RAISE EXCEPTION 'quote % was sent at %; what it says is final', OLD.quote_number, OLD.sent_at
                        USING ERRCODE = 'restrict_violation',
                              HINT = 'New terms are a new quote. The customer holds this one as it was sent.';
                END IF;

                RETURN NEW;
            END;
            $$;
            """;

        /// <summary>The pair exactly as <c>AddCrm</c> wrote them, for <c>Down</c>.</summary>
        internal const string OriginalSentQuoteInvariants = """
            ALTER TABLE crm.quotes DROP CONSTRAINT ck_quotes_sent_has_link;

            ALTER TABLE crm.quotes
                ADD CONSTRAINT ck_quotes_sent_has_link
                    CHECK ((status = 'Draft') = (public_token IS NULL)
                       AND (status = 'Draft') = (sent_at IS NULL));

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
            """;
    }
}
