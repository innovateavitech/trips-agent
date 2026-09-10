using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using TripsAgent.Infrastructure.Persistence;

namespace TripsAgent.Infrastructure.Messaging;

/// <summary>How worried to be about the outbox.</summary>
public enum OutboxBacklogLevel
{
    /// <summary>Messages are going out as fast as they come in.</summary>
    Healthy,

    /// <summary>Falling behind, or holding failed messages that need a person.</summary>
    Warning,

    /// <summary>Messages are not reaching the broker. Somebody should be paged.</summary>
    Critical,
}

/// <summary>A verdict on the backlog, with a sentence a person on call can act on.</summary>
public sealed record OutboxBacklogAssessment(OutboxBacklogLevel Level, string Summary);

/// <summary>The outbox's state at one moment.</summary>
/// <param name="PendingCount">Messages not yet published, including ones waiting to retry.</param>
/// <param name="FailedCount">Messages given up on. Each one needs a person.</param>
/// <param name="OldestPendingAge">How long the oldest pending message has been waiting. Zero when none are.</param>
public sealed record OutboxBacklogSnapshot(int PendingCount, int FailedCount, TimeSpan OldestPendingAge)
{
    /// <summary>Compares the snapshot with the thresholds in <paramref name="options"/>.</summary>
    /// <remarks>
    /// Age matters as much as count. Ten messages is not a big backlog, but ten messages that have been
    /// waiting twenty minutes means the broker or the Worker is down — and a traveller is still waiting
    /// for a confirmation that will not come.
    /// </remarks>
    public OutboxBacklogAssessment Assess(OutboxOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var counts = $"{PendingCount} pending (oldest {OldestPendingAge:c}), {FailedCount} failed";

        if (PendingCount >= options.BacklogCriticalCount || OldestPendingAge >= options.BacklogCriticalAge)
        {
            return new OutboxBacklogAssessment(
                OutboxBacklogLevel.Critical,
                $"{counts}. Messages are not reaching the broker: check the Worker is running and the broker is up.");
        }

        if (PendingCount >= options.BacklogWarningCount || OldestPendingAge >= options.BacklogWarningAge)
        {
            return new OutboxBacklogAssessment(
                OutboxBacklogLevel.Warning,
                $"{counts}. The outbox is falling behind.");
        }

        if (FailedCount > 0)
        {
            return new OutboxBacklogAssessment(
                OutboxBacklogLevel.Warning,
                $"{counts}. Failed messages are not retried automatically: look at platform.outbox_messages "
                + "where status = 'failed'.");
        }

        return new OutboxBacklogAssessment(OutboxBacklogLevel.Healthy, counts);
    }
}

/// <summary>Measures the outbox. Three small queries, each answered from a partial index.</summary>
public sealed class OutboxBacklogProbe(AppDbContext dbContext, TimeProvider clock)
{
    /// <summary>Counts pending and failed messages and times the oldest pending one.</summary>
    public async Task<OutboxBacklogSnapshot> MeasureAsync(CancellationToken cancellationToken = default)
    {
        var messages = dbContext.OutboxMessages.AsNoTracking();

        // Comparing against the constants (not variables) makes EF write them into the SQL as literals,
        // which is what lets PostgreSQL use the partial indexes. See OutboxMessageConfiguration.
        var pendingCount = await messages.CountAsync(m => m.Status == OutboxMessageStatus.Pending, cancellationToken);
        var failedCount = await messages.CountAsync(m => m.Status == OutboxMessageStatus.Failed, cancellationToken);
        var oldestPending = await messages
            .Where(m => m.Status == OutboxMessageStatus.Pending)
            .MinAsync(m => (DateTimeOffset?)m.OccurredAt, cancellationToken);

        var age = oldestPending is { } oldest ? clock.GetUtcNow() - oldest : TimeSpan.Zero;

        // Whole seconds: nobody on call needs "00:02:13.4471023", and it keeps the log lines comparable.
        return new OutboxBacklogSnapshot(
            pendingCount,
            failedCount,
            TimeSpan.FromSeconds(Math.Max(0, Math.Round(age.TotalSeconds))));
    }
}

/// <summary>Reports the outbox backlog on the Api's <c>/health</c> endpoint.</summary>
/// <remarks>
/// Never worse than <see cref="HealthStatus.Degraded"/>, on purpose. <c>/health</c> is what an
/// orchestrator probes to decide whether to restart the Api, and restarting the Api does nothing for a
/// backlog that the Worker or the broker is causing — it only adds an outage on top. Degraded still
/// appears in the response, with the numbers, for a monitor to alert on; the Worker's logs carry the
/// Critical level.
/// </remarks>
public sealed class OutboxBacklogHealthCheck(OutboxBacklogProbe probe, OutboxOptions options) : IHealthCheck
{
    /// <summary>The name this check reports under.</summary>
    public const string Name = "outbox-backlog";

    /// <inheritdoc />
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await probe.MeasureAsync(cancellationToken);
        var assessment = snapshot.Assess(options);

        var data = new Dictionary<string, object>
        {
            ["level"] = assessment.Level.ToString(),
            ["pending"] = snapshot.PendingCount,
            ["failed"] = snapshot.FailedCount,
            ["oldestPendingSeconds"] = (long)snapshot.OldestPendingAge.TotalSeconds,
        };

        return assessment.Level == OutboxBacklogLevel.Healthy
            ? HealthCheckResult.Healthy(assessment.Summary, data)
            : HealthCheckResult.Degraded(assessment.Summary, exception: null, data);
    }
}
