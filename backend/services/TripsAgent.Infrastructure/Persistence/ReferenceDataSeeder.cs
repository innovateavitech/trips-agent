using Microsoft.EntityFrameworkCore;
using TripsAgent.Application.Tenancy;
using TripsAgent.Domain.Identity;

namespace TripsAgent.Infrastructure.Persistence;

/// <summary>
/// Loads the rows the application cannot run without: the permission catalogue and the system
/// roles. Safe in every environment, and run by <c>migrate</c> as well as by <c>seed</c>.
/// </summary>
/// <remarks>
/// <para>
/// This used to live inside <see cref="DatabaseSeeder"/>, which also creates demo agencies with a
/// shared development password — and so must never run in production. That left production with
/// no "Owner" role, and registration would have failed on the first real signup. Reference data
/// and sample data have different audiences, so they now have different entry points.
/// </para>
/// <para>
/// Additive and idempotent: it inserts what is missing and never deletes. A permission removed
/// from the code but still granted by somebody's custom role should be revoked on purpose, not
/// vanish under a deploy and silently change what that role can do.
/// </para>
/// </remarks>
public static class ReferenceDataSeeder
{
    /// <summary>Ensures every permission and system role exists. Returns ids keyed by code and name.</summary>
    public static async Task<(Dictionary<string, Guid> Permissions, Dictionary<string, Guid> Roles)> EnsureAsync(
        AppDbContext dbContext,
        IPlatformScope platformScope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(platformScope);

        using var _ = platformScope.Enter("reference data — the permission catalogue and system roles are platform-wide");

        var permissions = await EnsurePermissionsAsync(dbContext, cancellationToken);
        var roles = await EnsureSystemRolesAsync(dbContext, permissions, cancellationToken);

        return (permissions, roles);
    }

    private static async Task<Dictionary<string, Guid>> EnsurePermissionsAsync(
        AppDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var existing = await dbContext.Permissions.ToDictionaryAsync(
            permission => permission.Code,
            permission => permission.Id,
            StringComparer.Ordinal,
            cancellationToken);

        foreach (var (code, category, description) in PermissionCodes.All)
        {
            if (existing.ContainsKey(code))
            {
                continue;
            }

            var permission = Permission.Create(code, category, description);
            dbContext.Permissions.Add(permission);
            existing[code] = permission.Id;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return existing;
    }

    private static async Task<Dictionary<string, Guid>> EnsureSystemRolesAsync(
        AppDbContext dbContext,
        Dictionary<string, Guid> permissions,
        CancellationToken cancellationToken)
    {
        var existing = await dbContext.Roles
            .Where(role => role.AgencyId == null)
            .ToDictionaryAsync(role => role.Name, role => role.Id, StringComparer.Ordinal, cancellationToken);

        foreach (var (name, scope, description, grantedCodes) in IdentitySeedData.SystemRoles)
        {
            if (existing.ContainsKey(name))
            {
                continue;
            }

            var role = Role.CreateSystemRole(name, scope, description);
            dbContext.Roles.Add(role);
            existing[name] = role.Id;

            foreach (var code in grantedCodes)
            {
                dbContext.RolePermissions.Add(RolePermission.Create(role.Id, permissions[code]));
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return existing;
    }
}
