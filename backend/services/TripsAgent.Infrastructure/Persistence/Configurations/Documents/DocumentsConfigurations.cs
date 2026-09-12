using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TripsAgent.Domain.Assets;
using TripsAgent.Domain.Documents;
using TripsAgent.Domain.Orders;
using TripsAgent.Domain.Tenancy;

namespace TripsAgent.Infrastructure.Persistence.Configurations.Documents;

/// <summary>Shared constants for the documents tables.</summary>
public static class DocumentsSchema
{
    /// <summary>The PostgreSQL schema holding document numbering and issued documents.</summary>
    public const string Name = "documents";

    /// <summary>Longest printed number: a 20-character prefix, the year, and a long counter.</summary>
    public const int DocumentNumberMaxLength = 64;

    /// <summary>Room for the longest <see cref="DocumentType"/> name.</summary>
    public const int DocumentTypeMaxLength = 30;

    /// <summary>Room for a template key such as <c>voucher.group-departure</c>.</summary>
    public const int TemplateKeyMaxLength = 60;
}

/// <summary>Maps <see cref="DocumentNumberFormat"/> to <c>documents.document_number_formats</c>.</summary>
public sealed class DocumentNumberFormatConfiguration : IEntityTypeConfiguration<DocumentNumberFormat>
{
    public void Configure(EntityTypeBuilder<DocumentNumberFormat> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("document_number_formats", DocumentsSchema.Name, table =>
        {
            // The domain rules, repeated in the database so a hand-written UPDATE cannot break them.
            table.HasCheckConstraint(
                "ck_document_number_formats_padding_range",
                $"padding BETWEEN {DocumentNumberFormat.MinPadding} AND {DocumentNumberFormat.MaxPadding}");

            // Restarting every January without the year in the number reprints last year's numbers.
            table.HasCheckConstraint(
                "ck_document_number_formats_reset_needs_year",
                "include_year OR NOT resets_yearly");

            table.HasCheckConstraint(
                "ck_document_number_formats_prefix_shape",
                "prefix ~ '^[A-Z0-9]([A-Z0-9/-]*[A-Z0-9])?$'");
        });

        builder.HasKey(format => format.Id);
        builder.Property(format => format.Id).ValueGeneratedNever();

        builder.Property(format => format.DocumentType)
            .HasConversion<string>()
            .HasMaxLength(DocumentsSchema.DocumentTypeMaxLength)
            .IsRequired();

        builder.Property(format => format.Prefix)
            .HasMaxLength(DocumentNumberFormat.MaxPrefixLength)
            .IsRequired();

        builder.HasOne<Agency>()
            .WithMany()
            .HasForeignKey(format => format.AgencyId)
            .OnDelete(DeleteBehavior.Cascade);

        // One format per agency and document type. Leads with agency_id like every tenant index.
        builder.HasIndex(format => new { format.AgencyId, format.DocumentType })
            .IsUnique()
            .HasDatabaseName("ix_document_number_formats_agency_id_document_type");
    }
}

/// <summary>Maps <see cref="DocumentNumberSequence"/> to <c>documents.document_number_sequences</c>.</summary>
public sealed class DocumentNumberSequenceConfiguration : IEntityTypeConfiguration<DocumentNumberSequence>
{
    public void Configure(EntityTypeBuilder<DocumentNumberSequence> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("document_number_sequences", DocumentsSchema.Name, table =>
        {
            // The row is created at 1 by the first document, so it is never below that.
            table.HasCheckConstraint("ck_document_number_sequences_last_value_positive", "last_value >= 1");

            // A real calendar year, or 0 for a counter that never resets.
            table.HasCheckConstraint(
                "ck_document_number_sequences_year",
                $"year = {DocumentNumberFormat.ContinuousSequenceYear} OR year BETWEEN 2000 AND 9999");
        });

        builder.HasKey(sequence => sequence.Id);
        builder.Property(sequence => sequence.Id).ValueGeneratedNever();

        builder.Property(sequence => sequence.DocumentType)
            .HasConversion<string>()
            .HasMaxLength(DocumentsSchema.DocumentTypeMaxLength)
            .IsRequired();

        // Restrict, not cascade: a counter is the memory of numbers already on tax documents.
        builder.HasOne<Agency>()
            .WithMany()
            .HasForeignKey(sequence => sequence.AgencyId)
            .OnDelete(DeleteBehavior.Restrict);

        // The allocator's ON CONFLICT target. Without this unique index there is no conflict to
        // hit, every call inserts a fresh row at 1, and every invoice is number 1.
        builder.HasIndex(sequence => new { sequence.AgencyId, sequence.DocumentType, sequence.Year })
            .IsUnique()
            .HasDatabaseName("ix_document_number_sequences_agency_id_document_type_year");
    }
}

