using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TripsAgent.Domain.Orders;
using TripsAgent.Domain.Payments;
using TripsAgent.Domain.Tenancy;
using TripsAgent.Infrastructure.Persistence.Encryption;

namespace TripsAgent.Infrastructure.Persistence.Configurations.Payments;

/// <summary>
/// <c>payments.agency_bank_accounts</c>: where an agency is paid (build plan F12, issue 69).
/// </summary>
public sealed class AgencyBankAccountConfiguration : IEntityTypeConfiguration<AgencyBankAccount>
{
    public void Configure(EntityTypeBuilder<AgencyBankAccount> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("agency_bank_accounts", PaymentsSchema.Name);
        builder.HasKey(account => account.Id);
        builder.Property(account => account.Id).ValueGeneratedNever();

        builder.Property(account => account.BankCode).HasMaxLength(20).IsRequired();
        builder.Property(account => account.BankName).HasMaxLength(120).IsRequired();
        // Ciphertext only (issue 104). Shown as its last four digits everywhere; never searchable.
        builder.Property(account => account.AccountNumber)
            .IsEncryptedAtRest("account_number_encrypted", EncryptedColumns.AgencyBankAccountNumber)
            .IsRequired();
        builder.Property(account => account.AccountNameProvided).HasMaxLength(160).IsRequired();
        builder.Property(account => account.AccountNameResolved).HasMaxLength(160);
        builder.Property(account => account.GatewayRecipientCode).HasMaxLength(80);
        builder.Property(account => account.RejectionReason).HasMaxLength(500);
        builder.Property(account => account.Currency).HasMaxLength(3).IsFixedLength().IsRequired();

        builder.Property(account => account.Status)
            .HasConversion<string>().HasMaxLength(30).IsRequired();

        // Derived for display only; there is nothing to store.
        builder.Ignore(account => account.CanReceiveMoney);
        builder.Ignore(account => account.MaskedNumber);

        builder.HasOne<Agency>().WithMany()
            .HasForeignKey(account => account.AgencyId)
            .OnDelete(DeleteBehavior.Restrict);

        // One default per agency per currency, as a partial index: a filtered unique index is the
        // only way to say "at most one row where is_default" without a trigger.
        builder.HasIndex(account => new { account.AgencyId, account.Currency })
            .IsUnique()
            .HasFilter("is_default")
            .HasDatabaseName("ix_agency_bank_accounts_one_default");
    }
}

/// <summary>
/// <c>payments.payouts</c>: an agency withdrawing to its bank (build plan F12, issue 69).
/// </summary>
public sealed class PayoutConfiguration : IEntityTypeConfiguration<Payout>
{
    public void Configure(EntityTypeBuilder<Payout> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("payouts", PaymentsSchema.Name);
        builder.HasKey(payout => payout.Id);
        builder.Property(payout => payout.Id).ValueGeneratedNever();

        builder.Property(payout => payout.Reference).HasMaxLength(60).IsRequired();
        builder.Property(payout => payout.Currency).HasMaxLength(3).IsFixedLength().IsRequired();
        builder.Property(payout => payout.GatewayTransferCode).HasMaxLength(80);
        builder.Property(payout => payout.GatewayStatus).HasMaxLength(40);
        builder.Property(payout => payout.RejectionReason).HasMaxLength(500);
        builder.Property(payout => payout.FailureReason).HasMaxLength(500);

        builder.Property(payout => payout.Status)
            .HasConversion<string>().HasMaxLength(20).IsRequired();

        // Two workers can reach one payout — the status poller and a transfer webhook — and both
        // can decide to post its settlement. The token means the second save matches no row.
        builder.Property(payout => payout.Version).IsConcurrencyToken().IsRequired();

        builder.Ignore(payout => payout.IsSettled);
        builder.Ignore(payout => payout.IsInFlight);
        builder.Ignore(payout => payout.AwaitsGatewayAnswer);

        builder.HasOne<Agency>().WithMany()
            .HasForeignKey(payout => payout.AgencyId)
            .OnDelete(DeleteBehavior.Restrict);

        // Restrict, not cascade: a payout whose destination has been deleted cannot be explained
        // to whoever is asking where their money went.
        builder.HasOne<AgencyBankAccount>().WithMany()
            .HasForeignKey(payout => payout.BankAccountId)
            .OnDelete(DeleteBehavior.Restrict);

        // Ours and the gateway's, so re-initiating one payout is refused by the gateway rather
        // than duplicated by it. Unique platform-wide, not per agency.
        builder.HasIndex(payout => payout.Reference)
            .IsUnique()
            .HasDatabaseName("ix_payouts_reference");

        builder.HasIndex(payout => new { payout.AgencyId, payout.RequestedAt })
            .IsDescending(false, true)
            .HasDatabaseName("ix_payouts_agency_id_requested_at");

        // The approval queue and the status poller both read by status, oldest first.
        builder.HasIndex(payout => new { payout.Status, payout.RequestedAt })
            .HasDatabaseName("ix_payouts_status_requested_at");
    }
}

