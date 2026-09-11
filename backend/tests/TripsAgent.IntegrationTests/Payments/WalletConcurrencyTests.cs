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
/// The wallet under contention, and the invariants that hold when it is.
/// </summary>
[Collection(PostgresCollection.Name)]
public class WalletConcurrencyTests
{
    private readonly PostgresFixture _postgres;

    public WalletConcurrencyTests(PostgresFixture postgres) => _postgres = postgres;

    [Fact]
    public async Task Twenty_parallel_debits_never_overdraw_and_never_lose_a_write()
    {
        // Twenty debits of ₦100 against a ₦1,000 balance. Ten must succeed and ten must fail;
        // the wallet must end at exactly zero.
        const int attempts = 20;
        var debit = Money.FromMajor(100);

        await using var world = await WorldAsync(openingBalance: Money.FromMajor(1_000));

        var succeeded = 0;
        var rejected = 0;

        var runs = Enumerable.Range(0, attempts).Select(async _ =>
        {
            // Each attempt gets its own context, as separate requests would.
            await using var context = world.NewContext();

            // Retry on a concurrency clash: losing the version race means the balance moved, not
            // that the debit was wrong. Without this the test would measure contention rather
            // than correctness.
            for (var attempt = 0; attempt < 50; attempt++)
            {
                var wallet = await context.Wallets.SingleAsync(w => w.Id == world.WalletId);

                if (wallet.AvailableMinor < debit)
                {
                    Interlocked.Increment(ref rejected);
                    return;
                }

                try
                {
                    wallet.Debit(debit);
                    await context.SaveChangesAsync();
                    Interlocked.Increment(ref succeeded);
                    return;
                }
                catch (DbUpdateConcurrencyException)
                {
                    // Somebody else wrote first. Drop the stale instance and read again.
                    context.ChangeTracker.Clear();
                }
            }

            throw new InvalidOperationException("Gave up retrying — contention was not converging.");
        });

        await Task.WhenAll(runs);

        succeeded.Should().Be(10, "a ₦1,000 balance funds exactly ten ₦100 debits");
        rejected.Should().Be(10);

        using var _ = world.Tenancy.Scope.Enter("test — reading the final balance");

        // AsNoTracking, because this context tracked the wallet during setup and would otherwise
        // hand back that stale instance rather than what the parallel writers committed.
        var final = await world.Db.Wallets.AsNoTracking().SingleAsync(w => w.Id == world.WalletId);

        // The invariant that matters: not one kobo more was spent than existed, and no successful
        // debit was silently overwritten by a concurrent one.
        final.BalanceMinor.Should().Be(Money.Zero);
    }

    [Fact]
    public async Task Parallel_credits_do_not_lose_a_write()
    {
        const int credits = 20;
        var each = Money.FromMajor(50);

        await using var world = await WorldAsync(openingBalance: Money.Zero);

        var runs = Enumerable.Range(0, credits).Select(async _ =>
        {
            await using var context = world.NewContext();

            for (var attempt = 0; attempt < 50; attempt++)
            {
                var wallet = await context.Wallets.SingleAsync(w => w.Id == world.WalletId);

                try
                {
                    wallet.Credit(each);
                    await context.SaveChangesAsync();
                    return;
                }
                catch (DbUpdateConcurrencyException)
                {
                    context.ChangeTracker.Clear();
                }
            }

            throw new InvalidOperationException("Gave up retrying.");
        });

        await Task.WhenAll(runs);

        using var _ = world.Tenancy.Scope.Enter("test — reading the final balance");

        // AsNoTracking, because this context tracked the wallet during setup and would otherwise
        // hand back that stale instance rather than what the parallel writers committed.
        var final = await world.Db.Wallets.AsNoTracking().SingleAsync(w => w.Id == world.WalletId);

        // A lost update here is money an agency paid for and did not receive.
        final.BalanceMinor.Should().Be(Money.FromMajor(50 * credits));
    }

    [Fact]
    public async Task A_stale_write_is_rejected_rather_than_silently_winning()
    {
        await using var world = await WorldAsync(openingBalance: Money.FromMajor(1_000));

        await using var first = world.NewContext();
        await using var second = world.NewContext();

        var a = await first.Wallets.SingleAsync(w => w.Id == world.WalletId);
        var b = await second.Wallets.SingleAsync(w => w.Id == world.WalletId);

        a.Debit(Money.FromMajor(100));
        await first.SaveChangesAsync();

        // b read the balance before a wrote it. Without the version check this would overwrite
        // a's debit and the ₦100 would reappear.
        b.Debit(Money.FromMajor(100));

        var act = async () => await second.SaveChangesAsync();

        await act.Should().ThrowAsync<DbUpdateConcurrencyException>();
    }

    // ------------------------------------------------------------------------------- holds

