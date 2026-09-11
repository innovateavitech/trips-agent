using Hangfire;
using TripsAgent.Application.Assets;

namespace TripsAgent.Infrastructure.Assets;

/// <summary>
/// Hands an uploaded asset to Hangfire, for the Worker to process.
/// </summary>
/// <remarks>
/// The API enqueues and the Worker executes. The job type is named here but only the Worker can
/// build it, because only the Worker registers the scanner and the image processor.
/// </remarks>
public sealed class HangfireAssetPipelineDispatcher : IAssetPipelineDispatcher
{
    private readonly IBackgroundJobClient _jobs;

    public HangfireAssetPipelineDispatcher(IBackgroundJobClient jobs) => _jobs = jobs;

    public Task EnqueueAsync(Guid assetId, CancellationToken cancellationToken = default)
    {
        // Hangfire substitutes its own token for CancellationToken.None when the job runs, so a
        // Worker shutting down can still stop it cleanly.
        _jobs.Enqueue<AssetProcessingJob>(job => job.RunAsync(assetId, CancellationToken.None));

        return Task.CompletedTask;
    }
}

/// <summary>
/// One asset's run through the pipeline, as Hangfire executes it.
/// </summary>
/// <remarks>
/// A wrapper so the retry policy can be an attribute, which Application cannot reference. Ten
/// attempts on Hangfire's growing back-off spans several hours — long enough to ride out a scanner
/// outage, after which the sweep keeps re-enqueueing until one succeeds.
/// </remarks>
public sealed class AssetProcessingJob
{
    private readonly IAssetProcessor _processor;
    private readonly AssetPipelineStatus _status;

    public AssetProcessingJob(IAssetProcessor processor, AssetPipelineStatus status)
    {
        _processor = processor;
        _status = status;
    }

    /// <remarks>
    /// Does nothing while the pipeline is disabled: the asset stays pending and unserved, and ten
    /// retries of a scan that cannot happen would only fill the dashboard with failures.
    /// </remarks>
    [AutomaticRetry(Attempts = 10)]
    public Task RunAsync(Guid assetId, CancellationToken cancellationToken) =>
        _status.IsEnabled ? _processor.ProcessAsync(assetId, cancellationToken) : Task.CompletedTask;
}

/// <summary>
/// The sweep, as Hangfire runs it: one at a time, never retried.
/// </summary>
/// <remarks>
/// The same shape as the webhook drain. A sweep that cannot take the lock is dropped rather than
/// queued, because the next one is minutes away and a pile of queued sweeps helps nobody.
/// </remarks>
public sealed class AssetSweepJob
{
    private readonly IAssetProcessor _processor;
    private readonly AssetPipelineStatus _status;

    public AssetSweepJob(IAssetProcessor processor, AssetPipelineStatus status)
    {
        _processor = processor;
        _status = status;
    }

    /// <remarks>Does nothing while the pipeline is disabled — there is nothing it could re-enqueue usefully.</remarks>
    [DisableConcurrentExecution(timeoutInSeconds: 10)]
    [AutomaticRetry(Attempts = 0, OnAttemptsExceeded = AttemptsExceededAction.Delete)]
    public Task<int> RunAsync(CancellationToken cancellationToken) =>
        _status.IsEnabled ? _processor.SweepAsync(cancellationToken) : Task.FromResult(0);
}

/// <summary>
/// Expires abandoned uploads and re-enqueues lost processing, on a timer.
/// </summary>
/// <remarks>
/// The enqueue in <see cref="HangfireAssetPipelineDispatcher"/> is the fast path; this is what makes
/// the guarantee that an uploaded asset is eventually processed or failed, never left pending.
/// Every five minutes, because a stalled asset only becomes stalled after fifteen.
/// </remarks>
public static class AssetSweepSchedule
{
    public const string JobId = "asset-pipeline-sweep";

    public const string CronExpression = "*/5 * * * *";

    public static void Register(IRecurringJobManager recurringJobs)
    {
        ArgumentNullException.ThrowIfNull(recurringJobs);

        recurringJobs.AddOrUpdate<AssetSweepJob>(
            JobId,
            job => job.RunAsync(CancellationToken.None),
            CronExpression,
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });
    }
}
