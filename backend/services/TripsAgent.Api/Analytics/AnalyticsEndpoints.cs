using System.Security.Claims;
using TripsAgent.Api.Authorization;
using TripsAgent.Application.Analytics;
using TripsAgent.Contracts.Analytics;
using TripsAgent.Domain.Analytics;
using TripsAgent.Domain.Identity;

namespace TripsAgent.Api.Analytics;

/// <summary>
/// An agency's own dashboard, and the drill-down from a number on it to the bookings behind it.
/// </summary>
/// <remarks>
/// <para>
/// Everything here reads the analytics read models under the ordinary tenant filter, so an agent
/// sees their own agency and nothing else without this file doing anything about it. There is no
/// platform scope in these endpoints and there must not be one.
/// </para>
/// <para>
/// <c>report.view</c> to look; <c>margin.view</c> as well to see what any of it cost. The margin
/// fields are dropped from the JSON rather than zeroed for a caller without the second — see
/// <see cref="AgencyAnalyticsService"/>.
/// </para>
/// </remarks>
public static class AnalyticsEndpoints
{
    /// <summary>
    /// The window used when a caller names neither end. Thirty days is what a dashboard opens on.
    /// </summary>
    public const int DefaultWindowDays = 30;

    /// <summary>
    /// The longest window a dashboard will draw.
    /// </summary>
    /// <remarks>
    /// Two years of days is 730 points, which is already more than a chart can show honestly. A
    /// longer window is a report, and reports are the thing that runs in the background.
    /// </remarks>
    public const int MaximumWindowDays = 731;

    public static IEndpointRouteBuilder MapAnalyticsEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/v1/analytics")
            .WithTags("Analytics")
            .RequireAuthorization(PermissionPolicies.For(PermissionCodes.ReportView));

        group.MapGet("/summary", async (
                DateOnly? from,
                DateOnly? to,
                ClaimsPrincipal user,
                AgencyAnalyticsService analytics,
                TimeProvider clock,
                CancellationToken cancellationToken) =>
            {
                if (!TryWindow(from, to, clock, out var window, out var problem))
                {
                    return problem;
                }

                var response = await analytics.SummaryAsync(
                    window.From,
                    window.To,
                    Pricing.PricingEndpoints.CanViewMargin(user),
                    cancellationToken);

                return Results.Ok(response);
            })
            .WithName("AgencyAnalyticsSummary")
            .Produces<AgencyAnalyticsResponse>()
            .ProducesValidationProblem();

        // The drill-down. A number on the dashboard is a range of days; this is the list of
        // bookings that add up to it.
        group.MapGet("/bookings", async (
                DateOnly? from,
                DateOnly? to,
                int? page,
                int? pageSize,
                ClaimsPrincipal user,
                AgencyAnalyticsService analytics,
                TimeProvider clock,
                CancellationToken cancellationToken) =>
            {
                if (!TryWindow(from, to, clock, out var window, out var problem))
                {
                    return problem;
                }

                var response = await analytics.BookingsAsync(
                    window.From,
                    window.To,
                    Pricing.PricingEndpoints.CanViewMargin(user),
                    page ?? 1,
                    pageSize ?? 50,
                    cancellationToken);

                return Results.Ok(response);
            })
            .WithName("AgencyAnalyticsBookings")
            .Produces<BookingDrillDownResponse>()
            .ProducesValidationProblem();

        return app;
    }

    /// <summary>
    /// Resolves the window a caller asked for, in Lagos days, or explains why it is not a window.
    /// </summary>
    /// <remarks>
    /// Defaults to the last thirty days ending today, where "today" is a Lagos day — the same day
    /// the aggregates are keyed by, so "today" on the screen and "today" in the table are the same
    /// date rather than differing for an hour either side of midnight.
    /// </remarks>
    internal static bool TryWindow(
        DateOnly? from,
        DateOnly? to,
        TimeProvider clock,
        out (DateOnly From, DateOnly To) window,
        out IResult problem)
    {
        var today = LagosDay.Of(clock.GetUtcNow());
        var resolvedTo = to ?? today;
        var resolvedFrom = from ?? resolvedTo.AddDays(-(DefaultWindowDays - 1));

        window = (resolvedFrom, resolvedTo);
        problem = Results.Empty;

        if (resolvedTo < resolvedFrom)
        {
            problem = Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["to"] = ["The last day of a window cannot come before its first."],
            });

            return false;
        }

        if (ReportScopeRules.DaysCovered(resolvedFrom, resolvedTo) > MaximumWindowDays)
        {
            problem = Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["from"] =
                [
                    $"A dashboard covers at most {MaximumWindowDays} days. Run a report for a longer window.",
                ],
            });

            return false;
        }

        return true;
    }
}
