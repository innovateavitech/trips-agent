using Microsoft.EntityFrameworkCore;
using TripsAgent.Application.Persistence;
using TripsAgent.Contracts.Analytics;
using TripsAgent.Domain.Analytics;

namespace TripsAgent.Application.Analytics;

/// <summary>
/// An agency's own sales, revenue and margin, read from the aggregates rather than from orders.
/// </summary>
/// <remarks>
/// <para>
/// Every query here reads <c>analytics.agg_agency_daily</c> and <c>analytics.fact_bookings</c>, both
/// of which the tenant filter scopes to the caller's own agency without this class doing anything —
/// which is the point. There is no <c>IPlatformScope</c> in this file and there must never be one:
/// an agent looking at their own dashboard is not a cross-tenant read.
/// </para>
/// <para>
/// <b>Margin is a permission, not a column.</b> Net cost, markup and margin are shown only to a
/// caller holding <c>margin.view</c>, and when they are withheld the properties are null and the
/// serialiser drops them. An agent who may not see the margin gets a response with no margin field
/// in it at all, rather than a zero they could mistake for a fact.
/// </para>
/// </remarks>
public sealed class AgencyAnalyticsService
{
    /// <summary>The most bookings a drill-down page returns.</summary>
    public const int MaximumPageSize = 200;

    private readonly IAppDbContext _db;

    public AgencyAnalyticsService(IAppDbContext db) => _db = db;

    /// <summary>Totals, the daily series and two breakdowns, for one window.</summary>
    /// <param name="showMargin">True when the caller holds <c>margin.view</c>.</param>
    public async Task<AgencyAnalyticsResponse> SummaryAsync(
        DateOnly from,
        DateOnly to,
        bool showMargin,
        CancellationToken cancellationToken = default)
    {
        var days = await _db.AgencyDailyAggregates.AsNoTracking()
            .Where(row => row.Day >= from && row.Day <= to)
            .OrderBy(row => row.Day)
            .ToListAsync(cancellationToken);

        // The window immediately before this one, of the same length, so "up 12%" means something.
        var length = ReportScopeRules.DaysCovered(from, to);
        var previousTo = from.AddDays(-1);
        var previousFrom = previousTo.AddDays(-(length - 1));

        var previousGross = await _db.AgencyDailyAggregates.AsNoTracking()
            .Where(row => row.Day >= previousFrom && row.Day <= previousTo)
            .SumAsync(row => row.GrossSalesMinor.AmountMinor, cancellationToken);

        var currency = days.FirstOrDefault()?.Currency
            ?? await _db.Agencies.AsNoTracking()
                .Select(agency => agency.BaseCurrency)
                .FirstOrDefaultAsync(cancellationToken)
            ?? "NGN";

        var totals = new AnalyticsTotalsResponse(
            currency,
            days.Sum(row => row.OrdersCount),
            days.Sum(row => row.BookingsCount),
            days.Sum(row => row.GrossSalesMinor.AmountMinor),
            days.Sum(row => row.RefundedCount),
            days.Sum(row => row.RefundedGrossMinor.AmountMinor),
            days.Sum(row => row.CancelledCount),
            days.Sum(row => row.FailedCount),
            previousGross,
            AnalyticsChange.BasisPoints(days.Sum(row => row.GrossSalesMinor.AmountMinor), previousGross),
            showMargin ? days.Sum(row => row.NetCostMinor.AmountMinor) : null,
            showMargin ? days.Sum(row => row.MarkupMinor.AmountMinor) : null,
            showMargin ? days.Sum(row => row.MarginMinor.AmountMinor) : null);

        var series = days
            .Select(row => new AgencyDayResponse(
                row.Day,
                row.Currency,
                row.OrdersCount,
                row.BookingsCount,
                row.GrossSalesMinor.AmountMinor,
                row.RefundedCount,
                row.RefundedGrossMinor.AmountMinor,
                row.CancelledCount,
                row.FailedCount,
                showMargin ? row.NetCostMinor.AmountMinor : null,
                showMargin ? row.MarkupMinor.AmountMinor : null,
                showMargin ? row.MarginMinor.AmountMinor : null))
            .ToList();

        // The breakdowns come off the facts, because the aggregates do not carry product type or
        // channel — deliberately: a daily table with every dimension in it is a fact table again.
        var breakdownSource = await _db.BookingFacts.AsNoTracking()
            .Where(fact => fact.BookingDay >= from && fact.BookingDay <= to && fact.IsSale)
            .Select(fact => new
            {
                fact.ItemType,
                fact.Channel,
                Gross = fact.GrossAmountMinor,
                Markup = fact.MarkupAmountMinor,
                Fee = fact.PlatformFeeMinor,
            })
            .ToListAsync(cancellationToken);

        var byProduct = breakdownSource
            .GroupBy(fact => fact.ItemType)
            .Select(group => new AnalyticsBreakdownResponse(
                group.Key.ToString(),
                group.Count(),
                group.Aggregate(0L, (total, fact) => total + fact.Gross.AmountMinor),
                showMargin
                    ? group.Aggregate(0L, (total, fact) => total + fact.Markup.AmountMinor - fact.Fee.AmountMinor)
                    : null))
            .OrderByDescending(row => row.GrossSalesMinor)
            .ToList();

        var byChannel = breakdownSource
            .GroupBy(fact => fact.Channel)
            .Select(group => new AnalyticsBreakdownResponse(
                group.Key.ToString(),
                group.Count(),
                group.Aggregate(0L, (total, fact) => total + fact.Gross.AmountMinor),
                showMargin
                    ? group.Aggregate(0L, (total, fact) => total + fact.Markup.AmountMinor - fact.Fee.AmountMinor)
                    : null))
            .OrderByDescending(row => row.GrossSalesMinor)
            .ToList();

        return new AgencyAnalyticsResponse(
            from,
            to,
            days.Count == 0 ? null : days.Max(row => row.BuiltAt),
            showMargin,
            totals,
            series,
            byProduct,
            byChannel);
    }

