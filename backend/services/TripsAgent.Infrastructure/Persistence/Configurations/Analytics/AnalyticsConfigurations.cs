using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TripsAgent.Domain.Analytics;
using TripsAgent.Domain.Identity;
using TripsAgent.Domain.Orders;
using TripsAgent.Domain.Suppliers;
using TripsAgent.Domain.Tenancy;

namespace TripsAgent.Infrastructure.Persistence.Configurations.Analytics;

/// <summary>Shared constants for the analytics read models.</summary>
public static class AnalyticsSchema
{
    /// <summary>The PostgreSQL schema holding the facts, the aggregates and the report tables.</summary>
    public const string Name = "analytics";
}

/// <summary>
/// <c>analytics.fact_bookings</c>.
/// </summary>
/// <remarks>
/// No foreign key to <c>orders.order_lines</c>, on purpose. A read model that refuses to be
/// rebuilt because a source row it once copied has gone is a read model that can take the whole
/// rollup down; and the retention purge is allowed to remove source rows this table has already
/// summarised. The link is <see cref="BookingFact.OrderLineId"/> and the rollup's own rebuild,
/// not a constraint. The agency reference <i>is</i> a real foreign key: a fact belongs to an
/// agency, and an agency is never deleted.
/// </remarks>
public sealed class BookingFactConfiguration : IEntityTypeConfiguration<BookingFact>
{
    public void Configure(EntityTypeBuilder<BookingFact> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("fact_bookings", AnalyticsSchema.Name);
        builder.HasKey(fact => fact.Id);
        builder.Property(fact => fact.Id).ValueGeneratedNever();

        builder.Property(fact => fact.Currency).HasMaxLength(3).IsFixedLength().IsRequired();

        // By name, like every other enum here: a psql query reads "Confirmed", not "4".
        builder.Property(fact => fact.OrderStatus).HasConversion<string>().HasMaxLength(30).IsRequired();
        builder.Property(fact => fact.FulfilmentStatus).HasConversion<string>().HasMaxLength(30).IsRequired();
        builder.Property(fact => fact.ItemType).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(fact => fact.Channel).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(fact => fact.BuyerType).HasConversion<string>().HasMaxLength(20).IsRequired();

        builder.Ignore(fact => fact.AgentMarginMinor);

        builder.HasOne<Agency>()
            .WithMany()
            .HasForeignKey(fact => fact.AgencyId)
            .OnDelete(DeleteBehavior.Restrict);

        // One fact per line, enforced rather than assumed: a rollup bug that inserted a day twice
        // would otherwise double an agency's revenue silently.
        builder.HasIndex(fact => fact.OrderLineId)
            .IsUnique()
            .HasDatabaseName("ix_fact_bookings_order_line_id");

        // What the agency dashboard reads: my rows, this day range.
        builder.HasIndex(fact => new { fact.AgencyId, fact.BookingDay })
            .HasDatabaseName("ix_fact_bookings_agency_id_booking_day");

        // What a principal's network report reads.
        builder.HasIndex(fact => new { fact.RootAgencyId, fact.BookingDay })
            .HasDatabaseName("ix_fact_bookings_root_agency_id_booking_day");

        // What the rollup itself reads when it deletes a day before rebuilding it.
        builder.HasIndex(fact => fact.BookingDay)
            .HasDatabaseName("ix_fact_bookings_booking_day");
    }
}

/// <summary><c>analytics.agg_agency_daily</c>. One agency's day.</summary>
public sealed class AgencyDailyAggregateConfiguration : IEntityTypeConfiguration<AgencyDailyAggregate>
{
    public void Configure(EntityTypeBuilder<AgencyDailyAggregate> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("agg_agency_daily", AnalyticsSchema.Name);
        builder.HasKey(row => row.Id);
        builder.Property(row => row.Id).ValueGeneratedNever();

        builder.Property(row => row.Currency).HasMaxLength(3).IsFixedLength().IsRequired();

        builder.Ignore(row => row.MarginMinor);

        builder.HasOne<Agency>()
            .WithMany()
            .HasForeignKey(row => row.AgencyId)
            .OnDelete(DeleteBehavior.Restrict);

        // The grain, as a constraint. Two rows for one agency-day-currency would mean the
        // dashboard shows one of them and the report sums both.
        builder.HasIndex(row => new { row.AgencyId, row.Day, row.Currency })
            .IsUnique()
            .HasDatabaseName("ix_agg_agency_daily_agency_id_day_currency");

        builder.HasIndex(row => new { row.RootAgencyId, row.Day })
            .HasDatabaseName("ix_agg_agency_daily_root_agency_id_day");

        builder.HasIndex(row => row.Day)
            .HasDatabaseName("ix_agg_agency_daily_day");
    }
}

/// <summary><c>analytics.agg_platform_daily</c>. Platform-wide; no agency column by design.</summary>
public sealed class PlatformDailyAggregateConfiguration : IEntityTypeConfiguration<PlatformDailyAggregate>
{
    public void Configure(EntityTypeBuilder<PlatformDailyAggregate> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("agg_platform_daily", AnalyticsSchema.Name);
        builder.HasKey(row => row.Id);
        builder.Property(row => row.Id).ValueGeneratedNever();

        builder.Property(row => row.Currency).HasMaxLength(3).IsFixedLength().IsRequired();

        builder.HasIndex(row => new { row.Day, row.Currency })
            .IsUnique()
            .HasDatabaseName("ix_agg_platform_daily_day_currency");
    }
}

