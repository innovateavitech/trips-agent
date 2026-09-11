using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TripsAgent.Domain.Payments;
using TripsAgent.Domain.Tenancy;

namespace TripsAgent.Infrastructure.Persistence.Configurations.Payments;

/// <summary>Shared constants for the money tables.</summary>
public static class PaymentsSchema
{
    /// <summary>The PostgreSQL schema holding the ledger, wallets and gateway records.</summary>
    public const string Name = "payments";
}

public sealed class LedgerAccountConfiguration : IEntityTypeConfiguration<LedgerAccount>
{
    public void Configure(EntityTypeBuilder<LedgerAccount> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("ledger_accounts", PaymentsSchema.Name);
        builder.HasKey(account => account.Id);
        builder.Property(account => account.Id).ValueGeneratedNever();

        builder.Property(account => account.AccountType)
            .HasConversion<string>().HasMaxLength(40).IsRequired();

        builder.Property(account => account.Currency).HasMaxLength(3).IsFixedLength().IsRequired();
        builder.Property(account => account.Name).HasMaxLength(120).IsRequired();

        builder.HasOne<Agency>()
            .WithMany()
            .HasForeignKey(account => account.AgencyId)
            .OnDelete(DeleteBehavior.Restrict);

        // One account per agency, type and currency. A second would split a balance in two and
        // make every total depend on remembering to add both.
        builder.HasIndex(account => new { account.AgencyId, account.AccountType, account.Currency })
            .IsUnique()
            .HasDatabaseName("ix_ledger_accounts_agency_id_account_type_currency");
    }
}

public sealed class LedgerEntryConfiguration : IEntityTypeConfiguration<LedgerEntry>
{
    public void Configure(EntityTypeBuilder<LedgerEntry> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("ledger_entries", PaymentsSchema.Name);
        builder.HasKey(entry => entry.Id);
        builder.Property(entry => entry.Id).ValueGeneratedNever();

        builder.Property(entry => entry.Direction)
            .HasConversion<string>().HasMaxLength(10).IsRequired();

        builder.Property(entry => entry.ReferenceType).HasMaxLength(60).IsRequired();
        builder.Property(entry => entry.Description).HasMaxLength(500).IsRequired();

        builder.HasOne<LedgerAccount>()
            .WithMany()
            .HasForeignKey(entry => entry.AccountId)
            .OnDelete(DeleteBehavior.Restrict);

        // An account's own history, newest first — what a statement reads.
        builder.HasIndex(entry => new { entry.AccountId, entry.OccurredAt })
            .IsDescending(false, true)
            .HasDatabaseName("ix_ledger_entries_account_id_occurred_at");

        // The balance trigger reads every entry in a group on commit, so this index is on the
        // hot path of every financial write, not just of reporting.
        builder.HasIndex(entry => entry.TransactionGroupId)
            .HasDatabaseName("ix_ledger_entries_transaction_group_id");

        builder.HasIndex(entry => new { entry.ReferenceType, entry.ReferenceId })
            .HasDatabaseName("ix_ledger_entries_reference");
    }
}

public sealed class WalletConfiguration : IEntityTypeConfiguration<Wallet>
{
    public void Configure(EntityTypeBuilder<Wallet> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("wallets", PaymentsSchema.Name);
        builder.HasKey(wallet => wallet.Id);
        builder.Property(wallet => wallet.Id).ValueGeneratedNever();

        builder.Property(wallet => wallet.Currency).HasMaxLength(3).IsFixedLength().IsRequired();

        builder.Property(wallet => wallet.Status)
            .HasConversion<string>().HasMaxLength(20).IsRequired();

        // The concurrency token. EF adds it to the WHERE clause of every update and throws when
        // no row matches — which is what stops two debits that read the same balance from both
        // writing, and one of them silently winning.
        builder.Property(wallet => wallet.Version).IsConcurrencyToken().IsRequired();

        builder.HasOne<Agency>()
            .WithMany()
            .HasForeignKey(wallet => wallet.AgencyId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(wallet => new { wallet.AgencyId, wallet.Currency })
            .IsUnique()
            .HasDatabaseName("ix_wallets_agency_id_currency");
    }
}

public sealed class WalletHoldConfiguration : IEntityTypeConfiguration<WalletHold>
{
    public void Configure(EntityTypeBuilder<WalletHold> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("wallet_holds", PaymentsSchema.Name);
        builder.HasKey(hold => hold.Id);
        builder.Property(hold => hold.Id).ValueGeneratedNever();

        builder.Property(hold => hold.Status)
            .HasConversion<string>().HasMaxLength(20).IsRequired();

        builder.HasOne<Wallet>()
            .WithMany()
            .HasForeignKey(hold => hold.WalletId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(hold => new { hold.AgencyId, hold.Status })
            .HasDatabaseName("ix_wallet_holds_agency_id_status");

        // The sweeper reads held rows past their deadline, so it wants the deadline indexed.
        builder.HasIndex(hold => new { hold.Status, hold.ExpiresAt })
            .HasDatabaseName("ix_wallet_holds_status_expires_at");
    }
}

public sealed class WalletTransactionConfiguration : IEntityTypeConfiguration<WalletTransaction>
{
    public void Configure(EntityTypeBuilder<WalletTransaction> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("wallet_transactions", PaymentsSchema.Name);
        builder.HasKey(transaction => transaction.Id);
        builder.Property(transaction => transaction.Id).ValueGeneratedNever();

        builder.Property(transaction => transaction.Type)
            .HasConversion<string>().HasMaxLength(30).IsRequired();

        builder.Property(transaction => transaction.Description).HasMaxLength(500).IsRequired();

        builder.HasOne<Wallet>()
            .WithMany()
            .HasForeignKey(transaction => transaction.WalletId)
            .OnDelete(DeleteBehavior.Restrict);

        // The statement: one agency's lines, newest first.
        builder.HasIndex(transaction => new { transaction.AgencyId, transaction.OccurredAt })
            .IsDescending(false, true)
            .HasDatabaseName("ix_wallet_transactions_agency_id_occurred_at");

        builder.HasIndex(transaction => transaction.TransactionGroupId)
            .HasDatabaseName("ix_wallet_transactions_transaction_group_id");
    }
}
