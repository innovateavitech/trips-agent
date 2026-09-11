using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace TripsAgent.Infrastructure.Assets;

/// <summary>
/// Says, once and loudly, when the Worker starts with the asset pipeline disabled.
/// </summary>
/// <remarks>
/// The replacement for refusing to start. A Worker that stops over a missing scanner is impossible
/// to miss, but it also stops the money-path jobs; this is impossible to miss for the same reason —
/// a critical log line at every start — and costs nothing else.
/// </remarks>
public sealed partial class AssetPipelineStatusReporter : IHostedService
{
    private readonly AssetPipelineStatus _status;
    private readonly ILogger<AssetPipelineStatusReporter> _logger;

    public AssetPipelineStatusReporter(AssetPipelineStatus status, ILogger<AssetPipelineStatusReporter> logger)
    {
        _status = status;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_status.IsEnabled)
        {
            LogDisabled(_logger, _status.DisabledReason ?? "no reason recorded");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(
        Level = LogLevel.Critical,
        Message = "The asset pipeline is DISABLED. {Reason} Uploads stay pending and are never served; every other job in this Worker runs normally.")]
    private static partial void LogDisabled(ILogger logger, string reason);
}