    /// <summary>
    /// The bookings behind an aggregate.
    /// </summary>
    /// <remarks>
    /// Driven from <c>fact_bookings</c> so the rows add up to exactly the number that was clicked
    /// on; the order number and the title are joined from <c>orders</c>, because a read model is
    /// for counting and a drill-down is for reading. One page at a time, and a page is bounded, so
    /// this is not the live OLTP query the dashboards are forbidden from making.
    /// </remarks>
    public async Task<BookingDrillDownResponse> BookingsAsync(
        DateOnly from,
        DateOnly to,
        bool showMargin,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, MaximumPageSize);

        var facts = _db.BookingFacts.AsNoTracking()
            .Where(fact => fact.BookingDay >= from && fact.BookingDay <= to);

        var total = await facts.CountAsync(cancellationToken);

        var rows = await facts
            .OrderByDescending(fact => fact.OccurredAt)
            .ThenBy(fact => fact.OrderLineId)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Join(
                _db.Orders.AsNoTracking(),
                fact => fact.OrderId,
                order => order.Id,
                (fact, order) => new { fact, order.OrderNumber })
            .Join(
                _db.OrderLines.AsNoTracking(),
                pair => pair.fact.OrderLineId,
                line => line.Id,
                (pair, line) => new BookingRowResponse(
                    pair.fact.OrderId,
                    pair.fact.OrderLineId,
                    pair.OrderNumber,
                    pair.fact.BookingDay,
                    pair.fact.OccurredAt,
                    pair.fact.ItemType.ToString(),
                    pair.fact.Channel.ToString(),
                    line.TitleSnapshot,
                    pair.fact.OrderStatus.ToString(),
                    pair.fact.FulfilmentStatus.ToString(),
                    pair.fact.Currency,
                    pair.fact.GrossAmountMinor.AmountMinor,
                    showMargin ? pair.fact.NetAmountMinor.AmountMinor : null,
                    showMargin
                        ? pair.fact.MarkupAmountMinor.AmountMinor - pair.fact.PlatformFeeMinor.AmountMinor
                        : null))
            .ToListAsync(cancellationToken);

        return new BookingDrillDownResponse(from, to, showMargin, total, page, pageSize, rows);
    }
}

/// <summary>Period-over-period change, in basis points, without a floating-point division.</summary>
/// <remarks>
/// Basis points because a dashboard wants "+12.5%" and a <c>double</c> is the wrong way to get
/// there when the inputs are money: the numerator and denominator are both exact integers of minor
/// units, so the ratio should be computed exactly and formatted once, at the edge.
/// </remarks>
public static class AnalyticsChange
{
    /// <summary>
    /// How <paramref name="current"/> moved against <paramref name="previous"/>, in basis points.
    /// </summary>
    /// <returns>
    /// Null when the previous window was zero. There is no percentage change from nothing, and
    /// returning a very large number instead is how a dashboard ends up claiming infinite growth.
    /// </returns>
    public static int? BasisPoints(long current, long previous)
    {
        if (previous == 0)
        {
            return null;
        }

        var change = (current - previous) * 10_000L / previous;

        // Clamped so a tiny denominator cannot overflow the int the contract carries. A change of
        // more than a thousandfold is not a number anybody reads off a chart anyway.
        return (int)Math.Clamp(change, int.MinValue, int.MaxValue);
    }
}
