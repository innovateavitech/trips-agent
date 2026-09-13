using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TripsAgent.Domain.Payments;
using TripsAgent.Domain.Tenancy;

namespace TripsAgent.Infrastructure.Persistence.Configurations.Payments;

/// <summary>
/// Maps <see cref="WalletAllowance"/> to <c>payments.wallet_allowances</c>. Feature F10, issue 63.
/// </summary>
public sealed class WalletAllowanceConfiguration : IEntityTypeConfiguration<WalletAllowance>
{
    public void Configure(EntityTypeBuilder<WalletAllowance> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("wallet_allowances", PaymentsSchema.Name);
        builder.HasKey(allowance => allowance.Id);
        builder.Property(allowance => allowance.Id).ValueGeneratedNever();

        builder.Property(allowance => allowance.Currency).HasMaxLength(3).IsFixedLength().IsRequired();

        builder.Property(allowance => allowance.Period)
            .HasConversion<string>().HasMaxLength(20).IsRequired();

        builder.Property(allowance => allowance.Status)
            .HasConversion<string>().HasMaxLength(20).IsRequired();

        // The same optimistic concurrency the wallet uses. The hot path — reserving at checkout —
        // does not go through the change tracker at all (see SubAgentAllowanceService), but every
        // screen that edits a limit does, and two managers editing at once must not lose one.
        builder.Property(allowance => allowance.Version).IsConcurrencyToken();

        builder.HasOne<Agency>()
            .WithMany()
            .HasForeignKey(allowance => allowance.AgencyId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Agency>()
            .WithMany()
            .HasForeignKey(allowance => allowance.SubAgencyId)
            .OnDelete(DeleteBehavior.Restrict);

        // One allowance per sub-agent per currency. Two would mean two caps, and a booking would
        // have to choose — which is the sort of choice that ends in neither being enforced.
        builder.HasIndex(allowance => new { allowance.SubAgencyId, allowance.Currency })
            .IsUnique()
            .HasDatabaseName("ix_wallet_allowances_sub_agency_id_currency");

        builder.HasIndex(allowance => allowance.AgencyId)
            .HasDatabaseName("ix_wallet_allowances_agency_id");

        // What the reset job scans: the allowances whose period has turned over.
        builder.HasIndex(allowance => allowance.ResetsAt)
            .HasDatabaseName("ix_wallet_allowances_resets_at");
    }
}
