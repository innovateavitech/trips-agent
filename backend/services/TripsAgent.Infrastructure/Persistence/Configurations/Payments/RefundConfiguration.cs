using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TripsAgent.Domain.Orders;
using TripsAgent.Domain.Payments;
using TripsAgent.Domain.Suppliers;
using TripsAgent.Domain.Tenancy;

namespace TripsAgent.Infrastructure.Persistence.Configurations.Payments;

/// <summary>
/// <c>payments.refunds</c>: money given back for an order line (#43, #44). One per line and append-only;
/// the migration adds the trigger, the grants and row-level security.
/// </summary>
public sealed class RefundConfiguration : IEntityTypeConfiguration<Refund>
{
    public void Configure(EntityTypeBuilder<Refund> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("refunds", PaymentsSchema.Name);
        builder.HasKey(refund => refund.Id);
        builder.Property(refund => refund.Id).ValueGeneratedNever();

        builder.Property(refund => refund.Reason).HasConversion<string>().HasMaxLength(30).IsRequired();
        builder.Property(refund => refund.Method).HasConversion<string>().HasMaxLength(30).IsRequired();
        builder.Property(refund => refund.Currency).HasMaxLength(3).IsFixedLength().IsRequired();
        builder.Property(refund => refund.Note).HasMaxLength(500);

        builder.HasOne<Agency>().WithMany().HasForeignKey(refund => refund.AgencyId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Order>().WithMany().HasForeignKey(refund => refund.OrderId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<OrderLine>().WithMany().HasForeignKey(refund => refund.OrderLineId).OnDelete(DeleteBehavior.Restrict);

        // The evidence a supplier reversal rests on must exist: a key, not just an id someone typed.
        builder.HasOne<SupplierStatusPoll>().WithMany()
            .HasForeignKey(refund => refund.SupplierStatusPollId)
            .IsRequired(false)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<SupplierBooking>().WithMany()
            .HasForeignKey(refund => refund.SupplierBookingId)
            .IsRequired(false)
            .OnDelete(DeleteBehavior.Restrict);

        // One refund per line: a reversal delivered twice, or racing an agent's refund, cannot pay out twice.
        builder.HasIndex(refund => refund.OrderLineId).IsUnique().HasDatabaseName("ix_refunds_order_line_id");

        builder.HasIndex(refund => new { refund.AgencyId, refund.RefundedAt })
            .IsDescending(false, true)
            .HasDatabaseName("ix_refunds_agency_id_refunded_at");
    }
}
