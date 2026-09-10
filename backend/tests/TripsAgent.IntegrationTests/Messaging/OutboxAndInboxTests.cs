using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TripsAgent.Application.Messaging;
using TripsAgent.Infrastructure.Messaging;
using TripsAgent.Infrastructure.Persistence;
using TripsAgent.IntegrationTests.Persistence;

namespace TripsAgent.IntegrationTests.Messaging;

/// <summary>A stand-in for a real domain event, used across these tests.</summary>
internal sealed record SampleAgencyVerified(Guid AgencyId, string LegalName);

/// <summary>A second event type, used where a test needs to tell two kinds of message apart.</summary>
internal sealed record SampleWalletCredited(Guid WalletId, long AmountMinor);

/// <summary>
/// Issue #30's acceptance criteria, proven against a real PostgreSQL and a fake broker.
///
/// Every test uses its own database (see <see cref="MessagingDatabase.MigratedAsync"/>), so tests
/// never collide with each other or depend on the order they run in.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class OutboxAndInboxTests(PostgresFixture postgres)
{
    private static readonly DateTimeOffset Now = new(2026, 3, 1, 8, 0, 0, TimeSpan.Zero);

    private static OutboxDispatcher Dispatcher(
        AppDbContext context,
        IMessageBus bus,
        TimeProvider clock,
        OutboxOptions? options = null)
        => new(context, bus, Options.Create(options ?? new OutboxOptions()), clock, NullLogger<OutboxDispatcher>.Instance);

    // =====================================================================================
    //  AC: outbox_messages written inside the business transaction
    // =====================================================================================

    [Fact]
    public async Task Enqueue_and_SaveChanges_should_commit_the_row()
    {
        await using var context = await MessagingDatabase.MigratedAsync(postgres, nameof(Enqueue_and_SaveChanges_should_commit_the_row));
        var writer = new OutboxWriter(context, new MutableTimeProvider(Now));

        writer.Enqueue(new SampleAgencyVerified(Guid.CreateVersion7(), "Test Ltd"));
        await context.SaveChangesAsync();

        await using var verify = MessagingDatabase.As(context, TimeProvider.System);
        (await verify.OutboxMessages.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task A_rolled_back_transaction_should_take_the_outbox_row_with_it()
    {
        // The whole point of IOutboxWriter: the row lives or dies with whatever else was in the
        // same unit of work. Proved directly, by rolling one back, rather than by inference.
        await using var context = await MessagingDatabase.MigratedAsync(postgres, nameof(A_rolled_back_transaction_should_take_the_outbox_row_with_it));
        var writer = new OutboxWriter(context, new MutableTimeProvider(Now));

        await using (var transaction = await context.Database.BeginTransactionAsync())
        {
            writer.Enqueue(new SampleAgencyVerified(Guid.CreateVersion7(), "Test Ltd"));
            await context.SaveChangesAsync();

            await transaction.RollbackAsync();
        }

        await using var verify = MessagingDatabase.As(context, TimeProvider.System);
        (await verify.OutboxMessages.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Enqueue_should_stage_onto_the_same_context_as_an_unrelated_change_in_one_commit()
    {
        // Simulates a real handler: mutate something else, stage an event, save once. Both rows
        // reach the database together because they were always one SaveChanges call, not two.
        await using var context = await MessagingDatabase.MigratedAsync(postgres, nameof(Enqueue_should_stage_onto_the_same_context_as_an_unrelated_change_in_one_commit));
        var writer = new OutboxWriter(context, new MutableTimeProvider(Now));

        var other = InboxMessageForTest(Guid.CreateVersion7());
        context.InboxMessages.Add(other);
        writer.Enqueue(new SampleAgencyVerified(Guid.CreateVersion7(), "Test Ltd"));

        await context.SaveChangesAsync();

        await using var verify = MessagingDatabase.As(context, TimeProvider.System);
        (await verify.OutboxMessages.CountAsync()).Should().Be(1);
        (await verify.InboxMessages.CountAsync()).Should().Be(1);
    }

    // =====================================================================================
    //  AC: a test kills the process between commit and dispatch, proves it is still
    //  delivered on restart
    // =====================================================================================

    [Fact]
    public async Task A_message_committed_but_never_dispatched_should_still_go_out_after_a_restart()
    {
        var databaseName = nameof(A_message_committed_but_never_dispatched_should_still_go_out_after_a_restart);

        // --- "the process", before it dies -------------------------------------------------
        Guid eventAgencyId;
        await using (var context = await MessagingDatabase.MigratedAsync(postgres, databaseName))
        {
            var writer = new OutboxWriter(context, new MutableTimeProvider(Now));
            eventAgencyId = Guid.CreateVersion7();

            writer.Enqueue(new SampleAgencyVerified(eventAgencyId, "Test Ltd"));
            await context.SaveChangesAsync();

            // The process dies here. No dispatcher ever ran; nothing was published. This is
            // deliberately just falling out of the using block with no call to a dispatcher —
            // there is nothing else a crash needs to simulate, because the row is already safely
            // committed and everything after this point is "what happens on restart".
        }

        // --- "the process", restarted --------------------------------------------------------
        // A brand new context, a brand new dispatcher, a brand new bus — nothing here is the
        // same instance as before the "crash".
        await using var restarted = await MessagingDatabase.MigratedAsync(postgres, databaseName);
        var bus = new RecordingMessageBus();
        var dispatcher = Dispatcher(restarted, bus, new MutableTimeProvider(Now.AddMinutes(5)));

        var result = await dispatcher.DispatchPendingAsync();

        result.Dispatched.Should().Be(1);
        result.PendingBacklog.Should().Be(0);

        bus.Published.Should().ContainSingle();
        var published = bus.Published[0];
        published.MessageType.Should().Be<SampleAgencyVerified>();
        published.Message.Should().BeEquivalentTo(new SampleAgencyVerified(eventAgencyId, "Test Ltd"));

        var reloaded = await restarted.OutboxMessages.AsNoTracking().SingleAsync();
        reloaded.IsDispatched.Should().BeTrue();
    }

    // =====================================================================================
    //  Dispatch mechanics
    // =====================================================================================

    [Fact]
    public async Task Dispatching_should_deserialise_the_payload_back_to_an_equivalent_object()
    {
        await using var context = await MessagingDatabase.MigratedAsync(postgres, nameof(Dispatching_should_deserialise_the_payload_back_to_an_equivalent_object));
        var writer = new OutboxWriter(context, new MutableTimeProvider(Now));
        var expected = new SampleWalletCredited(Guid.CreateVersion7(), 150_000);

        writer.Enqueue(expected);
        await context.SaveChangesAsync();

        var bus = new RecordingMessageBus();
        await Dispatcher(context, bus, new MutableTimeProvider(Now)).DispatchPendingAsync();

        bus.Published.Should().ContainSingle();
        bus.Published[0].Message.Should().BeEquivalentTo(expected);
        bus.Published[0].MessageType.Should().Be<SampleWalletCredited>();
    }

    [Fact]
    public async Task Dispatching_should_set_the_wire_message_id_to_the_rows_own_id()
    {
        // What lets a consumer hand ConsumeContext.MessageId straight to IInboxDeduplicator.
        await using var context = await MessagingDatabase.MigratedAsync(postgres, nameof(Dispatching_should_set_the_wire_message_id_to_the_rows_own_id));
        var writer = new OutboxWriter(context, new MutableTimeProvider(Now));

        writer.Enqueue(new SampleAgencyVerified(Guid.CreateVersion7(), "Test Ltd"));
        await context.SaveChangesAsync();
        var rowId = (await context.OutboxMessages.SingleAsync()).Id;

        var bus = new RecordingMessageBus();
        await Dispatcher(context, bus, new MutableTimeProvider(Now)).DispatchPendingAsync();

        bus.Published.Single().MessageId.Should().Be(rowId);
    }

    [Fact]
    public async Task An_already_dispatched_message_should_not_be_dispatched_again()
    {
        await using var context = await MessagingDatabase.MigratedAsync(postgres, nameof(An_already_dispatched_message_should_not_be_dispatched_again));
        var writer = new OutboxWriter(context, new MutableTimeProvider(Now));
        writer.Enqueue(new SampleAgencyVerified(Guid.CreateVersion7(), "Test Ltd"));
        await context.SaveChangesAsync();

        var bus = new RecordingMessageBus();
        var clock = new MutableTimeProvider(Now);
        var dispatcher = Dispatcher(context, bus, clock);

        (await dispatcher.DispatchPendingAsync()).Dispatched.Should().Be(1);
        clock.Now = Now.AddMinutes(1);
        var second = await dispatcher.DispatchPendingAsync();

        second.Dispatched.Should().Be(0);
        second.PendingBacklog.Should().Be(0);
        bus.Published.Should().ContainSingle();
    }

    [Fact]
    public async Task Pending_messages_should_dispatch_oldest_first()
    {
        await using var context = await MessagingDatabase.MigratedAsync(postgres, nameof(Pending_messages_should_dispatch_oldest_first));

        // Written out of order, oldest OccurredAt last, so the assertion cannot pass by
        // accidentally matching insertion order.
        new OutboxWriter(context, new MutableTimeProvider(Now.AddSeconds(20))).Enqueue(new SampleWalletCredited(Guid.NewGuid(), 3));
        await context.SaveChangesAsync();
        new OutboxWriter(context, new MutableTimeProvider(Now)).Enqueue(new SampleWalletCredited(Guid.NewGuid(), 1));
        await context.SaveChangesAsync();
        new OutboxWriter(context, new MutableTimeProvider(Now.AddSeconds(10))).Enqueue(new SampleWalletCredited(Guid.NewGuid(), 2));
        await context.SaveChangesAsync();

        var bus = new RecordingMessageBus();
        await Dispatcher(context, bus, new MutableTimeProvider(Now.AddMinutes(1))).DispatchPendingAsync();

        bus.Published.Select(p => ((SampleWalletCredited)p.Message).AmountMinor).Should().Equal(1, 2, 3);
    }

    [Fact]
    public async Task A_dispatch_pass_should_publish_at_most_BatchSize_messages()
    {
        await using var context = await MessagingDatabase.MigratedAsync(postgres, nameof(A_dispatch_pass_should_publish_at_most_BatchSize_messages));
        var writer = new OutboxWriter(context, new MutableTimeProvider(Now));

        for (var i = 0; i < 5; i++)
        {
            writer.Enqueue(new SampleWalletCredited(Guid.NewGuid(), i));
        }

        await context.SaveChangesAsync();

        var bus = new RecordingMessageBus();
        var result = await Dispatcher(context, bus, new MutableTimeProvider(Now), new OutboxOptions { BatchSize = 2 })
            .DispatchPendingAsync();

        result.Dispatched.Should().Be(2);
        result.PendingBacklog.Should().Be(3, "backlog counts everything still pending, not just this pass");
        bus.Published.Should().HaveCount(2);
    }

    // =====================================================================================
    //  Failure handling
    // =====================================================================================

    [Fact]
    public async Task A_failed_publish_should_stay_pending_with_the_attempt_recorded()
    {
        await using var context = await MessagingDatabase.MigratedAsync(postgres, nameof(A_failed_publish_should_stay_pending_with_the_attempt_recorded));
        var writer = new OutboxWriter(context, new MutableTimeProvider(Now));
        writer.Enqueue(new SampleAgencyVerified(Guid.CreateVersion7(), "Test Ltd"));
        await context.SaveChangesAsync();

        var bus = new RecordingMessageBus { FailWith = new InvalidOperationException("broker unreachable") };
        var result = await Dispatcher(context, bus, new MutableTimeProvider(Now)).DispatchPendingAsync();

        result.Dispatched.Should().Be(0);
        result.Failed.Should().Be(1);
        result.PendingBacklog.Should().Be(1);

        var row = await context.OutboxMessages.AsNoTracking().SingleAsync();
        row.IsDispatched.Should().BeFalse();
        row.Attempts.Should().Be(1);
        row.LastError.Should().Contain("broker unreachable");
        row.NotBefore.Should().NotBeNull().And.BeAfter(Now);
    }

    [Fact]
    public async Task A_message_still_backing_off_should_not_be_retried_before_its_time()
    {
        await using var context = await MessagingDatabase.MigratedAsync(postgres, nameof(A_message_still_backing_off_should_not_be_retried_before_its_time));
        var writer = new OutboxWriter(context, new MutableTimeProvider(Now));
        writer.Enqueue(new SampleAgencyVerified(Guid.CreateVersion7(), "Test Ltd"));
        await context.SaveChangesAsync();

        var clock = new MutableTimeProvider(Now);
        var failingBus = new RecordingMessageBus { FailWith = new InvalidOperationException("first attempt fails") };
        await Dispatcher(context, failingBus, clock).DispatchPendingAsync();

        var notBefore = (await context.OutboxMessages.AsNoTracking().SingleAsync()).NotBefore!.Value;

        // Still inside the backoff window: a healthy bus this time, but the row should not even
        // be looked at yet.
        clock.Now = notBefore - TimeSpan.FromSeconds(1);
        var workingBus = new RecordingMessageBus();
        var tooSoon = await Dispatcher(context, workingBus, clock).DispatchPendingAsync();

        tooSoon.Dispatched.Should().Be(0);
        workingBus.Published.Should().BeEmpty();

        // Past the window: now it is eligible again.
        clock.Now = notBefore.AddSeconds(1);
        var onTime = await Dispatcher(context, workingBus, clock).DispatchPendingAsync();

        onTime.Dispatched.Should().Be(1);
    }

    [Fact]
    public async Task A_message_that_exhausts_MaxAttempts_should_stop_being_retried()
    {
        await using var context = await MessagingDatabase.MigratedAsync(postgres, nameof(A_message_that_exhausts_MaxAttempts_should_stop_being_retried));
        var writer = new OutboxWriter(context, new MutableTimeProvider(Now));
        writer.Enqueue(new SampleAgencyVerified(Guid.CreateVersion7(), "Test Ltd"));
        await context.SaveChangesAsync();

        var clock = new MutableTimeProvider(Now);
        var failingBus = new RecordingMessageBus { FailWith = new InvalidOperationException("always fails") };
        var options = new OutboxOptions { MaxAttempts = 2 };

        for (var attempt = 1; attempt <= 2; attempt++)
        {
            var result = await Dispatcher(context, failingBus, clock, options).DispatchPendingAsync();
            result.Failed.Should().Be(1);

            var notBefore = (await context.OutboxMessages.AsNoTracking().SingleAsync()).NotBefore!.Value;
            clock.Now = notBefore.AddSeconds(1);
        }

        // A third pass, once eligible by NotBefore, must not even attempt it: Attempts has
        // already reached MaxAttempts.
        var thirdPass = await Dispatcher(context, failingBus, clock, options).DispatchPendingAsync();

        thirdPass.Failed.Should().Be(0);
        thirdPass.Dispatched.Should().Be(0);

        var row = await context.OutboxMessages.AsNoTracking().SingleAsync();
        row.Attempts.Should().Be(2);
        row.IsDispatched.Should().BeFalse("the row is left for an operator to see, not deleted");
    }

    [Fact]
    public async Task An_unresolvable_message_type_should_fail_that_message_without_stopping_the_batch()
    {
        await using var context = await MessagingDatabase.MigratedAsync(postgres, nameof(An_unresolvable_message_type_should_fail_that_message_without_stopping_the_batch));

        // Simulates a message whose contract assembly is not loaded by this process — a real
        // possibility given the Api and the Worker deploy independently. Written directly,
        // bypassing OutboxWriter, since no real type produces a name like this.
        var poison = TripsAgent.Domain.Messaging.OutboxMessage.Create(
            "NoSuchNamespace.NoSuchType, NoSuchAssembly", "{}", Now);
        context.Add(poison);

        var writer = new OutboxWriter(context, new MutableTimeProvider(Now));
        writer.Enqueue(new SampleAgencyVerified(Guid.CreateVersion7(), "Test Ltd"));
        await context.SaveChangesAsync();

        var bus = new RecordingMessageBus();
        var result = await Dispatcher(context, bus, new MutableTimeProvider(Now)).DispatchPendingAsync();

        result.Dispatched.Should().Be(1, "the good message in the same batch must still go out");
        result.Failed.Should().Be(1);
        bus.Published.Should().ContainSingle();

        var poisonRow = await context.OutboxMessages.AsNoTracking().SingleAsync(m => m.Id == poison.Id);
        poisonRow.Attempts.Should().Be(1);
        poisonRow.LastError.Should().NotBeNullOrEmpty();
    }

    // =====================================================================================
    //  AC: backlog depth is monitored
    // =====================================================================================

    [Fact]
    public async Task The_result_should_report_the_backlog_still_waiting_after_the_pass()
    {
        await using var context = await MessagingDatabase.MigratedAsync(postgres, nameof(The_result_should_report_the_backlog_still_waiting_after_the_pass));
        var writer = new OutboxWriter(context, new MutableTimeProvider(Now));

        for (var i = 0; i < 3; i++)
        {
            writer.Enqueue(new SampleWalletCredited(Guid.NewGuid(), i));
        }

        await context.SaveChangesAsync();

        // A bus that fails everything, so the backlog stays put and the count is easy to reason
        // about — this is the number OutboxDispatcherHostedService compares against
        // BacklogAlertThreshold.
        var bus = new RecordingMessageBus { FailWith = new InvalidOperationException("unreachable") };
        var result = await Dispatcher(context, bus, new MutableTimeProvider(Now)).DispatchPendingAsync();

        result.PendingBacklog.Should().Be(3);
    }

    // =====================================================================================
    //  AC: inbox_messages dedupe by message_id, consumer-side
    // =====================================================================================

    [Fact]
    public async Task The_first_delivery_should_be_allowed_to_proceed()
    {
        await using var context = await MessagingDatabase.MigratedAsync(postgres, nameof(The_first_delivery_should_be_allowed_to_proceed));
        var deduplicator = new InboxDeduplicator(context, new MutableTimeProvider(Now));

        var isNew = await deduplicator.TryBeginProcessingAsync(Guid.CreateVersion7(), "SomeConsumer");

        isNew.Should().BeTrue();
    }

    [Fact]
    public async Task A_redelivery_to_the_same_consumer_should_be_refused()
    {
        await using var context = await MessagingDatabase.MigratedAsync(postgres, nameof(A_redelivery_to_the_same_consumer_should_be_refused));
        var deduplicator = new InboxDeduplicator(context, new MutableTimeProvider(Now));
        var messageId = Guid.CreateVersion7();

        (await deduplicator.TryBeginProcessingAsync(messageId, "IssueTicketConsumer")).Should().BeTrue();
        var redelivery = await deduplicator.TryBeginProcessingAsync(messageId, "IssueTicketConsumer");

        redelivery.Should().BeFalse();
    }

    [Fact]
    public async Task The_same_message_should_be_allowed_to_reach_a_different_consumer()
    {
        // Publish/subscribe: several consumers legitimately see the same event. Dedupe is per
        // (message, consumer), not per message alone.
        await using var context = await MessagingDatabase.MigratedAsync(postgres, nameof(The_same_message_should_be_allowed_to_reach_a_different_consumer));
        var deduplicator = new InboxDeduplicator(context, new MutableTimeProvider(Now));
        var messageId = Guid.CreateVersion7();

        (await deduplicator.TryBeginProcessingAsync(messageId, "IssueTicketConsumer")).Should().BeTrue();
        var secondConsumer = await deduplicator.TryBeginProcessingAsync(messageId, "SendEmailConsumer");

        secondConsumer.Should().BeTrue();
    }

    [Fact]
    public async Task Concurrent_redeliveries_should_let_exactly_one_through()
    {
        // The scenario ON CONFLICT DO NOTHING exists for: two redeliveries of the same message,
        // handled by two consumer instances at once, both racing to be first.
        await using var context = await MessagingDatabase.MigratedAsync(postgres, nameof(Concurrent_redeliveries_should_let_exactly_one_through));
        var messageId = Guid.CreateVersion7();

        var attempts = await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
        {
            await using var raced = MessagingDatabase.As(context, TimeProvider.System);
            var deduplicator = new InboxDeduplicator(raced, TimeProvider.System);
            return await deduplicator.TryBeginProcessingAsync(messageId, "IssueTicketConsumer");
        }));

        attempts.Count(allowed => allowed).Should().Be(1);
    }

    private static TripsAgent.Domain.Messaging.InboxMessage InboxMessageForTest(Guid messageId) =>
        TripsAgent.Domain.Messaging.InboxMessage.Create(messageId, "TestConsumer", Now);
}
