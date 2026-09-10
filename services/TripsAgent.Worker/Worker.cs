namespace TripsAgent.Worker;

/// <summary>
/// Placeholder host. Replaced in issue #31 by Hangfire recurring jobs and
/// MassTransit consumers — see docs/BACKLOG.md.
/// </summary>
public sealed class Worker(ILogger<Worker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("TripsAgent worker started");

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        }
    }
}
