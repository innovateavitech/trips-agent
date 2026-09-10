using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TripsAgent.Application.Auditing;
using TripsAgent.Infrastructure.Persistence;

namespace TripsAgent.Infrastructure.Auditing;

/// <summary>
/// Drives the two SQL functions the audit log migration installs.
///
/// The work is in the database rather than here on purpose: creating a partition is DDL that has
/// to name a table computed from a date, and doing that from C# means building SQL strings by
/// hand. A function invoked with a parameter cannot be tricked into naming a different table.
///
/// Every query aliases its result <c>AS "Value"</c>. EF Core's scalar <c>SqlQuery&lt;T&gt;</c> wraps
/// the SQL in a subquery as soon as a LINQ operator such as <c>SingleAsync</c> is applied, and
/// selects a column by that exact name; without the alias the command fails at runtime.
/// </summary>
public sealed class AuditLogPartitionMaintenance(
    AppDbContext context,
    IOptions<AuditLogOptions> options) : IAuditLogMaintenance
{
    /// <inheritdoc />
    public async Task<AuditLogMaintenanceResult> RunAsync(CancellationToken cancellationToken = default)
    {
        var settings = options.Value;
        var ensured = new List<string>();

        // From the current month outwards. The current one is included because a database
        // restored from a backup taken last year would otherwise have no partition to write to.
        for (var offset = 0; offset <= settings.PartitionsCreatedAhead; offset++)
        {
            var name = await context.Database
                .SqlQuery<string>(
                    $"""
                     SELECT platform.create_audit_log_partition(
                         (date_trunc('month', now()) + make_interval(months => {offset}))::date) AS "Value"
                     """)
                .SingleAsync(cancellationToken);

            ensured.Add(name);
        }

        var dropped = await context.Database
            .SqlQuery<int>(
                $"SELECT platform.drop_expired_audit_log_partitions({settings.RetentionMonths}) AS \"Value\"")
            .SingleAsync(cancellationToken);

        return new AuditLogMaintenanceResult(ensured, dropped, settings.RetentionMonths);
    }
}
