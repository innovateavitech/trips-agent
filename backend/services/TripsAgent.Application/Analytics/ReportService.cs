using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TripsAgent.Application.Auditing;
using TripsAgent.Application.Persistence;
using TripsAgent.Application.Storage;
using TripsAgent.Application.Tenancy;
using TripsAgent.Contracts.Analytics;
using TripsAgent.Domain.Analytics;
using TripsAgent.Domain.Auditing;
using TripsAgent.Domain.Identity;

namespace TripsAgent.Application.Analytics;

/// <summary>Why a report could not be run.</summary>
public enum ReportRequestOutcome
{
    /// <summary>It ran, or it was queued.</summary>
    Accepted = 1,

    /// <summary>There is no such report.</summary>
    UnknownDefinition = 2,

    /// <summary>The caller does not hold the permission the definition requires.</summary>
    Forbidden = 3,

    /// <summary>The window is backwards, or longer than a report may cover.</summary>
    InvalidWindow = 4,

    /// <summary>An agency report was asked for by a caller with no agency, or the other way round.</summary>
    WrongScope = 5,
}

/// <summary>What happened to a request to run a report.</summary>
/// <param name="Outcome">Whether it ran, and if not, why not.</param>
/// <param name="Job">The recorded run, when there is one.</param>
/// <param name="Content">The finished file, for a synchronous run only.</param>
/// <param name="Message">A sentence for the caller when the outcome is not <c>Accepted</c>.</param>
public sealed record ReportRequestResult(
    ReportRequestOutcome Outcome,
    ReportJob? Job = null,
    ReportContent? Content = null,
    string? Message = null);

/// <summary>
/// Runs reports, decides which ones may run in the request, and logs every export.
/// </summary>
/// <remarks>
/// <para>
/// <b>Sync or async is not a preference.</b> <see cref="ReportScopeRules"/> decides, and the console
/// is told before the button is pressed. Over ninety days, or crossing tenants, and the work goes to
/// the background — a cross-tenant read is unbounded by definition and an HTTP request is the wrong
/// place to discover that.
/// </para>
/// <para>
/// <b>Every export is logged.</b> FRD §2.15 UC-1C RS-6 asks for it explicitly, and this is the only
/// place that writes <c>analytics.report_exports_audit</c>. A row is written each time rows leave:
/// once when a file is produced, and once each time one is downloaded — a file taken three times has
/// left three times, and "who has this data" is the question the table exists to answer. The two are
/// told apart by the scope description, which says what was taken and how it left.
/// </para>
/// <para>
/// <b>A failed report logs nothing.</b> Nothing left the database, so there is nothing to record,
/// and an audit trail padded with exports that did not happen is one nobody reads.
/// </para>
/// </remarks>
public sealed partial class ReportService
{
    /// <summary>The longest window any report may cover. Three years.</summary>
    /// <remarks>
    /// A ceiling rather than no ceiling: an accidental <c>from=0001-01-01</c> should be a sentence,
    /// not a worker pinned for an hour building a file nobody wanted.
    /// </remarks>
    public const int MaximumReportDays = 1_096;

    private readonly IAppDbContext _db;
    private readonly ReportGenerator _generator;
    private readonly IBlobStorage _storage;
    private readonly IPlatformScope _platformScope;
    private readonly IReportDispatcher _dispatcher;
    private readonly IAuditContext _audit;
    private readonly ITenantContext _tenant;
    private readonly TimeProvider _clock;
    private readonly ILogger<ReportService> _logger;