    [Fact]
    public async Task A_hold_reserves_funds_without_spending_them()
    {
        await using var world = await WorldAsync(openingBalance: Money.FromMajor(1_000));

        await using var context = world.NewContext();
        var wallet = await context.Wallets.SingleAsync(w => w.Id == world.WalletId);

        var hold = wallet.PlaceHold(Money.FromMajor(300), world.Clock.GetUtcNow(), TimeSpan.FromMinutes(30));
        context.WalletHolds.Add(hold);
        await context.SaveChangesAsync();

        wallet.BalanceMinor.Should().Be(Money.FromMajor(1_000), "a hold does not spend");
        wallet.ReservedMinor.Should().Be(Money.FromMajor(300));
        wallet.AvailableMinor.Should().Be(Money.FromMajor(700));
    }

    [Fact]
    public async Task Reserved_funds_cannot_be_spent_twice()
    {
        await using var world = await WorldAsync(openingBalance: Money.FromMajor(1_000));

        await using var context = world.NewContext();
        var wallet = await context.Wallets.SingleAsync(w => w.Id == world.WalletId);

        context.WalletHolds.Add(wallet.PlaceHold(Money.FromMajor(800), world.Clock.GetUtcNow(), TimeSpan.FromMinutes(30)));
        await context.SaveChangesAsync();

        // ₦1,000 in the wallet but only ₦200 available — the rest is spoken for.
        var act = () => wallet.Debit(Money.FromMajor(500));

        act.Should().Throw<InvalidOperationException>().WithMessage("*only*available*");
    }

    [Fact]
    public async Task Capturing_a_hold_spends_it_exactly_once()
    {
        await using var world = await WorldAsync(openingBalance: Money.FromMajor(1_000));

        await using var context = world.NewContext();
        var wallet = await context.Wallets.SingleAsync(w => w.Id == world.WalletId);

        var hold = wallet.PlaceHold(Money.FromMajor(300), world.Clock.GetUtcNow(), TimeSpan.FromMinutes(30));
        context.WalletHolds.Add(hold);
        await context.SaveChangesAsync();

        wallet.CaptureHold(hold, world.Clock.GetUtcNow());
        await context.SaveChangesAsync();

        wallet.BalanceMinor.Should().Be(Money.FromMajor(700));
        wallet.ReservedMinor.Should().Be(Money.Zero);
        wallet.AvailableMinor.Should().Be(Money.FromMajor(700));
        hold.Status.Should().Be(WalletHoldStatus.Captured);
    }

    [Fact]
    public async Task Releasing_a_hold_gives_the_money_back()
    {
        await using var world = await WorldAsync(openingBalance: Money.FromMajor(1_000));

        await using var context = world.NewContext();
        var wallet = await context.Wallets.SingleAsync(w => w.Id == world.WalletId);

        var hold = wallet.PlaceHold(Money.FromMajor(300), world.Clock.GetUtcNow(), TimeSpan.FromMinutes(30));
        context.WalletHolds.Add(hold);
        await context.SaveChangesAsync();

        wallet.ReleaseHold(hold, world.Clock.GetUtcNow());
        await context.SaveChangesAsync();

        wallet.BalanceMinor.Should().Be(Money.FromMajor(1_000));
        wallet.AvailableMinor.Should().Be(Money.FromMajor(1_000));
    }

    [Fact]
    public async Task Releasing_twice_does_not_give_the_money_back_twice()
    {
        await using var world = await WorldAsync(openingBalance: Money.FromMajor(1_000));

        await using var context = world.NewContext();
        var wallet = await context.Wallets.SingleAsync(w => w.Id == world.WalletId);

        var hold = wallet.PlaceHold(Money.FromMajor(300), world.Clock.GetUtcNow(), TimeSpan.FromMinutes(30));
        context.WalletHolds.Add(hold);
        await context.SaveChangesAsync();

        // A timeout racing a failure produces exactly this, and it must be harmless.
        wallet.ReleaseHold(hold, world.Clock.GetUtcNow());
        wallet.ReleaseHold(hold, world.Clock.GetUtcNow());

        wallet.ReservedMinor.Should().Be(Money.Zero);
        wallet.BalanceMinor.Should().Be(Money.FromMajor(1_000));
    }

    [Fact]
    public async Task An_expired_hold_is_visible_to_a_sweeper()
    {
        await using var world = await WorldAsync(openingBalance: Money.FromMajor(1_000));

        await using var context = world.NewContext();
        var wallet = await context.Wallets.SingleAsync(w => w.Id == world.WalletId);

        var hold = wallet.PlaceHold(Money.FromMajor(300), world.Clock.GetUtcNow(), TimeSpan.FromMinutes(30));
        context.WalletHolds.Add(hold);
        await context.SaveChangesAsync();

        hold.HasExpired(world.Clock.GetUtcNow()).Should().BeFalse();

        // Without expiry a booking that dies would strand the money and the agent would have to
        // ring support to spend their own balance.
        hold.HasExpired(world.Clock.GetUtcNow().AddMinutes(31)).Should().BeTrue();
    }

    // -------------------------------------------------------------------------- constraints

