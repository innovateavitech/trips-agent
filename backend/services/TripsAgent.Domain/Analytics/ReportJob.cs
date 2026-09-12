using TripsAgent.Domain.Auditing;
using TripsAgent.Domain.Common;

namespace TripsAgent.Domain.Analytics;

/// <summary>
/// <c>analytics.report_jobs</c> — one run of one report, whether or not anybody waited for it.
/// </summary>
/// <remarks>
/// <para>
/// A synchronous export gets a row too. It costs one insert and it buys two things: every export
/// in the system is reachable from one table, and the console's "recent reports" list is the same
/// list whether the report took 200 milliseconds or ten minutes. A run that is never recorded is a
/// run nobody can answer questions about later.
/// </para>
/// <para>
/// <b>The nullable agency.</b> <see cref="AgencyId"/> is null for a platform report, exactly as
/// <c>identity.user_roles.agency_id</c> is null for a back-office grant, and for the same reason:
/// the tenant policy is <c>platform_scope_active() OR agency_id = current_agency_id()</c>, and
/// NULL is never equal to anything, so a platform run is invisible to every agency for both
/// reading and writing. It is not a tenant-scoped entity, because <c>ITenantScoped</c> needs a
/// non-nullable agency; its filter is written out in <c>AppDbContext</c> alongside the audit log's.
/// </para>
/// <para>
/// <b>Where the file goes.</b> A finished report is written straight to <c>IBlobStorage</c> and
/// the key is kept here, rather than becoming a row in <c>assets</c>. Assets belong to exactly one
/// agency and go through the upload pipeline — presigned PUT, virus scan, image variants — none of
/// which applies to bytes this system generated itself, and the first of which a platform report
/// with no agency cannot satisfy.
/// </para>
/// </remarks>
public sealed class ReportJob : Entity, IAuditableEntity
{
    private ReportJob()
    {
        DefinitionCode = string.Empty;
        ScopeDescription = string.Empty;
    }

    /// <summary>Records a report that is about to run.</summary>
    /// <param name="agencyId">The agency whose rows these are; null for a platform report.</param>
    /// <param name="scopeDescription">
    /// What was asked for, in a sentence — "Sales, 1 Jan 2026 to 31 Mar 2026, Kano Travel". Written
    /// once, here, and copied onto the export audit row: six months later the parameters mean
    /// nothing without it.
    /// </param>
    public static ReportJob Request(
        string definitionCode,
        ReportScope scope,
        ReportRunMode mode,
        ReportFormat format,
        Guid? agencyId,
        Guid? requestedByUserId,
        DateOnly fromDay,
        DateOnly toDay,
        string scopeDescription,
        DateTimeOffset requestedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(definitionCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeDescription);

        if (scope == ReportScope.Agency && agencyId is null)
        {
            throw new ArgumentException("An agency-scoped report must name its agency.", nameof(agencyId));
        }

        if (toDay < fromDay)
        {
            throw new ArgumentException("A report's last day cannot precede its first.", nameof(toDay));
        }

        return new ReportJob
        {
            DefinitionCode = definitionCode,
            Scope = scope,
            RunMode = mode,
            Format = format,
            AgencyId = agencyId,
            RequestedByUserId = requestedByUserId,
            FromDay = fromDay,
            ToDay = toDay,
            ScopeDescription = scopeDescription,
            Status = mode == ReportRunMode.Asynchronous ? ReportJobStatus.Queued : ReportJobStatus.Running,
            RequestedAt = requestedAt,
            StartedAt = mode == ReportRunMode.Asynchronous ? null : requestedAt,
        };
    }

    /// <summary>The definition this ran. Text rather than a foreign key so a retired report's runs survive.</summary>
    public string DefinitionCode { get; private set; }

    public ReportScope Scope { get; private set; }

    public ReportRunMode RunMode { get; private set; }

    public ReportFormat Format { get; private set; }

    /// <summary>Whose rows. Null means every agency — a platform report.</summary>
    public Guid? AgencyId { get; private set; }

    /// <summary>Who asked. Null only for a scheduled run with no human behind it.</summary>
    public Guid? RequestedByUserId { get; private set; }

    public DateOnly FromDay { get; private set; }

    public DateOnly ToDay { get; private set; }

    /// <summary>What was asked for, in words.</summary>
    public string ScopeDescription { get; private set; }

    public ReportJobStatus Status { get; private set; }

    public DateTimeOffset RequestedAt { get; private set; }

