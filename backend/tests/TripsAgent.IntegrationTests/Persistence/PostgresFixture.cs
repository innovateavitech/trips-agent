using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using TripsAgent.Application.Tenancy;
using TripsAgent.Infrastructure.Persistence;

namespace TripsAgent.IntegrationTests.Persistence;

/// <summary>
/// A real PostgreSQL 16 server in a throwaway Docker container, shared by every test in the
/// collection.
/// </summary>
/// <remarks>
/// <para>
/// Migrations are the one thing an in-memory provider cannot honestly test. <c>citext</c>,
/// <c>ltree</c>, GIST indexes and CHECK constraints either exist in PostgreSQL or they do not,
/// and a fake provider will happily accept a migration that PostgreSQL would reject.
/// </para>
/// <para>
/// The container is created once per collection and thrown away afterwards, so tests never
/// inherit state from a previous run — and nobody has to remember to reset a shared local
/// database.
/// </para>
/// </remarks>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:16-alpine")
        .WithDatabase("tripsagent_test")
        .WithUsername("postgres")
        .WithPassword("postgres")
        .Build();

    public string ConnectionString => _container.GetConnectionString();

    public Task InitializeAsync() => _container.StartAsync();

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    /// <summary>
    /// A context pointed at a brand-new, empty database on the shared server.
    /// </summary>
    /// <remarks>
    /// Each test gets its own database rather than its own container: creating a database is
    /// milliseconds, starting PostgreSQL is seconds. Tests stay isolated without the wait.
    /// </remarks>
    public async Task<AppDbContext> CreateEmptyDatabaseAsync(
        string databaseName,
        ITenantContext? tenantContext = null,
        IPlatformScope? platformScope = null,
        TimeProvider? clock = null)
    {
        await using (var admin = new Npgsql.NpgsqlConnection(ConnectionString))
        {
            await admin.OpenAsync();

            // The database name comes from a test method's own [CallerMemberName], never from
            // user input, but it is still an identifier being concatenated into DDL — so quote
            // it properly rather than trusting the caller.
            var quoted = Quote(databaseName);

            // WITH (FORCE) terminates any connection still open against the old database.
            // Npgsql pools connections, so a context disposed moments ago can still be holding
            // one — and the cases of an xUnit [Theory] share a database name, so the second
            // case would otherwise fail with "database is being accessed by other users".
            await using var command = admin.CreateCommand();
            command.CommandText = $"DROP DATABASE IF EXISTS {quoted} WITH (FORCE); CREATE DATABASE {quoted};";
            await command.ExecuteNonQueryAsync();
        }

        // FORCE killed those connections server-side, but the pool on this side still holds
        // them as idle and would hand one straight to the next context, which then fails with
        // "terminating connection due to administrator command". Discard them.
        var target = new Npgsql.NpgsqlConnectionStringBuilder(ConnectionString)
        {
            Database = databaseName,
            Pooling = false,
            Timeout = 60,
            CommandTimeout = 60,
        };
        using (var stale = new Npgsql.NpgsqlConnection(target.ConnectionString))
        {
            Npgsql.NpgsqlConnection.ClearPool(stale);
        }

        return Connect(databaseName, tenantContext, platformScope, clock);
    }

    /// <summary>
    /// Opens another context against a database that already exists, optionally acting as a
    /// different tenant.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="CreateEmptyDatabaseAsync"/>, which drops and recreates. Reaching
    /// for the wrong one silently wipes the rows a test has just set up — the whole test then
    /// passes or fails for reasons unrelated to what it is checking.
    /// </remarks>
    public AppDbContext Connect(
        string databaseName,
        ITenantContext? tenantContext = null,
        IPlatformScope? platformScope = null,
        TimeProvider? clock = null)
    {
        var builder = new Npgsql.NpgsqlConnectionStringBuilder(ConnectionString)
        {
            Database = databaseName,

            // Pooling off for test connections. Every test gets its own database, and Npgsql
            // keeps a pool per connection string for the life of the process — so pools from
            // finished tests sit on idle connections until the server runs out of them, and the
            // suite starts failing with "sorry, too many clients already" as it grows. Capping
            // the pool size only moves the ceiling; not pooling at all removes it.
            Pooling = false,

            // The cost of not pooling is a fresh TCP connection per operation, and against a
            // container under load from the whole suite the default 15-second timeouts are
            // occasionally not enough — which showed up as a rare transient failure rather than
            // a consistent one. Generous here; production keeps the defaults.
            Timeout = 60,
            CommandTimeout = 60,
        };

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(builder.ConnectionString)
            .UseSnakeCaseNamingConvention()
            .Options;

        // Defaults to no tenant, which is what a migration or a seed run looks like.
        var fallback = TestTenancy.None();

        return new AppDbContext(
            options,
            clock ?? TimeProvider.System,
            tenantContext ?? fallback.Tenant,
            platformScope ?? fallback.Scope);
    }

    private static string Quote(string identifier) =>
        "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}

/// <summary>
/// Binds the fixture to a collection so one container serves every persistence test.
/// </summary>
[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres";
}
