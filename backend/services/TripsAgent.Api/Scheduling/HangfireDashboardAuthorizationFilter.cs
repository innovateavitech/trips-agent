using Hangfire.Dashboard;
using TripsAgent.Infrastructure.Scheduling;

namespace TripsAgent.Api.Scheduling;

/// <summary>
/// Adapts Hangfire's dashboard authorisation hook onto <see cref="HangfireDashboardPolicy"/>.
///
/// All the thinking lives in the policy, which is testable on its own. This class only pulls the
/// two facts the policy needs out of the live request.
/// </summary>
/// <param name="policy">The rule to apply.</param>
public sealed class HangfireDashboardAuthorizationFilter(HangfireDashboardPolicy policy)
    : IDashboardAuthorizationFilter
{
    /// <inheritdoc />
    public bool Authorize(DashboardContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var httpContext = context.GetHttpContext();

        // IsLoopback is only meaningful because the policy refuses to consult it outside
        // Development. Behind a reverse proxy every remote address looks local, so on its own
        // this check would let the whole internet in.
        var isLocalRequest = httpContext.Connection.RemoteIpAddress is { } remote
                             && System.Net.IPAddress.IsLoopback(remote);

        return policy.IsAllowed(httpContext.User, isLocalRequest);
    }
}
