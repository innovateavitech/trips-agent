namespace TripsAgent.Application.Retention;

/// <summary>
/// Applies the retention schedule in <c>docs/DATA_RETENTION.md</c>: deletes operational rows past
/// their window, strips travel documents after the trip, and reports on the supplier call log.
/// </summary>
/// <remarks>
/// <para>
/// Runs daily as the <c>data-retention</c> recurring job in the Worker. <b>Dry run by default</b>: it
/// counts what it would delete and records that, and deletes nothing until
/// <c>DataRetention__DryRun=false</c> is set deliberately. Deleting is not reversible and this runs
/// unattended; the dry run is how a wrong <c>WHERE</c> clause is found before it costs a customer
/// their booking history.
/// </para>
/// <para>
/// Financial records, the audit log and everything that supports them are <b>never</b> touched —
/// the job refuses to start if any of its rules names one of those tables.
/// </para>
/// </remarks>
public interface IDataRetentionPurge
{
    public Task<DataRetentionRunResult> RunAsync(CancellationToken cancellationToken = default);
}

/// <summary>What one run did, or in a dry run would have done.</summary>
/// <param name="RunId">Also the correlation id on every audit row the run wrote.</param>
/// <param name="DryRun">True when nothing was deleted and the counts are what would have been.</param>
/// <param name="Tables">One entry per table the run considered.</param>
public sealed record DataRetentionRunResult(Guid RunId, bool DryRun, IReadOnlyList<RetentionTableOutcome> Tables)
{
    /// <summary>The outcome for <paramref name="table"/>, a schema-qualified name.</summary>
    public RetentionTableOutcome For(string table) =>
        Tables.SingleOrDefault(outcome => outcome.Table == table)
        ?? throw new ArgumentException($"The run did not consider {table}.", nameof(table));
}

/// <summary>One table's part of a run.</summary>
/// <param name="Table">Schema-qualified, e.g. <c>identity.login_attempts</c>.</param>
/// <param name="Action"><c>delete</c>, <c>anonymise</c> or <c>drop-partitions</c>.</param>
/// <param name="Rows">Rows deleted or anonymised — or, in a dry run, that would have been.</param>
/// <param name="Window">The retention window applied, in words: <c>90 days</c>, <c>3 whole months</c>.</param>
/// <param name="Cutoff">Rows older than this were in scope.</param>
/// <param name="Error">Why the table failed, or null when it did not.</param>
public sealed record RetentionTableOutcome(
    string Table,
    string Action,
    int Rows,
    string Window,
    DateTimeOffset Cutoff,
    string? Error = null);
