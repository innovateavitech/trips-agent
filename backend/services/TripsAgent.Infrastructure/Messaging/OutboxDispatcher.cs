using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TripsAgent.Application.Messaging;
using TripsAgent.Domain.Messaging;
using TripsAgent.Infrastructure.Persistence;

namespace TripsAgent.Infrastructure.Messaging;

/// <summary>
/// Publishes whatever is waiting in <c>platform.outbox_messages</c>. See <see cref="IOutboxDispatcher"/>.
/// </summary>
/// <remarks>
/// <para>
/// Registered scoped and run from a fresh scope on every tick by
/// <see cref="OutboxDispatcherHostedService"/>, so each pass gets its own short-lived
/// <see cref="AppDbContext"/> rather than one connection held open for the Worker's whole
/// lifetime.
/// </para>
/// <para>
/// Publishing is best-effort per message: one message failing to publish does not stop the rest
/// of the batch, and <see cref="AppDbContext.SaveChangesAsync(CancellationToken)"/> is called once
/// at the end so a mid-batch crash does not lose the dispatch bookkeeping for messages that did
/// succeed before it — at worst they are republished on the next pass, which
/// <see cref="IInboxDeduplicator"/> exists to make harmless.
/// </para>
/// </remarks>
public sealed partial class OutboxDispatcher(
    AppDbContext context,
    IMessageBus messageBus,
    IOptions<OutboxOptions> options,
    TimeProvider timeProvider,
    ILogger<OutboxDispatcher> logger) : IOutboxDispatcher
{
    private static readonly JsonSerializerOptions PayloadOptions = new()
    {
        Converters = { new JsonStringEnumConverter() },
    };

    /// <inheritdoc />
    public async Task<OutboxDispatchResult> DispatchPendingAsync(CancellationToken cancellationToken = default)
    {
        var settings = options.Value;
        var now = timeProvider.GetUtcNow();

        var batch = await context.OutboxMessages
            .Where(message => message.DispatchedAt == null)
            .Where(message => message.NotBefore == null || message.NotBefore <= now)
            .Where(message => message.Attempts < settings.MaxAttempts)

            // Time-ordered: OccurredAt is when the row was written, and ids are UUIDv7, so this
            // is also insertion order. An event describing an earlier state change should reach
            // the broker before one describing a later one.
            .OrderBy(message => message.OccurredAt)
            .ThenBy(message => message.Id)
            .Take(settings.BatchSize)
            .ToListAsync(cancellationToken);

        var dispatched = 0;
        var failed = 0;

        foreach (var message in batch)
        {
            if (await TryPublishAsync(message, cancellationToken))
            {
                dispatched++;
            }
            else
            {
                failed++;
            }
        }

        if (batch.Count > 0)
        {
            await context.SaveChangesAsync(cancellationToken);
        }

        var pendingBacklog = await context.OutboxMessages
            .CountAsync(message => message.DispatchedAt == null, cancellationToken);

        if (pendingBacklog >= settings.BacklogAlertThreshold)
        {
            LogBacklogAlert(logger, pendingBacklog, settings.BacklogAlertThreshold);
        }

        return new OutboxDispatchResult(dispatched, failed, pendingBacklog);
    }

    private async Task<bool> TryPublishAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        try
        {
            var messageType = ResolveType(message.MessageType);
            var payload = JsonSerializer.Deserialize(message.PayloadJson, messageType, PayloadOptions)
                          ?? throw new InvalidOperationException(
                              $"Outbox message {message.Id} deserialised to null.");

            // The row's own id becomes the wire MessageId, so a consumer that reads it off
            // ConsumeContext can hand it straight to IInboxDeduplicator.
            await messageBus.PublishAsync(payload, messageType, message.Id, cancellationToken);

            message.MarkDispatched(timeProvider.GetUtcNow());
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            message.RecordFailedAttempt(ex.Message, timeProvider.GetUtcNow(), Backoff(message.Attempts + 1));
            LogDispatchFailed(logger, message.Id, message.MessageType, message.Attempts + 1, ex);
            return false;
        }
    }

    private static Type ResolveType(string messageType) =>
        Type.GetType(messageType, throwOnError: true)
        ?? throw new InvalidOperationException($"'{messageType}' resolved to null despite throwOnError: true.");

    /// <summary>
    /// Exponential, capped at a minute. A message that keeps failing should not be retried every
    /// tick — that is a busy loop against a broker or database that is already unhappy — but the
    /// gap should not grow so large that a transient problem, once fixed, waits a long time to be
    /// noticed.
    /// </summary>
    private static TimeSpan Backoff(int attempt) =>
        TimeSpan.FromSeconds(Math.Min(60, Math.Pow(2, Math.Min(attempt, 6))));

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Failed to dispatch outbox message {MessageId} ({MessageType}), attempt {Attempt}. " +
                  "It remains pending and will be retried.")]
    private static partial void LogDispatchFailed(
        ILogger logger, Guid messageId, string messageType, int attempt, Exception exception);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Outbox backlog is {PendingBacklog}, at or above the alert threshold of {Threshold}. " +
                  "The dispatcher may be falling behind, or the broker may be unreachable.")]
    private static partial void LogBacklogAlert(ILogger logger, int pendingBacklog, int threshold);
}
