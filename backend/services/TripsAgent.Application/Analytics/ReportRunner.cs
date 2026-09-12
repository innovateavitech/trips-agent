using Microsoft.EntityFrameworkCore;
using TripsAgent.Application.Notifications;
using TripsAgent.Application.Persistence;
using TripsAgent.Application.Tenancy;
using TripsAgent.Domain.Analytics;
using TripsAgent.Domain.Identity;

namespace TripsAgent.Application.Analytics;

/// <summary>Runs a queued report. The Worker's entry point, resolved by the job runner.</summary>
public interface IReportRunner
{
    public Task RunAsync(Guid reportJobId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Gives a queued report the tenancy it needs, runs it, and tells the requester it is ready.
/// </summary>
/// <remarks>
/// <para>
/// <b>The tenancy dance is the whole point of this class.</b> A Hangfire job starts with no tenant
/// and no scope, so it can see nothing — including the job row telling it what to do. It therefore
/// opens a platform scope for exactly one query, to read that row and learn whose report this is,
/// and then either adopts that agency as its tenant (so the ordinary query filter scopes the whole
/// report, exactly as it would for the agent who asked) or, for a platform report, keeps a scope
/// open for the duration. At no point does a generator filter by agency itself.
/// </para>
/// <para>
/// <b>Margin is re-decided here, not remembered.</b> Whether the file shows net cost and margin is
/// worked out from the requesting user's permissions at the moment the report runs, not from what
/// they held when they asked. Somebody who lost <c>margin.view</c> yesterday does not get a margin
/// report today because they queued it the day before.
/// </para>
/// </remarks>
public sealed class ReportRunner : IReportRunner
{
    private readonly IAppDbContext _db;
    private readonly ReportService _reports;
    private readonly IPlatformScope _platformScope;
    private readonly TenantContext _tenant;
    private readonly INotifier _notifier;

    public ReportRunner(
        IAppDbContext db,
        ReportService reports,
        IPlatformScope platformScope,
        TenantContext tenant,
        INotifier notifier)
    {
        _db = db;
        _reports = reports;
        _platformScope = platformScope;
        _tenant = tenant;
        _notifier = notifier;
    }

    public async Task RunAsync(Guid reportJobId, CancellationToken cancellationToken = default)
    {
        ReportJob? header;

        using (_platformScope.Enter("Report worker — reads one queued report job to learn whose rows it covers"))
        {
            header = await _db.ReportJobs.AsNoTracking()
                .FirstOrDefaultAsync(job => job.Id == reportJobId, cancellationToken);
        }

        if (header is null)
        {
            // Nothing to do. A job row that has gone is not an error worth retrying for ever.
            return;
        }

        var showMargin = await HoldsMarginViewAsync(header.RequestedByUserId, cancellationToken);

        ReportJob? finished;

        if (header.Scope == ReportScope.Agency && header.AgencyId is { } agencyId)
        {
            // Adopt the agency, exactly as the request middleware would have. From here on the
            // query filter does the scoping and the generator needs to know nothing about tenancy.
            _tenant.SetTenant(agencyId, userId: header.RequestedByUserId);
            finished = await _reports.RunQueuedAsync(reportJobId, showMargin, cancellationToken);
        }
        else
        {
            using var scope = _platformScope.Enter(
                $"Report worker — {header.DefinitionCode} across every agency, requested by {header.RequestedByUserId}");

            finished = await _reports.RunQueuedAsync(reportJobId, showMargin, cancellationToken);
        }

        if (finished is not null)
        {
            await NotifyAsync(finished, cancellationToken);
        }
    }

    /// <summary>
    /// Whether the requesting user may see margin, as of now.
    /// </summary>
    /// <remarks>
    /// Read inside a platform scope because the worker holds no tenant yet when this runs, and a
    /// user's role grants are tenant-scoped rows. False when there is no requester at all — a run
    /// with nobody behind it gets the cautious answer.
    /// </remarks>
    private async Task<bool> HoldsMarginViewAsync(Guid? userId, CancellationToken cancellationToken)
    {
        if (userId is null)
        {
            return false;
        }

        using var scope = _platformScope.Enter(
            "Report worker — checks whether the requester may see margin before writing it into a file");

        return await (
            from userRole in _db.UserRoles
            join rolePermission in _db.RolePermissions on userRole.RoleId equals rolePermission.RoleId
            join permission in _db.Permissions on rolePermission.PermissionId equals permission.Id
            where userRole.UserId == userId && permission.Code == PermissionCodes.MarginView
            select permission.Id).AnyAsync(cancellationToken);
    }

    /// <summary>
    /// Emails the requester that their file is ready, or that it failed.
    /// </summary>
    /// <remarks>
    /// The notification is queued inside the agency the report belongs to, so it is branded as that
    /// agency like every other email the system sends. A platform report has no agency: it is sent
    /// under the platform's own branding, because the recipient is Trips staff and there is no
    /// agent whose name it could sensibly carry.
    /// </remarks>
    private async Task NotifyAsync(ReportJob job, CancellationToken cancellationToken)
    {
        if (job.RequestedByUserId is not { } userId)
        {
            return;
        }

        using var scope = _platformScope.Enter(
            "Report worker — finds the requester's address to tell them their report is ready");

        var recipient = await _db.Users.AsNoTracking()
            .Where(user => user.Id == userId)
            .Select(user => new { user.Email, user.FirstName, user.AgencyId })
            .FirstOrDefaultAsync(cancellationToken);

        if (recipient is null)
        {
            return;
        }

        var agencyId = job.AgencyId ?? recipient.AgencyId;

        if (agencyId is null)
        {
            // A Trips back-office account belongs to no agency, and every notification row does.
            // Their reports are collected from the console rather than emailed; the run is still
            // recorded and still shows as finished there.
            return;
        }

        // Only the template's own variables: the brand tokens are filled in by the renderer from
        // the agency's branding record, which is what keeps every email in the agency's name.
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["reportName"] = job.ScopeDescription,
            ["rowCount"] = CsvWriter.Number(job.RowCount ?? 0),
            ["status"] = job.Status == ReportJobStatus.Succeeded ? "ready" : "failed",
        };

        await _notifier.QueueEmailAsync(
            new EmailNotificationRequest(
                agencyId.Value,
                NotificationTemplateCatalog.ReportReady,
                recipient.Email,
                recipient.FirstName,
                values,

                // One notification per finished job, however many times Hangfire retries it.
                $"{NotificationTemplateCatalog.ReportReady}:{job.Id}",
                userId),
            cancellationToken);

        await _db.SaveChangesAsync(cancellationToken);
    }
}
