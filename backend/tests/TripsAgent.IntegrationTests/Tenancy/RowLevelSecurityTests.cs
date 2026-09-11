using System.Runtime.CompilerServices;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using TripsAgent.Application.Tenancy;
using TripsAgent.Domain.Payments;
using TripsAgent.Domain.Tenancy;
using TripsAgent.Infrastructure.Persistence;
using TripsAgent.Infrastructure.Tenancy;
using TripsAgent.IntegrationTests.Persistence;

namespace TripsAgent.IntegrationTests.Tenancy;

/// <summary>
/// The row-level security backstop, proven against the database as the role production runs as.
/// Issue #12, ADR-0006.
/// </summary>
/// <remarks>
/// <para>
/// The EF filters are the first line of tenant isolation and have their own tests. These switch the
/// filter off on purpose — <c>IgnoreQueryFilters()</c>, raw SQL — and show that PostgreSQL still
/// refuses to cross tenants. That is the whole claim of a backstop, so it is the thing to test.
/// </para>
/// <para>
/// Every context here connects as <c>tripsagent_app</c>. A superuser skips every policy, so a test
/// run as one would pass whether the policies worked or not.
/// </para>
/// </remarks>
[Collection(PostgresCollection.Name)]
public class RowLevelSecurityTests
{
    private readonly PostgresFixture _postgres;

    public RowLevelSecurityTests(PostgresFixture postgres) => _postgres = postgres;

    // ------------------------------------------------------------------ the role

    [Fact]
    public async Task The_application_role_is_policed_rather_than_privileged()
    {
        await using var world = await WorldAsync();

        var (super, bypass) = await world.AdminScalarAsync<(bool, bool)>(
            "SELECT rolsuper, rolbypassrls FROM pg_roles WHERE rolname = 'tripsagent_app'",
            reader => (reader.GetBoolean(0), reader.GetBoolean(1)));

        super.Should().BeFalse("a superuser skips every policy");
        bypass.Should().BeFalse("BYPASSRLS skips every policy");
    }

    // ------------------------------------------------------------------ reads

    [Fact]
    public async Task Removing_the_EF_filter_still_cannot_cross_tenants()
    {
        await using var world = await WorldAsync();
        await using var asA = world.ActingAs(world.AgencyA);

        // The acceptance criterion, literally: the EF filter is gone, and B's wallet still is not there.
        var visible = await asA.Db.Wallets.IgnoreQueryFilters().Select(w => w.AgencyId).ToListAsync();

        visible.Should().Equal(world.AgencyA);
    }

    [Fact]
    public async Task A_raw_query_with_the_wrong_agency_returns_nothing()
    {
        await using var world = await WorldAsync();

        (await world.CountWalletsAsApplicationRole(agencyId: Guid.CreateVersion7())).Should().Be(0);
        (await world.CountWalletsAsApplicationRole(agencyId: world.AgencyA)).Should().Be(1);
    }