/// <summary><c>analytics.agg_supplier_daily</c>. Platform-wide, built from the supplier call log.</summary>
public sealed class SupplierDailyAggregateConfiguration : IEntityTypeConfiguration<SupplierDailyAggregate>
{
    public void Configure(EntityTypeBuilder<SupplierDailyAggregate> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("agg_supplier_daily", AnalyticsSchema.Name);
        builder.HasKey(row => row.Id);
        builder.Property(row => row.Id).ValueGeneratedNever();

        builder.Ignore(row => row.TotalCalls);

        builder.HasOne<Supplier>()
            .WithMany()
            .HasForeignKey(row => row.SupplierId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(row => new { row.Day, row.SupplierId })
            .IsUnique()
            .HasDatabaseName("ix_agg_supplier_daily_day_supplier_id");
    }
}

/// <summary><c>analytics.rollup_runs</c>. The watermark, and the rollup's own history.</summary>
public sealed class RollupRunConfiguration : IEntityTypeConfiguration<RollupRun>
{
    public void Configure(EntityTypeBuilder<RollupRun> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("rollup_runs", AnalyticsSchema.Name);
        builder.HasKey(run => run.Id);
        builder.Property(run => run.Id).ValueGeneratedNever();

        builder.Property(run => run.Kind).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(run => run.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(run => run.ErrorMessage).HasMaxLength(2000);

        // How the next incremental run finds its watermark: the newest successful one.
        builder.HasIndex(run => new { run.Status, run.WatermarkTo })
            .HasDatabaseName("ix_rollup_runs_status_watermark_to");
    }
}

/// <summary><c>analytics.report_definitions</c>. Reference data, seeded by the migration.</summary>
public sealed class ReportDefinitionConfiguration : IEntityTypeConfiguration<ReportDefinition>
{
    public void Configure(EntityTypeBuilder<ReportDefinition> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("report_definitions", AnalyticsSchema.Name);
        builder.HasKey(definition => definition.Id);
        builder.Property(definition => definition.Id).ValueGeneratedNever();

        builder.Property(definition => definition.Code).HasMaxLength(100).IsRequired();
        builder.Property(definition => definition.Name).HasMaxLength(200).IsRequired();
        builder.Property(definition => definition.Description).HasMaxLength(500).IsRequired();
        builder.Property(definition => definition.Scope).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(definition => definition.RequiredPermission).HasMaxLength(100).IsRequired();

        builder.HasIndex(definition => definition.Code)
            .IsUnique()
            .HasDatabaseName("ix_report_definitions_code");
    }
}

/// <summary><c>analytics.report_jobs</c>. Nullable agency: null is a platform run.</summary>
public sealed class ReportJobConfiguration : IEntityTypeConfiguration<ReportJob>
{
    public void Configure(EntityTypeBuilder<ReportJob> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("report_jobs", AnalyticsSchema.Name);
        builder.HasKey(job => job.Id);
        builder.Property(job => job.Id).ValueGeneratedNever();

        builder.Property(job => job.DefinitionCode).HasMaxLength(100).IsRequired();
        builder.Property(job => job.Scope).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(job => job.RunMode).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(job => job.Format).HasConversion<string>().HasMaxLength(10).IsRequired();
        builder.Property(job => job.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(job => job.ScopeDescription).HasMaxLength(500).IsRequired();
        builder.Property(job => job.ResultStorageKey).HasMaxLength(500);
        builder.Property(job => job.ErrorMessage).HasMaxLength(1000);

        builder.Ignore(job => job.HasResult);

        builder.HasOne<Agency>()
            .WithMany()
            .HasForeignKey(job => job.AgencyId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(job => job.RequestedByUserId)
            .OnDelete(DeleteBehavior.Restrict);

        // The console's list: my agency's runs, newest first.
        builder.HasIndex(job => new { job.AgencyId, job.RequestedAt })
            .HasDatabaseName("ix_report_jobs_agency_id_requested_at");

        // What the worker picks up.
        builder.HasIndex(job => new { job.Status, job.RequestedAt })
            .HasDatabaseName("ix_report_jobs_status_requested_at");
    }
}

/// <summary>
/// <c>analytics.report_exports_audit</c>. Append-only; the migration adds the trigger that says so.
/// </summary>
public sealed class ReportExportAuditConfiguration : IEntityTypeConfiguration<ReportExportAudit>
{
    public void Configure(EntityTypeBuilder<ReportExportAudit> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("report_exports_audit", AnalyticsSchema.Name);
        builder.HasKey(entry => entry.Id);
        builder.Property(entry => entry.Id).ValueGeneratedNever();

        builder.Property(entry => entry.DefinitionCode).HasMaxLength(100).IsRequired();
        builder.Property(entry => entry.Scope).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(entry => entry.Format).HasConversion<string>().HasMaxLength(10).IsRequired();
        builder.Property(entry => entry.ActorType).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(entry => entry.ActorIpAddress).HasMaxLength(64);
        builder.Property(entry => entry.ScopeDescription).HasMaxLength(500).IsRequired();
        builder.Property(entry => entry.CorrelationId).HasMaxLength(100);

        builder.HasOne<ReportJob>()
            .WithMany()
            .HasForeignKey(entry => entry.ReportJobId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(entry => entry.ExportedAt)
            .HasDatabaseName("ix_report_exports_audit_exported_at");

        builder.HasIndex(entry => new { entry.AgencyId, entry.ExportedAt })
            .HasDatabaseName("ix_report_exports_audit_agency_id_exported_at");

        builder.HasIndex(entry => new { entry.ActorUserId, entry.ExportedAt })
            .HasDatabaseName("ix_report_exports_audit_actor_user_id_exported_at");
    }
}
