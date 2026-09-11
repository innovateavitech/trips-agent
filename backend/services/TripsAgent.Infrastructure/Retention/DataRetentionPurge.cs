using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using TripsAgent.Application.Retention;
using TripsAgent.Application.Suppliers;
using TripsAgent.Application.Tenancy;
using TripsAgent.Domain.Auditing;
using TripsAgent.Infrastructure.Persistence;
using TripsAgent.Infrastructure.Suppliers;

namespace TripsAgent.Infrastructure.Retention;

/// <summary>
/// The retention schedule's purge job (issue #105). See <see cref="IDataRetentionPurge"/> for what it
/// promises, and <c>docs/DATA_RETENTION.md</c> for the schedule itself.
/// </summary>
/// <remarks>
/// <para>
/// <b>Refuses before it starts.</b> The rules are checked against <see cref="RetentionCatalogue"/> in
/// the constructor, so a rule aimed at a protected table stops the whole job with nothing deleted — not
/// after the tables ahead of it in the list are already gone. They are checked again beside each
/// statement.
/// </para>
/// <para>
/// <b>Runs as the application role,</b> not the schema owner. It only deletes rows, and
/// <c>tripsagent_app</c> has no DELETE on the ledger, the audit log, orders or issued documents at all —
/// one more thing between a bad rule and the books. Crossing agencies goes through
/// <see cref="IPlatformScope"/>, the logged way to do it (CLAUDE.md rule 3), and row-level security
/// admits exactly that.
/// </para>
/// <para>
/// <b>Idempotent.</b> The cutoff is the start of today less the window, not "now" less the window, so
/// every run on the same day draws the same line — a second run finds nothing the first did not already
/// handle. And the anonymise rule only matches rows that still hold something to clear.
/// </para>
/// <para>
/// <b>Every table, every run, gets an audit row</b>: table, row count, window, cutoff and whether it was
/// a dry run. In a live run the rows and their audit row commit in one transaction, so nothing is ever
/// deleted without its record. A table that fails is recorded as failed, the rest still run, and the
/// run then throws so Hangfire shows it red.
/// </para>
/// </remarks>
public sealed partial class DataRetentionPurge : IDataRetentionPurge
{
    /// <summary>The <c>entity_type</c> of this job's audit rows.</summary>
    public const string AuditEntityType = "DataRetention";

    /// <summary>The partitioned supplier call log, which this job reports on and never deletes from itself.</summary>
    public const string SupplierApiCallsTable = "supplier.supplier_api_calls";

    private const string AuditReason = "Retention schedule: docs/DATA_RETENTION.md";

    private readonly AppDbContext _db;
    private readonly IPlatformScope _platformScope;
    private readonly ISupplierApiCallMaintenance _supplierApiCalls;
    private readonly DataRetentionOptions _options;
    private readonly SupplierApiCallOptions _supplierApiCallOptions;
    private readonly TimeProvider _clock;
    private readonly ILogger<DataRetentionPurge> _logger;
    private readonly IReadOnlyList<RetentionRule> _rules;

    /// <param name="rules">
    /// The rules to run. Null — the only value production passes — means the catalogue's. Tests pass
    /// their own to prove a rule aimed at a protected table is refused.
    /// </param>
    public DataRetentionPurge(
        AppDbContext db,
        IPlatformScope platformScope,
        ISupplierApiCallMaintenance supplierApiCalls,
        DataRetentionOptions options,
        SupplierApiCallOptions supplierApiCallOptions,
        TimeProvider clock,
        ILogger<DataRetentionPurge> logger,
        IReadOnlyList<RetentionRule>? rules = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(platformScope);
        ArgumentNullException.ThrowIfNull(supplierApiCalls);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(supplierApiCallOptions);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);

        var chosen = rules ?? RetentionCatalogue.Rules(options);

        // Before anything runs. See the class remarks.
        RetentionCatalogue.EnsureAllowed(chosen);

