namespace TripsAgent.Application.Auditing;

/// <summary>
/// Keeps the audit log's monthly partitions moving: creates the ones coming up, drops the ones
/// past their retention window.
///
/// Runs daily as the <c>audit-log-maintenance</c> recurring job in the Worker, and on demand via
/// <c>dotnet run --project services/TripsAgent.Api -- audit-maintenance</c>. Safe to run as often
/// as anyone likes: creating a partition that already exists does nothing, and only whole expired
/// months are ever dropped.
/// </summary>
public interface IAuditLogMaintenance
{
    /// <summary>Creates any missing upcoming partitions and drops any that have expired.</summary>
    public Task<AuditLogMaintenanceResult> RunAsync(CancellationToken cancellationToken = default);
}

/// <summary>What one maintenance run did.</summary>
/// <param name="PartitionsEnsured">
/// Names of the partitions that now exist for the coming months. Includes ones that were already
/// there — the point is the set that is ready, not the set that was new.
/// </param>
/// <param name="PartitionsDropped">How many expired months were removed.</param>
/// <param name="RetentionMonths">The window applied, so a log line records the policy in force.</param>
public sealed record AuditLogMaintenanceResult(
    IReadOnlyList<string> PartitionsEnsured,
    int PartitionsDropped,
    int RetentionMonths);
