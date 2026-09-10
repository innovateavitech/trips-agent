using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TripsAgent.Application.Auditing;

namespace TripsAgent.Infrastructure.Auditing;

/// <summary>
/// Prepares the coming months' audit partitions and drops the expired ones, then exits.
/// Invoked by <c>dotnet run --project services/TripsAgent.Api -- audit-maintenance</c>.
/// </summary>
/// <remarks>
/// <para>
/// Shaped exactly like <c>DatabaseMigrator</c>: a command, not a startup hook, returning an exit
/// code a script can branch on. The same work already runs daily as a Hangfire recurring job in
/// the Worker — see <see cref="AuditLogMaintenanceSchedule"/>. This is the manual lever: after
/// restoring a backup, or when the job has been failing and you want the error in your terminal.
/// </para>
/// <para>
/// If maintenance stops running, nothing breaks for three months — partitions are prepared that
/// far ahead. Then every audited save fails. See docs/runbooks/audit-log.md.
/// </para>
/// </remarks>
public static partial class AuditLogMaintenanceCommand
{
    /// <summary>The argument that triggers a maintenance run instead of serving traffic.</summary>
    public const string CommandName = "audit-maintenance";

    /// <summary>True when the process was started to maintain the audit log rather than to serve.</summary>
    public static bool IsMaintenanceCommand(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        return args.Contains(CommandName, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Runs one maintenance pass. Returns 0 on success, 1 on failure.</summary>
    public static async Task<int> RunAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(services);

        await using var scope = services.CreateAsyncScope();

        var logger = scope.ServiceProvider
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger(typeof(AuditLogMaintenanceCommand));

        var maintenance = scope.ServiceProvider.GetRequiredService<IAuditLogMaintenance>();

        try
        {
            var outcome = await maintenance.RunAsync(cancellationToken);

            // Joined into a local first, for the same CA1873 reason as DatabaseMigrator.
            var ready = string.Join(", ", outcome.PartitionsEnsured);
            LogCompleted(logger, ready, outcome.PartitionsDropped, outcome.RetentionMonths);
            return 0;
        }
        catch (Exception ex)
        {
            LogFailed(logger, ex);
            return 1;
        }
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Audit partitions ready: {Partitions}. Dropped {Dropped} past the {RetentionMonths}-month window.")]
    private static partial void LogCompleted(ILogger logger, string partitions, int dropped, int retentionMonths);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Audit log maintenance failed. Existing partitions are untouched; run it again once "
                  + "the cause is fixed, and do it before the prepared months run out.")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}
