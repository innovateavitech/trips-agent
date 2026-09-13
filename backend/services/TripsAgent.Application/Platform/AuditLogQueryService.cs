using Microsoft.EntityFrameworkCore;
using TripsAgent.Application.Persistence;
using TripsAgent.Application.Tenancy;
using TripsAgent.Contracts.Platform;
using TripsAgent.Domain.Auditing;

namespace TripsAgent.Application.Platform;

/// <summary>What slice of the audit trail the viewer asked for.</summary>
/// <param name="AgencyId">Only actions about this agency. Null for every agency and the platform.</param>
/// <param name="ActorUserId">Only what this person did.</param>
/// <param name="Action">An exact action name — <c>agency.suspended</c>.</param>
/// <param name="EntityType">The kind of thing acted on — <c>Agency</c>, <c>Wallet</c>.</param>
public sealed record AuditLogQuery(
    Guid? AgencyId = null,
    Guid? ActorUserId = null,
    string? Action = null,
    string? EntityType = null,
    string? EntityId = null,
    DateTimeOffset? From = null,
    DateTimeOffset? To = null,
    int Page = 1,
    int PageSize = 50)
{
    /// <summary>A page big enough to read, small enough that jsonb columns do not fill a response.</summary>
    public const int MaxPageSize = 200;

    public int SafePage => Page < 1 ? 1 : Page;

    public int SafePageSize => Math.Clamp(PageSize, 1, MaxPageSize);
}

/// <summary>
/// Reads <c>platform.audit_logs</c> for the back office's audit viewer.
/// </summary>
/// <remarks>
/// <para>
/// Read-only, and there is no write path here at all: the table refuses updates and deletes, and
/// rows are written by the save interceptor. The FRD's requirement (§2.15 RS-5) is actor,
/// timestamp and before/after state for every admin action, and this is where somebody goes to
/// find them.
/// </para>
/// <para>
/// The table has its own query filter — an agency sees its own trail, a caller with no agency
/// sees everything — so a platform user already reads across agencies here. The scope is entered
/// anyway, because "this read crosses agencies" should be visible in the log whichever mechanism
/// makes it possible.
/// </para>
/// </remarks>
public sealed class AuditLogQueryService
{
    private readonly IAppDbContext _db;
    private readonly IPlatformScope _platformScope;

    public AuditLogQueryService(IAppDbContext db, IPlatformScope platformScope)
    {
        _db = db;
        _platformScope = platformScope;
    }

    /// <summary>One page of the trail, newest first.</summary>
    public async Task<AuditLogPageResponse> SearchAsync(
        AuditLogQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        using var scope = _platformScope.Enter(
            "Admin console audit viewer — reads the platform audit trail across every agency");

        var entries = Filtered(query);
        var total = await entries.CountAsync(cancellationToken);

        var page = await entries
            .OrderByDescending(entry => entry.OccurredAt)
            .ThenByDescending(entry => entry.Id)
            .Skip((query.SafePage - 1) * query.SafePageSize)
            .Take(query.SafePageSize)
            .Select(entry => new
            {
                entry.Id,
                entry.OccurredAt,
                entry.AgencyId,
                AgencyName = _db.Agencies
                    .Where(agency => agency.Id == entry.AgencyId)
                    .Select(agency => agency.TradingName ?? agency.LegalName)
                    .FirstOrDefault(),
                entry.ActorUserId,
                ActorFirstName = _db.Users.Where(user => user.Id == entry.ActorUserId).Select(user => user.FirstName).FirstOrDefault(),
                ActorLastName = _db.Users.Where(user => user.Id == entry.ActorUserId).Select(user => user.LastName).FirstOrDefault(),
                entry.ActorType,
                entry.Action,
                entry.EntityType,
                entry.EntityId,
                entry.Reason,
                entry.BeforeState,
                entry.AfterState,
                entry.ActorIpAddress,
            })
            .ToListAsync(cancellationToken);

        return new AuditLogPageResponse(
            [.. page.Select(entry => new AuditLogEntryResponse(
                entry.Id,
                entry.OccurredAt,
                entry.AgencyId,
                entry.AgencyName,
                entry.ActorUserId,
                NameOf(entry.ActorFirstName, entry.ActorLastName, entry.ActorType),
                entry.ActorType.ToString(),
                entry.Action,
                entry.EntityType,
                entry.EntityId,
                entry.Reason,
                entry.BeforeState,
                entry.AfterState,
                entry.ActorIpAddress))],
            total,
            query.SafePage,
            query.SafePageSize);
    }

