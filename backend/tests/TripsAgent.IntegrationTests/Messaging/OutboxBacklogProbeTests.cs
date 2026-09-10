using System.Runtime.CompilerServices;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using TripsAgent.Infrastructure.Messaging;
using TripsAgent.Infrastructure.Persistence;
using TripsAgent.IntegrationTests.Persistence;

namespace TripsAgent.IntegrationTests.Messaging;

/// <summary>
/// <see cref="OutboxBacklogProbe"/>'s three counts are each answered from a partial index (see
/// <see cref="OutboxMessageConfiguration"/>), so this runs them against real PostgreSQL rather than
/// trusting that the predicates and the indexes still agree.
/// </summary>
[Collection(PostgresCollection.Name)]
public class OutboxBacklogProbeTests
{
    private static readonly DateTimeOffset Now = new(2026, 3, 1, 8, 0, 0, TimeSpan.Zero);

    private readonly PostgresFixture _postgres;

    public OutboxBacklogProbeTests(PostgresFixture postgres) => _postgres = postgres;

    [Fact]
    public async Task An_empty_outbox_measures_as_zero_with_no_age()
    {
        await using var context = await NewMigratedContextAsync();

        var snapshot = await Probe(context).MeasureAsync();

        snapshot.Should().Be(new OutboxBacklogSnapshot(PendingCount: 0, FailedCount: 0, OldestPendingAge: TimeSpan.Zero));
    }

    [Fact]
    public async Task Pending_and_failed_messages_are_counted_separately()
    {
        await using var context = await NewMigratedContextAsync();

        await AddAsync(context, occurredAt: Now);
        await AddAsync(context, occurredAt: Now);
        await AddAsync(context, occurredAt: Now, thenFail: true);

        var snapshot = await Probe(context).MeasureAsync();

        snapshot.PendingCount.Should().Be(2);
        snapshot.FailedCount.Should().Be(1);
    }

    [Fact]
    public async Task A_dispatched_message_counts_as_neither_pending_nor_failed()
    {
        await using var context = await NewMigratedContextAsync();

        await AddAsync(context, occurredAt: Now, thenDispatch: true);

        var snapshot = await Probe(context).MeasureAsync();

        snapshot.PendingCount.Should().Be(0);
        snapshot.FailedCount.Should().Be(0);
    }

    [Fact]
    public async Task The_oldest_pending_message_sets_the_reported_age()
    {
        await using var context = await NewMigratedContextAsync();

        await AddAsync(context, occurredAt: Now.AddMinutes(-30));
        await AddAsync(context, occurredAt: Now.AddMinutes(-5));

        var snapshot = await Probe(context, clockAt: Now).MeasureAsync();

        snapshot.OldestPendingAge.Should().Be(TimeSpan.FromMinutes(30));
    }

    private async Task<AppDbContext> NewMigratedContextAsync([CallerMemberName] string testName = "")
    {
        var databaseName = testName.ToLowerInvariant()[..Math.Min(testName.Length, 60)];

        await using (var setup = await _postgres.CreateEmptyDatabaseAsync(databaseName))
        {
            await setup.Database.MigrateAsync();
        }

        var builder = new NpgsqlConnectionStringBuilder(_postgres.ConnectionString) { Database = databaseName };

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(builder.ConnectionString)
            .UseSnakeCaseNamingConvention()
            .Options;

        return new AppDbContext(options, TimeProvider.System);
    }

    private static async Task AddAsync(
        AppDbContext context,
        DateTimeOffset occurredAt,
        bool thenDispatch = false,
        bool thenFail = false)
    {
        var message = OutboxMessage.Create(new { Detail = "probe" }, agencyId: null, occurredAt);

        if (thenDispatch)
        {
            message.MarkDispatched(occurredAt.AddSeconds(1));
        }

        if (thenFail)
        {
            message.RecordFailure("boom", retryAt: null);
        }

        context.OutboxMessages.Add(message);
        await context.SaveChangesAsync();
    }

    private static OutboxBacklogProbe Probe(AppDbContext context, DateTimeOffset? clockAt = null) =>
        new(context, new FixedClock(clockAt ?? Now));

    private sealed class FixedClock : TimeProvider
    {
        private readonly DateTimeOffset _now;

        public FixedClock(DateTimeOffset now) => _now = now;

        public override DateTimeOffset GetUtcNow() => _now;
    }
}
