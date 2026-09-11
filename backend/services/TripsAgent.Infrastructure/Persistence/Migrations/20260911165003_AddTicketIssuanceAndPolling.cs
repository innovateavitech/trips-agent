using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TripsAgent.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTicketIssuanceAndPolling : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "escalated_at",
                schema: "supplier",
                table: "supplier_bookings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "fifteen_minute_warning_sent_at",
                schema: "supplier",
                table: "supplier_bookings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "sixty_minute_warning_sent_at",
                schema: "supplier",
                table: "supplier_bookings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "version",
                schema: "supplier",
                table: "supplier_bookings",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "ix_supplier_bookings_status_ticket_time_limit",
                schema: "supplier",
                table: "supplier_bookings",
                columns: new[] { "status", "ticket_time_limit" });

            // ------------------------------------------------------------------------ shape
            //
            // Every IS NOT NULL below is load-bearing: a CHECK constraint PASSES when its expression is
            // NULL, so "a booking awaiting its outcome has a next poll" has to say so out loud.
            migrationBuilder.Sql("""
                ALTER TABLE supplier.supplier_bookings
                    ADD CONSTRAINT ck_supplier_bookings_poll_attempts_not_negative
                        CHECK (poll_attempts >= 0),
                    ADD CONSTRAINT ck_supplier_bookings_version_not_negative
                        CHECK (version >= 0),
                    -- The poller finds bookings by next_poll_at. A booking awaiting its outcome with no
                    -- next poll would never be asked about again — and its money would stay held for ever.
                    ADD CONSTRAINT ck_supplier_bookings_awaiting_outcome_is_polled
                        CHECK (status NOT IN ('Issuing', 'IssueOutcomeUnknown', 'TicketPending')
                            OR next_poll_at IS NOT NULL),
                    -- Nothing reaches these states except through the issue step, which records when it began.
                    ADD CONSTRAINT ck_supplier_bookings_issue_recorded
                        CHECK (status NOT IN ('Issuing', 'IssueOutcomeUnknown', 'TicketPending', 'Ticketed')
                            OR issue_started_at IS NOT NULL);
                """);

            // --------------------------------------------------------------- the evidence is final
            //
            // A poll is the evidence a payment reversal rests on: #43 never reverses without one. A
            // trigger as well as a REVOKE, because a REVOKE does not bind the table's owner, and evidence
            // its owner can quietly edit is not evidence.
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION supplier.reject_status_poll_rewrite()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $$
                BEGIN
                    RAISE EXCEPTION 'supplier.supplier_status_polls is append-only (attempted %)', TG_OP
                        USING ERRCODE = 'restrict_violation',
                              HINT = 'A poll is the evidence a payment reversal rests on. Record a new poll; never change an old one.';
                END;
                $$;

                CREATE TRIGGER supplier_status_polls_append_only_trg
                    BEFORE UPDATE OR DELETE ON supplier.supplier_status_polls
                    FOR EACH ROW
                    EXECUTE FUNCTION supplier.reject_status_poll_rewrite();
                """);

            migrationBuilder.Sql(
                $"REVOKE UPDATE, DELETE ON supplier.supplier_status_polls FROM {AddRowLevelSecurity.ApplicationRole};");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                $"GRANT UPDATE, DELETE ON supplier.supplier_status_polls TO {AddRowLevelSecurity.ApplicationRole};");

            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS supplier_status_polls_append_only_trg ON supplier.supplier_status_polls;
                DROP FUNCTION IF EXISTS supplier.reject_status_poll_rewrite();

                ALTER TABLE supplier.supplier_bookings
                    DROP CONSTRAINT IF EXISTS ck_supplier_bookings_poll_attempts_not_negative,
                    DROP CONSTRAINT IF EXISTS ck_supplier_bookings_version_not_negative,
                    DROP CONSTRAINT IF EXISTS ck_supplier_bookings_awaiting_outcome_is_polled,
                    DROP CONSTRAINT IF EXISTS ck_supplier_bookings_issue_recorded;
                """);

            migrationBuilder.DropIndex(
                name: "ix_supplier_bookings_status_ticket_time_limit",
                schema: "supplier",
                table: "supplier_bookings");

            migrationBuilder.DropColumn(
                name: "escalated_at",
                schema: "supplier",
                table: "supplier_bookings");

            migrationBuilder.DropColumn(
                name: "fifteen_minute_warning_sent_at",
                schema: "supplier",
                table: "supplier_bookings");

            migrationBuilder.DropColumn(
                name: "sixty_minute_warning_sent_at",
                schema: "supplier",
                table: "supplier_bookings");

            migrationBuilder.DropColumn(
                name: "version",
                schema: "supplier",
                table: "supplier_bookings");
        }
    }
}
