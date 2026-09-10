using System.Runtime.CompilerServices;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using TripsAgent.Infrastructure.Messaging;
using TripsAgent.Infrastructure.Persistence;
using TripsAgent.IntegrationTests.Persistence;

namespace TripsAgent.IntegrationTests.Messaging;

/// <summary>
/// <see cref="EfInbox"/> is what turns "at-least-once delivery" into "processed exactly once per
/// consumer", so its guarantee is worth proving against a real database rather than trusting the
/// <c>PRIMARY KEY</c> to behave the way the docs say it does.
/// </summary>
[Collection(PostgresCollection.Name)]
public class EfInboxTests
{
    private static readonly DateTimeOffset Now = new(2026, 3, 1, 8, 0, 0, TimeSpan.Zero);

    private readonly PostgresFixture _postgres;

    public EfInboxTests(PostgresFixture postgres) => _postgres = postgres;

    [Fact]
    public async Task The_first_delivery_of_a_message_runs_the_work_and_commits_it()
    {
        var databaseName = await NewMigratedDatabaseAsync();
        await using var context = await OpenAsync(databaseName);

        var ran = false;

        var didRun = await Inbox(context).ProcessOnceAsync(
            Guid.CreateVersion7(),
            "wallet.credit-on-top-up",
            _ =>
            {
                ran = true;
                return Task.CompletedTask;
            });

        didRun.Should().BeTrue();
        ran.Should().BeTrue();
    }

    [Fact]
    public async Task A_redelivery_of_an_already_processed_message_is_skipped()
    {
        var databaseName = await NewMigratedDatabaseAsync();
        await using var context = await OpenAsync(databaseName);
        var messageId = Guid.CreateVersion7();

        await Inbox(context).ProcessOnceAsync(messageId, "wallet.credit-on-top-up", _ => Task.CompletedTask);

        var ranASecondTime = false;
        var didRun = await Inbox(context).ProcessOnceAsync(
            messageId,
            "wallet.credit-on-top-up",
            _ =>
            {
                ranASecondTime = true;
                return Task.CompletedTask;
            });

        didRun.Should().BeFalse();
        ranASecondTime.Should().BeFalse("the wallet must not be credited twice for the same message");
    }

    [Fact]
    public async Task The_same_message_can_be_processed_once_by_each_of_several_consumers()
    {
        // OrderPlaced might trigger both "reserve inventory" and "notify the agent" — two
        // consumers of one event, each with their own once-only guarantee.
        var databaseName = await NewMigratedDatabaseAsync();
        await using var context = await OpenAsync(databaseName);
        var messageId = Guid.CreateVersion7();

        var firstConsumerRan = await Inbox(context).ProcessOnceAsync(messageId, "reserve-inventory", _ => Task.CompletedTask);
        var secondConsumerRan = await Inbox(context).ProcessOnceAsync(messageId, "notify-agent", _ => Task.CompletedTask);

        firstConsumerRan.Should().BeTrue();
        secondConsumerRan.Should().BeTrue();
    }

    [Fact]
    public async Task A_message_id_can_be_reused_by_a_renamed_consumer_because_the_key_is_the_pair()
    {
        // Documents the primary key's shape rather than recommending the practice: renaming a
        // consumer does forget what it has seen (see IInbox's remarks), and this is why —
        // (message_id, consumer) is one row, not message_id alone.
        var databaseName = await NewMigratedDatabaseAsync();
        await using var context = await OpenAsync(databaseName);
        var messageId = Guid.CreateVersion7();

        await Inbox(context).ProcessOnceAsync(messageId, "old-name", _ => Task.CompletedTask);
        var ranUnderNewName = await Inbox(context).ProcessOnceAsync(messageId, "new-name", _ => Task.CompletedTask);

        ranUnderNewName.Should().BeTrue();
    }

    [Fact]
    public async Task If_the_work_throws_nothing_is_recorded_and_a_retry_can_still_run_it()
    {
        var databaseName = await NewMigratedDatabaseAsync();
        await using var context = await OpenAsync(databaseName);
        var messageId = Guid.CreateVersion7();

        var act = () => Inbox(context).ProcessOnceAsync(
            messageId,
            "flaky-consumer",
            _ => throw new InvalidOperationException("supplier timed out"));

        await act.Should().ThrowAsync<InvalidOperationException>();

        // Nothing committed — including the changes the failed work may have staged — so this
        // message is still "unprocessed" and MassTransit's own retry will hand it back.
        var recorded = await context.InboxMessages.AnyAsync(m => m.MessageId == messageId);
        recorded.Should().BeFalse();
    }

    [Fact]
    public async Task Two_deliveries_racing_at_once_commit_the_work_of_only_one()
    {
        // The check-then-insert in ProcessOnceAsync has a window: two copies of a message, handed
        // to two Workers, can both pass the "have I seen this?" check before either commits. IInbox
        // documents that the *callback itself* can therefore run twice if it has a side effect
        // outside the database — this test is not about that. It is about the guarantee that does
        // hold: whatever the work stages in *this* transaction is atomic with the inbox row. The
        // loser's staged changes must roll back along with its failed insert, not survive it.
        var databaseName = await NewMigratedDatabaseAsync();
        var messageId = Guid.CreateVersion7();

        await using var contextA = await OpenAsync(databaseName);
        await using var contextB = await OpenAsync(databaseName);

        Task<bool> RunAsync(AppDbContext context) => Inbox(context).ProcessOnceAsync(
            messageId,
            "wallet.credit-on-top-up",
            _ =>
            {
                // Stands in for "credit the wallet": a database change staged, not saved, by the
                // work — exactly what ProcessOnceAsync's contract asks a consumer to do.
                context.OutboxMessages.Add(OutboxMessage.Create(new { Note = "credit applied" }, agencyId: null, Now));
                return Task.CompletedTask;
            });

        var results = await Task.WhenAll(RunAsync(contextA), RunAsync(contextB));

        results.Count(didRun => didRun).Should().Be(1, "exactly one delivery should win the race");

        await using var verify = await OpenAsync(databaseName);
        (await verify.InboxMessages.CountAsync()).Should().Be(1);
        (await verify.OutboxMessages.CountAsync()).Should()
            .Be(1, "the loser's staged wallet credit must roll back with its failed inbox row, not survive it");
    }

    private async Task<string> NewMigratedDatabaseAsync([CallerMemberName] string testName = "")
    {
        var databaseName = testName.ToLowerInvariant()[..Math.Min(testName.Length, 60)];

        await using var setup = await _postgres.CreateEmptyDatabaseAsync(databaseName);
        await setup.Database.MigrateAsync();

        return databaseName;
    }

    private async Task<AppDbContext> OpenAsync(string databaseName)
    {
        var builder = new NpgsqlConnectionStringBuilder(_postgres.ConnectionString) { Database = databaseName };

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(builder.ConnectionString)
            .UseSnakeCaseNamingConvention()
            .Options;

        return new AppDbContext(options, TimeProvider.System);
    }

    private static EfInbox Inbox(AppDbContext context) =>
        new(context, new FixedClock(Now), NullLogger<EfInbox>.Instance);

    private sealed class FixedClock : TimeProvider
    {
        private readonly DateTimeOffset _now;

        public FixedClock(DateTimeOffset now) => _now = now;

        public override DateTimeOffset GetUtcNow() => _now;
    }
}
