using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TripsAgent.Domain.Tenancy;

namespace TripsAgent.Infrastructure.Persistence.Configurations.Tenancy;

/// <summary>Maps <see cref="AgencySettings"/> to <c>tenancy.agency_settings</c>.</summary>
public sealed class AgencySettingsConfiguration : IEntityTypeConfiguration<AgencySettings>
{
    public void Configure(EntityTypeBuilder<AgencySettings> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("agency_settings", AgencyConfiguration.Schema);

        builder.HasKey(settings => settings.Id);

        builder.Property(settings => settings.Id).ValueGeneratedNever();

        // One row per agency. The unique index is what makes it 1:1 rather than 1:many.
        builder.HasIndex(settings => settings.AgencyId)
            .IsUnique()
            .HasDatabaseName("ix_agency_settings_agency_id");

        builder.HasOne<Agency>()
            .WithOne()
            .HasForeignKey<AgencySettings>(settings => settings.AgencyId)
            .OnDelete(DeleteBehavior.Cascade);

        // A list of ISO codes is exactly what jsonb is for, and it saves a join table for
        // something no query ever filters on.
        builder.Property(settings => settings.SupportedCurrencies)
            .HasColumnType("jsonb")
            .IsRequired();

        builder.Property(settings => settings.InvoicePrefix)
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(settings => settings.BookingReferencePrefix)
            .HasMaxLength(10)
            .IsRequired();

        builder.Property(settings => settings.NotificationPreferences)
            .HasColumnType("jsonb")
            .IsRequired();
    }
}
