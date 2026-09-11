using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TripsAgent.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDocumentNumbering : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "documents");

            migrationBuilder.CreateTable(
                name: "document_number_formats",
                schema: "documents",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    agency_id = table.Column<Guid>(type: "uuid", nullable: false),
                    document_type = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    prefix = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    padding = table.Column<int>(type: "integer", nullable: false),
                    include_year = table.Column<bool>(type: "boolean", nullable: false),
                    resets_yearly = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_document_number_formats", x => x.id);
                    table.CheckConstraint("ck_document_number_formats_padding_range", "padding BETWEEN 1 AND 10");
                    table.CheckConstraint("ck_document_number_formats_prefix_shape", "prefix ~ '^[A-Z0-9]([A-Z0-9/-]*[A-Z0-9])?$'");
                    table.CheckConstraint("ck_document_number_formats_reset_needs_year", "include_year OR NOT resets_yearly");
                    table.ForeignKey(
                        name: "fk_document_number_formats_agencies_agency_id",
                        column: x => x.agency_id,
                        principalSchema: "tenancy",
                        principalTable: "agencies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "document_number_sequences",
                schema: "documents",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    agency_id = table.Column<Guid>(type: "uuid", nullable: false),
                    document_type = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    year = table.Column<int>(type: "integer", nullable: false),
                    last_value = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_document_number_sequences", x => x.id);
                    table.CheckConstraint("ck_document_number_sequences_last_value_positive", "last_value >= 1");
                    table.CheckConstraint("ck_document_number_sequences_year", "year = 0 OR year BETWEEN 2000 AND 9999");
                    table.ForeignKey(
                        name: "fk_document_number_sequences_agencies_agency_id",
                        column: x => x.agency_id,
                        principalSchema: "tenancy",
                        principalTable: "agencies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "generated_documents",
                schema: "documents",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    agency_id = table.Column<Guid>(type: "uuid", nullable: false),
                    document_type = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    document_number = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    sequence_year = table.Column<int>(type: "integer", nullable: false),
                    sequence_number = table.Column<long>(type: "bigint", nullable: false),
                    issued_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_generated_documents", x => x.id);
                    table.CheckConstraint("ck_generated_documents_sequence_number_positive", "sequence_number >= 1");
                    table.ForeignKey(
                        name: "fk_generated_documents_agencies_agency_id",
                        column: x => x.agency_id,
                        principalSchema: "tenancy",
                        principalTable: "agencies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_document_number_formats_agency_id_document_type",
                schema: "documents",
                table: "document_number_formats",
                columns: new[] { "agency_id", "document_type" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_document_number_sequences_agency_id_document_type_year",
                schema: "documents",
                table: "document_number_sequences",
                columns: new[] { "agency_id", "document_type", "year" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_generated_documents_agency_id_document_type_document_number",
                schema: "documents",
                table: "generated_documents",
                columns: new[] { "agency_id", "document_type", "document_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_generated_documents_agency_id_document_type_sequence",
                schema: "documents",
                table: "generated_documents",
                columns: new[] { "agency_id", "document_type", "sequence_year", "sequence_number" },
                unique: true);

            // ------------------------------------------------------------- issued numbers are final
            //
            // Changing an issued document's number, or deleting the row, would leave a hole in a
            // sequence the tax authority expects to be complete. A correction is a new document with
            // a new number. A trigger rather than a REVOKE, because a REVOKE does not bind the
            // migration role or a superuser, and those are exactly who runs a hand-written fix.
            migrationBuilder.Sql("""
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
                """);

            migrationBuilder.Sql("""
                CREATE TRIGGER generated_documents_number_final_trg
                    BEFORE UPDATE OR DELETE ON documents.generated_documents
                    FOR EACH ROW
                    EXECUTE FUNCTION documents.reject_issued_number_change();
                """);

            // ------------------------------------------------------------ counters only step by one
            //
            // The allocator only ever adds one. Anything else is a hand-written UPDATE that either
            // skips numbers (a gap) or winds the counter back (the next document reuses a number
            // already printed). Deleting a counter row would restart it at 1, which is the same
            // thing as winding it back.
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION documents.enforce_sequence_step()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $$
                BEGIN
                    IF TG_OP = 'DELETE' THEN
                        RAISE EXCEPTION
                            'document_number_sequences: counter % % % cannot be deleted',
                            OLD.agency_id, OLD.document_type, OLD.year
                            USING ERRCODE = 'restrict_violation',
                                  HINT = 'Deleting a counter restarts it at 1 and reissues numbers already printed.';
                    END IF;

                    IF NEW.agency_id     IS DISTINCT FROM OLD.agency_id
                    OR NEW.document_type IS DISTINCT FROM OLD.document_type
                    OR NEW.year          IS DISTINCT FROM OLD.year
                    OR NEW.last_value    <> OLD.last_value + 1 THEN
                        RAISE EXCEPTION
                            'document_number_sequences: a counter may only move forward by one (% to %)',
                            OLD.last_value, NEW.last_value
                            USING ERRCODE = 'restrict_violation',
                                  HINT = 'Skipping leaves a gap; going back reissues a number. Numbers are taken only through the allocator.';
                    END IF;

                    RETURN NEW;
                END;
                $$;
                """);

            migrationBuilder.Sql("""
                CREATE TRIGGER document_number_sequences_step_trg
                    BEFORE UPDATE OR DELETE ON documents.document_number_sequences
                    FOR EACH ROW
                    EXECUTE FUNCTION documents.enforce_sequence_step();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The triggers go with their tables; their functions have to be dropped by name.
            migrationBuilder.DropTable(
                name: "document_number_formats",
                schema: "documents");

            migrationBuilder.DropTable(
                name: "document_number_sequences",
                schema: "documents");

            migrationBuilder.DropTable(
                name: "generated_documents",
                schema: "documents");

            migrationBuilder.Sql("DROP FUNCTION IF EXISTS documents.reject_issued_number_change();");
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS documents.enforce_sequence_step();");
            migrationBuilder.Sql("DROP SCHEMA IF EXISTS documents;");
        }
    }
}
