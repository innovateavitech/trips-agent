using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TripsAgent.Application.Messaging;
using TripsAgent.Infrastructure.Persistence;

namespace TripsAgent.Infrastructure.Messaging;

/// <summary>What one pass over the outbox did.</summary>
/// <param name="Claimed">Due messages this pass locked.</param>
/// <param name="Published">Of those, how many the broker accepted.</param>
/// <param name="Failed">Of those, how many failed and were rescheduled or given up on.</param>
public sealed record OutboxDispatchResult(int Claimed, int Published, int Failed)
{
    /// <summary>Nothing was due.</summary>
    public static OutboxDispatchResult Empty { get; } = new(0, 0, 0);
}

/// <summary>
/// Publishes one batch of due outbox messages. <see cref="OutboxDispatcherService"/> calls this every
/// <see cref="OutboxOptions.PollInterval"/>; tests call it directly.
/// </summary>
/// <remarks>
/// <para>
/// <b>Safe with many Workers.</b> Rows are claimed with <c>FOR UPDATE SKIP LOCKED</c>: a row one
/// Worker is publishing is invisible to every other Worker until the first one commits, so scaling
/// out does not multiply deliveries.
/// </para>
/// <para>
/// <b>At-least-once, not exactly-once.</b> A message is marked dispatched only after the broker has
/// accepted it. If the process dies between those two steps the transaction rolls back, the row is
/// still pending, and it is published again after restart. That duplicate is by design and
/// <see cref="IInbox"/> absorbs it. The alternative — marking first, publishing second — loses the
/// message instead, and a lost <c>PaymentCaptured</c> is far worse than a repeated one.
/// </para>
/// <para>
/// <b>No ordering guarantee.</b> Messages go out roughly oldest first, but retries and parallel
/// Workers reorder them. A consumer must not assume <c>OrderPlaced</c> arrives before <c>OrderPaid</c>.
/// </para>
/// </remarks>
public sealed partial class OutboxDispatcher(
    AppDbContext dbContext,
    IOutboxPublisher publisher,
    OutboxOptions options,
    TimeProvider clock,
    ILogger<OutboxDispatcher> logger)
{
    /// <summary>Claims up to <see cref="OutboxOptions.BatchSize"/> due messages and publishes them.</summary>
    public Task<OutboxDispatchResult> DispatchBatchAsync(CancellationToken cancellationToken = default)
    {
        // AddInfrastructure switches on EF's retry-on-transient-failure, and that strategy refuses a
        // hand-opened transaction unless the whole unit of work is handed to it, so that it can replay
        // the unit from the top. A replay can republish a message — acceptable, see the remarks above.
        var strategy = dbContext.Database.CreateExecutionStrategy();
        return strategy.ExecuteAsync(DispatchBatchOnceAsync, cancellationToken);
    }

    private async Task<OutboxDispatchResult> DispatchBatchOnceAsync(CancellationToken cancellationToken)
    {
        // A replay after a transient failure starts clean, not with the last attempt's half-made changes.
        dbContext.ChangeTracker.Clear();

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        var now = clock.GetUtcNow();

        // 'pending' is a literal, not a parameter, so PostgreSQL can match this predicate to the
        // partial index ix_outbox_messages_due, which is defined with the same literal. Given a
        // parameter, the planner cannot prove the match and reads the whole table instead.
        var batch = await dbContext.OutboxMessages
            .FromSql($"""
                SELECT *
                FROM platform.outbox_messages
                WHERE status = 'pending'
                  AND next_attempt_at <= {now}
                ORDER BY next_attempt_at, id
                LIMIT {options.BatchSize}
                FOR UPDATE SKIP LOCKED
                """)
            .ToListAsync(cancellationToken);

        if (batch.Count == 0)
        {
            // Nothing to change. Disposing the transaction rolls back a read, which costs nothing.
            return OutboxDispatchResult.Empty;
        }

        var published = 0;
        var failed = 0;

        foreach (var message in batch)
        {
            try
            {
                await publisher.PublishAsync(ToEnvelope(message), cancellationToken);
                message.MarkDispatched(clock.GetUtcNow());
                published++;
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                // Shutting down is not a failure: that exception is allowed to escape, the batch rolls
                // back, and every message in it stays pending for the next Worker. Anything else is a
                // failure of this one message — record it, back off, and carry on with the rest.
                RecordFailure(message, ex);
                failed++;
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new OutboxDispatchResult(batch.Count, published, failed);
    }

    private void RecordFailure(OutboxMessage message, Exception exception)
    {
        var failedAttempts = message.AttemptCount + 1;
        var error = $"{exception.GetType().Name}: {exception.Message}";

        if (failedAttempts >= options.MaxAttempts)
        {
            message.RecordFailure(error, retryAt: null);
            LogGaveUp(logger, exception, message.Id, message.MessageType, failedAttempts);
            return;
        }

        var retryAt = clock.GetUtcNow() + options.RetryDelayAfter(failedAttempts);
        message.RecordFailure(error, retryAt);
        LogWillRetry(logger, exception, message.Id, message.MessageType, failedAttempts, retryAt);
    }

    private static OutboxEnvelope ToEnvelope(OutboxMessage message) =>
        new(
            message.Id,
            message.MessageType,
            message.Payload,
            message.OccurredAt,
            message.CorrelationId,
            message.AgencyId);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Publishing outbox message {MessageId} ({MessageType}) failed on attempt {Attempt}; "
                  + "it will be retried at {RetryAt}.")]
    private static partial void LogWillRetry(
        ILogger logger,
        Exception exception,
        Guid messageId,
        string messageType,
        int attempt,
        DateTimeOffset retryAt);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Outbox message {MessageId} ({MessageType}) failed {Attempts} times and has been marked "
                  + "failed. It will not be retried automatically; a person needs to look at it.")]
    private static partial void LogGaveUp(
        ILogger logger,
        Exception exception,
        Guid messageId,
        string messageType,
        int attempts);
}