    public DateTimeOffset? StartedAt { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    /// <summary>Rows in the file, not counting the header. Null until it finishes.</summary>
    public int? RowCount { get; private set; }

    /// <summary>Where the finished file lives in blob storage. Null until it finishes.</summary>
    public string? ResultStorageKey { get; private set; }

    public long? ResultSizeBytes { get; private set; }

    public string? ErrorMessage { get; private set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>True once there is a file to download.</summary>
    public bool HasResult => Status == ReportJobStatus.Succeeded && ResultStorageKey is not null;

    /// <summary>The worker has picked it up.</summary>
    public void Start(DateTimeOffset now)
    {
        if (Status != ReportJobStatus.Queued)
        {
            throw new InvalidOperationException($"A report job at {Status} cannot be started.");
        }

        Status = ReportJobStatus.Running;
        StartedAt = now;
    }

    /// <summary>The file is written and countable.</summary>
    public void Complete(string storageKey, long sizeBytes, int rowCount, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageKey);
        ArgumentOutOfRangeException.ThrowIfNegative(rowCount);

        Status = ReportJobStatus.Succeeded;
        ResultStorageKey = storageKey;
        ResultSizeBytes = sizeBytes;
        RowCount = rowCount;
        CompletedAt = now;
        ErrorMessage = null;
    }

    /// <summary>It threw. The message is kept so the requester is told something useful.</summary>
    public void Fail(string error, DateTimeOffset now)
    {
        Status = ReportJobStatus.Failed;
        ErrorMessage = string.IsNullOrWhiteSpace(error)
            ? "The report could not be produced."
            : error.Length <= 1000 ? error : error[..1000];
        CompletedAt = now;
    }
}

/// <summary>
/// <c>analytics.report_exports_audit</c> — every export, with who, what, how many rows and when.
/// </summary>
/// <remarks>
/// <para>
/// FRD §2.15 UC-1C RS-6 asks for this explicitly, and the reason is in the shape of the thing: an
/// export is the one operation that takes a large slice of the database out of the database, and a
/// platform export takes a slice of <i>every agency's</i>. If a customer list turns up somewhere it
/// should not be, this table is the only record of who could have taken it.
/// </para>
/// <para>
/// Append-only, like the audit log: no UPDATE and no DELETE is granted to the application role, and
/// a trigger refuses both to anyone the grants do not bind. A row is written when the rows leave,
/// not when they are asked for — a failed report exports nothing and logs nothing.
/// </para>
/// </remarks>
public sealed class ReportExportAudit : Entity
{
    private ReportExportAudit()
    {
        DefinitionCode = string.Empty;
        ScopeDescription = string.Empty;
    }

    public static ReportExportAudit Record(
        Guid? reportJobId,
        string definitionCode,
        ReportScope scope,
        ReportFormat format,
        Guid? agencyId,
        Guid? actorUserId,
        AuditActorType actorType,
        string? actorIpAddress,
        string scopeDescription,
        int rowCount,
        DateTimeOffset exportedAt,
        string? correlationId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(definitionCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeDescription);
        ArgumentOutOfRangeException.ThrowIfNegative(rowCount);

        return new ReportExportAudit
        {
            ReportJobId = reportJobId,
            DefinitionCode = definitionCode,
            Scope = scope,
            Format = format,
            AgencyId = agencyId,
            ActorUserId = actorUserId,
            ActorType = actorType,
            ActorIpAddress = actorIpAddress,
            ScopeDescription = scopeDescription,
            RowCount = rowCount,
            ExportedAt = exportedAt,
            CorrelationId = correlationId,
        };
    }

    /// <summary>The run that produced the file, when there was one.</summary>
    public Guid? ReportJobId { get; private set; }

    public string DefinitionCode { get; private set; }

    public ReportScope Scope { get; private set; }

    public ReportFormat Format { get; private set; }

    /// <summary>Whose rows left. Null means every agency's — the row that matters most here.</summary>
    public Guid? AgencyId { get; private set; }

    /// <summary>Who took them.</summary>
    public Guid? ActorUserId { get; private set; }

    /// <summary>In what capacity: an agency's own staff, Trips back office, or a scheduled job.</summary>
    public AuditActorType ActorType { get; private set; }

    /// <summary>Where from. Text, so IPv6 and a proxied chain both fit.</summary>
    public string? ActorIpAddress { get; private set; }

    /// <summary>What was taken, in words.</summary>
    public string ScopeDescription { get; private set; }

    /// <summary>How many rows left. Not counting the header.</summary>
    public int RowCount { get; private set; }

    public DateTimeOffset ExportedAt { get; private set; }

    public string? CorrelationId { get; private set; }
}