        _db = db;
        _platformScope = platformScope;
        _supplierApiCalls = supplierApiCalls;
        _options = options;
        _supplierApiCallOptions = supplierApiCallOptions;
        _clock = clock;
        _logger = logger;
        _rules = chosen;
    }

    public async Task<DataRetentionRunResult> RunAsync(CancellationToken cancellationToken = default)
    {
        var runId = Guid.CreateVersion7();
        var dryRun = _options.DryRun;
        var now = _clock.GetUtcNow();
        var today = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);

        LogStarting(_logger, runId, dryRun);

        // Every agency's rows, deliberately: the schedule applies to the platform, not to one tenant.
        using var scope = _platformScope.Enter(
            "Data retention: applying the retention schedule (docs/DATA_RETENTION.md) across all agencies");

        var outcomes = new List<RetentionTableOutcome>();

        foreach (var rule in _rules)
        {
            outcomes.Add(await ApplyAsync(rule, today - rule.Window, dryRun, runId, cancellationToken));
        }

        outcomes.Add(await ReportSupplierApiCallsAsync(today, dryRun, runId, cancellationToken));

        var failed = outcomes.Where(outcome => outcome.Error is not null).Select(outcome => outcome.Table).ToList();

        LogFinished(_logger, runId, dryRun, outcomes.Count, failed.Count);

        if (failed.Count > 0)
        {
            throw new InvalidOperationException(
                $"Data retention run {runId} failed for {string.Join(", ", failed)}. Every other table was "
                + "processed, and each failure has a retention.failed audit row. See docs/runbooks/data-retention.md.");
        }

        return new DataRetentionRunResult(runId, dryRun, outcomes);
    }

    private async Task<RetentionTableOutcome> ApplyAsync(
        RetentionRule rule,
        DateTimeOffset cutoff,
        bool dryRun,
        Guid runId,
        CancellationToken cancellationToken)
    {
        // Checked again right beside the statement it guards, not only in the constructor.
        RetentionCatalogue.EnsureAllowed([rule]);

        var action = rule.Action == RetentionAction.Delete ? "delete" : "anonymise";
        var outcome = new RetentionTableOutcome(rule.Table, action, 0, rule.WindowDescription, cutoff);

        try
        {
            if (dryRun)
            {
                var wouldAffect = await CountAsync(rule.Table, rule.Predicate, cutoff, cancellationToken);
                outcome = outcome with { Rows = wouldAffect };

                await SaveAuditAsync(outcome, dryRun, runId, cancellationToken);
            }
            else
            {
                // The rows and the audit row that records them commit together, or neither does.
                var affected = await new EfTransactionRunner(_db).RunAsync(
                    async token =>
                    {
                        var rows = await _db.Database.ExecuteSqlRawAsync(
                            StatementFor(rule), [CutoffParameter(cutoff)], token);

                        _db.AuditLogs.Add(AuditEntry(outcome with { Rows = rows }, dryRun, runId));
                        await _db.SaveChangesAsync(token);

                        return rows;
                    },
                    cancellationToken);

                outcome = outcome with { Rows = affected };
            }

            LogTable(_logger, runId, rule.Table, action, outcome.Rows, outcome.Window, cutoff, dryRun);
            return outcome;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return await RecordFailureAsync(outcome, ex, dryRun, runId, cancellationToken);
        }
    }

    /// <summary>
    /// <c>supplier_api_calls</c> is partitioned by month and aged out by dropping whole partitions — the
    /// plan's job 30, which already exists as <c>supplier-api-call-maintenance</c>. Not re-implemented
    /// here: this counts what is past the window and, in a live run, calls that same maintenance.
    /// </summary>
    /// <remarks>
    /// In a dry run the maintenance job still drops expired partitions on its own schedule at 03:15: it
    /// shipped before this job with no dry run of its own, and this does not hold it back. This job runs
    /// at 03:10, so its dry-run row is a report of what is about to go.
    /// </remarks>
    private async Task<RetentionTableOutcome> ReportSupplierApiCallsAsync(
        DateTimeOffset today,
        bool dryRun,
        Guid runId,
        CancellationToken cancellationToken)
    {
        var months = _supplierApiCallOptions.RetentionMonths;

        // The boundary supplier.drop_expired_supplier_api_call_partitions uses: the start of this month,
        // less the retention. Partitions are whole months, so every row before it is in one that goes.
        var cutoff = new DateTimeOffset(today.Year, today.Month, 1, 0, 0, 0, TimeSpan.Zero).AddMonths(-months);

        var outcome = new RetentionTableOutcome(
            SupplierApiCallsTable, "drop-partitions", 0, $"{months} whole months", cutoff);

        try
        {
            var rows = await CountAsync(SupplierApiCallsTable, "t.occurred_at < @cutoff", cutoff, cancellationToken);
            outcome = outcome with { Rows = rows };

            if (!dryRun)
            {
                var result = await _supplierApiCalls.RunAsync(cancellationToken);
                LogPartitionsDropped(_logger, runId, result.PartitionsDropped, months);
            }

            await SaveAuditAsync(outcome, dryRun, runId, cancellationToken);

            LogTable(_logger, runId, outcome.Table, outcome.Action, rows, outcome.Window, cutoff, dryRun);
            return outcome;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return await RecordFailureAsync(outcome, ex, dryRun, runId, cancellationToken);
        }
    }

    /// <summary>
    /// Records a table that failed, and carries on with the next. If even the failure cannot be recorded,
    /// that throws and the run stops — a job that cannot write down what it did should not keep going.
    /// </summary>
    private async Task<RetentionTableOutcome> RecordFailureAsync(
        RetentionTableOutcome outcome,
        Exception exception,
        bool dryRun,
        Guid runId,
        CancellationToken cancellationToken)
    {
        LogTableFailed(_logger, exception, runId, outcome.Table);

        // Anything the failed attempt staged must not ride along with the failure record.
        _db.ChangeTracker.Clear();

        var failed = outcome with { Rows = 0, Error = exception.Message };
        await SaveAuditAsync(failed, dryRun, runId, cancellationToken);

        return failed;
    }

    private Task<int> CountAsync(string table, string predicate, DateTimeOffset cutoff, CancellationToken cancellationToken) =>
        _db.Database
            .SqlQueryRaw<int>(CountStatement(table, predicate), CutoffParameter(cutoff))
            .SingleAsync(cancellationToken);

    private async Task SaveAuditAsync(RetentionTableOutcome outcome, bool dryRun, Guid runId, CancellationToken cancellationToken)
    {
        _db.AuditLogs.Add(AuditEntry(outcome, dryRun, runId));
        await _db.SaveChangesAsync(cancellationToken);
    }

    private AuditLogEntry AuditEntry(RetentionTableOutcome outcome, bool dryRun, Guid runId) =>
        new()
        {
            OccurredAt = _clock.GetUtcNow(),
            AgencyId = null,
            ActorType = AuditActorType.System,
            Action = ActionName(outcome, dryRun),
            EntityType = AuditEntityType,
            EntityId = outcome.Table,
            AfterState = JsonSerializer.Serialize(new
            {
                table = outcome.Table,
                action = outcome.Action,
                dryRun,
                rows = outcome.Rows,
                window = outcome.Window,
                cutoff = outcome.Cutoff,
                error = outcome.Error,
            }),
            Reason = AuditReason,
            CorrelationId = runId.ToString(),
        };

    /// <summary>The audit <c>action</c>, so "what did retention delete last month" is one query.</summary>
    public static string ActionName(RetentionTableOutcome outcome, bool dryRun)
    {
        ArgumentNullException.ThrowIfNull(outcome);

        if (outcome.Error is not null)
        {
            return "retention.failed";
        }

        if (dryRun)
        {
            return "retention.dry_run";
        }

        return outcome.Action switch
        {
            "anonymise" => "retention.anonymised",
            "drop-partitions" => "retention.partitions_dropped",
            _ => "retention.deleted",
        };
    }

    // The table names and predicates below come from RetentionCatalogue — constants in this codebase,
    // checked against the catalogue before use — never from input. The one value that varies, the
    // cutoff, is a parameter.
    private static string CountStatement(string table, string predicate) =>
        $"SELECT count(*)::int AS \"Value\" FROM {table} AS t WHERE {predicate}";

    private static string StatementFor(RetentionRule rule) =>
        rule.Action switch
        {
            RetentionAction.Delete => $"DELETE FROM {rule.Table} AS t WHERE {rule.Predicate}",
            RetentionAction.Anonymise => $"UPDATE {rule.Table} AS t SET {rule.Assignments} WHERE {rule.Predicate}",
            _ => throw new InvalidOperationException($"No statement for retention action {rule.Action}."),
        };

    private static NpgsqlParameter CutoffParameter(DateTimeOffset cutoff) =>
        new("cutoff", NpgsqlDbType.TimestampTz) { Value = cutoff };

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Data retention run {RunId} starting. Dry run: {DryRun}.")]
    private static partial void LogStarting(ILogger logger, Guid runId, bool dryRun);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Data retention run {RunId}: {Table} — {Action} {Rows} rows older than {Cutoff} ({Window}). Dry run: {DryRun}.")]
    private static partial void LogTable(
        ILogger logger, Guid runId, string table, string action, int rows, string window, DateTimeOffset cutoff, bool dryRun);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Data retention run {RunId}: supplier call log maintenance dropped {Partitions} partitions past {Months} months.")]
    private static partial void LogPartitionsDropped(ILogger logger, Guid runId, int partitions, int months);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "Data retention run {RunId}: {Table} failed and nothing in it was changed. The run carries on with the other tables.")]
    private static partial void LogTableFailed(ILogger logger, Exception exception, Guid runId, string table);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Data retention run {RunId} finished. Dry run: {DryRun}. Tables: {Tables}. Failed: {Failed}.")]
    private static partial void LogFinished(ILogger logger, Guid runId, bool dryRun, int tables, int failed);
}
