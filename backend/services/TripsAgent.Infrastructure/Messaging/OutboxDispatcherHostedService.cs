using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TripsAgent.Application.Messaging;

namespace TripsAgent.Infrastructure.Messaging;

/// <summary>
/// Puts <see cref="IOutboxDispatcher"/> on a clock: publish whatever is pending, wait
/// <see cref="OutboxOptions.PollingInterval"/>, repeat.
/// </summary>
/// <remarks>
/// <para>
/// Not a Hangfire recurring job. Hangfire's own schedule storage lives in PostgreSQL and its
/// cron grain is minutes — nowhere near the 1-5 second cadence the plan asks for — so this is a
/// plain <see cref="BackgroundService"/> with its own timer, the same shape MassTransit's own
/// consumers run under.
/// </para>
/// <para>
/// Registered only by <c>AddOutboxDispatching</c>, which only the Worker calls — mirroring
/// <c>AddMessageConsuming</c> and <c>AddJobProcessing</c>. Every API instance running this would
/// mean every one of them polling the same table on the same clock; one Worker process is enough,
/// and more Worker instances means running more of them, not turning this on somewhere else.
/// </para>
/// <para>
/// A fresh <see cref="IServiceScope"/> per tick, not one held for the service's lifetime: the
/// scoped <c>AppDbContext</c> a long-lived scope would pin stays open, and its connection, for as
/// long as the Worker runs.
/// </para>
/// </remarks>
public sealed partial class OutboxDispatcherHostedService(
    IServiceScopeFactory scopeFactory,
    IOptions<OutboxOptions> options,
    ILogger<OutboxDispatcherHostedService> logger) : BackgroundService
{
    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogStarted(logger, options.Value.PollingInterval);

        while (!stoppingToken.IsCancellationRequested)
        {
            await RunOneTickAsync(stoppingToken);

            try
            {
                await Task.Delay(options.Value.PollingInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task RunOneTickAsync(CancellationToken stoppingToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var dispatcher = scope.ServiceProvider.GetRequiredService<IOutboxDispatcher>();

            var result = await dispatcher.DispatchPendingAsync(stoppingToken);

            if (result.Dispatched > 0 || result.Failed > 0)
            {
                LogTick(logger, result.Dispatched, result.Failed, result.PendingBacklog);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down. Nothing to log — this is the expected way the loop ends.
        }
        catch (Exception ex)
        {
            // A whole tick failing — the database is unreachable, say — must not take the
            // Worker's other hosted services down with it. Log and try again next tick.
            LogTickFailed(logger, ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Outbox dispatcher started, polling every {Interval}.")]
    private static partial void LogStarted(ILogger logger, TimeSpan interval);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Outbox tick: dispatched {Dispatched}, failed {Failed}, backlog now {PendingBacklog}.")]
    private static partial void LogTick(ILogger logger, int dispatched, int failed, int pendingBacklog);

    [LoggerMessage(Level = LogLevel.Error, Message = "Outbox dispatch tick failed. Retrying next tick.")]
    private static partial void LogTickFailed(ILogger logger, Exception exception);
}
