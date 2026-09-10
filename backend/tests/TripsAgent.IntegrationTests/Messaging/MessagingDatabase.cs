using Microsoft.EntityFrameworkCore;
using TripsAgent.Infrastructure.Persistence;
using TripsAgent.IntegrationTests.Persistence;

namespace TripsAgent.IntegrationTests.Messaging;

/// <summary>Shared set-up for the outbox/inbox tests, on top of <see cref="PostgresFixture"/>.</summary>
internal static class MessagingDatabase
{
    /// <summary>A fresh database with every migration applied, so the real outbox/inbox schema exists.</summary>
    public static async Task<AppDbContext> MigratedAsync(PostgresFixture postgres, string testName, TimeProvider? clock = null)
    {
        // Lower-cased and truncated for the same reasons as MigrationTests: unquoted identifiers
        // fold to lower case, and PostgreSQL stops at 63 bytes.
        var name = testName.ToLowerInvariant();
        name = name[..Math.Min(name.Length, 60)];

        var context = await postgres.CreateEmptyDatabaseAsync(name);

        if (clock is not null)
        {
            context = As(context, clock);
        }

        await context.Database.MigrateAsync();
        return context;
    }

    /// <summary>A second context on the same database, running on <paramref name="clock"/>.</summary>
    public static AppDbContext As(AppDbContext existing, TimeProvider clock) =>
        Connect(existing.Database.GetConnectionString()!, clock);

    /// <summary>
    /// A context pointed at an already-existing database, given its connection string directly
    /// rather than another live context.
    /// </summary>
    /// <remarks>
    /// This is what a real restart is: a fresh connection to the database that was already
    /// there, not <see cref="PostgresFixture.CreateEmptyDatabaseAsync"/> called a second time —
    /// that drops and recreates the database, which fails with "database is being accessed by
    /// other users" the moment anything still holds a pooled connection to it, and would defeat
    /// the point of a restart test by destroying the very row the restart is supposed to find.
    /// </remarks>
    public static AppDbContext Connect(string connectionString, TimeProvider clock)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(connectionString)
            .UseSnakeCaseNamingConvention()
            .Options;

        return new AppDbContext(options, clock);
    }
}

/// <summary>A clock a test can move forward by hand, so a backoff window can be asserted exactly.</summary>
internal sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}
