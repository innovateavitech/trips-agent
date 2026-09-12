using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TripsAgent.Domain.Assets;
using TripsAgent.Domain.Tenancy;

namespace TripsAgent.Infrastructure.Persistence.Configurations.Platform;

/// <summary>
/// <c>platform.assets</c> — plan §2.14 puts the asset tables with the platform plumbing, because
/// every module stores files and none of them owns the pipeline.
/// </summary>
/// <remarks>
/// Tenant-scoped all the same: each row has an <c>agency_id</c>, the EF filter, and a row-level
/// security policy added in the migration that creates the table (ADR-0006).
/// </remarks>
public sealed class AssetConfiguration : IEntityTypeConfiguration<Asset>
{
    public void Configure(EntityTypeBuilder<Asset> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("assets", Schemas.Platform, table =>
        {
            // The product rule, repeated where a hand-written INSERT cannot dodge it.
            table.HasCheckConstraint(
                "ck_assets_size_bytes",
                $"size_bytes >= 0 AND size_bytes <= {AssetRules.AbsoluteMaxSizeBytes.ToString(CultureInfo.InvariantCulture)}");

            // Issue #18, held by the database as well as by AssetDelivery: a row cannot be Ready
            // unless its scan came back Clean, however it was written — or unless it is a document
            // the platform rendered itself (#46), which had nothing to scan.
            table.HasCheckConstraint(
                "ck_assets_ready_only_when_clean",
                "status <> 'Ready' OR scan_status = 'Clean' "
                + "OR (scan_status = 'NotRequired' AND purpose = 'GeneratedDocument')");

            // The exception above is only safe while the two go together, both ways: a generated
            // document is never scanned, and nothing else may ever claim it did not need to be.
            table.HasCheckConstraint(
                "ck_assets_only_generated_documents_skip_the_scan",
                "(purpose = 'GeneratedDocument') = (scan_status = 'NotRequired')");

            table.HasCheckConstraint(
                "ck_assets_dimensions",
                "(width IS NULL OR width > 0) AND (height IS NULL OR height > 0)");
        });

        builder.HasKey(asset => asset.Id);
        builder.Property(asset => asset.Id).ValueGeneratedNever();

        builder.Property(asset => asset.Purpose).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(asset => asset.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(asset => asset.ScanStatus).HasConversion<string>().HasMaxLength(20).IsRequired();

        builder.Property(asset => asset.FileName).HasMaxLength(AssetRules.MaxFileNameLength).IsRequired();
        builder.Property(asset => asset.StorageKey).HasMaxLength(512).IsRequired();
        builder.Property(asset => asset.ContentType).HasMaxLength(100);
        builder.Property(asset => asset.Checksum).HasMaxLength(64);
        builder.Property(asset => asset.ScanSignature).HasMaxLength(AssetRules.MaxScanSignatureLength);
        builder.Property(asset => asset.FailureReason).HasMaxLength(500);

        // The pipeline's claim is only atomic if two saves of the same version cannot both win.
        builder.Property(asset => asset.Version).IsConcurrencyToken().IsRequired();

        // Computed, not stored: the columns it reads are the truth.
        builder.Ignore(asset => asset.IsServable);
        builder.Ignore(asset => asset.IsImage);

        builder.HasOne<Agency>()
            .WithMany()
            .HasForeignKey(asset => asset.AgencyId)
            .OnDelete(DeleteBehavior.Cascade);

        // Leading with agency_id: every tenant-scoped read filters on it first.
        builder.HasIndex(asset => new { asset.AgencyId, asset.Status })
            .HasDatabaseName("ix_assets_agency_id_status");

        // The sweep reads across agencies by state and age.
        builder.HasIndex(asset => new { asset.Status, asset.UpdatedAt })
            .HasDatabaseName("ix_assets_status_updated_at");

        // One object per key; a duplicate would mean two rows owning the same bytes.
        builder.HasIndex(asset => asset.StorageKey)
            .IsUnique()
            .HasDatabaseName("ix_assets_storage_key");
    }
}

/// <summary><c>platform.asset_variants</c> — the WebP renditions of an image asset.</summary>
public sealed class AssetVariantConfiguration : IEntityTypeConfiguration<AssetVariant>
{
    public void Configure(EntityTypeBuilder<AssetVariant> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("asset_variants", Schemas.Platform, table =>
        {
            table.HasCheckConstraint(
                "ck_asset_variants_positive",
                "size_bytes > 0 AND width > 0 AND height > 0");
        });

        builder.HasKey(variant => variant.Id);
        builder.Property(variant => variant.Id).ValueGeneratedNever();

        builder.Property(variant => variant.Kind).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(variant => variant.StorageKey).HasMaxLength(512).IsRequired();
        builder.Property(variant => variant.ContentType).HasMaxLength(100).IsRequired();

        builder.HasOne<Asset>()
            .WithMany()
            .HasForeignKey(variant => variant.AssetId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<Agency>()
            .WithMany()
            .HasForeignKey(variant => variant.AgencyId)
            .OnDelete(DeleteBehavior.Cascade);

        // One rendition of each kind per asset. Leading with agency_id for the tenant filter.
        builder.HasIndex(variant => new { variant.AgencyId, variant.AssetId, variant.Kind })
            .IsUnique()
            .HasDatabaseName("ix_asset_variants_agency_id_asset_id_kind");

        builder.HasIndex(variant => variant.AssetId)
            .HasDatabaseName("ix_asset_variants_asset_id");

        builder.HasIndex(variant => variant.StorageKey)
            .IsUnique()
            .HasDatabaseName("ix_asset_variants_storage_key");
    }
}
