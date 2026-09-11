using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TripsAgent.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDocumentRendering : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_assets_ready_only_when_clean",
                schema: "platform",
                table: "assets");

            migrationBuilder.AddColumn<Guid[]>(
                name: "attachment_asset_ids",
                schema: "notifications",
                table: "notifications",
                type: "uuid[]",
                nullable: false,
                defaultValue: new Guid[0]);

            migrationBuilder.AddColumn<Guid>(
                name: "asset_id",
                schema: "documents",
                table: "generated_documents",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "checksum",
                schema: "documents",
                table: "generated_documents",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "email_notification_id",
                schema: "documents",
                table: "generated_documents",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "issue_number",
                schema: "documents",
                table: "generated_documents",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<string>(
                name: "last_render_error",
                schema: "documents",
                table: "generated_documents",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "order_id",
                schema: "documents",
                table: "generated_documents",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "order_line_id",
                schema: "documents",
                table: "generated_documents",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "recipient_email",
                schema: "documents",
                table: "generated_documents",
                type: "character varying(320)",
                maxLength: 320,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "recipient_name",
                schema: "documents",
                table: "generated_documents",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "render_attempts",
                schema: "documents",
                table: "generated_documents",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "rendered_at",
                schema: "documents",
                table: "generated_documents",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "size_bytes",
                schema: "documents",
                table: "generated_documents",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "status",
                schema: "documents",
                table: "generated_documents",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Pending");

            migrationBuilder.AddColumn<Guid>(
                name: "supersedes_document_id",
                schema: "documents",
                table: "generated_documents",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "template_key",
                schema: "documents",
                table: "generated_documents",
                type: "character varying(60)",
                maxLength: 60,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "template_version",
                schema: "documents",
                table: "generated_documents",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_generated_documents_agency_id_order_id",
                schema: "documents",
                table: "generated_documents",
                columns: new[] { "agency_id", "order_id" });

            migrationBuilder.CreateIndex(
                name: "ix_generated_documents_asset_id",
                schema: "documents",
                table: "generated_documents",
                column: "asset_id");

            migrationBuilder.CreateIndex(
                name: "ix_generated_documents_first_issue",
                schema: "documents",
                table: "generated_documents",
                columns: new[] { "order_id", "order_line_id", "document_type" },
                unique: true,
                filter: "issue_number = 1 AND order_id IS NOT NULL")
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "ix_generated_documents_order_line_id",
                schema: "documents",
                table: "generated_documents",
                column: "order_line_id");

            migrationBuilder.CreateIndex(
                name: "ix_generated_documents_supersedes_document_id",
                schema: "documents",
                table: "generated_documents",
                column: "supersedes_document_id",
                unique: true,
                filter: "supersedes_document_id IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_generated_documents_checksum_shape",
                schema: "documents",
                table: "generated_documents",
                sql: "checksum IS NULL OR checksum ~ '^[0-9a-f]{64}$'");

            migrationBuilder.AddCheckConstraint(
                name: "ck_generated_documents_issue_chain",
                schema: "documents",
                table: "generated_documents",
                sql: "issue_number >= 1 AND (issue_number = 1) = (supersedes_document_id IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_generated_documents_line_fits_type",
                schema: "documents",
                table: "generated_documents",
                sql: "(document_type = 'Voucher' AND (order_id IS NULL OR order_line_id IS NOT NULL)) OR (document_type <> 'Voucher' AND order_line_id IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_generated_documents_ready_has_file",
                schema: "documents",
                table: "generated_documents",
                sql: "(status = 'Ready') = (asset_id IS NOT NULL AND checksum IS NOT NULL AND COALESCE(size_bytes, 0) > 0 AND rendered_at IS NOT NULL AND template_key IS NOT NULL AND template_version IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_generated_documents_render_attempts",
                schema: "documents",
                table: "generated_documents",
                sql: "render_attempts >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "ck_generated_documents_status",
                schema: "documents",
                table: "generated_documents",
                sql: "status IN ('Pending', 'Ready', 'Failed')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_assets_only_generated_documents_skip_the_scan",
                schema: "platform",
                table: "assets",
                sql: "(purpose = 'GeneratedDocument') = (scan_status = 'NotRequired')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_assets_ready_only_when_clean",
                schema: "platform",
                table: "assets",
                sql: "status <> 'Ready' OR scan_status = 'Clean' OR (scan_status = 'NotRequired' AND purpose = 'GeneratedDocument')");

            migrationBuilder.AddForeignKey(
                name: "fk_generated_documents_assets_asset_id",
                schema: "documents",
                table: "generated_documents",
                column: "asset_id",
                principalSchema: "platform",
                principalTable: "assets",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_generated_documents_generated_documents_supersedes_document",
                schema: "documents",
                table: "generated_documents",
                column: "supersedes_document_id",
                principalSchema: "documents",
                principalTable: "generated_documents",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_generated_documents_order_lines_order_line_id",
                schema: "documents",
                table: "generated_documents",
                column: "order_line_id",
                principalSchema: "orders",
                principalTable: "order_lines",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_generated_documents_orders_order_id",
                schema: "documents",
                table: "generated_documents",
                column: "order_id",
                principalSchema: "orders",
                principalTable: "orders",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            // ---------------------------------------------------- an issued document is never mutated
            //
            // Hand-written: EF cannot express a trigger, and `ef migrations add` never regenerates this
            // block — carry it across by hand if this migration is ever rebuilt.
            //
            // AddDocumentNumbering's trigger already refuses to change an issued number or delete the
            // row. #46 widens it. What a document is and who it is for never change: a correction is
            // a reissue, a new row that supersedes this one. And once its PDF is stored, the file is
            // final — a reprint must be the same bytes, so nothing may point the row at other ones.
            // A trigger rather than a REVOKE, because a REVOKE does not bind the owner or a superuser,
            // and those are exactly who runs a hand-written fix.
            migrationBuilder.Sql(IssuedDocumentGuard);
        }

        /// <summary>The trigger function from #46: numbers final, identity final, file final once rendered.</summary>
        internal const string IssuedDocumentGuard = """
            CREATE OR REPLACE FUNCTION documents.reject_issued_number_change()
            RETURNS trigger
            LANGUAGE plpgsql
            AS $$
            BEGIN
                IF TG_OP = 'DELETE' THEN
                    RAISE EXCEPTION
                        'generated_documents: document % is issued and cannot be deleted', OLD.document_number
                        USING ERRCODE = 'restrict_violation',
                              HINT = 'Deleting an issued document leaves a gap in the numbering. Issue a credit note or a new version instead.';
                END IF;

                IF NEW.agency_id       IS DISTINCT FROM OLD.agency_id
                OR NEW.document_type   IS DISTINCT FROM OLD.document_type
                OR NEW.document_number IS DISTINCT FROM OLD.document_number
                OR NEW.sequence_year   IS DISTINCT FROM OLD.sequence_year
                OR NEW.sequence_number IS DISTINCT FROM OLD.sequence_number
                OR NEW.issued_at       IS DISTINCT FROM OLD.issued_at THEN
                    RAISE EXCEPTION
                        'generated_documents: the number of issued document % cannot change', OLD.document_number
                        USING ERRCODE = 'restrict_violation',
                              HINT = 'An issued number is final. Issue a new document instead.';
                END IF;

                IF NEW.order_id               IS DISTINCT FROM OLD.order_id
                OR NEW.order_line_id          IS DISTINCT FROM OLD.order_line_id
                OR NEW.issue_number           IS DISTINCT FROM OLD.issue_number
                OR NEW.supersedes_document_id IS DISTINCT FROM OLD.supersedes_document_id
                OR NEW.recipient_name         IS DISTINCT FROM OLD.recipient_name
                OR NEW.recipient_email        IS DISTINCT FROM OLD.recipient_email THEN
                    RAISE EXCEPTION
                        'generated_documents: issued document % cannot change what it is or who it is for', OLD.document_number
                        USING ERRCODE = 'restrict_violation',
                              HINT = 'Reissue it: a new document with a new number supersedes this one, which stays as it was.';
                END IF;

                IF OLD.asset_id IS NOT NULL
                AND (NEW.asset_id         IS DISTINCT FROM OLD.asset_id
                  OR NEW.checksum         IS DISTINCT FROM OLD.checksum
                  OR NEW.size_bytes       IS DISTINCT FROM OLD.size_bytes
                  OR NEW.template_key     IS DISTINCT FROM OLD.template_key
                  OR NEW.template_version IS DISTINCT FROM OLD.template_version
                  OR NEW.rendered_at      IS DISTINCT FROM OLD.rendered_at
                  OR NEW.status           IS DISTINCT FROM OLD.status) THEN
                    RAISE EXCEPTION
                        'generated_documents: the file of issued document % is final', OLD.document_number
                        USING ERRCODE = 'restrict_violation',
                              HINT = 'A reprint must be byte-identical to what was issued. Reissue it instead.';
                END IF;

                RETURN NEW;
            END;
            $$;
            """;

        /// <summary>The trigger function as AddDocumentNumbering left it, for Down.</summary>
        private const string NumberOnlyGuard = """
            CREATE OR REPLACE FUNCTION documents.reject_issued_number_change()
            RETURNS trigger
            LANGUAGE plpgsql
            AS $$
            BEGIN
                IF TG_OP = 'DELETE' THEN
                    RAISE EXCEPTION
                        'generated_documents: document % is issued and cannot be deleted', OLD.document_number
                        USING ERRCODE = 'restrict_violation',
                              HINT = 'Deleting an issued document leaves a gap in the numbering. Issue a credit note or a new version instead.';
                END IF;

                IF NEW.agency_id       IS DISTINCT FROM OLD.agency_id
                OR NEW.document_type   IS DISTINCT FROM OLD.document_type
                OR NEW.document_number IS DISTINCT FROM OLD.document_number
                OR NEW.sequence_year   IS DISTINCT FROM OLD.sequence_year
                OR NEW.sequence_number IS DISTINCT FROM OLD.sequence_number
                OR NEW.issued_at       IS DISTINCT FROM OLD.issued_at THEN
                    RAISE EXCEPTION
                        'generated_documents: the number of issued document % cannot change', OLD.document_number
                        USING ERRCODE = 'restrict_violation',
                              HINT = 'An issued number is final. Issue a new document instead.';
                END IF;

                RETURN NEW;
            END;
            $$;
            """;

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // First, while the columns the #46 function names still exist.
            migrationBuilder.Sql(NumberOnlyGuard);

            migrationBuilder.DropForeignKey(
                name: "fk_generated_documents_assets_asset_id",
                schema: "documents",
                table: "generated_documents");

            migrationBuilder.DropForeignKey(
                name: "fk_generated_documents_generated_documents_supersedes_document",
                schema: "documents",
                table: "generated_documents");

            migrationBuilder.DropForeignKey(
                name: "fk_generated_documents_order_lines_order_line_id",
                schema: "documents",
                table: "generated_documents");

            migrationBuilder.DropForeignKey(
                name: "fk_generated_documents_orders_order_id",
                schema: "documents",
                table: "generated_documents");

            migrationBuilder.DropIndex(
                name: "ix_generated_documents_agency_id_order_id",
                schema: "documents",
                table: "generated_documents");

            migrationBuilder.DropIndex(
                name: "ix_generated_documents_asset_id",
                schema: "documents",
                table: "generated_documents");

            migrationBuilder.DropIndex(
                name: "ix_generated_documents_first_issue",
                schema: "documents",
                table: "generated_documents");

            migrationBuilder.DropIndex(
                name: "ix_generated_documents_order_line_id",
                schema: "documents",
                table: "generated_documents");

            migrationBuilder.DropIndex(
                name: "ix_generated_documents_supersedes_document_id",
                schema: "documents",
                table: "generated_documents");

            migrationBuilder.DropCheckConstraint(
                name: "ck_generated_documents_checksum_shape",
                schema: "documents",
                table: "generated_documents");

            migrationBuilder.DropCheckConstraint(
                name: "ck_generated_documents_issue_chain",
                schema: "documents",
                table: "generated_documents");

            migrationBuilder.DropCheckConstraint(
                name: "ck_generated_documents_line_fits_type",
                schema: "documents",
                table: "generated_documents");

            migrationBuilder.DropCheckConstraint(
                name: "ck_generated_documents_ready_has_file",
                schema: "documents",
                table: "generated_documents");

            migrationBuilder.DropCheckConstraint(
                name: "ck_generated_documents_render_attempts",
                schema: "documents",
                table: "generated_documents");

            migrationBuilder.DropCheckConstraint(
                name: "ck_generated_documents_status",
                schema: "documents",
                table: "generated_documents");

            migrationBuilder.DropCheckConstraint(
                name: "ck_assets_only_generated_documents_skip_the_scan",
                schema: "platform",
                table: "assets");

            migrationBuilder.DropCheckConstraint(
                name: "ck_assets_ready_only_when_clean",
                schema: "platform",
                table: "assets");

            migrationBuilder.DropColumn(
                name: "attachment_asset_ids",
                schema: "notifications",
                table: "notifications");

            migrationBuilder.DropColumn(
                name: "asset_id",
                schema: "documents",
                table: "generated_documents");

            migrationBuilder.DropColumn(
                name: "checksum",
                schema: "documents",
                table: "generated_documents");

            migrationBuilder.DropColumn(
                name: "email_notification_id",
                schema: "documents",
                table: "generated_documents");

            migrationBuilder.DropColumn(
                name: "issue_number",
                schema: "documents",
                table: "generated_documents");

            migrationBuilder.DropColumn(
                name: "last_render_error",
                schema: "documents",
                table: "generated_documents");

            migrationBuilder.DropColumn(
                name: "order_id",
                schema: "documents",
                table: "generated_documents");

            migrationBuilder.DropColumn(
                name: "order_line_id",
                schema: "documents",
                table: "generated_documents");

            migrationBuilder.DropColumn(
                name: "recipient_email",
                schema: "documents",
                table: "generated_documents");

            migrationBuilder.DropColumn(
                name: "recipient_name",
                schema: "documents",
                table: "generated_documents");

            migrationBuilder.DropColumn(
                name: "render_attempts",
                schema: "documents",
                table: "generated_documents");

            migrationBuilder.DropColumn(
                name: "rendered_at",
                schema: "documents",
                table: "generated_documents");

            migrationBuilder.DropColumn(
                name: "size_bytes",
                schema: "documents",
                table: "generated_documents");

            migrationBuilder.DropColumn(
                name: "status",
                schema: "documents",
                table: "generated_documents");

            migrationBuilder.DropColumn(
                name: "supersedes_document_id",
                schema: "documents",
                table: "generated_documents");

            migrationBuilder.DropColumn(
                name: "template_key",
                schema: "documents",
                table: "generated_documents");

            migrationBuilder.DropColumn(
                name: "template_version",
                schema: "documents",
                table: "generated_documents");

            migrationBuilder.AddCheckConstraint(
                name: "ck_assets_ready_only_when_clean",
                schema: "platform",
                table: "assets",
                sql: "status <> 'Ready' OR scan_status = 'Clean'");
        }
    }
}
