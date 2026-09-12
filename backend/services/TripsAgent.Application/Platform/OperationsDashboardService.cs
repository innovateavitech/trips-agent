using Microsoft.EntityFrameworkCore;
using TripsAgent.Application.Persistence;
using TripsAgent.Application.Tenancy;
using TripsAgent.Contracts.Platform;
using TripsAgent.Domain.Common;
using TripsAgent.Domain.Orders;
using TripsAgent.Domain.Platform;
using TripsAgent.Domain.Tenancy;
using TripsAgent.Domain.Tenancy.Kyb;

namespace TripsAgent.Application.Platform;

/// <summary>
/// The numbers on the back office's front page, and the alerts under them.
/// </summary>
/// <remarks>
/// <para>
/// The acceptance criterion is "no more than ten minutes stale". Rather than a snapshot table and
/// a job to fill it, this counts on demand and the API caches the answer for five — half the
/// budget, so what a screen shows is always inside it, and there is no second copy of the
/// platform's numbers to go wrong. Swap in a materialised snapshot when the counts outgrow a
/// query, not before.
/// </para>
/// <para>
/// Every read crosses agencies, which is the point of the dashboard, so it runs inside
/// <see cref="IPlatformScope"/> with a reason. The endpoint above requires
/// <c>platform.report.view</c>.
/// </para>
/// </remarks>
public sealed class OperationsDashboardService
{
    /// <summary>How many alerts the widget shows before "and 40 more".</summary>
    public const int AlertLimit = 20;

    private readonly IAppDbContext _db;
    private readonly IPlatformScope _platformScope;
    private readonly TimeProvider _clock;

    public OperationsDashboardService(IAppDbContext db, IPlatformScope platformScope, TimeProvider clock)
    {
        _db = db;
        _platformScope = platformScope;
        _clock = clock;
    }

    /// <summary>Counts everything the front page shows, as of now.</summary>
    public async Task<OperationsDashboardResponse> BuildAsync(
        TimeSpan freshFor,
        CancellationToken cancellationToken = default)
    {
        using var scope = _platformScope.Enter(
            "Operations dashboard — counts agencies, sales and alerts across every agency");

        var now = _clock.GetUtcNow();

        var byStatus = await _db.Agencies.AsNoTracking()
            .GroupBy(agency => agency.Status)
            .Select(group => new { Status = group.Key, Count = group.Count() })
            .ToListAsync(cancellationToken);

        int CountOf(AgencyStatus status) =>
            byStatus.FirstOrDefault(entry => entry.Status == status)?.Count ?? 0;

        var agencies = new AgencyCountsResponse(
            byStatus.Sum(entry => entry.Count),
            CountOf(AgencyStatus.PendingVerification),
            CountOf(AgencyStatus.Verified),
            CountOf(AgencyStatus.Rejected),
            CountOf(AgencyStatus.Suspended),
            CountOf(AgencyStatus.Terminated));

        var pendingKyb = await _db.KybSubmissions.AsNoTracking()
            .CountAsync(
                submission => submission.Status == KybSubmissionStatus.Submitted
                    || submission.Status == KybSubmissionStatus.UnderReview,
                cancellationToken);

        var openAlerts = await _db.AdminAlerts.AsNoTracking()
            .Where(alert => alert.Status != AdminAlertStatus.Resolved)
            .Select(alert => new { alert.Severity })
            .ToListAsync(cancellationToken);

        // A line whose money was taken and whose supplier did not deliver. The single most
        // urgent number on this page: somebody has paid and has nothing.
        var needingResolution = await _db.OrderLines.AsNoTracking()
            .CountAsync(
                line => line.FulfilmentStatus == FulfilmentStatus.FailedNeedsResolution
                    && line.ResolutionStatus != Domain.Orders.ResolutionStatus.ResolvedRebooked
                    && line.ResolutionStatus != Domain.Orders.ResolutionStatus.ResolvedRefunded,
                cancellationToken);

        var sales = new List<SalesWindowResponse>
        {
            await SalesAsync("Today", now.Subtract(TimeSpan.FromDays(1)), now, cancellationToken),
            await SalesAsync("Last 7 days", now.Subtract(TimeSpan.FromDays(7)), now, cancellationToken),
            await SalesAsync("Last 30 days", now.Subtract(TimeSpan.FromDays(30)), now, cancellationToken),
        };

        var alerts = await AlertsAsync(cancellationToken);

        return new OperationsDashboardResponse(
            now,
            now.Add(freshFor),
            agencies,
            pendingKyb,
            openAlerts.Count,
            openAlerts.Count(alert => alert.Severity == AdminAlertSeverity.Critical),
            needingResolution,
            sales,
            alerts);
    }

