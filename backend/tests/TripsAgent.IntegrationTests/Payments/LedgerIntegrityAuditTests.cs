using System.Runtime.CompilerServices;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using TripsAgent.Application.Notifications;
using TripsAgent.Application.Payments;
using TripsAgent.Application.Tenancy;
using TripsAgent.Domain.Common;
using TripsAgent.Domain.Payments;
using TripsAgent.Domain.Tenancy;
using TripsAgent.Infrastructure.Payments;
using TripsAgent.Infrastructure.Persistence;
using TripsAgent.Infrastructure.Tenancy;
using TripsAgent.IntegrationTests.Identity;
using TripsAgent.IntegrationTests.Persistence;

namespace TripsAgent.IntegrationTests.Payments;

/// <summary>
/// The nightly audit, against real corruption.
/// </summary>
/// <remarks>
/// <para>
/// Every test here has to break the books first, and the database is built to make that hard —
/// a deferred constraint trigger refuses an unbalanced commit and the entries table has UPDATE
/// and DELETE revoked. So the setup disables the trigger, or writes as the owning role, to
/// manufacture the corruption the audit is supposed to find.
/// </para>
/// <para>
/// That is the point. An audit tested only against clean data proves it can return zero, which
/// is also what a completely broken audit returns.
/// </para>
/// </remarks>
[Collection(PostgresCollection.Name)]
public class LedgerIntegrityAuditTests
{
    private readonly PostgresFixture _postgres;

    public LedgerIntegrityAuditTests(PostgresFixture postgres) => _postgres = postgres;

    // ------------------------------------------------------------------- clean books

    [Fact]
    public async Task Balanced_books_produce_no_exceptions_and_no_alert()
    {
        await using var world = await WorldAsync();

        // A real, balanced top-up: ₦5,000.00 into the wallet.
        await world.PostBalanced(500_000);

        var result = await world.Audit.RunAsync();

        result.IsClean.Should().BeTrue();
        result.ChecksRun.Should().Be(LedgerIntegrityAudit.CheckCount);
        result.NewExceptions.Should().Be(0);

        world.Alerts.Should().BeEmpty("a clean run must never page anyone");

        using var _ = world.Tenancy.Scope.Enter("test — no exceptions on file");
        (await world.Db.ReconciliationExceptions.CountAsync()).Should().Be(0);
    }

    // ------------------------------------------------------------------- check 1

    [Fact]
    public async Task A_deliberate_imbalance_is_caught_and_raises_a_P1()
    {
        await using var world = await WorldAsync();

        // ₦1,500.00 debited, ₦1,400.00 credited. A hundred naira from nowhere — which the
        // balance trigger normally refuses, so the trigger is switched off to plant it.
        var group = await world.PostUnbalanced(debitMinor: 150_000, creditMinor: 140_000);

        var result = await world.Audit.RunAsync();

        result.IsClean.Should().BeFalse();
        result.NewExceptions.Should().Be(1);

        using var _ = world.Tenancy.Scope.Enter("test — reading the exception");

        var exception = await world.Db.ReconciliationExceptions.AsNoTracking().SingleAsync();
        exception.Check.Should().Be(ReconciliationCheck.UnbalancedTransaction);
        exception.Severity.Should().Be(ReconciliationSeverity.P1);
        exception.Subject.Should().Be(group.ToString());
        exception.Status.Should().Be(ReconciliationStatus.Open);
        exception.ExpectedMinor.AmountMinor.Should().Be(150_000);
        exception.ActualMinor.AmountMinor.Should().Be(140_000);
        exception.DifferenceMinor.AmountMinor.Should().Be(-10_000);

        // The detail is read by someone woken up at 03:00, so it has to say what happened
        // without them going to look anything up.
        exception.Detail.Should().Contain("does not balance");
        exception.Detail.Should().Contain("1500.00");
        exception.Detail.Should().Contain("1400.00");

        var alert = world.Alerts.Should().ContainSingle().Subject;
        alert.Severity.Should().Be(AlertSeverity.P1);
        alert.Source.Should().Be(nameof(LedgerIntegrityAudit));
        alert.Detail.Should().Contain("does not balance");
    }

