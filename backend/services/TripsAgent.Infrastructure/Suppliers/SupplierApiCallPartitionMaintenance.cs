using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TripsAgent.Application.Suppliers;
using TripsAgent.Infrastructure.Persistence;

namespace TripsAgent.Infrastructure.Suppliers;

/// <summary>
/// How long supplier call history is kept, and how far ahead its partitions are prepared.
/// </summary>
/// <remarks>
/// The plan gives <c>supplier_api_calls</c> 90 days of hot retention. Three whole months, dropped a
/// month at a time, keeps between three and four months — never less than 90 days. The evidence a
/// payment reversal rests on is <c>supplier_status_polls</c>, which is not partitioned and not
/// dropped; this table is the full request and response, which is large and loses value fast.
/// </remarks>
public sealed class SupplierApiCallOptions
{
    /// <summary>Configuration section this binds to.</summary>
    public const string SectionName = "SupplierApiCalls";

    /// <summary>Months of history to keep. Partitions older than this are dropped whole.</summary>
    public int RetentionMonths { get; set; } = 3;

    /// <summary>
    /// Future months to keep partitions ready for. More than one so a failing job has a quarter's
    /// runway before a missing partition makes every supplier call fail to record.
    /// </summary>
    public int PartitionsCreatedAhead { get; set; } = 3;
}

/// <summary>
/// Drives the two SQL functions the AddSupplierSchema migration installs for
/// <c>supplier.supplier_api_calls</c>.
/// </summary>
/// <remarks>
/// <para>
/// The same design as <c>AuditLogPartitionMaintenance</c>: the DDL lives in database functions, so no
/// table name is ever built from a string in C#. Runs on the admin connection, because creating and
/// dropping tables is the schema owner's job and the application role is not allowed to (ADR-0006).
/// </para>
/// <para>
/// Every query aliases its result <c>AS "Value"</c>: EF's scalar <c>SqlQuery&lt;T&gt;</c> selects a
/// column by that exact name once a LINQ operator is applied.
/// </para>
/// </remarks>
public sealed class SupplierApiCallPartitionMaintenance(
    AppDbContext context,
    IOptions<SupplierApiCallOptions> options) : ISupplierApiCallMaintenance
{
    public async Task<SupplierApiCallMaintenanceResult> RunAsync(CancellationToken cancellationToken = default)
    {
        var settings = options.Value;
        var ensured = new List<string>();

        // From the current month outwards — a database restored from an old backup has no partition
        // for today otherwise.
        for (var offset = 0; offset <= settings.PartitionsCreatedAhead; offset++)
        {
            var name = await context.Database
                .SqlQuery<string>(
                    $"""
                     SELECT supplier.create_supplier_api_call_partition(
                         (date_trunc('month', now()) + make_interval(months => {offset}))::date) AS "Value"
                     """)
                .SingleAsync(cancellationToken);

            ensured.Add(name);
        }

        var dropped = await context.Database
            .SqlQuery<int>(
                $"SELECT supplier.drop_expired_supplier_api_call_partitions({settings.RetentionMonths}) AS \"Value\"")
            .SingleAsync(cancellationToken);

        return new SupplierApiCallMaintenanceResult(ensured, dropped, settings.RetentionMonths);
    }
}

/// <summary>
/// Puts supplier call log maintenance on Hangfire's clock.
/// </summary>
/// <remarks>
/// Daily although the partitions are monthly: the run is idempotent, so running it more often costs
/// nothing and a failure is retried tomorrow rather than next month. 03:15 UTC, a quarter-hour after
/// the audit log's maintenance so the two DDL jobs do not queue behind each other. Registered by the
/// Worker only — the API must never run jobs.
/// </remarks>
public static class SupplierApiCallMaintenanceSchedule
{
    /// <summary>The recurring job's id, as it appears in the Hangfire dashboard.</summary>
    public const string JobId = "supplier-api-call-maintenance";

    public const string CronExpression = "15 3 * * *";

    public static void Register(IRecurringJobManager recurringJobs)
    {
        ArgumentNullException.ThrowIfNull(recurringJobs);

        recurringJobs.AddOrUpdate<ISupplierApiCallMaintenance>(
            JobId,

            // Hangfire swaps CancellationToken.None for its own token when the job runs.
            maintenance => maintenance.RunAsync(CancellationToken.None),
            CronExpression,
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });
    }
}