    /// <summary>What sold between two instants, across every agency.</summary>
    /// <remarks>
    /// <para>
    /// Placed orders only, and only those whose money landed: an order sitting at
    /// <c>PendingPayment</c> is a hope, not a sale, and cancelled or refunded ones have been given
    /// back. Counting either would make the platform look like it earned money it does not have.
    /// </para>
    /// <para>
    /// The amounts are read back as <see cref="Money"/> and added up here rather than with a SQL
    /// <c>SUM</c>. Money is a value-converted type, so the provider can compare it but not
    /// aggregate it; the window is bounded by date, which keeps the row count sane. Revisit with a
    /// mapped aggregate if a thirty-day window ever stops being cheap.
    /// </para>
    /// <para>
    /// Every agency sells in NGN today (build-plan decision 17). When that stops being true this
    /// has to group by currency rather than assert one — adding kobo to cents would be worse than
    /// showing nothing.
    /// </para>
    /// </remarks>
    private async Task<SalesWindowResponse> SalesAsync(
        string label,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken)
    {
        var rows = await _db.Orders.AsNoTracking()
            .Where(order =>
                order.PlacedAt != null
                && order.PlacedAt >= from
                && order.PlacedAt < to
                && order.Status != OrderStatus.PendingPayment
                && order.Status != OrderStatus.Cancelled
                && order.Status != OrderStatus.Refunded)
            .Select(order => new
            {
                order.Currency,
                order.TotalGrossMinor,
                order.TotalNetMinor,
                order.TotalMarkupMinor,
                order.TotalPlatformFeeMinor,
            })
            .ToListAsync(cancellationToken);

        return new SalesWindowResponse(
            label,
            from,
            to,
            rows.Count > 0 ? rows[0].Currency : "NGN",
            rows.Count,
            Total(rows.Select(row => row.TotalGrossMinor)),
            Total(rows.Select(row => row.TotalNetMinor)),
            Total(rows.Select(row => row.TotalMarkupMinor)),
            Total(rows.Select(row => row.TotalPlatformFeeMinor)));
    }

    /// <summary>Open alerts, most urgent first and oldest within that — the order the index serves.</summary>
    private async Task<List<AdminAlertResponse>> AlertsAsync(CancellationToken cancellationToken)
    {
        var alerts = await _db.AdminAlerts.AsNoTracking()
            .Where(alert => alert.Status != AdminAlertStatus.Resolved)
            .OrderByDescending(alert => alert.Severity)
            .ThenBy(alert => alert.CreatedAt)
            .Take(AlertLimit)
            .Select(alert => new
            {
                alert.Id,
                alert.Type,
                alert.Severity,
                alert.Status,
                alert.AgencyId,
                AgencyName = _db.Agencies
                    .Where(agency => agency.Id == alert.AgencyId)
                    .Select(agency => agency.TradingName ?? agency.LegalName)
                    .FirstOrDefault(),
                alert.EntityType,
                alert.EntityId,
                alert.Message,
                alert.CreatedAt,
            })
            .ToListAsync(cancellationToken);

        return [.. alerts.Select(alert => new AdminAlertResponse(
            alert.Id,
            alert.Type.ToString(),
            alert.Severity.ToString(),
            alert.Status.ToString(),
            alert.AgencyId,
            alert.AgencyName,
            alert.EntityType,
            alert.EntityId,
            alert.Message,
            alert.CreatedAt))];
    }

    private static long Total(IEnumerable<Money> amounts) =>
        amounts.Aggregate(0L, (running, amount) => checked(running + amount.AmountMinor));
}
