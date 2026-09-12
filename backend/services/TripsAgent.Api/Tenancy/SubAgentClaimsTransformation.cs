using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using TripsAgent.Application.Identity;
using TripsAgent.Application.Tenancy.SubAgents;

namespace TripsAgent.Api.Tenancy;

/// <summary>
/// Takes off a sub-agent's token any permission its principal has denied it.
/// </summary>
/// <remarks>
/// <para>
/// Permissions are claims on a fifteen-minute access token, which is what keeps authorisation off
/// the database. That is fine for a permission an agency changes on its own roles — the user signs
/// in again soon enough. It is not fine for the one a <i>principal</i> takes away: an agency that
/// has just stopped a sub-agent seeing its net rates expects that to be true now, not in a quarter
/// of an hour, and "it will stop working shortly" is not something to write on that screen.
/// </para>
/// <para>
/// So the overrides are applied per request, here, before any endpoint's policy is evaluated. The
/// cost is one indexed query on a table with a handful of rows, and only for users who belong to
/// a sub-agency — a principal's request reads nothing.
/// </para>
/// <para>
/// It only ever <b>removes</b> claims. A transformation that could add one would be a way to grant
/// a permission outside the role system.
/// </para>
/// </remarks>
public sealed class SubAgentClaimsTransformation : IClaimsTransformation
{
    private readonly SubAgentPermissionService _permissions;
    private readonly IHttpContextAccessor _httpContext;

    public SubAgentClaimsTransformation(
        SubAgentPermissionService permissions,
        IHttpContextAccessor httpContext)
    {
        _permissions = permissions;
        _httpContext = httpContext;
    }

    public async Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);

        if (principal.Identity?.IsAuthenticated != true)
        {
            return principal;
        }

        // A principal is its own root. Only an agency whose root is somebody else is a sub-agent,
        // and only a sub-agent can have permissions taken away from it.
        if (!TryReadGuid(principal, TripsClaimTypes.AgencyId, out var agencyId)
            || !TryReadGuid(principal, TripsClaimTypes.RootAgencyId, out var rootAgencyId)
            || agencyId == rootAgencyId)
        {
            return principal;
        }

        var denied = await _permissions.DeniedCodesAsync(
            agencyId, _httpContext.HttpContext?.RequestAborted ?? CancellationToken.None);

        if (denied.Count == 0)
        {
            return principal;
        }

        var blocked = new HashSet<string>(denied, StringComparer.Ordinal);

        var toRemove = principal.Claims
            .Where(claim => claim.Type == TripsClaimTypes.Permission && blocked.Contains(claim.Value))
            .ToList();

        if (toRemove.Count == 0)
        {
            return principal;
        }

        // A copy, not the incoming principal: ASP.NET Core may call this more than once for one
        // request, and mutating the original would make the second call operate on a changed set.
        var trimmed = new ClaimsPrincipal(
            principal.Identities.Select(identity =>
                new ClaimsIdentity(
                    identity.Claims.Where(claim =>
                        claim.Type != TripsClaimTypes.Permission || !blocked.Contains(claim.Value)),
                    identity.AuthenticationType,
                    identity.NameClaimType,
                    identity.RoleClaimType)));

        return trimmed;
    }

    private static bool TryReadGuid(ClaimsPrincipal principal, string claimType, out Guid value) =>
        Guid.TryParse(principal.FindFirstValue(claimType), out value);
}
