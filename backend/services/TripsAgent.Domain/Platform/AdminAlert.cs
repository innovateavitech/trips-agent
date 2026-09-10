using TripsAgent.Domain.Common;
using TripsAgent.Domain.Tenancy.Kyb;

namespace TripsAgent.Domain.Platform;

/// <summary>What an alert is about.</summary>
public enum AdminAlertType
{
    /// <summary>An agency is waiting on a KYB decision.</summary>
    PendingKyb = 1,

    GatewayError = 2,
    Dispute = 3,
    ReversalRequired = 4,

    /// <summary>A booking's ticket time limit is about to pass, or has.</summary>
    TicketTimeLimitBreach = 5,
}

/// <summary>How quickly somebody needs to look.</summary>
public enum AdminAlertSeverity
{
    Info = 1,
    Warning = 2,

    /// <summary>Money or a live booking is at stake.</summary>
    Critical = 3,
}

public enum AdminAlertStatus
{
    Open = 1,
    Acknowledged = 2,
    Resolved = 3,
}

/// <summary>
/// Something in the platform that needs a person to act. Drives the back-office alerts widget.
/// </summary>
/// <remarks>
/// Deliberately not tenant-scoped. Alerts are read by Trips staff across every agency — that is
/// the whole point of the queue — so <see cref="AgencyId"/> is which agency an alert is <i>about</i>,
/// not who may see it.
/// </remarks>
public sealed class AdminAlert : Entity, IAuditableEntity
{
    private AdminAlert()
    {
        Message = string.Empty;
        EntityType = string.Empty;
    }

    /// <summary>Raises an alert that an agency has submitted KYB and is waiting.</summary>
    public static AdminAlert ForPendingKyb(Guid agencyId, Guid submissionId, string agencyName) =>
        new()
        {
            Type = AdminAlertType.PendingKyb,

            // An agency that cannot transact until somebody looks is losing business every day
            // it waits, but nothing is broken — a warning, not a crisis.
            Severity = AdminAlertSeverity.Warning,
            Status = AdminAlertStatus.Open,
            AgencyId = agencyId,
            EntityType = nameof(KybSubmission),
            EntityId = submissionId,
            Message = $"{agencyName} has submitted KYB documents and is waiting for review.",
        };

    public AdminAlertType Type { get; private set; }

    public AdminAlertSeverity Severity { get; private set; }

    public AdminAlertStatus Status { get; private set; }

    /// <summary>The agency this is about. Null for alerts about the platform itself.</summary>
    public Guid? AgencyId { get; private set; }

    /// <summary>What kind of record the alert points at, and which one.</summary>
    public string EntityType { get; private set; }

    public Guid? EntityId { get; private set; }

    /// <summary>One sentence, written for whoever sees it in the queue.</summary>
    public string Message { get; private set; }

    public Guid? AcknowledgedByUserId { get; private set; }

    public DateTimeOffset? AcknowledgedAt { get; private set; }

    public DateTimeOffset? ResolvedAt { get; private set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Someone has picked it up.</summary>
    public void Acknowledge(Guid userId, DateTimeOffset at)
    {
        if (Status != AdminAlertStatus.Open)
        {
            return;
        }

        Status = AdminAlertStatus.Acknowledged;
        AcknowledgedByUserId = userId;
        AcknowledgedAt = at;
    }

    /// <summary>The underlying thing has been dealt with.</summary>
    public void Resolve(DateTimeOffset at)
    {
        if (Status == AdminAlertStatus.Resolved)
        {
            return;
        }

        Status = AdminAlertStatus.Resolved;
        ResolvedAt = at;
    }
}