/// <summary>
/// <c>payments.disputes</c>: a cardholder's bank taking money back (build plan F12, issue 69).
/// </summary>
public sealed class DisputeConfiguration : IEntityTypeConfiguration<Dispute>
{
    public void Configure(EntityTypeBuilder<Dispute> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("disputes", PaymentsSchema.Name);
        builder.HasKey(dispute => dispute.Id);
        builder.Property(dispute => dispute.Id).ValueGeneratedNever();

        builder.Property(dispute => dispute.GatewayDisputeId).HasMaxLength(80).IsRequired();
        builder.Property(dispute => dispute.PaymentReference).HasMaxLength(60).IsRequired();
        builder.Property(dispute => dispute.Currency).HasMaxLength(3).IsFixedLength().IsRequired();
        builder.Property(dispute => dispute.Category).HasMaxLength(60);
        builder.Property(dispute => dispute.Reason).HasMaxLength(1000);
        builder.Property(dispute => dispute.Resolution).HasMaxLength(200);
        builder.Property(dispute => dispute.HoldFailureReason).HasMaxLength(500);
        builder.Property(dispute => dispute.EvidenceNote).HasMaxLength(4000);

        builder.Property(dispute => dispute.Status)
            .HasConversion<string>().HasMaxLength(30).IsRequired();

        builder.Property(dispute => dispute.HoldOutcome)
            .HasConversion<string>().HasMaxLength(20).IsRequired();

        // What was actually sent to the gateway, and which files went with it. jsonb rather than
        // text so a question like "which disputes cited the voucher" is answerable in SQL.
        builder.Property(dispute => dispute.EvidencePayload).HasColumnType("jsonb");
        builder.Property(dispute => dispute.EvidenceAssetIds).HasColumnType("jsonb");

        builder.Property(dispute => dispute.Version).IsConcurrencyToken().IsRequired();

        builder.Ignore(dispute => dispute.IsResolved);

        builder.HasOne<Agency>().WithMany()
            .HasForeignKey(dispute => dispute.AgencyId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<PaymentTransaction>().WithMany()
            .HasForeignKey(dispute => dispute.PaymentTransactionId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Order>().WithMany()
            .HasForeignKey(dispute => dispute.OrderId)
            .IsRequired(false)
            .OnDelete(DeleteBehavior.Restrict);

        // The dedup key. The same webhook delivered five times produces one row, and it is the
        // database that decides so rather than a check in application code that two deliveries
        // can both pass.
        builder.HasIndex(dispute => dispute.GatewayDisputeId)
            .IsUnique()
            .HasDatabaseName("ix_disputes_gateway_dispute_id");

        // The queue, in the only order that reflects what is actually urgent.
        builder.HasIndex(dispute => new { dispute.Status, dispute.EvidenceDueAt })
            .HasDatabaseName("ix_disputes_status_evidence_due_at");

        builder.HasIndex(dispute => new { dispute.AgencyId, dispute.OpenedAt })
            .IsDescending(false, true)
            .HasDatabaseName("ix_disputes_agency_id_opened_at");
    }
}

/// <summary>
/// <c>payments.reconciliation_runs</c>: one day of the gateway, matched against the books.
/// </summary>
/// <remarks>
/// Platform-wide, like the exceptions it produces — a settlement that matches nothing belongs to
/// no agency, and that is precisely what makes it worth reading. No tenant filter and no
/// row-level security policy; the only way in is <c>IPlatformScope</c> plus a platform permission.
/// </remarks>
public sealed class ReconciliationRunConfiguration : IEntityTypeConfiguration<ReconciliationRun>
{
    public void Configure(EntityTypeBuilder<ReconciliationRun> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("reconciliation_runs", PaymentsSchema.Name);
        builder.HasKey(run => run.Id);
        builder.Property(run => run.Id).ValueGeneratedNever();

        builder.Property(run => run.Type).HasConversion<string>().HasMaxLength(40).IsRequired();
        builder.Property(run => run.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(run => run.Gateway).HasMaxLength(40).IsRequired();
        builder.Property(run => run.Currency).HasMaxLength(3).IsFixedLength().IsRequired();
        builder.Property(run => run.FailureReason).HasMaxLength(2000);

        // A calendar day in Lagos, not an instant. `date`, so no reader has to wonder which zone
        // midnight was in.
        builder.Property(run => run.BusinessDate).HasColumnType("date").IsRequired();

        builder.Ignore(run => run.DifferenceMinor);
        builder.Ignore(run => run.IsClean);

        // One run per reconciler per gateway per day. Re-running a day updates this row rather
        // than laying a second one beside it — and it is the index that guarantees that, not the
        // job remembering.
        builder.HasIndex(run => new { run.Type, run.Gateway, run.BusinessDate })
            .IsUnique()
            .HasDatabaseName("ix_reconciliation_runs_type_gateway_business_date");

        builder.HasIndex(run => new { run.Status, run.BusinessDate })
            .IsDescending(false, true)
            .HasDatabaseName("ix_reconciliation_runs_status_business_date");
    }
}