    [Fact]
    public async Task A_balanced_group_beside_a_broken_one_is_not_reported()
    {
        await using var world = await WorldAsync();

        await world.PostBalanced(500_000);
        await world.PostBalanced(250_000);
        var broken = await world.PostUnbalanced(debitMinor: 100_000, creditMinor: 99_999);

        var result = await world.Audit.RunAsync();

        // Only the broken one. A check that flags healthy groups is worse than none, because the
        // response to a noisy P1 is to stop reading them.
        result.NewExceptions.Should().Be(1);

        using var _ = world.Tenancy.Scope.Enter("test — only the broken group");
        var exception = await world.Db.ReconciliationExceptions.AsNoTracking().SingleAsync();
        exception.Subject.Should().Be(broken.ToString());
    }

    // ------------------------------------------------------------------- check 2

    [Fact]
    public async Task A_wallet_balance_that_drifts_from_the_ledger_is_caught()
    {
        await using var world = await WorldAsync();

        await world.PostBalanced(500_000);

        // Move the wallet without moving the ledger — what a hand-written UPDATE in psql does.
        await world.Execute(
            $"UPDATE payments.wallets SET balance_minor = 900000 WHERE agency_id = '{world.AgencyId}'");

        var result = await world.Audit.RunAsync();

        result.NewExceptions.Should().Be(1);

        using var _ = world.Tenancy.Scope.Enter("test — reading the drift");

        var exception = await world.Db.ReconciliationExceptions.AsNoTracking().SingleAsync();
        exception.Check.Should().Be(ReconciliationCheck.WalletBalanceDrift);
        exception.Severity.Should().Be(ReconciliationSeverity.P1);

        // The ledger is the truth, so it is the expectation and the wallet is what is wrong.
        exception.ExpectedMinor.AmountMinor.Should().Be(500_000);
        exception.ActualMinor.AmountMinor.Should().Be(900_000);
        exception.AgencyId.Should().Be(world.AgencyId);

        world.Alerts.Should().ContainSingle().Which.Severity.Should().Be(AlertSeverity.P1);
    }

    [Fact]
    public async Task A_wallet_with_no_entries_at_all_is_clean_at_zero()
    {
        await using var world = await WorldAsync();

        // A brand-new agency: a wallet at zero and an empty ledger. The join finds no account
        // balance, and COALESCE has to turn that into 0 rather than NULL — otherwise every new
        // agency on the platform reports as drifted the first night.
        var result = await world.Audit.RunAsync();

        result.IsClean.Should().BeTrue();
        world.Alerts.Should().BeEmpty();
    }

    // ------------------------------------------------------------------- check 4

    [Fact]
    public async Task A_hold_past_its_deadline_is_caught_as_a_P2_not_a_P1()
    {
        await using var world = await WorldAsync();

        await world.PostBalanced(500_000);

        using (var _ = world.Tenancy.Scope.Enter("test setup — a hold that will expire"))
        {
            // The fixtures move balance_minor with raw SQL, so the wallet the context has been
            // tracking since setup still reads zero. Drop it and read the real row.
            world.Db.ChangeTracker.Clear();
            var wallet = await world.Db.Wallets.SingleAsync();

            // Placed an hour ago with a one-minute deadline, so it is long overdue by now.
            // Added explicitly: there is no navigation from Wallet to its holds, so the hold
            // PlaceHold returns is not tracked by reachability and would never be saved.
            var hold = wallet.PlaceHold(
                new Money(100_000), world.Clock.GetUtcNow().AddHours(-1), TimeSpan.FromMinutes(1));

            world.Db.WalletHolds.Add(hold);
            await world.Db.SaveChangesAsync();
        }

        var result = await world.Audit.RunAsync();

        result.NewExceptions.Should().Be(1);

        using var scope = world.Tenancy.Scope.Enter("test — reading the stale hold");

        var exception = await world.Db.ReconciliationExceptions.AsNoTracking().SingleAsync();
        exception.Check.Should().Be(ReconciliationCheck.ExpiredHoldOutstanding);

        // P2 on purpose. The money is still accounted for — a hold reserves without moving — so
        // no figure is wrong and the sweeper is simply behind. A P1 that is usually nothing is a
        // P1 nobody reads.
        exception.Severity.Should().Be(ReconciliationSeverity.P2);

        var alert = world.Alerts.Should().ContainSingle().Subject;
        alert.Severity.Should().Be(AlertSeverity.P2);
        alert.Detail.Should().Contain("sweeper");
    }