    /// <summary>Every action name that appears in the trail, so the viewer's filter is real.</summary>
    /// <remarks>
    /// Read from the data rather than from a list in code: a job or a feature that started
    /// writing a new action name should appear in the filter without anyone remembering to add it.
    /// </remarks>
    public async Task<IReadOnlyList<string>> ActionsAsync(CancellationToken cancellationToken = default)
    {
        using var scope = _platformScope.Enter("Admin console audit viewer — lists the action names in the trail");

        return await _db.AuditLogs.AsNoTracking()
            .Select(entry => entry.Action)
            .Distinct()
            .OrderBy(action => action)
            .ToListAsync(cancellationToken);
    }

    private IQueryable<AuditLogEntry> Filtered(AuditLogQuery query)
    {
        var entries = _db.AuditLogs.AsNoTracking();

        if (query.AgencyId is { } agencyId)
        {
            // Two ways a row belongs to an agency, and the viewer wants both.
            //
            // Rows the agency's own staff wrote carry its agency_id. Rows a Trips admin wrote
            // about it do not: the interceptor takes agency_id from the actor's own agency, and a
            // back-office admin has none. That is deliberate, and it is what keeps the reason for
            // a suspension out of the suspended agency's reach — so the second clause matches on
            // the subject of the action rather than widening agency_id.
            var subject = agencyId.ToString();

            entries = entries.Where(entry =>
                entry.AgencyId == agencyId
                || (entry.EntityType == nameof(Domain.Tenancy.Agency) && entry.EntityId == subject));
        }

        if (query.ActorUserId is { } actorId)
        {
            entries = entries.Where(entry => entry.ActorUserId == actorId);
        }

        if (!string.IsNullOrWhiteSpace(query.Action))
        {
            var action = query.Action.Trim();
            entries = entries.Where(entry => entry.Action == action);
        }

        if (!string.IsNullOrWhiteSpace(query.EntityType))
        {
            var entityType = query.EntityType.Trim();
            entries = entries.Where(entry => entry.EntityType == entityType);
        }

        if (!string.IsNullOrWhiteSpace(query.EntityId))
        {
            var entityId = query.EntityId.Trim();
            entries = entries.Where(entry => entry.EntityId == entityId);
        }

        // Both bounds are compared against occurred_at, which is the partition key: a bounded
        // search touches only the months it needs rather than every partition there is.
        if (query.From is { } from)
        {
            entries = entries.Where(entry => entry.OccurredAt >= from);
        }

        if (query.To is { } to)
        {
            entries = entries.Where(entry => entry.OccurredAt < to);
        }

        return entries;
    }

    /// <summary>
    /// The actor's name, or what acted when there was no person.
    /// </summary>
    /// <remarks>
    /// A deactivated colleague's row still has to name them: audit rows outlive accounts, and
    /// "someone" is not an answer. The name comes from the users table, which is never deleted.
    /// </remarks>
    private static string NameOf(string? firstName, string? lastName, AuditActorType actorType)
    {
        var name = $"{firstName} {lastName}".Trim();

        if (!string.IsNullOrEmpty(name))
        {
            return name;
        }

        return actorType switch
        {
            AuditActorType.System => "System",
            AuditActorType.Anonymous => "Anonymous",
            _ => "Unknown",
        };
    }
}
