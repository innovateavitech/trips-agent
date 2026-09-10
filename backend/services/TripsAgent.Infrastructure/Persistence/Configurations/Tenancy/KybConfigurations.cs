using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TripsAgent.Domain.Tenancy;
using TripsAgent.Domain.Tenancy.Kyb;

namespace TripsAgent.Infrastructure.Persistence.Configurations.Tenancy;

public sealed class KybSubmissionConfiguration : IEntityTypeConfiguration<KybSubmission>
{
    public void Configure(EntityTypeBuilder<KybSubmission> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("kyb_submissions", AgencyConfiguration.Schema);
        builder.HasKey(submission => submission.Id);
        builder.Property(submission => submission.Id).ValueGeneratedNever();

        builder.Property(submission => submission.Status)
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(submission => submission.RejectionReason).HasMaxLength(2000);

        builder.HasOne<Agency>()
            .WithMany()
            .HasForeignKey(submission => submission.AgencyId)
            .OnDelete(DeleteBehavior.Cascade);

        // Leading with agency_id: every tenant-scoped read filters on it first.
        builder.HasIndex(submission => new { submission.AgencyId, submission.Status })
            .HasDatabaseName("ix_kyb_submissions_agency_id_status");

        // The admin queue reads submissions awaiting a decision, oldest first, across agencies.
        builder.HasIndex(submission => submission.SubmittedAt)
            .HasDatabaseName("ix_kyb_submissions_submitted_at");
    }
}

public sealed class KybDocumentConfiguration : IEntityTypeConfiguration<KybDocument>
{
    public void Configure(EntityTypeBuilder<KybDocument> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("kyb_documents", AgencyConfiguration.Schema);
        builder.HasKey(document => document.Id);
        builder.Property(document => document.Id).ValueGeneratedNever();

        builder.Property(document => document.DocumentType)
            .HasConversion<string>()
            .HasMaxLength(40)
            .IsRequired();

        builder.Property(document => document.FileName).HasMaxLength(200).IsRequired();
        builder.Property(document => document.StorageKey).HasMaxLength(512).IsRequired();
        builder.Property(document => document.ContentType).HasMaxLength(100).IsRequired();
        builder.Property(document => document.Checksum).HasMaxLength(64).IsRequired();
        builder.Property(document => document.SizeBytes).IsRequired();

        builder.HasOne<KybSubmission>()
            .WithMany()
            .HasForeignKey(document => document.SubmissionId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<Agency>()
            .WithMany()
            .HasForeignKey(document => document.AgencyId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(document => new { document.AgencyId, document.SubmissionId })
            .HasDatabaseName("ix_kyb_documents_agency_id_submission_id");

        // One object per key; a duplicate would mean two rows owning the same bytes, and deleting
        // one would break the other.
        builder.HasIndex(document => document.StorageKey)
            .IsUnique()
            .HasDatabaseName("ix_kyb_documents_storage_key");
    }
}
