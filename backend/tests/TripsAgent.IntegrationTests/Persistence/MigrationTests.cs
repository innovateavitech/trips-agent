using System.Runtime.CompilerServices;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using TripsAgent.Infrastructure.Persistence;

namespace TripsAgent.IntegrationTests.Persistence;

/// <summary>
/// Proves the migration pipeline works against real PostgreSQL, not against our expectations
/// of it.
/// </summary>
[Collection(PostgresCollection.Name)]
public class MigrationTests
{
    private readonly PostgresFixture _postgres;

    public MigrationTests(PostgresFixture postgres) => _postgres = postgres;

    [Fact]
    public async Task Migrations_apply_to_an_empty_database()
    {
        await using var context = await NewDatabaseAsync();

        await context.Database.MigrateAsync();

        var applied = await context.Database.GetAppliedMigrationsAsync();

        applied.Should().NotBeEmpty();
        applied.Should().Contain(name => name.EndsWith("InitialExtensions", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Migrating_enables_the_extensions_the_schema_depends_on()
    {
        await using var context = await NewDatabaseAsync();

        await context.Database.MigrateAsync();

        var installed = await QueryExtensionNamesAsync(context);

        // citext gives users.email a case-insensitive unique index, so Ada@x.com and ada@x.com
        // cannot both register. ltree gives agencies.path a GIST-indexable hierarchy.
        installed.Should().Contain("citext");
        installed.Should().Contain("ltree");
    }

    [Fact]
    public async Task Migrating_twice_is_a_no_op()
    {
        await using var context = await NewDatabaseAsync();

        await context.Database.MigrateAsync();
        var afterFirst = await context.Database.GetAppliedMigrationsAsync();

        // A deploy that retries, or two instances starting together, must not corrupt anything.
        await context.Database.MigrateAsync();
        var afterSecond = await context.Database.GetAppliedMigrationsAsync();

        afterSecond.Should().BeEquivalentTo(afterFirst);
        (await context.Database.GetPendingMigrationsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task The_migrate_command_applies_migrations_and_reports_success()
    {
        await using var context = await NewDatabaseAsync();

        var services = BuildServiceProviderFor(context);

        var exitCode = await DatabaseMigrator.RunAsync(services);

        exitCode.Should().Be(0);
        (await context.Database.GetPendingMigrationsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task The_migrate_command_loads_reference_data()
    {
        await using var context = await NewDatabaseAsync();

        (await DatabaseMigrator.RunAsync(BuildServiceProviderFor(context))).Should().Be(0);

        // Registration needs the Owner role on the very first signup, in every environment. It
        // used to come only from the dev `seed` command, which production never runs.
        await using var connection = new NpgsqlConnection(context.Database.GetConnectionString());
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT count(*) FROM identity.roles WHERE agency_id IS NULL AND name = 'Owner'";

        (await command.ExecuteScalarAsync()).Should().Be(1L);
    }

    [Fact]
    public async Task The_migrate_command_succeeds_when_there_is_nothing_to_do()
    {
        await using var context = await NewDatabaseAsync();
        await context.Database.MigrateAsync();

        var exitCode = await DatabaseMigrator.RunAsync(BuildServiceProviderFor(context));

        exitCode.Should().Be(0);
    }

    [Fact]
    public async Task The_migrate_command_reports_failure_rather_than_throwing()
    {
        // Pointed at a server that is not there. A deploy step needs a non-zero exit code it can
        // branch on, not an unhandled exception in the logs.
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=127.0.0.1;Port=1;Database=nope;Username=x;Password=y;Timeout=1")
            .UseSnakeCaseNamingConvention()
            .Options;

        var tenancy = TestTenancy.None();
        await using var unreachable = new AppDbContext(
            options, TimeProvider.System, tenancy.Tenant, tenancy.Scope);

        var exitCode = await DatabaseMigrator.RunAsync(BuildServiceProviderFor(unreachable));

        exitCode.Should().Be(1);
    }

    [Theory]
    [InlineData("migrate")]
    [InlineData("MIGRATE")]
    public void The_migrate_argument_is_recognised(string argument) =>
        DatabaseMigrator.IsMigrationCommand([argument]).Should().BeTrue();

    [Fact]
    public void Ordinary_startup_arguments_do_not_trigger_a_migration() =>
        DatabaseMigrator.IsMigrationCommand(["--urls", "http://localhost:5000"]).Should().BeFalse();

    [Fact]
    public async Task A_UTC_timestamp_round_trips_unchanged()
    {
        await using var context = await NewDatabaseAsync();
        await context.Database.MigrateAsync();

        var instant = new DateTimeOffset(2026, 3, 1, 8, 30, 0, TimeSpan.Zero);

        var stored = await RoundTripTimestampAsync(context, instant);

        stored.Should().Be(instant);
        stored.Offset.Should().Be(TimeSpan.Zero, "the database stores UTC");
    }

    [Fact]
    public async Task A_timestamp_carrying_a_non_UTC_offset_is_rejected_outright()
    {
        await using var context = await NewDatabaseAsync();
        await context.Database.MigrateAsync();

        // 09:30 in Lagos (+01:00) is the same instant as 08:30 UTC, and Npgsql could quietly
        // convert it. It refuses instead, and that refusal is worth a test: it is what makes
        // "the database stores UTC" true by construction rather than by everyone remembering.
        //
        // The practical consequence for application code: take the time from TimeProvider
        // (GetUtcNow already has a zero offset), or call ToUniversalTime() before you save.
        var lagos = new DateTimeOffset(2026, 3, 1, 9, 30, 0, TimeSpan.FromHours(1));

        var act = async () => await RoundTripTimestampAsync(context, lagos);

        await act.Should().ThrowAsync<ArgumentException>()
            .Where(ex => ex.Message.Contains("only offset 0 (UTC) is supported", StringComparison.Ordinal));

        // And the conversion the application is expected to do makes it acceptable again.
        var converted = await RoundTripTimestampAsync(context, lagos.ToUniversalTime());
        converted.Should().Be(lagos.ToUniversalTime());
    }

    private static async Task<DateTimeOffset> RoundTripTimestampAsync(AppDbContext context, DateTimeOffset value)
    {
        await using var connection = new NpgsqlConnection(context.Database.GetConnectionString());
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT $1::timestamptz";
        command.Parameters.AddWithValue(value);

        // Npgsql hands back timestamptz as a UTC DateTime unless asked for a DateTimeOffset,
        // so read the field by its type rather than casting whatever ExecuteScalar returns.
        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();

        return await reader.GetFieldValueAsync<DateTimeOffset>(0);
    }

    private Task<AppDbContext> NewDatabaseAsync([CallerMemberName] string testName = "") =>
        // Lower-cased because an unquoted PostgreSQL identifier folds to lower case anyway,
        // and truncated because the limit is 63 bytes.
        _postgres.CreateEmptyDatabaseAsync(
            testName.ToLowerInvariant()[..Math.Min(testName.Length, 60)]);

    private static async Task<List<string>> QueryExtensionNamesAsync(AppDbContext context)
    {
        await using var connection = new NpgsqlConnection(context.Database.GetConnectionString());
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT extname FROM pg_extension";

        var names = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    /// <summary>
    /// The minimum container <see cref="DatabaseMigrator"/> needs: the context it should migrate
    /// and somewhere to log.
    /// </summary>
    private static ServiceProvider BuildServiceProviderFor(AppDbContext context)
    {
        // The migrator now also loads reference data, which it does inside an audited platform
        // scope — so the container needs one.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(context);
        services.AddSingleton<TripsAgent.Application.Tenancy.IPlatformScope>(TestTenancy.None().Scope);
        return services.BuildServiceProvider();
    }
}
