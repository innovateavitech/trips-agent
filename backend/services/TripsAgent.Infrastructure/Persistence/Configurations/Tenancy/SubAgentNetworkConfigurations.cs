using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TripsAgent.Domain.Suppliers;
using TripsAgent.Domain.Tenancy;
using TripsAgent.Domain.Tenancy.SubAgents;

namespace TripsAgent.Infrastructure.Persistence.Configurations.Tenancy;

/// <summary>
/// What a sub-agent may sell. Feature F10, issue 63.
/// </summary>
public sealed class SubAgentScopeConfiguration : IEntityTypeConfiguration<SubAgentScope>
{
    public void Configure(EntityTypeBuilder<SubAgentScope> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("sub_agent_scopes", AgencyConfiguration.Schema);
        builder.HasKey(scope => scope.Id);
        builder.Property(scope => scope.Id).ValueGeneratedNever();

        builder.Property(scope => scope.ProductType)
            .HasConversion<string>().HasMaxLength(20).IsRequired();

        builder.HasOne<Agency>()
            .WithMany()
            .HasForeignKey(scope => scope.AgencyId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Agency>()
            .WithMany()
            .HasForeignKey(scope => scope.SubAgencyId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Supplier>()
            .WithMany()
            .HasForeignKey(scope => scope.SupplierId)
            .OnDelete(DeleteBehavior.Restrict);

        // One row per sub-agent, product type and supplier. NULLS NOT DISTINCT because the common
        // scope names no supplier: without it, "Flight, every supplier" could be granted twice and
        // revoking it once would leave the other behind.
        builder.HasIndex(scope => new { scope.SubAgencyId, scope.ProductType, scope.SupplierId })
            .IsUnique()
            .AreNullsDistinct(false)
            .HasDatabaseName("ix_sub_agent_scopes_sub_agency_id_product_type_supplier_id");

        // The principal's own matrix screen reads every scope it granted.
        builder.HasIndex(scope => scope.AgencyId)
            .HasDatabaseName("ix_sub_agent_scopes_agency_id");
    }
}

/// <summary>
/// Permissions a principal has taken away from a sub-agent. Feature F10, issue 63.
/// </summary>
public sealed class PermissionOverrideConfiguration : IEntityTypeConfiguration<PermissionOverride>
{
    public void Configure(EntityTypeBuilder<PermissionOverride> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("permission_overrides", AgencyConfiguration.Schema);
        builder.HasKey(entry => entry.Id);
        builder.Property(entry => entry.Id).ValueGeneratedNever();

        builder.Property(entry => entry.PermissionCode)
            .HasMaxLength(PermissionOverride.MaxPermissionCodeLength).IsRequired();

        builder.Property(entry => entry.Reason)
            .HasMaxLength(PermissionOverride.MaxReasonLength).IsRequired();

        builder.HasOne<Agency>()
            .WithMany()
            .HasForeignKey(entry => entry.AgencyId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Agency>()
            .WithMany()
            .HasForeignKey(entry => entry.SubAgencyId)
            .OnDelete(DeleteBehavior.Restrict);

        // A permission is denied once or not at all. The upsert in SubAgentPermissionService
        // relies on this to turn a second deny into a restated reason rather than a duplicate.
        builder.HasIndex(entry => new { entry.SubAgencyId, entry.PermissionCode })
            .IsUnique()
            .HasDatabaseName("ix_permission_overrides_sub_agency_id_permission_code");

        builder.HasIndex(entry => entry.AgencyId)
            .HasDatabaseName("ix_permission_overrides_agency_id");
    }
}