    [Fact]
    public async Task A_hold_still_inside_its_deadline_is_left_alone()
    {
        await using var world = await WorldAsync();

        await world.PostBalanced(500_000);

        using (var _ = world.Tenancy.Scope.Enter("test setup — a live hold"))
        {
            // The fixtures move balance_minor with raw SQL, so the wallet the context has been
            // tracking since setup still reads zero. Drop it and read the real row.
            world.Db.ChangeTracker.Clear();
            var wallet = await world.Db.Wallets.SingleAsync();
            world.Db.WalletHolds.Add(
                wallet.PlaceHold(new Money(100_000), world.Clock.GetUtcNow(), TimeSpan.FromHours(2)));

            await world.Db.SaveChangesAsync();
        }

        (await world.Audit.RunAsync()).IsClean.Should().BeTrue();
        world.Alerts.Should().BeEmpty();
    }

    // ------------------------------------------------------------------- repeat runs

    [Fact]
    public async Task A_second_run_updates_the_existing_exception_rather_than_adding_another()
    {
        await using var world = await WorldAsync();

        await world.PostUnbalanced(debitMinor: 150_000, creditMinor: 140_000);

        var first = await world.Audit.RunAsync();
        first.NewExceptions.Should().Be(1);
        first.RecurringExceptions.Should().Be(0);

        var second = await world.Audit.RunAsync();
        second.NewExceptions.Should().Be(0);
        second.RecurringExceptions.Should().Be(1);

        using var _ = world.Tenancy.Scope.Enter("test — still one row");

        // One row per problem, not one per night. A hundred rows for one unresolved issue would
        // bury everything else in the queue.
        var exception = await world.Db.ReconciliationExceptions.AsNoTracking().SingleAsync();
        exception.TimesSeen.Should().Be(2, "a rising count is how you see that nobody is looking");
        exception.LastSeenAt.Should().NotBeNull();
    }

    [Fact]
    public async Task A_problem_declared_resolved_that_comes_back_is_reopened()
    {
        await using var world = await WorldAsync();

        await world.PostUnbalanced(debitMinor: 150_000, creditMinor: 140_000);
        await world.Audit.RunAsync();

        using (var _ = world.Tenancy.Scope.Enter("test — someone closes it"))
        {
            var exception = await world.Db.ReconciliationExceptions.SingleAsync();
            exception.Resolve("Corrected by hand in DB-1234.", world.Clock.GetUtcNow());
            await world.Db.SaveChangesAsync();
        }

        // But it was not actually fixed, so tonight's run finds it again.
        await world.Audit.RunAsync();

        using var scope = world.Tenancy.Scope.Enter("test — reopened");

        var reopened = await world.Db.ReconciliationExceptions.AsNoTracking().SingleAsync();

        // Reopened, because "we fixed it" followed by "no you didn't" is worse than never having
        // closed it — and a resolved row would sit unread in a filtered queue forever.
        reopened.Status.Should().Be(ReconciliationStatus.Open);
        reopened.ResolvedAt.Should().BeNull();
        reopened.ResolutionNote.Should().BeNull();
    }

    [Fact]
    public async Task Once_the_books_are_corrected_a_run_is_clean_again()
    {
        await using var world = await WorldAsync();

        var group = await world.PostUnbalanced(debitMinor: 150_000, creditMinor: 140_000);
        (await world.Audit.RunAsync()).IsClean.Should().BeFalse();

        // Correct it the way a fix actually would: a balancing entry, not an edit.
        await world.AddEntryTo(group, "Credit", 10_000);

        var result = await world.Audit.RunAsync();

        result.NewExceptions.Should().Be(0);
        result.RecurringExceptions.Should().Be(0);
        result.IsClean.Should().BeTrue();

        using var _ = world.Tenancy.Scope.Enter("test — the record stays");

        // The exception row stays, still open. "This happened once and here is what we did" is
        // the whole value of the table, so nothing deletes it — a person closes it with a note.
        var exception = await world.Db.ReconciliationExceptions.AsNoTracking().SingleAsync();
        exception.Status.Should().Be(ReconciliationStatus.Open);
        exception.TimesSeen.Should().Be(1);
    }

    // ------------------------------------------------------------------- several at once

