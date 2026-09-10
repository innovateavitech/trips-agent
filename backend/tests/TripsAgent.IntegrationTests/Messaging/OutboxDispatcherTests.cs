using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using TripsAgent.Application.Messaging;
using TripsAgent.Infrastructure.Messaging;
using TripsAgent.Infrastructure.Persistence;
using TripsAgent.IntegrationTests.Persistence;

namespace TripsAgent.IntegrationTests.Messaging;

/// <summary>Records what was handed to it. Stands in for a real broker.</summary>
file sealed class RecordingPublisher : IOutboxPublisher
{
    private readonly Func<OutboxEnvelope, bool>? _shouldFail;

    public RecordingPublisher(Func<OutboxEnvelope, bool>? shouldFail = null) => _shouldFail = shouldFail;

    public List<OutboxEnvelope> Published { get; } = [];

    public Task PublishAsync(OutboxEnvelope envelope, CancellationToken cancellationToken = default)
    {
        if (_shouldFail?.Invoke(envelope) == true)
        {
            throw new InvalidOperationException("the broker is unavailable");
        }

        Published.Add(envelope);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Exercises <see cref="OutboxDispatcher"/> against real PostgreSQL. A fake <see cref="IOutboxPublisher"/>
/// stands in for the broker — what matters here is the outbox's own behaviour: which rows it claims,
/// how it reacts to a failure, and that a message committed before this process existed is still
/// picked up. Delivery through MassTransit itself is covered in
/// <see cref="MassTransitOutboxPublisherTests"/>.
/// </summary>
[Collection(PostgresCollection.Name)]
public class OutboxDispatcherTests
{
    private static readonly DateTimeOffset Now = new(2026, 3, 1, 8, 0, 0, TimeSpan.Zero);

    private readonly PostgresFixture _postgres;

    public OutboxDispatcherTests(PostgresFixture postgres) => _postgres = postgres;

    [Fact]
    public async Task DispatchBatch_publishes_a_due_pending_message_and_marks_it_dispatched()
    {
        var databaseName = await NewMigratedDatabaseAsync();
        await using var context = await OpenAsync(databaseName);

        var messageId = await EnqueueAsync(context, "hello");

        var publisher = new RecordingPublisher();
        var result = await Dispatcher(context, publisher).DispatchBatchAsync();

        result.Should().Be(new OutboxDispatchResult(Claimed: 1, Published: 1, Failed: 0));
        publisher.Published.Should().ContainSingle().Which.MessageId.Should().Be(messageId);

        var stored = await context.OutboxMessages.SingleAsync(m => m.Id == messageId);
        stored.Status.Should().Be(OutboxMessageStatus.Dispatched);
        stored.DispatchedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task DispatchBatch_does_not_touch_a_message_that_is_not_due_yet()
    {
        var databaseName = await NewMigratedDatabaseAsync();
        await using var context = await OpenAsync(databaseName);

        await EnqueueAsync(context, "future", occurredAt: Now.AddMinutes(5));

        var result = await Dispatcher(context, new RecordingPublisher(), clockAt: Now).DispatchBatchAsync();

        result.Should().Be(OutboxDispatchResult.Empty);
    }

    [Fact]
    public async Task A_failed_publish_is_rescheduled_rather_than_lost()
    {
        var databaseName = await NewMigratedDatabaseAsync();
        await using var context = await OpenAsync(databaseName);

        var messageId = await EnqueueAsync(context, "will fail once");

        var publisher = new RecordingPublisher(shouldFail: _ => true);
        var options = new OutboxOptions { MaxAttempts = 5, RetryBaseDelay = TimeSpan.FromSeconds(5) };

        var result = await Dispatcher(context, publisher, options).DispatchBatchAsync();

        result.Should().Be(new OutboxDispatchResult(Claimed: 1, Published: 0, Failed: 1));

        var stored = await context.OutboxMessages.SingleAsync(m => m.Id == messageId);
        stored.Status.Should().Be(OutboxMessageStatus.Pending, "it still has attempts left");
        stored.AttemptCount.Should().Be(1);
        stored.LastError.Should().Contain("broker is unavailable");
        stored.NextAttemptAt.Should().BeAfter(Now);
    }

    [Fact]
    public async Task A_message_that_keeps_failing_is_marked_failed_after_MaxAttempts()
    {
        var databaseName = await NewMigratedDatabaseAsync();
        await using var context = await OpenAsync(databaseName);
        await EnqueueAsync(context, "always fails");

        var publisher = new RecordingPublisher(shouldFail: _ => true);
        var options = new OutboxOptions { MaxAttempts = 3, RetryBaseDelay = TimeSpan.FromMilliseconds(1) };

        // Each attempt reschedules NextAttemptAt in the future relative to *its own* clock read,
        // so the clock is nudged forward before each retry — otherwise the second and third
        // attempts would never become due again inside this test.
        for (var attempt = 1; attempt <= options.MaxAttempts; attempt++)
        {
            var clockAt = Now.AddMinutes(attempt);
            await Dispatcher(context, publisher, options, clockAt).DispatchBatchAsync();
        }

        var stored = await context.OutboxMessages.SingleAsync();
        stored.Status.Should().Be(OutboxMessageStatus.Failed);
        stored.AttemptCount.Should().Be(options.MaxAttempts);
    }

    [Fact]
    public async Task Two_dispatchers_racing_the_same_backlog_never_publish_the_same_message_twice()
    {
        // FOR UPDATE SKIP LOCKED is the whole safety story for running several Workers: this is
        // the test that would fail if that clause were ever dropped from the query.
        var databaseName = await NewMigratedDatabaseAsync();
        await using var seed = await OpenAsync(databaseName);
        for (var i = 0; i < 20; i++)
        {
            await EnqueueAsync(seed, $"message-{i}");
        }

        await using var contextA = await OpenAsync(databaseName);
        await using var contextB = await OpenAsync(databaseName);

        var publisherA = new RecordingPublisher();
        var publisherB = new RecordingPublisher();

        var options = new OutboxOptions { BatchSize = 20 };

        // Both dispatchers start their claiming transaction at the same time; SKIP LOCKED is what
        // keeps them from claiming the same rows.
        await Task.WhenAll(
            Dispatcher(contextA, publisherA, options).DispatchBatchAsync(),
            Dispatcher(contextB, publisherB, options).DispatchBatchAsync());

        var allPublished = publisherA.Published.Select(e => e.MessageId)
            .Concat(publisherB.Published.Select(e => e.MessageId))
            .ToList();

        allPublished.Should().OnlyHaveUniqueItems();
        allPublished.Should().HaveCount(20);
    }

    [Fact]
    public async Task A_message_committed_before_this_process_existed_is_still_delivered()
    {
        // This is the crash scenario in issue #30's acceptance criteria: a handler commits the
        // outbox row and then the process dies before anything dispatches it. We cannot literally
        // SIGKILL the test runner, but the outbox row does not know or care which process wrote
        // it — it is a row in PostgreSQL, not state held in memory. So the honest way to prove "the
        // message survives a crash between commit and dispatch" is to write it with one AppDbContext
        // and *never reuse that context or any in-memory state from it* — a brand-new context and a
        // brand-new dispatcher, exactly what a restarted Worker process would create, picks it up.
        var databaseName = await NewMigratedDatabaseAsync();
        Guid messageId;
        await using (var writer = await OpenAsync(databaseName))
        {
            messageId = await EnqueueAsync(writer, "written by the process that died");
        } // writer is disposed here — nothing about it survives into the next block.

        // Simulates the restart: nothing here is derived from `writer` above.
        await using var restarted = await OpenAsync(databaseName);
        var publisher = new RecordingPublisher();

        var result = await Dispatcher(restarted, publisher).DispatchBatchAsync();

        result.Published.Should().Be(1);
        publisher.Published.Should().ContainSingle().Which.MessageId.Should().Be(messageId);
    }

    private async Task<string> NewMigratedDatabaseAsync([System.Runtime.CompilerServices.CallerMemberName] string testName = "")
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

        return new AppDbContext(options, TimeProvider.System, TestTenancy.None().Tenant, TestTenancy.None().Scope);
    }

    private static async Task<Guid> EnqueueAsync(AppDbContext context, string detail, DateTimeOffset? occurredAt = null)
    {
        var message = OutboxMessage.Create(new { Detail = detail }, agencyId: null, occurredAt ?? Now);
        context.OutboxMessages.Add(message);
        await context.SaveChangesAsync();
        return message.Id;
    }

    private static OutboxDispatcher Dispatcher(
        AppDbContext context,
        IOutboxPublisher publisher,
        OutboxOptions? options = null,
        DateTimeOffset? clockAt = null) =>
        new(
            context,
            publisher,
            options ?? new OutboxOptions(),
            new FixedTimeProvider(clockAt ?? Now.AddMinutes(10)),
            NullLogger<OutboxDispatcher>.Instance);
}

/// <summary>A clock frozen at one instant, so "is this message due yet" is deterministic in tests.</summary>
file sealed class FixedTimeProvider : TimeProvider
{
    private readonly DateTimeOffset _now;

    public FixedTimeProvider(DateTimeOffset now) => _now = now;

    public override DateTimeOffset GetUtcNow() => _now;
}
