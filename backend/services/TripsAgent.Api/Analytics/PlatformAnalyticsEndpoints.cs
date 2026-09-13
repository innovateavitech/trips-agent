using Microsoft.Extensions.Caching.Memory;
using TripsAgent.Api.Authorization;
using TripsAgent.Application.Analytics;
using TripsAgent.Contracts.Analytics;
using TripsAgent.Domain.Identity;

namespace TripsAgent.Api.Analytics;

/// <summary>
/// The platform's own dashboard: GMV and growth, and how the suppliers are behaving.
/// </summary>
/// <remarks>
/// <para>
/// <c>platform.report.view</c>, the same permission the operations dashboard requires. Every read
/// behind these endpoints crosses agencies and goes through <c>IPlatformScope.Enter</c> with a
/// reason, which is logged with the acting user.
/// </para>
/// <para>
/// Cached for five minutes, like the operations dashboard, and for the same reason: these numbers
/// are the same for every admin, so caching per caller would multiply the work and shorten nothing.
/// The figures are already at most five minutes behind reality because that is how often the rollup
/// runs, so a five-minute cache doubles the worst case to ten — exactly the FRD's budget, and the
/// response carries the read models' own <c>generatedAt</c> so a screen can say how old they are.
/// </para>
/// </remarks>
public static class PlatformAnalyticsEndpoints
{
    /// <summary>How long an answer is reused. The rollup's own cadence.</summary>
    public static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(5);

    private const string SummaryCachePrefix = "platform.analytics.summary";
    private const string SuppliersCachePrefix = "platform.analytics.suppliers";

    public static IEndpointRouteBuilder MapPlatformAnalyticsEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/v1/admin/analytics")
            .WithTags("Platform analytics")
            .RequireAuthorization(PermissionPolicies.For(PermissionCodes.PlatformReportView));

        group.MapGet("/", async (
                DateOnly? from,
                DateOnly? to,
                bool? refresh,
                PlatformAnalyticsService analytics,
                IMemoryCache cache,
                TimeProvider clock,
                CancellationToken cancellationToken) =>
            {
                if (!AnalyticsEndpoints.TryWindow(from, to, clock, out var window, out var problem))
                {
                    return problem;
                }

                var key = $"{SummaryCachePrefix}:{window.From:O}:{window.To:O}";

                if (refresh is not true
                    && cache.TryGetValue(key, out PlatformAnalyticsResponse? cached)
                    && cached is not null)
                {
                    return Results.Ok(cached);
                }

                var fresh = await analytics.SummaryAsync(window.From, window.To, cancellationToken);
                cache.Set(key, fresh, CacheFor);

                return Results.Ok(fresh);
            })
            .WithName("PlatformAnalyticsSummary")
            .Produces<PlatformAnalyticsResponse>()
            .ProducesValidationProblem();

        group.MapGet("/suppliers", async (
                DateOnly? from,
                DateOnly? to,
                bool? refresh,
                PlatformAnalyticsService analytics,
                IMemoryCache cache,
                TimeProvider clock,
                CancellationToken cancellationToken) =>
            {
                if (!AnalyticsEndpoints.TryWindow(from, to, clock, out var window, out var problem))
                {
                    return problem;
                }

                var key = $"{SuppliersCachePrefix}:{window.From:O}:{window.To:O}";

                if (refresh is not true
                    && cache.TryGetValue(key, out SupplierPerformanceResponse? cached)
                    && cached is not null)
                {
                    return Results.Ok(cached);
                }

                var fresh = await analytics.SuppliersAsync(window.From, window.To, cancellationToken);
                cache.Set(key, fresh, CacheFor);

                return Results.Ok(fresh);
            })
            .WithName("PlatformSupplierPerformance")
            .Produces<SupplierPerformanceResponse>()
            .ProducesValidationProblem();

        // The export log. Writing it is the acceptance criterion; reading it is what makes the
        // criterion worth anything.
        group.MapGet("/exports", async (
                int? limit,
                PlatformAnalyticsService analytics,
                CancellationToken cancellationToken) =>
                Results.Ok(await analytics.ExportsAsync(limit ?? 100, cancellationToken)))
            .WithName("ListReportExports")
            .Produces<List<ReportExportAuditResponse>>();

        return app;
    }
}
