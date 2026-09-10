using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TripsAgent.Domain.Tenancy;

namespace TripsAgent.Infrastructure.Persistence.Configurations.Tenancy;

/// <summary>Maps <see cref="AgencyBranding"/> to <c>tenancy.agency_branding</c>.</summary>
public sealed class AgencyBrandingConfiguration : IEntityTypeConfiguration<AgencyBranding>
{
    public void Configure(EntityTypeBuilder<AgencyBranding> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("agency_branding", AgencyConfiguration.Schema);

        builder.HasKey(branding => branding.Id);

        builder.Property(branding => branding.Id).ValueGeneratedNever();

        builder.HasIndex(branding => branding.AgencyId)
            .IsUnique()
            .HasDatabaseName("ix_agency_branding_agency_id");

        builder.HasOne<Agency>()
            .WithOne()
            .HasForeignKey<AgencyBranding>(branding => branding.AgencyId)
            .OnDelete(DeleteBehavior.Cascade);

        // No FK to assets yet — that table arrives with the upload pipeline in #18. Adding the
        // constraint then is a one-line migration; inventing the table here would collide with it.
        builder.Property(branding => branding.LogoAssetId);

        builder.Property(branding => branding.PrimaryColor)
            .HasMaxLength(7)
            .IsRequired();

        builder.Property(branding => branding.SecondaryColor)
            .HasMaxLength(7);

        builder.Property(branding => branding.FontFamily)
            .HasMaxLength(64)
            .IsRequired();

        builder.Property(branding => branding.ContactAddress)
            .HasMaxLength(500);

        builder.Property(branding => branding.SocialLinks)
            .HasColumnType("jsonb")
            .IsRequired();
    }
}
