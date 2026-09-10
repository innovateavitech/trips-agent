using System.Security.Claims;

namespace TripsAgent.Infrastructure.Scheduling;

/// <summary>
/// Decides who may open <c>/hangfire</c>.
///
/// This is a plain class taking plain arguments, with no reference to Hangfire or to ASP.NET, so
/// the rule can be unit-tested directly. The adapter that pulls those arguments out of a real
/// request is <c>HangfireDashboardAuthorizationFilter</c> in the Api.
///
/// The Hangfire dashboard is not read-only. Anyone who reaches it can requeue, delete and trigger
/// jobs — including jobs that move money. Hangfire ships with a default that allows local requests
/// only, and plenty of teams have shipped that default to production and found their dashboard
/// open to the internet, because behind a reverse proxy every request looks local.
///
/// So this fails closed: no signed-in user in the right role, no dashboard.
/// </summary>
/// <param name="requiredRole">Role the user must hold. Never an agent-facing role.</param>
/// <param name="allowUnauthenticatedLocalRequests">
/// Development escape hatch. The Api passes false unless the environment is Development.
/// </param>
public sealed class HangfireDashboardPolicy(
    string requiredRole,
    bool allowUnauthenticatedLocalRequests)
{
    /// <summary>Whether this request may see the dashboard.</summary>
    /// <param name="user">The request's principal, or null if the request is anonymous.</param>
    /// <param name="isLocalRequest">
    /// Whether the connection came from the loopback address. Only consulted in Development —
    /// see the class remarks for why it is not trustworthy on its own.
    /// </param>
    public bool IsAllowed(ClaimsPrincipal? user, bool isLocalRequest)
    {
        if (user?.Identity?.IsAuthenticated == true && user.IsInRole(requiredRole))
        {
            return true;
        }

        return allowUnauthenticatedLocalRequests && isLocalRequest;
    }
}
