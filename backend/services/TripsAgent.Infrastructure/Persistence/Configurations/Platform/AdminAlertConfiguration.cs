using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TripsAgent.Domain.Platform;

namespace TripsAgent.Infrastructure.Persistence.Configurations.Platform;

/// <summary>Maps <see cref="AdminAlert"/> to <c>platform.admin_alerts</c>.</summary>
public sealed class AdminAlertConfiguration : IEntityTypeConfiguration<AdminAlert>
{
    /// <summary>The PostgreSQL schema for audit and back-office operations.</summary>
    public const string Schema = "platform";

    public void Configure(EntityTypeBuilder<AdminAlert> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("admin_alerts", Schema);
        builder.HasKey(alert => alert.Id);
        builder.Property(alert => alert.Id).ValueGeneratedNever();

        builder.Property(alert => alert.Type).HasConversion<string>().HasMaxLength(40).IsRequired();
        builder.Property(alert => alert.Severity).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(alert => alert.Status).HasConversion<string>().HasMaxLength(20).IsRequired();

        builder.Property(alert => alert.EntityType).HasMaxLength(100).IsRequired();
        builder.Property(alert => alert.Message).HasMaxLength(1000).IsRequired();

        // No FK to agencies: an alert outlives the thing it is about, and the queue is a record of
        // what happened as much as a to-do list.
        builder.Property(alert => alert.AgencyId);

        // The widget reads open alerts, most urgent first, then oldest first.
        builder.HasIndex(alert => new { alert.Status, alert.Severity, alert.CreatedAt })
            .HasDatabaseName("ix_admin_alerts_status_severity_created_at");

        builder.HasIndex(alert => new { alert.Type, alert.EntityId })
            .HasDatabaseName("ix_admin_alerts_type_entity_id");
    }
}
