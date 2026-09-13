using System.Security.Claims;
using TripsAgent.Application.Identity;
using TripsAgent.Application.Tenancy;
using TripsAgent.Application.Tenancy.SubAgents;

namespace TripsAgent.Api.Tenancy;

/// <summary>
/// Takes off a sub-agent's token any permission its principal has denied it, before authorisation
/// looks at it.
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
/// <b>Middleware rather than an <c>IClaimsTransformation</c>.</b> A transformation runs inside
/// <c>UseAuthentication</c>, before <c>UseTenantContext</c> has read the token — so the query
/// filters would still be unresolved and the read would find nothing, silently. Here the tenant is
/// already set and the read is an ordinary, policed one. It must stay between
/// <c>UseTenantContext</c> and <c>UseAuthorization</c>; <c>Program.cs</c> says so where it is
/// registered.
/// </para>
/// <para>
/// It only ever <b>removes</b> claims. One that could add a claim would be a way to grant a
/// permission outside the role system.
/// </para>
/// </remarks>
public sealed class SubAgentPermissionMiddleware
{
    private readonly RequestDelegate _next;

    public SubAgentPermissionMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(
        HttpContext context,
        ITenantContext tenant,
        SubAgentPermissionService permissions)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(permissions);

        // A principal is its own root, and only an agency whose root is somebody else is a
        // sub-agent. A principal's request therefore reads nothing here.
        if (context.User.Identity?.IsAuthenticated == true
            && tenant.AgencyId is { } agencyId
            && tenant.RootAgencyId is { } rootAgencyId
            && agencyId != rootAgencyId)
        {
            var denied = await permissions.DeniedCodesAsync(agencyId, context.RequestAborted);

            if (denied.Count > 0)
            {
                context.User = WithoutClaims(context.User, denied);
            }
        }

        await _next(context);
    }

    private static ClaimsPrincipal WithoutClaims(ClaimsPrincipal principal, IReadOnlyList<string> denied)
    {
        var blocked = new HashSet<string>(denied, StringComparer.Ordinal);

        // A copy rather than a mutation: the incoming principal is the one the authentication
        // handler cached, and removing claims from it would outlive this request.
        return new ClaimsPrincipal(
            principal.Identities.Select(identity =>
                new ClaimsIdentity(
                    identity.Claims.Where(claim =>
                        claim.Type != TripsClaimTypes.Permission || !blocked.Contains(claim.Value)),
                    identity.AuthenticationType,
                    identity.NameClaimType,
                    identity.RoleClaimType)));
    }
}

/// <summary>Registers <see cref="SubAgentPermissionMiddleware"/> in the pipeline.</summary>
public static class SubAgentPermissionMiddlewareExtensions
{
    public static IApplicationBuilder UseSubAgentPermissions(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        return app.UseMiddleware<SubAgentPermissionMiddleware>();
    }
}
