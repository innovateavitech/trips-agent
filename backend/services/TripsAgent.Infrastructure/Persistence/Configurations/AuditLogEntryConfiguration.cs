using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TripsAgent.Domain.Auditing;

namespace TripsAgent.Infrastructure.Persistence.Configurations;

/// <summary>
/// Maps <see cref="AuditLogEntry"/> onto <c>platform.audit_logs</c>.
///
/// The table is range-partitioned by month on <c>occurred_at</c> and is append-only. Neither is
/// expressible in EF's model, so both are applied by raw SQL in the AddPlatformAuditLog
/// migration. What EF does own is here.
/// </summary>
internal sealed class AuditLogEntryConfiguration : IEntityTypeConfiguration<AuditLogEntry>
{
    /// <summary>Every table the plan puts under "audit &amp; operations" lives in this schema.</summary>
    public const string Schema = "platform";

    public void Configure(EntityTypeBuilder<AuditLogEntry> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("audit_logs", Schema);

        // PostgreSQL requires the partition key to be part of every unique constraint, so the
        // key is (id, occurred_at) rather than id alone. Ids are UUIDv7 and therefore already
        // ordered by time, so this costs nothing at insert.
        builder.HasKey(entry => new { entry.Id, entry.OccurredAt });

        builder.Property(entry => entry.Action).HasMaxLength(100).IsRequired();
        builder.Property(entry => entry.EntityType).HasMaxLength(200).IsRequired();
        builder.Property(entry => entry.EntityId).HasMaxLength(200).IsRequired();

        // Stored as the name, not the number. This table outlives the code that wrote it, and a
        // row saying "PlatformAdmin" needs no reference to the enum as it was in 2026.
        builder.Property(entry => entry.ActorType).HasConversion<string>().HasMaxLength(30).IsRequired();

        // Long enough for IPv6 plus a proxied chain.
        builder.Property(entry => entry.ActorIpAddress).HasMaxLength(100);
        builder.Property(entry => entry.CorrelationId).HasMaxLength(100);
        builder.Property(entry => entry.Reason).HasMaxLength(1000);

        builder.Property(entry => entry.BeforeState).HasColumnType("jsonb");
        builder.Property(entry => entry.AfterState).HasColumnType("jsonb");

        // The two questions this table exists to answer, per the acceptance criteria.
        // Newest first in both, because that is the order anyone reads an audit trail.
        builder
            .HasIndex(entry => new { entry.EntityType, entry.EntityId, entry.OccurredAt })
            .IsDescending(false, false, true)
            .HasDatabaseName("ix_audit_logs_entity");

        builder
            .HasIndex(entry => new { entry.ActorUserId, entry.OccurredAt })
            .IsDescending(false, true)
            .HasDatabaseName("ix_audit_logs_actor");

        // And the one the tenant filter needs, so an agency reading its own trail does not scan
        // every other agency's rows to find it.
        builder
            .HasIndex(entry => new { entry.AgencyId, entry.OccurredAt })
            .IsDescending(false, true)
            .HasDatabaseName("ix_audit_logs_agency");
    }
}
