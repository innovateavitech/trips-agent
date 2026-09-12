using Microsoft.Extensions.Caching.Memory;
using TripsAgent.Api.Authorization;
using TripsAgent.Application.Platform;
using TripsAgent.Contracts.Platform;
using TripsAgent.Domain.Identity;

namespace TripsAgent.Api.Platform;

/// <summary>The back office's front page: core counts, sales and the alerts queue.</summary>
/// <remarks>
/// <para>
/// The acceptance criterion is metrics no more than ten minutes stale. The answer is cached for
/// five — half the budget, so what a screen shows is always inside it even if the browser holds
/// its copy for a while. One entry for the whole platform: these numbers are the same for every
/// admin, so caching per caller would multiply the work and shorten nothing.
/// </para>
/// <para>
/// A memory cache rather than Redis on purpose. The worst case is two instances counting
/// separately and disagreeing for five minutes about a figure that is already an estimate; a
/// distributed cache would buy agreement at the price of a dependency this page does not need.
/// </para>
/// </remarks>
public static class OperationsDashboardEndpoints
{
    /// <summary>Half the ten-minute staleness budget, so the screen is always inside it.</summary>
    public static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(5);

    private const string CacheKey = "platform.operations-dashboard";

    public static IEndpointRouteBuilder MapOperationsDashboardEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/v1/admin/dashboard")
            .WithTags("Operations dashboard")
            .RequireAuthorization(PermissionPolicies.For(PermissionCodes.PlatformReportView));

        group.MapGet("/", async (
                bool? refresh,
                OperationsDashboardService dashboard,
                IMemoryCache cache,
                CancellationToken cancellationToken) =>
            {
                // `?refresh=true` skips the cache. During an incident, a figure five minutes old
                // is exactly the figure nobody wants.
                if (refresh is not true && cache.TryGetValue(CacheKey, out OperationsDashboardResponse? cached) && cached is not null)
                {
                    return Results.Ok(cached);
                }

                var fresh = await dashboard.BuildAsync(CacheFor, cancellationToken);
                cache.Set(CacheKey, fresh, CacheFor);

                return Results.Ok(fresh);
            })
            .WithName("OperationsDashboard")
            .Produces<OperationsDashboardResponse>();

        return app;
    }
}