    public ReportService(
        IAppDbContext db,
        ReportGenerator generator,
        IBlobStorage storage,
        IPlatformScope platformScope,
        IReportDispatcher dispatcher,
        IAuditContext audit,
        ITenantContext tenant,
        TimeProvider clock,
        ILogger<ReportService> logger)
    {
        _db = db;
        _generator = generator;
        _storage = storage;
        _platformScope = platformScope;
        _dispatcher = dispatcher;
        _audit = audit;
        _tenant = tenant;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>The reports this caller may run, with how each one would run.</summary>
    /// <param name="heldPermissions">The permission codes on the caller's token.</param>
    public async Task<IReadOnlyList<ReportDefinitionResponse>> DefinitionsAsync(
        IReadOnlyCollection<string> heldPermissions,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(heldPermissions);

        var definitions = await _db.ReportDefinitions.AsNoTracking()
            .Where(definition => definition.IsActive)
            .OrderBy(definition => definition.Scope)
            .ThenBy(definition => definition.Name)
            .ToListAsync(cancellationToken);

        return definitions
            .Where(definition => heldPermissions.Contains(definition.RequiredPermission))
            .Select(definition => new ReportDefinitionResponse(
                definition.Code,
                definition.Name,
                definition.Description,
                definition.Scope.ToString(),
                definition.Scope == ReportScope.Platform,
                ReportScopeRules.MaximumSynchronousDays))
            .ToList();
    }

    /// <summary>
    /// Runs a report, or queues it.
    /// </summary>
    /// <param name="heldPermissions">The caller's permission codes — checked against the definition.</param>
    /// <param name="showMargin">True when the caller holds <c>margin.view</c>.</param>
    public async Task<ReportRequestResult> RequestAsync(
        string definitionCode,
        DateOnly from,
        DateOnly to,
        IReadOnlyCollection<string> heldPermissions,
        bool showMargin,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(heldPermissions);

        var definition = await _db.ReportDefinitions.AsNoTracking()
            .FirstOrDefaultAsync(
                candidate => candidate.Code == definitionCode && candidate.IsActive,
                cancellationToken);

        if (definition is null)
        {
            return new ReportRequestResult(
                ReportRequestOutcome.UnknownDefinition,
                Message: $"There is no report called '{definitionCode}'.");
        }

        if (!heldPermissions.Contains(definition.RequiredPermission))
        {
            return new ReportRequestResult(
                ReportRequestOutcome.Forbidden,
                Message: $"Running this report needs the {definition.RequiredPermission} permission.");
        }

        if (to < from)
        {
            return new ReportRequestResult(
                ReportRequestOutcome.InvalidWindow,
                Message: "The last day of a report cannot come before its first.");
        }

        if (ReportScopeRules.DaysCovered(from, to) > MaximumReportDays)
        {
            return new ReportRequestResult(
                ReportRequestOutcome.InvalidWindow,
                Message: $"A report covers at most {MaximumReportDays} days.");
        }

        // An agency report needs an agency to be about. A Trips back-office account has none, and
        // an agent cannot ask for the platform's.
        if (definition.Scope == ReportScope.Agency && !_tenant.HasTenant)
        {
            return new ReportRequestResult(
                ReportRequestOutcome.WrongScope,
                Message: "This report is about one agency, and this account does not belong to one.");
        }

        var mode = ReportScopeRules.ModeFor(definition.Scope, from, to);
        var agencyId = definition.Scope == ReportScope.Agency ? _tenant.AgencyId : null;

        // A platform report is a cross-tenant operation from its very first row. Its job record
        // carries no agency, and the policy on analytics.report_jobs is
        // `platform_scope_active() OR agency_id = current_agency_id()` — a NULL agency matches
        // nothing, so without a scope open the INSERT itself is refused by the database. Which is
        // the backstop working: writing a platform-wide row is exactly the act that should have to
        // say why.
        using var scope = definition.Scope == ReportScope.Platform
            ? _platformScope.Enter($"Report — {definition.Name} across every agency")
            : NullScope.Instance;

        var job = ReportJob.Request(
            definition.Code,
            definition.Scope,
            mode,
            ReportFormat.Csv,
            agencyId,
            _tenant.UserId,
            from,
            to,
            await DescribeAsync(definition, from, to, agencyId, cancellationToken),
            _clock.GetUtcNow());

        _db.ReportJobs.Add(job);
        await _db.SaveChangesAsync(cancellationToken);

        if (mode == ReportRunMode.Asynchronous)
        {
            // Handed over after the row is committed, so the worker can always find the job it is
            // told about. The requester is emailed when it finishes.
            await _dispatcher.EnqueueAsync(job.Id, cancellationToken);
            LogQueued(_logger, job.Id, definition.Code, ReportScopeRules.DaysCovered(from, to));

            return new ReportRequestResult(ReportRequestOutcome.Accepted, job);
        }

        var content = await _generator.GenerateAsync(definition.Code, from, to, showMargin, cancellationToken);
        var key = await StoreAsync(job, content, cancellationToken);

        job.Complete(key, content.Content.LongLength, content.RowCount, _clock.GetUtcNow());
        RecordExport(job, $"{job.ScopeDescription} — generated");
        await _db.SaveChangesAsync(cancellationToken);

        return new ReportRequestResult(ReportRequestOutcome.Accepted, job, content);
    }

    /// <summary>The caller's own report runs, newest first.</summary>
    public async Task<IReadOnlyList<ReportJobResponse>> JobsAsync(
        int limit = 25,
        CancellationToken cancellationToken = default)
    {
        var jobs = await _db.ReportJobs.AsNoTracking()
            .OrderByDescending(job => job.RequestedAt)
            .Take(Math.Clamp(limit, 1, 100))
            .ToListAsync(cancellationToken);

        return jobs.Select(ToResponseCore).ToList();
    }

    /// <summary>One run, or null when the caller may not see it.</summary>
    public async Task<ReportJobResponse?> JobAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        var job = await _db.ReportJobs.AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == jobId, cancellationToken);

