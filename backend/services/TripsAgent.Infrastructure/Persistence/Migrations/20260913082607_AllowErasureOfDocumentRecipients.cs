using Microsoft.EntityFrameworkCore.Migrations;
using TripsAgent.Domain.Platform;

#nullable disable

namespace TripsAgent.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Lets an erasure take a person's name off an issued document, and nothing else (issue 106).
    /// </summary>
    /// <remarks>
    /// <para>
    /// An issued document's recipient has been final since #46, for good reasons: the traveller holds what
    /// was sent, and a correction is a new document. But those reasons assume the person still exists. When
    /// they ask to be erased, the name on the row is what a search finds them by, and what a reissue would
    /// print again — so this permits exactly one transition, to the erasure placeholders, and goes on
    /// refusing every other change to the same columns.
    /// </para>
    /// <para>
    /// The <b>file</b> that was issued is untouched: it is the tax record as it was sent, and the ADR
    /// (docs/adr/0009-ndpa-erasure-as-anonymisation.md) names it as what the erasure deliberately leaves.
    /// </para>
    /// </remarks>
    public partial class AllowErasureOfDocumentRecipients : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);

        migrationBuilder.Sql(GuardAllowingErasure);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);

        migrationBuilder.Sql(AddDocumentRendering.IssuedDocumentGuard);
    }

    /// <summary>
    /// The guard from #46, with one exception: the recipient may become the erasure placeholders.
    /// </summary>
    /// <remarks>
    /// The placeholders are interpolated from <see cref="ErasureDefaults"/>, which is also what the
    /// application writes, so the two cannot drift apart.
    /// </remarks>
    private static readonly string GuardAllowingErasure =
        $"""
         CREATE OR REPLACE FUNCTION documents.reject_issued_number_change()
         RETURNS trigger
         LANGUAGE plpgsql
         AS $$
         DECLARE
             erasing boolean;
         BEGIN
             IF TG_OP = 'DELETE' THEN
                 RAISE EXCEPTION
                     'generated_documents: document % is issued and cannot be deleted', OLD.document_number
                     USING ERRCODE = 'restrict_violation',
                           HINT = 'Deleting an issued document leaves a gap in the numbering. Issue a credit note or a new version instead.';
             END IF;

             -- The one change an erasure is allowed to make: the recipient becomes the placeholder, the
             -- address goes, and nothing else on the row moves.
             erasing := NEW.recipient_name = '{ErasureDefaults.ErasedName}' AND NEW.recipient_email IS NULL;

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
             OR (NOT erasing AND (NEW.recipient_name  IS DISTINCT FROM OLD.recipient_name
                               OR NEW.recipient_email IS DISTINCT FROM OLD.recipient_email)) THEN
                 RAISE EXCEPTION
                     'generated_documents: issued document % cannot change what it is or who it is for', OLD.document_number
                     USING ERRCODE = 'restrict_violation',
                           HINT = 'Reissue it: a new document with a new number supersedes this one, which stays as it was. The one exception is an NDPA erasure (issue 106).';
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
}
}