    [Fact]
    public async Task With_no_tenant_and_no_scope_nothing_is_visible()
    {
        await using var world = await WorldAsync();
        await using var anonymous = world.Anonymous();

        // Fail closed: an unset setting is NULL, and NULL matches no agency.
        (await anonymous.Db.Wallets.IgnoreQueryFilters().CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task The_platform_scope_sees_every_agency()
    {
        await using var world = await WorldAsync();
        await using var anonymous = world.Anonymous();

        using (anonymous.Scope.Enter("test — the audited cross-tenant route"))
        {
            (await anonymous.Db.Wallets.IgnoreQueryFilters().CountAsync()).Should().Be(2);
        }
    }

    // ------------------------------------------------------------------ writes

    [Fact]
    public async Task A_tenant_cannot_insert_a_row_into_another_agency()
    {
        await using var world = await WorldAsync();
        await using var asA = world.ActingAs(world.AgencyA);

        // Raw SQL, around EF and its tenant-stamping guard, so this is the database refusing.
        var act = () => asA.Db.Database.ExecuteSqlInterpolatedAsync(
            $"""
             INSERT INTO payments.wallets
                 (id, agency_id, currency, balance_minor, reserved_minor, status, version, created_at, updated_at)
             VALUES ({Guid.CreateVersion7()}, {world.AgencyB}, 'USD', 0, 0, 'Active', 0, now(), now())
             """);

        (await act.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
    }

    [Fact]
    public async Task A_tenant_cannot_move_its_own_row_into_another_agency()
    {
        await using var world = await WorldAsync();
        await using var asA = world.ActingAs(world.AgencyA);

        var act = () => asA.Db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE payments.wallets SET agency_id = {world.AgencyB} WHERE agency_id = {world.AgencyA}");

        (await act.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
    }

    [Fact]
    public async Task A_tenant_cannot_touch_another_agencys_row_at_all()
    {
        await using var world = await WorldAsync();
        await using var asA = world.ActingAs(world.AgencyA);

        // Not an error — B's row is simply invisible, so there is nothing to update or delete.
        var updated = await asA.Db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE payments.wallets SET status = 'Frozen' WHERE agency_id = {world.AgencyB}");
        var deleted = await asA.Db.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM payments.wallet_transactions WHERE agency_id = {world.AgencyB}");

        updated.Should().Be(0);
        deleted.Should().Be(0);

        (await world.AdminScalarAsync($"SELECT status FROM payments.wallets WHERE agency_id = '{world.AgencyB}'",
            reader => reader.GetString(0))).Should().Be("Active");
    }

    [Fact]
    public async Task The_append_only_ledger_now_refuses_the_application_role()
    {
        await using var world = await WorldAsync();
        await using var anonymous = world.Anonymous();

        // The REVOKEs in earlier migrations were conditional on this role existing, and until this
        // change it never did. Even inside the platform scope, UPDATE is simply not permitted.
        using var _ = anonymous.Scope.Enter("test — proving the revoke");

        var act = () => anonymous.Db.Database.ExecuteSqlRawAsync(
            "UPDATE payments.ledger_entries SET description = 'rewritten'");

        (await act.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
    }

    // ------------------------------------------------------------------ the interceptor

    [Fact]
    public async Task The_setting_follows_the_platform_scope_on_one_open_connection()
    {
        await using var world = await WorldAsync();
        await using var asA = world.ActingAs(world.AgencyA);

        // Held open, so the interceptor cannot rely on a fresh ConnectionOpened to catch up.
        await asA.Db.Database.OpenConnectionAsync();

        (await asA.Db.Wallets.IgnoreQueryFilters().CountAsync()).Should().Be(1);

        using (asA.Scope.Enter("test — scope opened mid-connection"))
        {
            (await asA.Db.Wallets.IgnoreQueryFilters().CountAsync()).Should().Be(2);
        }

        (await asA.Db.Wallets.IgnoreQueryFilters().CountAsync()).Should().Be(1, "closing the scope must close it in the database too");
    }

    [Fact]
    public async Task A_pooled_connection_does_not_carry_the_previous_tenant_into_the_next_caller()
    {
        await using var world = await WorldAsync();

        // One physical connection in the pool, so the second context is handed the very connection
        // the first one used while acting as agency A.
        await using (var asA = world.ActingAs(world.AgencyA, pooled: true))
        {
            (await asA.Db.Wallets.IgnoreQueryFilters().CountAsync()).Should().Be(1);
        }

        await using (var anonymous = world.Anonymous(pooled: true))
        {
            (await anonymous.Db.Wallets.IgnoreQueryFilters().CountAsync()).Should().Be(0, "the next caller has no tenant");
        }

        NpgsqlConnection.ClearPool(new NpgsqlConnection(_postgres.ConnectionStringFor(world.Database, asApplicationRole: true, pooled: true)));
    }

    [Fact]
    public async Task A_rolled_back_transaction_does_not_leave_the_connection_describing_the_wrong_scope()
    {
        await using var world = await WorldAsync();
        await using var asA = world.ActingAs(world.AgencyA);

        await asA.Db.Database.OpenConnectionAsync();

        using (asA.Scope.Enter("test — a scope that outlives a rollback"))
        {
            await using (var transaction = await asA.Db.Database.BeginTransactionAsync())
            {
                // The scope's setting is written inside this transaction...
                (await asA.Db.Wallets.IgnoreQueryFilters().CountAsync()).Should().Be(2);

                // ...and PostgreSQL undoes it on rollback, back to "agency A, no scope".
                await transaction.RollbackAsync();
            }

            // Still inside the scope, so every agency must still be visible — the interceptor has to
            // notice the setting it wrote is gone, rather than trust it.
            (await asA.Db.Wallets.IgnoreQueryFilters().CountAsync()).Should().Be(2);
        }
    }

    // ------------------------------------------------------------------ integrity triggers

    [Fact]
    public async Task The_ledger_balance_check_sees_the_whole_group_whatever_the_callers_tenant()
    {
        await using var world = await WorldAsync();
        await using var asA = world.ActingAs(world.AgencyA);

        await using var transaction = await asA.Db.Database.BeginTransactionAsync();
        var group = Guid.CreateVersion7();

        using (asA.Scope.Enter("test — a balanced group spanning the platform and agency A"))
        {
            await World.InsertEntryAsync(asA.Db, group, world.ClearingAccountId, agencyId: null, "Debit", 10_000);
            await World.InsertEntryAsync(asA.Db, group, world.WalletAccountA, world.AgencyA, "Credit", 10_000);
        }

        // Out of the scope before COMMIT, so the deferred balance check runs while the connection is
        // acting as agency A alone. Run as the caller, it would see only A's credit and reject a
        // balanced group; run as its owner, it sees both entries.
        await asA.Db.Database.ExecuteSqlRawAsync("SELECT 1");

        var commit = () => transaction.CommitAsync();
        await commit.Should().NotThrowAsync();
    }

    [Fact]
    public async Task The_triggers_that_read_rows_run_as_their_owner()
    {
        await using var world = await WorldAsync();

        var invokers = await world.AdminListAsync(
            """
            SELECT n.nspname || '.' || p.proname
              FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace
             WHERE (n.nspname, p.proname) IN (('payments', 'assert_ledger_group_balanced'),
                                             ('tenancy',  'agencies_compute_path'),
                                             ('tenancy',  'agencies_reparent_descendants'))
               AND NOT p.prosecdef
            """);

        invokers.Should().BeEmpty("an integrity trigger run as the caller sees only the caller's rows");
    }

    // ------------------------------------------------------------------ coverage

    /// <summary>Tables with an agency_id that deliberately have no policy, and why.</summary>
    private static readonly Dictionary<string, string> Exempt = new()
    {
        ["platform.audit_logs"] = "has its own filter keyed on the audit actor, and platform-wide rows with no agency",
        ["platform.admin_alerts"] = "written from agency requests, read across every agency by Trips staff",
        ["platform.outbox_messages"] = "infrastructure: the dispatcher publishes every agency's messages",
        ["payments.reconciliation_exceptions"] = "platform-only findings about the platform's own books",
    };

    [Fact]
    public async Task Every_table_with_an_agency_id_is_policed_or_deliberately_exempt()
    {
        await using var world = await WorldAsync();

        var unpoliced = await world.AdminListAsync(
            """
            SELECT n.nspname || '.' || c.relname
              FROM pg_class c
              JOIN pg_namespace n ON n.oid = c.relnamespace
             WHERE c.relkind IN ('r', 'p')
               AND NOT c.relispartition
               AND n.nspname IN ('tenancy', 'identity', 'payments', 'platform', 'supplier')
               AND EXISTS (SELECT 1 FROM information_schema.columns col
                            WHERE col.table_schema = n.nspname
                              AND col.table_name = c.relname
                              AND col.column_name = 'agency_id')
               AND NOT (c.relrowsecurity AND c.relforcerowsecurity
                        AND EXISTS (SELECT 1 FROM pg_policies p
                                     WHERE p.schemaname = n.nspname AND p.tablename = c.relname))
            """);

        unpoliced.Except(Exempt.Keys).Should().BeEmpty(
            "a table owned by an agency needs ENABLE and FORCE ROW LEVEL SECURITY and a tenant_isolation "
            + "policy — see AddRowLevelSecurity — or an entry in Exempt saying why not");
    }

    [Fact]
    public async Task The_exemptions_and_the_credential_tables_are_what_they_claim()
    {
        await using var world = await WorldAsync();

        // An exemption for a table that no longer exists is a list rotting quietly.
        var existing = await world.AdminListAsync(
            "SELECT n.nspname || '.' || c.relname FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace");

        existing.Should().Contain(Exempt.Keys);

        // Tables policed without an agency_id column, which the coverage query above cannot see.
        var policed = await world.AdminListAsync(
            "SELECT schemaname || '.' || tablename FROM pg_policies WHERE policyname = 'tenant_isolation'");

        policed.Should().Contain(
            ["tenancy.agencies", "identity.refresh_tokens", "identity.password_reset_tokens", "identity.otp_codes"]);
    }

    // ------------------------------------------------------------------ helpers

    private async Task<World> WorldAsync([CallerMemberName] string testName = "")
    {
        var name = $"rls_{testName.ToLowerInvariant()}";
        name = name[..Math.Min(name.Length, 60)];

        var tenancy = TestTenancy.None();

        await using (var setup = await _postgres.CreateEmptyDatabaseAsync(name, tenancy.Tenant, tenancy.Scope))
        {
            await setup.Database.MigrateAsync();

            using var _ = tenancy.Scope.Enter("test setup — two agencies, each with a wallet");

            var a = Agency.RegisterPrincipal("Lagos Travel Limited", "lagos-travel", "NG", "NGN", "Africa/Lagos");
            var b = Agency.RegisterPrincipal("Abuja Tours Limited", "abuja-tours", "NG", "NGN", "Africa/Lagos");
            a.MarkVerified(DateTimeOffset.UtcNow);
            b.MarkVerified(DateTimeOffset.UtcNow);

            var walletAccountA = LedgerAccount.ForAgency(a.Id, LedgerAccountType.AgencyWallet, "NGN", "Wallet A");
            var clearing = LedgerAccount.ForPlatform(LedgerAccountType.GatewayClearing, "NGN", "Gateway clearing");

            setup.Agencies.AddRange(a, b);
            setup.Wallets.AddRange(Wallet.OpenFor(a.Id, "NGN"), Wallet.OpenFor(b.Id, "NGN"));
            setup.LedgerAccounts.AddRange(walletAccountA, clearing);
            await setup.SaveChangesAsync();

            return new World(_postgres, name, a.Id, b.Id, walletAccountA.Id, clearing.Id);
        }
    }

    private sealed class World : IAsyncDisposable
    {
        private readonly PostgresFixture _postgres;
        private readonly List<Session> _sessions = [];

        public World(PostgresFixture postgres, string database, Guid agencyA, Guid agencyB, Guid walletAccountA, Guid clearingAccountId)
        {
            _postgres = postgres;
            Database = database;
            AgencyA = agencyA;
            AgencyB = agencyB;
            WalletAccountA = walletAccountA;
            ClearingAccountId = clearingAccountId;
        }

        public string Database { get; }

        public Guid AgencyA { get; }

        public Guid AgencyB { get; }

        public Guid WalletAccountA { get; }

        public Guid ClearingAccountId { get; }

        /// <summary>A context acting as <paramref name="agencyId"/>, as the policed role.</summary>
        public Session ActingAs(Guid agencyId, bool pooled = false) => Open(TestTenancy.For(agencyId), pooled);

        /// <summary>A context with no tenant — an anonymous request or a background job — as the policed role.</summary>
        public Session Anonymous(bool pooled = false) => Open(TestTenancy.None(), pooled);

        private Session Open((TenantContext Tenant, PlatformScope Scope) tenancy, bool pooled)
        {
            var db = _postgres.Connect(Database, tenancy.Tenant, tenancy.Scope, pooled: pooled);
            var session = new Session(db, tenancy.Scope);
            _sessions.Add(session);
            return session;
        }

        /// <summary>Counts wallets on a bare connection as the application role, with only app.agency_id set.</summary>
        public async Task<long> CountWalletsAsApplicationRole(Guid agencyId)
        {
            await using var connection = new NpgsqlConnection(_postgres.ConnectionStringFor(Database, asApplicationRole: true));
            await connection.OpenAsync();

            // Two commands: Npgsql will not prepare a multi-statement command with a positional
            // parameter. The setting is session-level, so it holds for the second.
            await using (var set = connection.CreateCommand())
            {
                set.CommandText = "SELECT set_config('app.agency_id', $1, false)";
                set.Parameters.AddWithValue(agencyId.ToString());
                await set.ExecuteNonQueryAsync();
            }

            await using var count = connection.CreateCommand();
            count.CommandText = "SELECT count(*) FROM payments.wallets";

            return (long)(await count.ExecuteScalarAsync())!;
        }

        public static async Task InsertEntryAsync(AppDbContext db, Guid group, Guid accountId, Guid? agencyId, string direction, long amountMinor) =>
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"""
                 INSERT INTO payments.ledger_entries
                     (id, transaction_group_id, account_id, agency_id, direction, amount_minor,
                      reference_type, reference_id, description, occurred_at)
                 VALUES ({Guid.CreateVersion7()}, {group}, {accountId}, {agencyId}, {direction}, {amountMinor},
                         'test', NULL, 'rls test entry', now())
                 """);

        public async Task<T> AdminScalarAsync<T>(string sql, Func<NpgsqlDataReader, T> read)
        {
            await using var connection = new NpgsqlConnection(_postgres.ConnectionStringFor(Database, asApplicationRole: false));
            await connection.OpenAsync();

            await using var command = new NpgsqlCommand(sql, connection);
            await using var reader = await command.ExecuteReaderAsync();
            await reader.ReadAsync();

            return read(reader);
        }

        public async Task<List<string>> AdminListAsync(string sql)
        {
            await using var connection = new NpgsqlConnection(_postgres.ConnectionStringFor(Database, asApplicationRole: false));
            await connection.OpenAsync();

            await using var command = new NpgsqlCommand(sql, connection);
            await using var reader = await command.ExecuteReaderAsync();

            var rows = new List<string>();
            while (await reader.ReadAsync())
            {
                rows.Add(reader.GetString(0));
            }

            return rows;
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var session in _sessions)
            {
                await session.DisposeAsync();
            }
        }
    }

    private sealed class Session(AppDbContext db, PlatformScope scope) : IAsyncDisposable
    {
        public AppDbContext Db { get; } = db;

        public PlatformScope Scope { get; } = scope;

        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }
}