    [Fact]
    public async Task Several_different_problems_are_each_recorded_and_alerted_by_severity()
    {
        await using var world = await WorldAsync();

        await world.PostBalanced(500_000);
        await world.PostUnbalanced(debitMinor: 150_000, creditMinor: 140_000);

        await world.Execute(
            $"UPDATE payments.wallets SET balance_minor = 777000 WHERE agency_id = '{world.AgencyId}'");

        using (var _ = world.Tenancy.Scope.Enter("test setup — and a stale hold"))
        {
            // The fixtures move balance_minor with raw SQL, so the wallet the context has been
            // tracking since setup still reads zero. Drop it and read the real row.
            world.Db.ChangeTracker.Clear();
            var wallet = await world.Db.Wallets.SingleAsync();
            world.Db.WalletHolds.Add(
                wallet.PlaceHold(new Money(1_000), world.Clock.GetUtcNow().AddHours(-2), TimeSpan.FromMinutes(1)));

            await world.Db.SaveChangesAsync();
        }

        var result = await world.Audit.RunAsync();

        result.NewExceptions.Should().Be(3);

        using var scope = world.Tenancy.Scope.Enter("test — three distinct problems");

        var checks = await world.Db.ReconciliationExceptions
            .AsNoTracking()
            .Select(exception => exception.Check)
            .ToListAsync();

        checks.Should().BeEquivalentTo([
            ReconciliationCheck.UnbalancedTransaction,
            ReconciliationCheck.WalletBalanceDrift,
            ReconciliationCheck.ExpiredHoldOutstanding,
        ]);

        // Two alerts, not three and not one: the P1s are batched into a single interruption, and
        // the P2 is kept separate so it cannot dilute them.
        world.Alerts.Should().HaveCount(2);
        world.Alerts.Count(alert => alert.Severity == AlertSeverity.P1).Should().Be(1);
        world.Alerts.Count(alert => alert.Severity == AlertSeverity.P2).Should().Be(1);
    }

    // ------------------------------------------------------------------- performance

    [Fact]
    public async Task The_audit_stays_fast_with_a_hundred_thousand_entries()
    {
        await using var world = await WorldAsync();

        // 100,000 entries across 50,000 balanced groups, inserted in bulk. The acceptance
        // criterion is that the audit is still quick at this size, which it only can be because
        // every check is one aggregate query — the version that walks groups in C# takes minutes.
        await world.PostBulkBalanced(groups: 50_000);

        var result = await world.Audit.RunAsync();

        result.IsClean.Should().BeTrue();

        // A generous ceiling: the point is to catch an accidental N+1 or a client-side grouping,
        // both of which are orders of magnitude slower rather than marginally.
        result.Duration.Should().BeLessThan(
            TimeSpan.FromSeconds(30),
            "the checks must be aggregate queries, not row-by-row work");
    }

    [Fact]
    public async Task A_single_imbalance_is_still_found_among_a_hundred_thousand_entries()
    {
        await using var world = await WorldAsync();

        await world.PostBulkBalanced(groups: 50_000);
        var needle = await world.PostUnbalanced(debitMinor: 100_000, creditMinor: 99_999);

        var result = await world.Audit.RunAsync();

        // One kobo out, in a haystack. This is the case the job exists for.
        result.NewExceptions.Should().Be(1);

        using var _ = world.Tenancy.Scope.Enter("test — found the needle");
        var exception = await world.Db.ReconciliationExceptions.AsNoTracking().SingleAsync();
        exception.Subject.Should().Be(needle.ToString());
        exception.DifferenceMinor.AmountMinor.Should().Be(-1);
    }

    // ------------------------------------------------------------------- helpers

