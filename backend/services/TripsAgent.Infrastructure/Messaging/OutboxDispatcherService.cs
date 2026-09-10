using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TripsAgent.Application.Messaging;

namespace TripsAgent.Infrastructure.Messaging;

/// <summary>
/// The Worker's outbox loop: every <see cref="OutboxOptions.PollInterval"/>, publish whatever is due;
/// every <see cref="OutboxOptions.BacklogCheckInterval"/>, measure what is left and say so in the logs
/// if it is too much or too old.
/// </summary>
/// <remarks>
/// <para>
/// Registered by <c>AddOutboxDispatcher</c>, which only the Worker calls. The Api writes to the outbox
/// and never reads from it, so scaling the Api out never adds dispatchers.
/// </para>
/// <para>
/// A host that calls <c>AddOutboxDispatcher</c> without <c>AddMessageConsuming</c> has no
/// <see cref="IOutboxPublisher"/>, so there is nothing to publish <em>to</em>. The loop says so once,
/// at Warning, and then waits: messages accumulate safely in the table, the backlog check reports
/// them growing, and they all go out once a publisher exists. Guessing instead — logging them and
/// marking them sent, say — would lose every one.
/// </para>
/// </remarks>
public sealed partial class OutboxDispatcherService(
    IServiceScopeFactory scopeFactory,
    OutboxOptions options,
    TimeProvider clock,
    ILogger<OutboxDispatcherService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.PollInterval, clock);

        var nextBacklogCheck = DateTimeOffset.MinValue;
        var reportedMissingPublisher = false;

        do
        {
            try
            {
                // A fresh scope per tick: a DbContext is not meant to live for the life of the process.
                await using var scope = scopeFactory.CreateAsyncScope();
                var services = scope.ServiceProvider;

                if (services.GetService<IOutboxPublisher>() is null)
                {
                    if (!reportedMissingPublisher)
                    {
                        LogNoPublisher(logger);
                        reportedMissingPublisher = true;
                    }
                }
                else
                {
                    await DrainAsync(services.GetRequiredService<OutboxDispatcher>(), stoppingToken);
                }

                if (clock.GetUtcNow() >= nextBacklogCheck)
                {
                    var probe = services.GetRequiredService<OutboxBacklogProbe>();
                    Report(await probe.MeasureAsync(stoppingToken));
                    nextBacklogCheck = clock.GetUtcNow() + options.BacklogCheckInterval;
                }
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                // Most likely the database is unreachable. Log it and try again on the next tick: an
                // exception escaping this method would stop the whole Worker.
                LogTickFailed(logger, ex, options.PollInterval);
            }
        }
        while (await WaitForNextTickAsync(timer, stoppingToken));
    }

    private async Task DrainAsync(OutboxDispatcher dispatcher, CancellationToken stoppingToken)
    {
        // A full batch means there is probably more waiting. Go straight back for it rather than
        // sleeping a whole poll interval between every batch while a backlog is building.
        OutboxDispatchResult result;
        do
        {
            result = await dispatcher.DispatchBatchAsync(stoppingToken);
        }
        while (result.Claimed == options.BatchSize && !stoppingToken.IsCancellationRequested);
    }

    private void Report(OutboxBacklogSnapshot snapshot)
    {
        var assessment = snapshot.Assess(options);

        switch (assessment.Level)
        {
            case OutboxBacklogLevel.Critical:
                LogBacklogCritical(logger, snapshot.PendingCount, snapshot.FailedCount, snapshot.OldestPendingAge, assessment.Summary);
                break;

            case OutboxBacklogLevel.Warning:
                LogBacklogWarning(logger, snapshot.PendingCount, snapshot.FailedCount, snapshot.OldestPendingAge, assessment.Summary);
                break;

            default:
                LogBacklogHealthy(logger, snapshot.PendingCount, snapshot.FailedCount, snapshot.OldestPendingAge);
                break;
        }
    }

    private static async Task<bool> WaitForNextTickAsync(PeriodicTimer timer, CancellationToken stoppingToken)
    {
        try
        {
            return await timer.WaitForNextTickAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // The host is stopping. Leave the loop quietly; an unfinished batch has already rolled back.
            return false;
        }
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "No IOutboxPublisher is registered, so the outbox is holding messages rather than "
                  + "publishing them. Call AddMessageConsuming in this host to connect it to the broker. "
                  + "Nothing is lost: every message waits in platform.outbox_messages and goes out once a "
                  + "publisher is registered.")]
    private static partial void LogNoPublisher(ILogger logger);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Outbox dispatch failed; trying again in {PollInterval}. If this keeps happening, check "
                  + "that the database is reachable and has been migrated.")]
    private static partial void LogTickFailed(ILogger logger, Exception exception, TimeSpan pollInterval);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Outbox backlog is CRITICAL: {PendingCount} pending, {FailedCount} failed, oldest pending "
                  + "{OldestPendingAge}. {Summary}")]
    private static partial void LogBacklogCritical(
        ILogger logger,
        int pendingCount,
        int failedCount,
        TimeSpan oldestPendingAge,
        string summary);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Outbox backlog warning: {PendingCount} pending, {FailedCount} failed, oldest pending "
                  + "{OldestPendingAge}. {Summary}")]
    private static partial void LogBacklogWarning(
        ILogger logger,
        int pendingCount,
        int failedCount,
        TimeSpan oldestPendingAge,
        string summary);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Outbox backlog healthy: {PendingCount} pending, {FailedCount} failed, oldest pending {OldestPendingAge}.")]
    private static partial void LogBacklogHealthy(
        ILogger logger,
        int pendingCount,
        int failedCount,
        TimeSpan oldestPendingAge);
}
