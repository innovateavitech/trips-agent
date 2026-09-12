using Microsoft.EntityFrameworkCore;
using TripsAgent.Application.Persistence;
using TripsAgent.Application.Tenancy;
using TripsAgent.Contracts.Analytics;
using TripsAgent.Domain.Analytics;

namespace TripsAgent.Application.Analytics;

/// <summary>
/// The platform's own numbers: GMV, growth, and how the suppliers are behaving.
/// </summary>
/// <remarks>
/// <para>
/// Every read here crosses agencies, which is what a platform dashboard is, so every method opens
/// <see cref="IPlatformScope"/> with a reason and the entry is logged with the acting user
/// (CLAUDE.md rule 3). Nothing in this file calls <c>IgnoreQueryFilters</c>, and the aggregates it
/// reads are refused outright by row-level security to any session without a scope open, so a
/// missing scope fails as an empty result rather than as a leak.
/// </para>
/// <para>
/// The endpoints above require <c>platform.report.view</c>.
/// </para>
/// </remarks>
public sealed class PlatformAnalyticsService
{
    private readonly IAppDbContext _db;
    private readonly IPlatformScope _platformScope;

    public PlatformAnalyticsService(IAppDbContext db, IPlatformScope platformScope)
    {
        _db = db;
        _platformScope = platformScope;
    }

    /// <summary>GMV, fee revenue and growth over a window, with the daily series.</summary>
    public async Task<PlatformAnalyticsResponse> SummaryAsync(
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken = default)
    {
        using var scope = _platformScope.Enter(
            "Platform analytics — GMV and growth across every agency");

        var days = await _db.PlatformDailyAggregates.AsNoTracking()
            .Where(row => row.Day >= from && row.Day <= to)
            .OrderBy(row => row.Day)
            .ToListAsync(cancellationToken);

        var length = ReportScopeRules.DaysCovered(from, to);
        var previousTo = from.AddDays(-1);
        var previousFrom = previousTo.AddDays(-(length - 1));

        var previousGmv = await _db.PlatformDailyAggregates.AsNoTracking()
            .Where(row => row.Day >= previousFrom && row.Day <= previousTo)
            .SumAsync(row => row.GmvMinor.AmountMinor, cancellationToken);

        var gmv = days.Sum(row => row.GmvMinor.AmountMinor);

        // How many agencies traded at all in the window — not the sum of the daily counts, which
        // would count an agency once for every day it sold something.
        var activeAgencies = await _db.BookingFacts.AsNoTracking()
            .Where(fact => fact.BookingDay >= from && fact.BookingDay <= to && fact.IsSale)
            .Select(fact => fact.AgencyId)
            .Distinct()
            .CountAsync(cancellationToken);

        return new PlatformAnalyticsResponse(
            from,
            to,
            days.Count == 0 ? null : days.Max(row => row.BuiltAt),
            days.FirstOrDefault()?.Currency ?? "NGN",
            gmv,
            previousGmv,
            AnalyticsChange.BasisPoints(gmv, previousGmv),
            days.Sum(row => row.PlatformFeeMinor.AmountMinor),
            days.Sum(row => row.MarkupMinor.AmountMinor),
            days.Sum(row => row.OrdersCount),
            days.Sum(row => row.BookingsCount),
            days.Sum(row => row.NewAgenciesCount),
            activeAgencies,
            days.Sum(row => row.RefundedCount),
            days.Sum(row => row.RefundedGrossMinor.AmountMinor),
            days.Sum(row => row.FailedCount),
            days.Select(row => new PlatformDayResponse(
                    row.Day,
                    row.Currency,
                    row.SellingAgenciesCount,
                    row.NewAgenciesCount,
                    row.OrdersCount,
                    row.BookingsCount,
                    row.GmvMinor.AmountMinor,
                    row.PlatformFeeMinor.AmountMinor,
                    row.MarkupMinor.AmountMinor,
                    row.RefundedCount,
                    row.RefundedGrossMinor.AmountMinor,
                    row.FailedCount))
                .ToList());
    }

    /// <summary>Search-to-book conversion, error rate and latency, per supplier.</summary>
    public async Task<SupplierPerformanceResponse> SuppliersAsync(
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken = default)
    {
        using var scope = _platformScope.Enter(
            "Supplier performance — conversion and error rates across every agency's calls");

        var days = await _db.SupplierDailyAggregates.AsNoTracking()
            .Where(row => row.Day >= from && row.Day <= to)
            .OrderBy(row => row.Day)
            .ToListAsync(cancellationToken);

        var supplierIds = days.Select(row => row.SupplierId).Distinct().ToList();

        var names = await _db.Suppliers.AsNoTracking()
            .Where(supplier => supplierIds.Contains(supplier.Id))
            .Select(supplier => new { supplier.Id, supplier.Code, supplier.Name })
            .ToDictionaryAsync(entry => entry.Id, entry => entry, cancellationToken);

        var suppliers = days
            .GroupBy(row => row.SupplierId)
            .Select(group =>
            {
                var searches = group.Sum(row => row.SearchCount);
                var booked = group.Sum(row => row.BookedCount);
                var calls = group.Sum(row => row.TotalCalls);
                var errors = group.Sum(row => row.ErrorCount);

                // A weighted mean: the average of daily averages would give a day with three calls
                // the same say as a day with three thousand.
                var latencyWeight = group.Sum(row => (long)row.AverageLatencyMs * row.TotalCalls);

                return new SupplierPerformanceRowResponse(
                    group.Key,
                    names.TryGetValue(group.Key, out var named) ? named.Code : "unknown",
                    named?.Name ?? "Unknown supplier",
                    searches,
                    group.Sum(row => row.ConfirmPriceCount),
                    group.Sum(row => row.IssueCount),
                    booked,
                    group.Sum(row => row.StatusCount),
                    calls,
                    errors,
                    group.Sum(row => row.TimeoutCount),
                    Ratio(booked, searches),
                    Ratio(errors, calls),
                    calls == 0 ? 0 : (int)(latencyWeight / calls),
                    group.Max(row => row.MaxLatencyMs));
            })
            .OrderByDescending(row => row.TotalCalls)
            .ToList();

        return new SupplierPerformanceResponse(
            from,
            to,
            days.Count == 0 ? null : days.Max(row => row.BuiltAt),
            suppliers,
            days.Select(row => new SupplierDayResponse(
                    row.Day,
                    row.SupplierId,
                    row.SearchCount,
                    row.BookedCount,
                    row.TotalCalls,
                    row.ErrorCount,
                    row.AverageLatencyMs))
                .ToList());
    }

    /// <summary>
    /// A ratio in basis points, or null when the denominator is zero.
    /// </summary>
    /// <remarks>
    /// Null rather than zero. "No searches, so no conversion" and "many searches, none converted"
    /// are opposite facts about a supplier, and a dashboard that shows both as 0% is telling the
    /// reader the wrong one half the time.
    /// </remarks>
    private static int? Ratio(int numerator, int denominator) =>
        denominator == 0 ? null : (int)((long)numerator * 10_000L / denominator);
}