    private async Task<World> WorldAsync([CallerMemberName] string testName = "")
    {
        var name = testName.ToLowerInvariant();
        name = name[..Math.Min(name.Length, 55)];

        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var tenancy = TestTenancy.None();

        var db = await _postgres.CreateEmptyDatabaseAsync(name, tenancy.Tenant, tenancy.Scope, clock);
        await db.Database.MigrateAsync();

        Guid agencyId;
        Guid walletAccountId;
        Guid clearingAccountId;

        using (var _ = tenancy.Scope.Enter("test setup — an agency, a wallet and ledger accounts"))
        {
            var agency = Agency.RegisterPrincipal("Lagos Travel Limited", "lagos-travel", "NG", "NGN", "Africa/Lagos");
            agency.MarkVerified(clock.GetUtcNow());
            db.Agencies.Add(agency);

            db.Wallets.Add(Wallet.OpenFor(agency.Id, "NGN"));

            var wallet = LedgerAccount.ForAgency(agency.Id, LedgerAccountType.AgencyWallet, "NGN", "Wallet");
            var clearing = LedgerAccount.ForPlatform(LedgerAccountType.GatewayClearing, "NGN", "Gateway clearing");

            db.LedgerAccounts.AddRange(wallet, clearing);
            await db.SaveChangesAsync();

            agencyId = agency.Id;
            walletAccountId = wallet.Id;
            clearingAccountId = clearing.Id;
        }

        var alerts = new List<PlatformAlert>();

        var audit = new LedgerIntegrityAudit(
            db,
            new LedgerIntegrityQueries(db),
            tenancy.Scope,
            new CapturingAlerter(alerts),
            clock,
            NullLogger<LedgerIntegrityAudit>.Instance);

        return new World(db, tenancy, clock, agencyId, walletAccountId, clearingAccountId, audit, alerts);
    }

    /// <summary>Collects alerts so a test can assert on what would have woken someone up.</summary>
    private sealed class CapturingAlerter : IPlatformAlerter
    {
        private readonly List<PlatformAlert> _alerts;

        public CapturingAlerter(List<PlatformAlert> alerts) => _alerts = alerts;

        public Task RaiseAsync(PlatformAlert alert, CancellationToken cancellationToken = default)
        {
            _alerts.Add(alert);
            return Task.CompletedTask;
        }
    }

    private sealed class World : IAsyncDisposable
    {
        public World(
            AppDbContext db,
            (TenantContext Tenant, PlatformScope Scope) tenancy,
            ManualClock clock,
            Guid agencyId,
            Guid walletAccountId,
            Guid clearingAccountId,
            LedgerIntegrityAudit audit,
            List<PlatformAlert> alerts)
        {
            Db = db;
            Tenancy = tenancy;
            Clock = clock;
            AgencyId = agencyId;
            WalletAccountId = walletAccountId;
            ClearingAccountId = clearingAccountId;
            Audit = audit;
            Alerts = alerts;
        }

        public AppDbContext Db { get; }

        public (TenantContext Tenant, PlatformScope Scope) Tenancy { get; }

        public ManualClock Clock { get; }

        public Guid AgencyId { get; }

        public Guid WalletAccountId { get; }

        public Guid ClearingAccountId { get; }

        public LedgerIntegrityAudit Audit { get; }

        public List<PlatformAlert> Alerts { get; }

        /// <summary>A proper top-up: balanced entries, and the wallet moved to match.</summary>
        public async Task PostBalanced(long amountMinor)
        {
            var group = Guid.CreateVersion7();

            await using var connection = await OpenAsync();
            await using var transaction = await connection.BeginTransactionAsync();

            await InsertEntry(connection, transaction, group, ClearingAccountId, "Debit", amountMinor);
            await InsertEntry(connection, transaction, group, WalletAccountId, "Credit", amountMinor);

            await transaction.CommitAsync();
        }

        /// <summary>
        /// Entries that do not balance, planted past the trigger that forbids them.
        /// </summary>
        /// <remarks>
        /// <c>SET CONSTRAINTS ... DEFERRED</c> is not enough — the trigger is already deferred,
        /// so it would simply fire at commit. The trigger itself has to be off for the
        /// transaction, which needs the owning role, and that is exactly why this corruption
        /// cannot happen through the application.
        /// </remarks>
        public async Task<Guid> PostUnbalanced(long debitMinor, long creditMinor)
        {
            var group = Guid.CreateVersion7();

            await using var connection = await OpenAsync();
            await using var transaction = await connection.BeginTransactionAsync();

            await ExecuteIn(connection, transaction,
                "ALTER TABLE payments.ledger_entries DISABLE TRIGGER ledger_entries_balanced_trg");

            await InsertEntry(connection, transaction, group, ClearingAccountId, "Debit", debitMinor);
            await InsertEntry(connection, transaction, group, WalletAccountId, "Credit", creditMinor);

            await ExecuteIn(connection, transaction,
                "ALTER TABLE payments.ledger_entries ENABLE TRIGGER ledger_entries_balanced_trg");

            await transaction.CommitAsync();

            return group;
        }

