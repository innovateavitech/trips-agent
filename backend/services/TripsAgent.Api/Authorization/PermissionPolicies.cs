using Microsoft.AspNetCore.Authorization;
using TripsAgent.Application.Identity;
using TripsAgent.Domain.Identity;

namespace TripsAgent.Api.Authorization;

/// <summary>
/// Turns permission codes into authorization policies.
/// </summary>
/// <remarks>
/// <para>
/// A policy per permission code, each satisfied by a matching <c>permission</c> claim on the
/// token. Checking a claim rather than querying the database keeps authorisation off the hot
/// path; the cost is that a permission revoked today survives in an issued token until it
/// expires, which is capped at the fifteen-minute access-token lifetime.
/// </para>
/// <para>
/// Policies are registered from <see cref="PermissionCodes.All"/> rather than listed by hand, so
/// a new permission cannot be added to the catalogue and then quietly have no policy behind it.
/// </para>
/// </remarks>
public static class PermissionPolicies
{
    /// <summary>The policy name for a permission code — the code itself, so there is nothing to look up.</summary>
    public static string For(string permissionCode) => permissionCode;

    public static AuthorizationBuilder AddPermissionPolicies(this AuthorizationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        foreach (var (code, _, _) in PermissionCodes.All)
        {
            builder.AddPolicy(For(code), policy => policy
                .RequireAuthenticatedUser()
                .RequireClaim(TripsClaimTypes.Permission, code));
        }

        return builder;
    }
}
