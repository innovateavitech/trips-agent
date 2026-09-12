using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TripsAgent.Domain.Tenancy;

namespace TripsAgent.Infrastructure.Persistence.Configurations.Tenancy;

/// <summary>Maps <see cref="Agency"/> to <c>tenancy.agencies</c>.</summary>
public sealed class AgencyConfiguration : IEntityTypeConfiguration<Agency>
{
    /// <summary>The PostgreSQL schema holding agencies, their settings and their KYB records.</summary>
    public const string Schema = "tenancy";

    public void Configure(EntityTypeBuilder<Agency> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("agencies", Schema);

        builder.HasKey(agency => agency.Id);

        builder.Property(agency => agency.Id)
            .ValueGeneratedNever();   // UUID v7 assigned by the domain, not by the database

        // The materialised hierarchy path. Written by the trigger installed in this table's
        // migration, so EF reads it and never writes it — otherwise a SaveChanges would clobber
        // whatever the trigger had just computed.
        builder.Property(agency => agency.Path)
            .HasColumnType("ltree")
            .ValueGeneratedOnAddOrUpdate()
            .IsRequired();

        builder.Property(agency => agency.Type)
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(agency => agency.Status)
            .HasConversion<string>()
            .HasMaxLength(30)
            .IsRequired();

        builder.Property(agency => agency.LegalName)
            .HasMaxLength(200)
            .IsRequired();

        builder.Property(agency => agency.TradingName)
            .HasMaxLength(200);

        builder.Property(agency => agency.Slug)
            .HasMaxLength(63)   // fits a DNS label, since a slug can become a subdomain
            .IsRequired();

        builder.Property(agency => agency.CountryCode)
            .HasMaxLength(2)
            .IsFixedLength()
            .IsRequired();

        builder.Property(agency => agency.BaseCurrency)
            .HasMaxLength(3)
            .IsFixedLength()
            .IsRequired();

        builder.Property(agency => agency.Timezone)
            .HasMaxLength(64)
            .IsRequired();

        builder.Property(agency => agency.TaxId)
            .HasMaxLength(64);

        // Same ceiling as the audit log's reason column, so a reason that fits one fits the other
        // and neither is silently truncated on its way to the other.
        builder.Property(agency => agency.StatusReason)
            .HasMaxLength(1000);

        builder.Property(agency => agency.VatRateBasisPoints)
            .IsRequired();

        builder.HasOne<Agency>()
            .WithMany()
            .HasForeignKey(agency => agency.ParentAgencyId)
            // Restrict, not Cascade: deleting a principal must never silently take its
            // sub-agents — and their orders and ledger entries — with it.
            .OnDelete(DeleteBehavior.Restrict);

        // A slug appears in storefront URLs, so it has to be unique platform-wide.
        builder.HasIndex(agency => agency.Slug)
            .IsUnique()
            .HasDatabaseName("ix_agencies_slug");

        builder.HasIndex(agency => agency.ParentAgencyId)
            .HasDatabaseName("ix_agencies_parent_agency_id");

        builder.HasIndex(agency => agency.Status)
            .HasDatabaseName("ix_agencies_status");

        // GIST, not B-tree: it is what makes the ltree containment operators (`<@`, `@>`)
        // index-assisted, which is the entire point of materialising the path.
        builder.HasIndex(agency => agency.Path)
            .HasMethod("gist")
            .HasDatabaseName("ix_agencies_path_gist");
    }
}