/// <summary>Maps <see cref="GeneratedDocument"/> to <c>documents.generated_documents</c>.</summary>
/// <remarks>
/// The migration also installs a trigger that refuses to change an issued document's number or to
/// delete the row — either would leave a gap. See <c>AddDocumentNumbering</c>.
/// </remarks>
public sealed class GeneratedDocumentConfiguration : IEntityTypeConfiguration<GeneratedDocument>
{
    public void Configure(EntityTypeBuilder<GeneratedDocument> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("generated_documents", DocumentsSchema.Name, table =>
        {
            table.HasCheckConstraint("ck_generated_documents_sequence_number_positive", "sequence_number >= 1");

            table.HasCheckConstraint(
                "ck_generated_documents_status",
                "status IN ('Pending', 'Ready', 'Failed')");

            // A first issue replaces nothing, and every later issue replaces exactly one document.
            table.HasCheckConstraint(
                "ck_generated_documents_issue_chain",
                "issue_number >= 1 AND (issue_number = 1) = (supersedes_document_id IS NULL)");

            // Ready means a file exists, and a file exists only once it is ready. Every operand is
            // IS NOT NULL or COALESCE, never a bare comparison: a CHECK passes when its expression
            // is NULL, so one NULL column would otherwise let a Ready row with no file through.
            table.HasCheckConstraint(
                "ck_generated_documents_ready_has_file",
                "(status = 'Ready') = (asset_id IS NOT NULL AND checksum IS NOT NULL AND COALESCE(size_bytes, 0) > 0 "
                + "AND rendered_at IS NOT NULL AND template_key IS NOT NULL AND template_version IS NOT NULL)");

            table.HasCheckConstraint(
                "ck_generated_documents_checksum_shape",
                "checksum IS NULL OR checksum ~ '^[0-9a-f]{64}$'");

            // A voucher issued for an order is for one of its lines; nothing else names a line.
            table.HasCheckConstraint(
                "ck_generated_documents_line_fits_type",
                "(document_type = 'Voucher' AND (order_id IS NULL OR order_line_id IS NOT NULL)) "
                + "OR (document_type <> 'Voucher' AND order_line_id IS NULL)");

            table.HasCheckConstraint("ck_generated_documents_render_attempts", "render_attempts >= 0");
        });

        builder.HasKey(document => document.Id);
        builder.Property(document => document.Id).ValueGeneratedNever();

        builder.Property(document => document.DocumentType)
            .HasConversion<string>()
            .HasMaxLength(DocumentsSchema.DocumentTypeMaxLength)
            .IsRequired();

        builder.Property(document => document.DocumentNumber)
            .HasMaxLength(DocumentsSchema.DocumentNumberMaxLength)
            .IsRequired();

        // Defaults only for rows that existed before #46, which were numbered but never rendered.
        builder.Property(document => document.IssueNumber).HasDefaultValue(1).IsRequired();

        builder.Property(document => document.Status)
            .HasConversion<string>()
            .HasMaxLength(20)
            .HasDefaultValue(DocumentStatus.Pending)
            .IsRequired();

        builder.Property(document => document.RecipientName).HasMaxLength(GeneratedDocument.MaxRecipientNameLength);
        builder.Property(document => document.RecipientEmail).HasMaxLength(GeneratedDocument.MaxRecipientEmailLength);
        builder.Property(document => document.TemplateKey).HasMaxLength(DocumentsSchema.TemplateKeyMaxLength);
        builder.Property(document => document.Checksum).HasMaxLength(64);
        builder.Property(document => document.LastRenderError).HasMaxLength(GeneratedDocument.MaxErrorLength);

        builder.Ignore(document => document.IsReady);
        builder.Ignore(document => document.FileName);

        builder.HasOne<Agency>()
            .WithMany()
            .HasForeignKey(document => document.AgencyId)
            .OnDelete(DeleteBehavior.Restrict);

        // Restrict throughout: an issued document is a tax record, and nothing it points at may be
        // deleted out from under it.
        builder.HasOne<Order>()
            .WithMany()
            .HasForeignKey(document => document.OrderId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<OrderLine>()
            .WithMany()
            .HasForeignKey(document => document.OrderLineId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Asset>()
            .WithMany()
            .HasForeignKey(document => document.AssetId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<GeneratedDocument>()
            .WithMany()
            .HasForeignKey(document => document.SupersedesDocumentId)
            .OnDelete(DeleteBehavior.Restrict);

        // A document is superseded at most once. Two people pressing "reissue" at the same moment
        // get one new issue between them, and the second a conflict — never a fork in the chain.
        builder.HasIndex(document => document.SupersedesDocumentId)
            .IsUnique()
            .HasFilter("supersedes_document_id IS NOT NULL")
            .HasDatabaseName("ix_generated_documents_supersedes_document_id");

        // One first issue per order and type — per line, for a voucher — so asking for an order's
        // documents twice issues nothing new. NULLS NOT DISTINCT, or every invoice (whose line is
        // NULL) would count as different from every other and the guard would guard nothing.
        builder.HasIndex(document => new { document.OrderId, document.OrderLineId, document.DocumentType })
            .IsUnique()
            .AreNullsDistinct(false)
            .HasFilter("issue_number = 1 AND order_id IS NOT NULL")
            .HasDatabaseName("ix_generated_documents_first_issue");

        // An order's documents, for the booking screen. Leads with agency_id like every tenant index.
        builder.HasIndex(document => new { document.AgencyId, document.OrderId })
            .HasDatabaseName("ix_generated_documents_agency_id_order_id");

        // The printed number is unique within an agency and document type. Two agencies may both
        // have an INV-000001; one agency may never have two.
        builder.HasIndex(document => new { document.AgencyId, document.DocumentType, document.DocumentNumber })
            .IsUnique()
            .HasDatabaseName("ix_generated_documents_agency_id_document_type_document_number");

        // The same guarantee on the counter itself, independent of how the number is formatted —
        // a format change can alter the text, but a counter value is used exactly once.
        builder.HasIndex(document => new
        {
            document.AgencyId,
            document.DocumentType,
            document.SequenceYear,
            document.SequenceNumber,
        })
            .IsUnique()
            .HasDatabaseName("ix_generated_documents_agency_id_document_type_sequence");
    }
}
