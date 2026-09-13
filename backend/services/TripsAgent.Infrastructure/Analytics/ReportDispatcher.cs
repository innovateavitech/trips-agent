using Hangfire;
using TripsAgent.Application.Analytics;

namespace TripsAgent.Infrastructure.Analytics;

/// <summary>
/// Hands a queued report to Hangfire, for the Worker to produce.
/// </summary>
/// <remarks>
/// <para>
/// The API enqueues and the Worker executes — the API never runs a Hangfire server, or every API
/// instance would race to build the same file. Plan §3 puts reports on their own isolated pool
/// (<c>reports.generate</c>) precisely because they are long-running and must not sit in front of
/// anything on the booking path.
/// </para>
/// <para>
/// Re-running a job that already finished is harmless: <c>ReportService.RunQueuedAsync</c> does
/// nothing unless the row is still <c>Queued</c>, so a Hangfire retry after a successful run
/// neither rebuilds the file nor writes a second export record.
/// </para>
/// </remarks>
public sealed class HangfireReportDispatcher : IReportDispatcher
{
    /// <summary>The queue reports run on. Isolated, because they are slow by design.</summary>
    public const string QueueName = "reports-generate";

    private readonly IBackgroundJobClient _jobs;

    public HangfireReportDispatcher(IBackgroundJobClient jobs) => _jobs = jobs;

    public Task EnqueueAsync(Guid reportJobId, CancellationToken cancellationToken = default)
    {
        // Hangfire substitutes its own token for CancellationToken.None when the job runs, so a
        // Worker shutting down can stop a half-built report cleanly.
        _jobs.Enqueue<IReportRunner>(runner => runner.RunAsync(reportJobId, CancellationToken.None));

        return Task.CompletedTask;
    }
}
