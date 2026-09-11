using System.Runtime.CompilerServices;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using TripsAgent.Application.Tenancy;
using TripsAgent.Domain.Common;
using TripsAgent.Domain.Payments;
using TripsAgent.Domain.Tenancy;
using TripsAgent.Infrastructure.Persistence;
using TripsAgent.Infrastructure.Tenancy;
using TripsAgent.IntegrationTests.Identity;
using TripsAgent.IntegrationTests.Persistence;

namespace TripsAgent.IntegrationTests.Payments;

/// <summary>
/// The invariants the whole money path rests on, proven against the database rather than against
/// the domain that is supposed to uphold them.
/// </summary>
/// <remarks>
/// Every test here writes raw SQL on purpose. The domain already refuses these things; what is
/// being checked is that the database refuses them too, for the seed script, the migration and
/// the person in psql who never went near the domain model.
/// </remarks>
[Collection(PostgresCollection.Name)]
public class LedgerIntegrityTests
{
    private readonly PostgresFixture _postgres;

    public LedgerIntegrityTests(PostgresFixture postgres) => _postgres = postgres;

    // ------------------------------------------------------------------------ balance rule

    [Fact]
    public async Task A_balanced_transaction_commits()
    {
        await using var world = await WorldAsync();

        await world.PostRaw(
            (world.WalletAccountId, "Debit", 150_000),
            (world.GatewayAccountId, "Credit", 150_000));

        using var _ = world.Tenancy.Scope.Enter("test — counting entries");
        (await world.Db.LedgerEntries.CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task An_unbalanced_transaction_cannot_be_committed_at_all()
    {
        await using var world = await WorldAsync();

        // ₦1,500.00 in, ₦1,400.00 out. A hundred naira appearing from nowhere.
        var act = async () => await world.PostRaw(
            (world.WalletAccountId, "Debit", 150_000),
            (world.GatewayAccountId, "Credit", 140_000));

        var thrown = await act.Should().ThrowAsync<PostgresException>();
        thrown.Which.MessageText.Should().Contain("does not balance");

        using var _ = world.Tenancy.Scope.Enter("test — nothing should have landed");

        // The whole transaction is rolled back, not merely flagged. Neither side is there.
        (await world.Db.LedgerEntries.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task A_single_sided_entry_cannot_be_committed()
    {
        await using var world = await WorldAsync();

        // The most likely mistake in practice: somebody records the money arriving and forgets
        // to record where it came from.
        var act = async () => await world.PostRaw((world.WalletAccountId, "Debit", 150_000));

        await act.Should().ThrowAsync<PostgresException>();
    }

    [Fact]
    public async Task The_check_runs_at_commit_not_per_row()
    {
        await using var world = await WorldAsync();

        // The sides are separate rows. A check that ran per statement would fail on the first
        // one and make a balanced transaction impossible to write.
        await using var connection = await world.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        var group = Guid.CreateVersion7();

        await world.InsertEntry(connection, transaction, world.WalletAccountId, "Debit", 150_000, group);

        // Unbalanced at this instant, and that is fine — nothing has committed.
        await world.InsertEntry(connection, transaction, world.GatewayAccountId, "Credit", 150_000, group);

        var act = async () => await transaction.CommitAsync();
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task A_three_sided_transaction_balances()
    {
        await using var world = await WorldAsync();

        // A real booking: the agency pays, the supplier is owed, and Trips keeps the markup.
        await world.PostRaw(
            (world.WalletAccountId, "Credit", 150_000),
            (world.SupplierAccountId, "Debit", 120_000),
            (world.RevenueAccountId, "Debit", 30_000));

        using var _ = world.Tenancy.Scope.Enter("test — counting entries");
        (await world.Db.LedgerEntries.CountAsync()).Should().Be(3);
    }

    // -------------------------------------------------------------------------- append-only

    [Fact]
    public async Task An_entry_cannot_be_updated()
    {
        await using var world = await WorldAsync();
        await world.PostRaw(
            (world.WalletAccountId, "Debit", 150_000),
            (world.GatewayAccountId, "Credit", 150_000));

        var act = async () => await world.Db.Database.ExecuteSqlRawAsync(
            "UPDATE payments.ledger_entries SET amount_minor = 1 WHERE amount_minor = 150000");

        // A correction is a reversing entry. An edited ledger cannot be audited.
        (await act.Should().ThrowAsync<PostgresException>())
            .Which.MessageText.Should().Contain("append-only");
    }

    [Fact]
    public async Task An_entry_cannot_be_deleted()
    {
        await using var world = await WorldAsync();
        await world.PostRaw(
            (world.WalletAccountId, "Debit", 150_000),
            (world.GatewayAccountId, "Credit", 150_000));

        var act = async () => await world.Db.Database.ExecuteSqlRawAsync(
            "DELETE FROM payments.ledger_entries");

        (await act.Should().ThrowAsync<PostgresException>())
            .Which.MessageText.Should().Contain("append-only");
    }

    [Fact]
    public async Task A_correction_is_made_by_reversing_rather_than_editing()
    {
        await using var world = await WorldAsync();

        await world.PostRaw(
            (world.WalletAccountId, "Debit", 150_000),
            (world.GatewayAccountId, "Credit", 150_000));

        // The way a correction is actually made: post the opposite.
        await world.PostRaw(
            (world.WalletAccountId, "Credit", 150_000),
            (world.GatewayAccountId, "Debit", 150_000));

        using var _ = world.Tenancy.Scope.Enter("test — the history keeps both");

        // Four rows, not two edited into nothing. The mistake and its correction both remain.
        (await world.Db.LedgerEntries.CountAsync()).Should().Be(4);
        (await world.BalanceOf(world.WalletAccountId)).Should().Be(0);
    }

    // -------------------------------------------------------------------------- constraints

    [Fact]
    public async Task A_negative_amount_is_refused()
    {
        await using var world = await WorldAsync();

        // Direction carries the sign; a negative debit and a positive credit would be two
        // spellings of one thing, and every balance query would have to handle both.
        var act = async () => await world.PostRaw(
            (world.WalletAccountId, "Debit", -150_000),
            (world.GatewayAccountId, "Credit", -150_000));

        (await act.Should().ThrowAsync<PostgresException>())
            .Which.ConstraintName.Should().Be("ck_ledger_entries_amount_positive");
    }

    [Fact]
    public async Task A_zero_amount_is_refused()
    {
        await using var world = await WorldAsync();

        var act = async () => await world.PostRaw(
            (world.WalletAccountId, "Debit", 0),
            (world.GatewayAccountId, "Credit", 0));

        (await act.Should().ThrowAsync<PostgresException>())
            .Which.ConstraintName.Should().Be("ck_ledger_entries_amount_positive");
    }

    [Fact]
    public async Task An_agency_wallet_account_must_belong_to_an_agency()
    {
        await using var world = await WorldAsync();

        var act = async () => await world.Db.Database.ExecuteSqlInterpolatedAsync(
            $"""
             INSERT INTO payments.ledger_accounts
                 (id, agency_id, account_type, currency, name, created_at, updated_at)
             VALUES ({Guid.CreateVersion7()}, NULL, 'AgencyWallet', 'NGN', 'Orphan', now(), now())
             """);

        (await act.Should().ThrowAsync<PostgresException>())
            .Which.ConstraintName.Should().Be("ck_ledger_accounts_ownership");
    }

    // ------------------------------------------------------------------------------ helpers

    private async Task<World> WorldAsync([CallerMemberName] string testName = "")
    {
        var name = testName.ToLowerInvariant();
        name = name[..Math.Min(name.Length, 55)];

        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var tenancy = TestTenancy.None();

        var db = await _postgres.CreateEmptyDatabaseAsync(name, tenancy.Tenant, tenancy.Scope, clock);
        await db.Database.MigrateAsync();

        Guid agencyId;
        Guid wallet, gateway, revenue, supplier;

        using (var _ = tenancy.Scope.Enter("test setup — creating an agency and its ledger accounts"))
        {
            var agency = Agency.RegisterPrincipal("Lagos Travel Limited", "lagos-travel", "NG", "NGN", "Africa/Lagos");
            agency.MarkVerified(clock.GetUtcNow());
            db.Agencies.Add(agency);

            var walletAccount = LedgerAccount.ForAgency(agency.Id, LedgerAccountType.AgencyWallet, "NGN", "Wallet");
            var gatewayAccount = LedgerAccount.ForPlatform(LedgerAccountType.GatewayClearing, "NGN", "Gateway clearing");
            var revenueAccount = LedgerAccount.ForPlatform(LedgerAccountType.PlatformRevenue, "NGN", "Platform revenue");
            var supplierAccount = LedgerAccount.ForPlatform(LedgerAccountType.SupplierPayable, "NGN", "Supplier payable");

            db.LedgerAccounts.AddRange(walletAccount, gatewayAccount, revenueAccount, supplierAccount);
            await db.SaveChangesAsync();

            agencyId = agency.Id;
            wallet = walletAccount.Id;
            gateway = gatewayAccount.Id;
            revenue = revenueAccount.Id;
            supplier = supplierAccount.Id;
        }

        return new World(db, tenancy, clock, agencyId, wallet, gateway, revenue, supplier);
    }

    private sealed class World : IAsyncDisposable
    {
        public World(
            AppDbContext db,
            (TenantContext Tenant, PlatformScope Scope) tenancy,
            ManualClock clock,
            Guid agencyId,
            Guid walletAccountId,
            Guid gatewayAccountId,
            Guid revenueAccountId,
            Guid supplierAccountId)
        {
            Db = db;
            Tenancy = tenancy;
            Clock = clock;
            AgencyId = agencyId;
            WalletAccountId = walletAccountId;
            GatewayAccountId = gatewayAccountId;
            RevenueAccountId = revenueAccountId;
            SupplierAccountId = supplierAccountId;
        }

        public AppDbContext Db { get; }

        public (TenantContext Tenant, PlatformScope Scope) Tenancy { get; }

        public ManualClock Clock { get; }

        public Guid AgencyId { get; }

        public Guid WalletAccountId { get; }

        public Guid GatewayAccountId { get; }

        public Guid RevenueAccountId { get; }

        public Guid SupplierAccountId { get; }

        public async Task<NpgsqlConnection> OpenAsync()
        {
            var connection = new NpgsqlConnection(Db.Database.GetConnectionString());
            await connection.OpenAsync();
            return connection;
        }

        /// <summary>Posts entries in one transaction, bypassing the domain entirely.</summary>
        public async Task PostRaw(params (Guid AccountId, string Direction, long AmountMinor)[] entries)
        {
            await using var connection = await OpenAsync();
            await using var transaction = await connection.BeginTransactionAsync();

            var group = Guid.CreateVersion7();

            foreach (var (accountId, direction, amount) in entries)
            {
                await InsertEntry(connection, transaction, accountId, direction, amount, group);
            }

            await transaction.CommitAsync();
        }

        public async Task InsertEntry(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            Guid accountId,
            string direction,
            long amountMinor,
            Guid? group = null)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT INTO payments.ledger_entries
                    (id, transaction_group_id, account_id, agency_id, direction, amount_minor,
                     reference_type, reference_id, description, occurred_at)
                VALUES ($1, $2, $3, $4, $5, $6, 'test', NULL, 'test entry', now())
                """;

            command.Parameters.AddWithValue(Guid.CreateVersion7());
            command.Parameters.AddWithValue(group ?? Guid.CreateVersion7());
            command.Parameters.AddWithValue(accountId);
            command.Parameters.AddWithValue(AgencyId);
            command.Parameters.AddWithValue(direction);
            command.Parameters.AddWithValue(amountMinor);

            await command.ExecuteNonQueryAsync();
        }

        /// <summary>Debits less credits for one account, straight from the entries.</summary>
        public async Task<long> BalanceOf(Guid accountId)
        {
            await using var connection = await OpenAsync();
            await using var command = connection.CreateCommand();

            command.CommandText =
                """
                SELECT COALESCE(SUM(amount_minor) FILTER (WHERE direction = 'Debit'), 0)
                     - COALESCE(SUM(amount_minor) FILTER (WHERE direction = 'Credit'), 0)
                  FROM payments.ledger_entries
                 WHERE account_id = $1
                """;

            command.Parameters.AddWithValue(accountId);

            // SUM over bigint returns numeric, not bigint — PostgreSQL widens to avoid overflow.
            return (long)(decimal)(await command.ExecuteScalarAsync())!;
        }

        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }
}