        return job is null ? null : ToResponseCore(job);
    }

    /// <summary>
    /// Hands over a finished file, and records that it left.
    /// </summary>
    /// <returns>The bytes, or null when there is no such finished job for this caller.</returns>
    public async Task<ReportDownload?> DownloadAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        var job = await _db.ReportJobs
            .FirstOrDefaultAsync(candidate => candidate.Id == jobId, cancellationToken);

        if (job is null || !job.HasResult)
        {
            return null;
        }

        await using var stream = await _storage.OpenReadAsync(job.ResultStorageKey!, cancellationToken);
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, cancellationToken);

        // A download is an export: the rows leave the system again, possibly into different hands
        // from the ones that asked for them.
        RecordExport(job, $"{job.ScopeDescription} — downloaded");
        await _db.SaveChangesAsync(cancellationToken);

        return new ReportDownload(FileNameFor(job), ReportContent.ContentType, buffer.ToArray());
    }

    /// <summary>
    /// Runs a queued report. The worker's entry point.
    /// </summary>
    /// <remarks>
    /// Called with the job's own agency already set as the tenant for an agency report, or inside a
    /// platform scope for a platform one — see <c>ReportRunner</c>. This method does not choose its
    /// own tenancy, deliberately: a background job that decided for itself which agency's rows to
    /// read would be the one place in the system where that decision is not reviewable.
    /// </remarks>
    public async Task<ReportJob?> RunQueuedAsync(Guid jobId, bool showMargin, CancellationToken cancellationToken = default)
    {
        var job = await _db.ReportJobs.FirstOrDefaultAsync(candidate => candidate.Id == jobId, cancellationToken);

        if (job is null || job.Status != ReportJobStatus.Queued)
        {
            // Already running, already finished, or not visible to this tenant. Not an error: a
            // Hangfire retry after a completed run lands here, and doing nothing is right.
            return job;
        }

        job.Start(_clock.GetUtcNow());
        await _db.SaveChangesAsync(cancellationToken);

        try
        {
            var content = await _generator.GenerateAsync(
                job.DefinitionCode, job.FromDay, job.ToDay, showMargin, cancellationToken);

            var key = await StoreAsync(job, content, cancellationToken);

            job.Complete(key, content.Content.LongLength, content.RowCount, _clock.GetUtcNow());
            RecordExport(job, $"{job.ScopeDescription} — generated");
            await _db.SaveChangesAsync(cancellationToken);

            LogFinished(_logger, job.Id, job.DefinitionCode, content.RowCount);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _db.ChangeTracker.Clear();

            var failed = await _db.ReportJobs.FirstOrDefaultAsync(
                candidate => candidate.Id == jobId, cancellationToken);

            if (failed is not null)
            {
                failed.Fail(exception.Message, _clock.GetUtcNow());
                await _db.SaveChangesAsync(cancellationToken);
            }

            LogFailed(_logger, exception, jobId);
            throw;
        }

        return job;
    }

    /// <summary>The file name a browser saves it as.</summary>
    public static string FileNameFor(ReportJob job)
    {
        ArgumentNullException.ThrowIfNull(job);

        return $"{job.DefinitionCode}_{CsvWriter.Date(job.FromDay)}_{CsvWriter.Date(job.ToDay)}.csv";
    }

    /// <summary>Adds the export record to the caller's unit of work. Never saves on its own.</summary>
    private void RecordExport(ReportJob job, string scopeDescription) =>
        _db.ReportExportAudits.Add(ReportExportAudit.Record(
            job.Id,
            job.DefinitionCode,
            job.Scope,
            job.Format,
            job.AgencyId,
            _audit.ActorUserId,
            _audit.ActorType,
            _audit.ActorIpAddress,
            scopeDescription,
            job.RowCount ?? 0,
            _clock.GetUtcNow(),
            _audit.CorrelationId));

    private async Task<string> StoreAsync(ReportJob job, ReportContent content, CancellationToken cancellationToken)
    {
        // Keyed by scope and job id. A platform report has no agency to file it under, which is
        // itself worth being able to see at a glance in a bucket listing.
        var folder = job.AgencyId?.ToString() ?? "platform";
        var key = $"reports/{folder}/{job.Id:N}.csv";

        using var stream = new MemoryStream(content.Content, writable: false);
        await _storage.StoreAsync(stream, key, ReportContent.ContentType, cancellationToken);

        return key;
    }

    /// <summary>What was asked for, in a sentence a reviewer will understand in six months.</summary>
    private async Task<string> DescribeAsync(
        ReportDefinition definition,
        DateOnly from,
        DateOnly to,
        Guid? agencyId,
        CancellationToken cancellationToken)
    {
        var who = "every agency";

        if (agencyId is not null)
        {
            who = await _db.Agencies.AsNoTracking()
                .Where(agency => agency.Id == agencyId)
                .Select(agency => agency.LegalName)
                .FirstOrDefaultAsync(cancellationToken) ?? agencyId.Value.ToString();
        }

        return $"{definition.Name}, {CsvWriter.Date(from)} to {CsvWriter.Date(to)}, {who}";
    }

    /// <summary>One run, as the console sees it.</summary>
    public static ReportJobResponse ToResponse(ReportJob job)
    {
        ArgumentNullException.ThrowIfNull(job);

        return ToResponseCore(job);
    }

    private static ReportJobResponse ToResponseCore(ReportJob job) =>
        new(
            job.Id,
            job.DefinitionCode,
            job.Scope.ToString(),
            job.RunMode.ToString(),
            job.Status.ToString(),
            job.Format.ToString(),
            job.FromDay,
            job.ToDay,
            job.ScopeDescription,
            job.RequestedAt,
            job.CompletedAt,
            job.RowCount,
            job.ResultSizeBytes,
            job.HasResult,
            job.ErrorMessage);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Report {JobId} ({Definition}) queued: {Days} day(s), too large or too wide to answer in the request.")]
    private static partial void LogQueued(ILogger logger, Guid jobId, string definition, int days);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Report {JobId} ({Definition}) finished with {RowCount} row(s).")]
    private static partial void LogFinished(ILogger logger, Guid jobId, string definition, int rowCount);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Report {JobId} failed. Nothing was exported, so nothing was logged as exported.")]
    private static partial void LogFailed(ILogger logger, Exception exception, Guid jobId);
}

/// <summary>
/// A scope handle that opens nothing.
/// </summary>
/// <remarks>
/// So an agency report and a platform report can be written as one path, with the platform scope
/// as the only difference, rather than two copies of the same fifteen lines.
/// </remarks>
internal sealed class NullScope : IDisposable
{
    public static readonly NullScope Instance = new();

    private NullScope()
    {
    }

    public void Dispose()
    {
    }
}

/// <summary>A finished report, ready to be handed to a browser.</summary>
public sealed record ReportDownload(string FileName, string ContentType, byte[] Content);

/// <summary>Hands a queued report to the background. A port: Application does not know Hangfire.</summary>
public interface IReportDispatcher
{
    public Task EnqueueAsync(Guid reportJobId, CancellationToken cancellationToken = default);
}