    [Fact]
    public async Task The_database_refuses_a_negative_balance()
    {
        await using var world = await WorldAsync(openingBalance: Money.FromMajor(100));

        var act = async () => await world.Db.Database.ExecuteSqlRawAsync(
            "UPDATE payments.wallets SET balance_minor = -1");

        (await act.Should().ThrowAsync<PostgresException>())
            .Which.ConstraintName.Should().Be("ck_wallets_balance_not_negative");
    }

    [Fact]
    public async Task The_database_refuses_reserving_more_than_the_balance()
    {
        await using var world = await WorldAsync(openingBalance: Money.FromMajor(100));

        var act = async () => await world.Db.Database.ExecuteSqlRawAsync(
            "UPDATE payments.wallets SET reserved_minor = balance_minor + 1");

        (await act.Should().ThrowAsync<PostgresException>())
            .Which.ConstraintName.Should().Be("ck_wallets_reserved_within_balance");
    }

    [Fact]
    public async Task An_agency_cannot_have_two_wallets_in_one_currency()
    {
        await using var world = await WorldAsync(openingBalance: Money.Zero);

        var act = async () => await world.Db.Database.ExecuteSqlInterpolatedAsync(
            $"""
             INSERT INTO payments.wallets
                 (id, agency_id, currency, balance_minor, reserved_minor, status, version, created_at, updated_at)
             VALUES ({Guid.CreateVersion7()}, {world.AgencyId}, 'NGN', 0, 0, 'Active', 0, now(), now())
             """);

        // Two would split a balance in two and make every total depend on remembering both.
        (await act.Should().ThrowAsync<PostgresException>())
            .Which.ConstraintName.Should().Be("ix_wallets_agency_id_currency");
    }

    [Fact]
    public async Task A_statement_line_whose_arithmetic_does_not_add_up_is_refused()
    {
        await using var world = await WorldAsync(openingBalance: Money.Zero);

        var act = async () => await world.Db.Database.ExecuteSqlInterpolatedAsync(
            $"""
             INSERT INTO payments.wallet_transactions
                 (id, wallet_id, agency_id, type, amount_minor, balance_before_minor,
                  balance_after_minor, description, transaction_group_id, occurred_at,
                  created_at, updated_at)
             VALUES ({Guid.CreateVersion7()}, {world.WalletId}, {world.AgencyId}, 'TopUp',
                     10000, 0, 99999, 'wrong', {Guid.CreateVersion7()}, now(), now(), now())
             """);

        // A statement whose running balance does not add up is fiction.
        (await act.Should().ThrowAsync<PostgresException>())
            .Which.ConstraintName.Should().Be("ck_wallet_transactions_running_balance");
    }

    // ------------------------------------------------------------------------------ helpers

    private async Task<World> WorldAsync(Money openingBalance, [CallerMemberName] string testName = "")
    {
        var name = testName.ToLowerInvariant();
        name = name[..Math.Min(name.Length, 55)];

        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var tenancy = TestTenancy.None();

        var db = await _postgres.CreateEmptyDatabaseAsync(name, tenancy.Tenant, tenancy.Scope, clock);
        await db.Database.MigrateAsync();

        Guid agencyId;
        Guid walletId;

        using (var _ = tenancy.Scope.Enter("test setup — opening a funded wallet"))
        {
            var agency = Agency.RegisterPrincipal("Lagos Travel Limited", "lagos-travel", "NG", "NGN", "Africa/Lagos");
            agency.MarkVerified(clock.GetUtcNow());
            db.Agencies.Add(agency);

            var wallet = Wallet.OpenFor(agency.Id, "NGN");

            if (openingBalance.AmountMinor > 0)
            {
                wallet.Credit(openingBalance);
            }

            db.Wallets.Add(wallet);
            await db.SaveChangesAsync();

            agencyId = agency.Id;
            walletId = wallet.Id;
        }

        return new World(db, tenancy, clock, agencyId, walletId, name, _postgres);
    }

    private sealed class World : IAsyncDisposable
    {
        private readonly string _database;
        private readonly PostgresFixture _postgres;

        public World(
            AppDbContext db,
            (TenantContext Tenant, PlatformScope Scope) tenancy,
            ManualClock clock,
            Guid agencyId,
            Guid walletId,
            string database,
            PostgresFixture postgres)
        {
            Db = db;
            Tenancy = tenancy;
            Clock = clock;
            AgencyId = agencyId;
            WalletId = walletId;
            _database = database;
            _postgres = postgres;
        }

        public AppDbContext Db { get; }

        public (TenantContext Tenant, PlatformScope Scope) Tenancy { get; }

        public ManualClock Clock { get; }

        public Guid AgencyId { get; }

        public Guid WalletId { get; }

        /// <summary>A fresh context acting as the agency, as a separate request would be.</summary>
        public AppDbContext NewContext()
        {
            var tenancy = TestTenancy.For(AgencyId);
            return _postgres.Connect(_database, tenancy.Tenant, tenancy.Scope, Clock);
        }

        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }
}
