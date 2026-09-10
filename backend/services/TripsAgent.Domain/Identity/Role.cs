using TripsAgent.Domain.Common;

namespace TripsAgent.Domain.Identity;

/// <summary>Whether a role applies inside an agency or across the platform.</summary>
public enum RoleScope
{
    /// <summary>Held by staff at a travel agency.</summary>
    Agency = 1,

    /// <summary>Held by Trips back-office staff.</summary>
    Platform = 2,
}

/// <summary>
/// A named bundle of permissions.
/// </summary>
/// <remarks>
/// <see cref="AgencyId"/> is null for the system roles we ship — Owner, Manager, Agent — which
/// every agency shares. An agency that wants something different creates its own role, which
/// carries its agency id and is invisible to everyone else.
/// </remarks>
public sealed class Role : Entity, IAuditableEntity
{
    /// <summary>Names of the roles seeded for every agency.</summary>
    public static class SystemRoles
    {
        public const string Owner = "Owner";
        public const string Manager = "Manager";
        public const string Agent = "Agent";

        /// <summary>Trips staff with unrestricted access.</summary>
        public const string SuperAdmin = "Super Admin";

        /// <summary>Trips staff who review KYB and support agencies.</summary>
        public const string OperationsAdmin = "Operations Admin";
    }

    private Role()
    {
        Name = string.Empty;
        Description = string.Empty;
    }

    /// <summary>Creates a role we ship, shared by every agency.</summary>
    public static Role CreateSystemRole(string name, RoleScope scope, string description) =>
        new()
        {
            AgencyId = null,
            Name = name,
            Scope = scope,
            IsSystem = true,
            Description = description,
        };

    /// <summary>Creates a role belonging to one agency.</summary>
    public static Role CreateForAgency(Guid agencyId, string name, string description)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(agencyId, Guid.Empty);

        return new Role
        {
            AgencyId = agencyId,
            Name = name,
            Scope = RoleScope.Agency,
            IsSystem = false,
            Description = description,
        };
    }

    /// <summary>Null for a system role shared by every agency.</summary>
    public Guid? AgencyId { get; private set; }

    public string Name { get; private set; }

    public RoleScope Scope { get; private set; }

    /// <summary>
    /// True for roles we ship. They cannot be renamed or deleted by an agency, because code and
    /// documentation refer to them by name.
    /// </summary>
    public bool IsSystem { get; private set; }

    public string Description { get; private set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Joins a <see cref="Role"/> to a <see cref="Permission"/>.</summary>
public sealed class RolePermission
{
    private RolePermission()
    {
    }

    public static RolePermission Create(Guid roleId, Guid permissionId) =>
        new() { RoleId = roleId, PermissionId = permissionId };

    public Guid RoleId { get; private set; }

    public Guid PermissionId { get; private set; }
}

/// <summary>
/// Grants a <see cref="Role"/> to a <see cref="User"/> within one agency.
/// </summary>
/// <remarks>
/// The agency is on this row, not implied by the user, because one person can work for a
/// principal and one of its sub-agents with different access in each — an owner in their own
/// business, an agent in a branch they help out with.
/// </remarks>
public sealed class UserRole : Entity, IAuditableEntity, ITenantScoped
{
    private UserRole()
    {
    }

    public static UserRole Grant(Guid userId, Guid roleId, Guid agencyId)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(agencyId, Guid.Empty);

        return new UserRole { UserId = userId, RoleId = roleId, AgencyId = agencyId };
    }

    public Guid UserId { get; private set; }

    public Guid RoleId { get; private set; }

    public Guid AgencyId { get; private set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
