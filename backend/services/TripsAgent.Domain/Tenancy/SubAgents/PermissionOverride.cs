using TripsAgent.Domain.Auditing;
using TripsAgent.Domain.Common;

namespace TripsAgent.Domain.Tenancy.SubAgents;

/// <summary>
/// A permission a principal has taken away from one of its sub-agents.
/// </summary>
/// <remarks>
/// <para>
/// <b>Deny only.</b> There is no grant effect, and that is the point: a principal can narrow what
/// a sub-agent's roles already give it, and can never widen it. A grant effect would let a
/// principal hand out a permission it does not hold itself, which is how a privilege-escalation
/// bug gets written by accident.
/// </para>
/// <para>
/// Effective permissions are therefore <c>role permissions minus overrides</c> — a subtraction,
/// computed by <see cref="SubAgentPermissions"/> and applied to the caller's claims on every
/// request, so removing one bites immediately rather than when the access token expires.
/// </para>
/// <para>
/// Margin visibility is an override like any other: denying <c>margin.view</c> is how a principal
/// hides net rates and markup from a sub-agent. There is no second switch for it.
/// </para>
/// <para>
/// <see cref="AgencyId"/> is the principal — see the remarks on <see cref="SubAgentScope"/> for why.
/// </para>
/// </remarks>
public sealed class PermissionOverride : Entity, IAuditableEntity, ITenantScoped, IAuditLogged
{
    /// <summary>The column's width, and the longest permission code the catalogue allows.</summary>
    public const int MaxPermissionCodeLength = 60;

    /// <summary>The column's width. A reason is required, so it is never silently empty.</summary>
    public const int MaxReasonLength = 500;

    private PermissionOverride()
    {
        PermissionCode = string.Empty;
        Reason = string.Empty;
    }

    /// <summary>Takes <paramref name="permissionCode"/> away from <paramref name="subAgencyId"/>.</summary>
    public static PermissionOverride Deny(
        Guid agencyId,
        Guid subAgencyId,
        string permissionCode,
        string reason)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(agencyId, Guid.Empty);
        ArgumentOutOfRangeException.ThrowIfEqual(subAgencyId, Guid.Empty);

        if (agencyId == subAgencyId)
        {
            throw new InvalidOperationException(
                "An agency cannot override its own permissions. Change its roles instead.");
        }

        if (string.IsNullOrWhiteSpace(permissionCode))
        {
            throw new ArgumentException("A permission code is required.", nameof(permissionCode));
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException(
                "A reason is required. Somebody will ask in six months why this sub-agent cannot do this.",
                nameof(reason));
        }

        return new PermissionOverride
        {
            AgencyId = agencyId,
            SubAgencyId = subAgencyId,
            PermissionCode = permissionCode.Trim().ToLowerInvariant(),
            Reason = reason.Trim(),
        };
    }

    /// <summary>The principal that imposed the override, and owns the row.</summary>
    public Guid AgencyId { get; private set; }

    /// <summary>The sub-agent the permission is taken from.</summary>
    public Guid SubAgencyId { get; private set; }

    /// <summary>A code from <c>PermissionCodes</c>, lower-cased.</summary>
    public string PermissionCode { get; private set; }

    /// <summary>Why, in the principal's own words. Shown on the permissions matrix.</summary>
    public string Reason { get; private set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Records a new reason for an override that already exists.</summary>
    public void Restate(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("A reason is required.", nameof(reason));
        }

        Reason = reason.Trim();
    }
}
