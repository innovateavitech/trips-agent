using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;
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
    public async Task<AppDbContext> CreateEmptyDatabaseAsync(string databaseName)
    {
        var builder = new Npgsql.NpgsqlConnectionStringBuilder(ConnectionString);

        await using (var admin = new Npgsql.NpgsqlConnection(ConnectionString))
        {
            await admin.OpenAsync();

            // The database name comes from a test method's own [CallerMemberName], never from
            // user input, but it is still an identifier being concatenated into DDL — so quote
            // it properly rather than trusting the caller.
            var quoted = "\"" + databaseName.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

            await using var command = admin.CreateCommand();
            command.CommandText = $"DROP DATABASE IF EXISTS {quoted}; CREATE DATABASE {quoted};";
            await command.ExecuteNonQueryAsync();
        }

        builder.Database = databaseName;

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(builder.ConnectionString)
            .UseSnakeCaseNamingConvention()
            .Options;

        return new AppDbContext(options, TimeProvider.System);
    }
}

/// <summary>
/// Binds the fixture to a collection so one container serves every persistence test.
/// </summary>
[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres";
}
