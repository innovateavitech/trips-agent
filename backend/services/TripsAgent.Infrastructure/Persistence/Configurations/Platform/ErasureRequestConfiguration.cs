using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TripsAgent.Domain.Crm;
using TripsAgent.Domain.Platform;
using TripsAgent.Domain.Tenancy;

namespace TripsAgent.Infrastructure.Persistence.Configurations.Platform;

/// <summary>
/// <c>platform.erasure_requests</c> — the record that somebody's details were erased (issue 106).
/// </summary>
/// <remarks>
/// Tenant-scoped like every business table: an <c>agency_id</c>, the EF filter, and a row-level
/// security policy in its migration (ADR-0006). In practice only Trips staff write it, through
/// <see cref="TripsAgent.Application.Tenancy.IPlatformScope"/>, but an agency asking which of its
/// customers were erased is an ordinary tenant read and the filter is what makes that safe.
/// </remarks>
public sealed class ErasureRequestConfiguration : IEntityTypeConfiguration<ErasureRequest>
{
    public void Configure(EntityTypeBuilder<ErasureRequest> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("erasure_requests", Schemas.Platform, table =>
            table.HasCheckConstraint(
                "ck_erasure_requests_finished_is_explained",
                "(status = 'Requested' AND completed_at IS NULL) "
                + "OR (status = 'Completed' AND completed_at IS NOT NULL AND outcome IS NOT NULL) "
                + "OR (status = 'Refused' AND completed_at IS NOT NULL AND refusal_reason IS NOT NULL)"));

        builder.HasKey(request => request.Id);
        builder.Property(request => request.Id).ValueGeneratedNever();

        builder.Property(request => request.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(request => request.Reason).HasMaxLength(ErasureRequest.MaximumReasonLength).IsRequired();
        builder.Property(request => request.RefusalReason).HasMaxLength(ErasureRequest.MaximumReasonLength);
        builder.Property(request => request.Outcome).HasColumnType("jsonb");

        builder.HasOne<Agency>()
            .WithMany()
            .HasForeignKey(request => request.AgencyId)
            .OnDelete(DeleteBehavior.Restrict);

        // The customer row stays — anonymised, not deleted — so the request can always point at what
        // it erased without holding a copy of it.
        builder.HasOne<Customer>()
            .WithMany()
            .HasForeignKey(request => request.CustomerId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(request => new { request.AgencyId, request.RequestedAt })
            .HasDatabaseName("ix_erasure_requests_agency_id_requested_at");

        builder.HasIndex(request => request.CustomerId)
            .HasDatabaseName("ix_erasure_requests_customer_id");
    }
}
