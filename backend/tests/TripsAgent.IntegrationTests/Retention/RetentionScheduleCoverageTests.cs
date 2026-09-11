using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using TripsAgent.Infrastructure.Retention;
using TripsAgent.IntegrationTests.Persistence;

namespace TripsAgent.IntegrationTests.Retention;

/// <summary>
/// Keeps the retention schedule complete. A table added in a year's time — by someone who never read
/// docs/DATA_RETENTION.md — fails these until someone decides how long it is kept.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class RetentionScheduleCoverageTests
{
    private readonly PostgresFixture _postgres;

    public RetentionScheduleCoverageTests(PostgresFixture postgres) => _postgres = postgres;

    [Fact]
    public async Task Every_table_in_the_database_has_a_retention_decision()
    {
        await using var db = await _postgres.CreateEmptyDatabaseAsync($"retention_coverage_{Guid.NewGuid():N}");
        await db.Database.MigrateAsync();

        // Real tables and partitioned parents, not their monthly partitions. Hangfire's tables are its
        // own, and public holds only the migrations history.
        var tables = await db.Database.SqlQueryRaw<string>(
                """
                SELECT n.nspname || '.' || c.relname AS "Value"
                  FROM pg_class c
                  JOIN pg_namespace n ON n.oid = c.relnamespace
                 WHERE c.relkind IN ('r', 'p')
                   AND NOT c.relispartition
                   AND n.nspname NOT IN ('pg_catalog', 'information_schema', 'public', 'hangfire')
                   AND n.nspname NOT LIKE 'pg_toast%'
                """)
            .ToListAsync();

        var classified = RetentionCatalogue.Tables.Select(table => table.Table).ToHashSet(StringComparer.Ordinal);

        tables.Should().NotBeEmpty();
        tables.Where(table => !classified.Contains(table)).Should().BeEmpty(
            "every table needs a line in RetentionCatalogue.Tables and in docs/DATA_RETENTION.md — "
            + "how long it is kept, why, and whether the purge job may touch it");
        classified.Where(table => !tables.Contains(table)).Should().BeEmpty(
            "the retention schedule names a table the database does not have — renamed or dropped?");
    }

    [Fact]
    public void The_written_schedule_names_every_table_the_code_knows_about()
    {
        var document = File.ReadAllText(RepositoryFile("docs", "DATA_RETENTION.md"));

        RetentionCatalogue.Tables
            .Where(table => !document.Contains($"`{table.Table}`", StringComparison.Ordinal))
            .Select(table => table.Table)
            .Should().BeEmpty("docs/DATA_RETENTION.md is the schedule people read, so it must list every table");
    }

    /// <summary>A file in the repository, found by walking up from the test binary.</summary>
    private static string RepositoryFile(params string[] parts)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "backend", "TripsAgent.slnx"))
                && Directory.Exists(Path.Combine(directory.FullName, "docs")))
            {
                return Path.Combine([directory.FullName, .. parts]);
            }
        }

        throw new InvalidOperationException($"Could not find the repository root above {AppContext.BaseDirectory}.");
    }
}
