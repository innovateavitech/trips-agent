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
    public static AppDbContext As(AppDbContext existing, TimeProvider clock)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(existing.Database.GetConnectionString())
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