        /// <summary>Adds one entry to an existing group, the way a correcting entry would.</summary>
        public async Task AddEntryTo(Guid group, string direction, long amountMinor)
        {
            await using var connection = await OpenAsync();
            await using var transaction = await connection.BeginTransactionAsync();

            // Still needs the trigger off: it checks the whole group, and the group is currently
            // unbalanced until this very entry lands.
            await ExecuteIn(connection, transaction,
                "ALTER TABLE payments.ledger_entries DISABLE TRIGGER ledger_entries_balanced_trg");

            var account = direction == "Credit" ? WalletAccountId : ClearingAccountId;
            await InsertEntry(connection, transaction, group, account, direction, amountMinor);

            await ExecuteIn(connection, transaction,
                "ALTER TABLE payments.ledger_entries ENABLE TRIGGER ledger_entries_balanced_trg");

            await transaction.CommitAsync();
        }

        /// <summary>
        /// Bulk balanced entries, for the size test.
        /// </summary>
        /// <remarks>
        /// Written as one <c>generate_series</c> insert rather than a loop: 100,000 round trips
        /// would make the setup slower than the thing being measured, and the wallet is moved
        /// once at the end to match.
        /// </remarks>
        public async Task PostBulkBalanced(int groups)
        {
            await using var connection = await OpenAsync();
            await using var transaction = await connection.BeginTransactionAsync();

            await ExecuteIn(connection, transaction,
                $"""
                 INSERT INTO payments.ledger_entries
                     (id, transaction_group_id, account_id, agency_id, direction, amount_minor,
                      reference_type, reference_id, description, occurred_at)
                 SELECT gen_random_uuid(),
                        g.group_id,
                        CASE WHEN d.direction = 'Debit'
                             THEN '{ClearingAccountId}'::uuid
                             ELSE '{WalletAccountId}'::uuid END,
                        CASE WHEN d.direction = 'Debit'
                             THEN NULL
                             ELSE '{AgencyId}'::uuid END,
                        d.direction,
                        100,
                        'bulk',
                        NULL,
                        'bulk entry',
                        now()
                 FROM (SELECT gen_random_uuid() AS group_id FROM generate_series(1, {groups})) g
                 CROSS JOIN (VALUES ('Debit'), ('Credit')) AS d(direction)
                 """);

            // Each group credits the wallet 100 kobo, so the projection has to move to match or
            // the drift check would report this whole fixture as broken.
            await ExecuteIn(connection, transaction,
                $"UPDATE payments.wallets SET balance_minor = balance_minor + {groups * 100L} "
                + $"WHERE agency_id = '{AgencyId}'");

            await transaction.CommitAsync();
        }

        /// <summary>Runs SQL outside the domain, the way a support script would.</summary>
        public async Task Execute(string sql)
        {
            await using var connection = await OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync();
        }

        private async Task<NpgsqlConnection> OpenAsync()
        {
            var connection = new NpgsqlConnection(Db.Database.GetConnectionString());
            await connection.OpenAsync();
            return connection;
        }

        private static async Task ExecuteIn(
            NpgsqlConnection connection, NpgsqlTransaction transaction, string sql)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync();
        }

        private async Task InsertEntry(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            Guid group,
            Guid accountId,
            string direction,
            long amountMinor)
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
            command.Parameters.AddWithValue(group);
            command.Parameters.AddWithValue(accountId);
            command.Parameters.AddWithValue(
                accountId == WalletAccountId ? AgencyId : (object)DBNull.Value);
            command.Parameters.AddWithValue(direction);
            command.Parameters.AddWithValue(amountMinor);

            await command.ExecuteNonQueryAsync();

            if (accountId != WalletAccountId)
            {
                return;
            }

            // Keep wallets.balance_minor in step with the wallet's ledger account, so a test that
            // plants an imbalance exhibits *only* that. Without this, every unbalanced group also
            // showed up as wallet drift — two findings where the test meant one, and it was the
            // test that was wrong rather than the audit.
            //
            // The wallet account is credit-normal: a credit increases what we owe the agency.
            var delta = direction == "Credit" ? amountMinor : -amountMinor;

            await ExecuteIn(connection, transaction,
                $"UPDATE payments.wallets SET balance_minor = balance_minor + {delta} "
                + $"WHERE agency_id = '{AgencyId}'");
        }

        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }
}
