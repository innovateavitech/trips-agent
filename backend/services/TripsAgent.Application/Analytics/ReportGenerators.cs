using Microsoft.EntityFrameworkCore;
using TripsAgent.Application.Persistence;
using TripsAgent.Domain.Analytics;

namespace TripsAgent.Application.Analytics;

/// <summary>The bytes of a finished report, and how many rows are in them.</summary>
/// <param name="Content">The file. UTF-8 CSV with a byte-order mark.</param>
/// <param name="RowCount">Data rows, not counting the header. What the export audit records.</param>
public sealed record ReportContent(byte[] Content, int RowCount)
{
    /// <summary>The media type the file is served with.</summary>
    public const string ContentType = "text/csv; charset=utf-8";
}

/// <summary>
/// Turns a report definition and a window into a file.
/// </summary>
/// <remarks>
/// <para>
/// Every generator reads the analytics read models rather than <c>orders</c> — the same rule the
/// dashboards follow, for the same reason. A report over a year of a busy platform is exactly the
/// query nobody wants running against the tables a booking is being written to.
/// </para>
/// <para>
/// Tenancy is decided before this class is reached, not inside it. An agency report runs with that
/// agency as the tenant, so the ordinary query filter scopes it; a platform report runs inside
/// <c>IPlatformScope</c>. Nothing here calls <c>IgnoreQueryFilters</c>, and nothing here takes an
/// agency id to filter by — which is deliberate, because a generator that filtered by hand would be
/// one forgotten <c>WHERE</c> away from being a leak.
/// </para>
/// </remarks>
public sealed class ReportGenerator
{
    private readonly IAppDbContext _db;

    public ReportGenerator(IAppDbContext db) => _db = db;

    /// <summary>Produces the report named by <paramref name="definitionCode"/>.</summary>
    /// <exception cref="ArgumentException">The code is not one this build can produce.</exception>
    public Task<ReportContent> GenerateAsync(
        string definitionCode,
        DateOnly from,
        DateOnly to,
        bool showMargin,
        CancellationToken cancellationToken = default) =>
        definitionCode switch
        {
            ReportCatalog.AgencySalesDaily => AgencySalesAsync(from, to, showMargin, cancellationToken),
            ReportCatalog.AgencyBookings => AgencyBookingsAsync(from, to, showMargin, cancellationToken),
            ReportCatalog.PlatformGmvDaily => PlatformGmvAsync(from, to, cancellationToken),
            ReportCatalog.PlatformSupplierPerformance => SupplierPerformanceAsync(from, to, cancellationToken),
            ReportCatalog.PlatformAgencySales => PlatformAgencySalesAsync(from, to, cancellationToken),
            _ => throw new ArgumentException(
                $"There is no generator for report '{definitionCode}'.", nameof(definitionCode)),
        };

    private async Task<ReportContent> AgencySalesAsync(
        DateOnly from,
        DateOnly to,
        bool showMargin,
        CancellationToken cancellationToken)
    {
        var rows = await _db.AgencyDailyAggregates.AsNoTracking()
            .Where(row => row.Day >= from && row.Day <= to)
            .OrderBy(row => row.Day)
            .ToListAsync(cancellationToken);

        var csv = new CsvWriter();
        var currency = rows.FirstOrDefault()?.Currency ?? "NGN";

        // The currency is named in the header once, so the amount columns stay parseable numbers.
        string[] header =
        [
            "Day", "Orders", "Bookings", $"Gross sales ({currency})", "Refunds",
            $"Refunded ({currency})", "Cancellations", "Failures",
        ];

        csv.WriteHeader(showMargin
            ? [.. header, $"Cost ({currency})", $"Markup ({currency})", $"Margin ({currency})"]
            : header);

        foreach (var row in rows)
        {
            string[] fields =
            [
                CsvWriter.Date(row.Day),
                CsvWriter.Number(row.OrdersCount),
                CsvWriter.Number(row.BookingsCount),
                CsvWriter.Money(row.GrossSalesMinor.AmountMinor),
                CsvWriter.Number(row.RefundedCount),
                CsvWriter.Money(row.RefundedGrossMinor.AmountMinor),
                CsvWriter.Number(row.CancelledCount),
                CsvWriter.Number(row.FailedCount),
            ];

            csv.WriteRow(showMargin
                ? [
                    .. fields,
                    CsvWriter.Money(row.NetCostMinor.AmountMinor),
                    CsvWriter.Money(row.MarkupMinor.AmountMinor),
                    CsvWriter.Money(row.MarginMinor.AmountMinor),
                ]
                : fields);
        }

        return new ReportContent(csv.ToBytes(), csv.RowCount);
    }

    private async Task<ReportContent> AgencyBookingsAsync(
        DateOnly from,
        DateOnly to,
        bool showMargin,
        CancellationToken cancellationToken)
    {
        var rows = await _db.BookingFacts.AsNoTracking()
            .Where(fact => fact.BookingDay >= from && fact.BookingDay <= to)
            .OrderBy(fact => fact.OccurredAt)
            .ThenBy(fact => fact.OrderLineId)
            .Join(
                _db.Orders.AsNoTracking(),
                fact => fact.OrderId,
                order => order.Id,
                (fact, order) => new { fact, order.OrderNumber })
            .Join(
                _db.OrderLines.AsNoTracking(),
                pair => pair.fact.OrderLineId,
                line => line.Id,
                (pair, line) => new { pair.fact, pair.OrderNumber, line.TitleSnapshot })
            .ToListAsync(cancellationToken);

        var csv = new CsvWriter();
        var currency = rows.FirstOrDefault()?.fact.Currency ?? "NGN";

        string[] header =
        [
            "Day", "Placed at (UTC)", "Order number", "Item", "Type", "Channel",
            "Order status", "Fulfilment", $"Gross ({currency})",
        ];

        csv.WriteHeader(showMargin
            ? [.. header, $"Cost ({currency})", $"Margin ({currency})"]
            : header);

        foreach (var row in rows)
        {
            string[] fields =
            [
                CsvWriter.Date(row.fact.BookingDay),
                CsvWriter.Instant(row.fact.OccurredAt),
                row.OrderNumber,
                row.TitleSnapshot,
                row.fact.ItemType.ToString(),
                row.fact.Channel.ToString(),
                row.fact.OrderStatus.ToString(),
                row.fact.FulfilmentStatus.ToString(),
                CsvWriter.Money(row.fact.GrossAmountMinor.AmountMinor),
            ];

            csv.WriteRow(showMargin
                ? [
                    .. fields,
                    CsvWriter.Money(row.fact.NetAmountMinor.AmountMinor),
                    CsvWriter.Money(row.fact.AgentMarginMinor.AmountMinor),
                ]
                : fields);
        }

        return new ReportContent(csv.ToBytes(), csv.RowCount);
    }

    private async Task<ReportContent> PlatformGmvAsync(
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken)
    {
        var rows = await _db.PlatformDailyAggregates.AsNoTracking()
            .Where(row => row.Day >= from && row.Day <= to)
            .OrderBy(row => row.Day)
            .ToListAsync(cancellationToken);

        var csv = new CsvWriter();
        var currency = rows.FirstOrDefault()?.Currency ?? "NGN";

        csv.WriteHeader(
            "Day",
            "Selling agencies",
            "New agencies",
            "Orders",
            "Bookings",
            $"GMV ({currency})",
            $"Supplier cost ({currency})",
            $"Agent markup ({currency})",
            $"Trips fee revenue ({currency})",
            "Refunds",
            $"Refunded ({currency})",
            "Failures");

        foreach (var row in rows)
        {
            csv.WriteRow(
                CsvWriter.Date(row.Day),
                CsvWriter.Number(row.SellingAgenciesCount),
                CsvWriter.Number(row.NewAgenciesCount),
                CsvWriter.Number(row.OrdersCount),
                CsvWriter.Number(row.BookingsCount),
                CsvWriter.Money(row.GmvMinor.AmountMinor),
                CsvWriter.Money(row.NetCostMinor.AmountMinor),
                CsvWriter.Money(row.MarkupMinor.AmountMinor),
                CsvWriter.Money(row.PlatformFeeMinor.AmountMinor),
                CsvWriter.Number(row.RefundedCount),
                CsvWriter.Money(row.RefundedGrossMinor.AmountMinor),
                CsvWriter.Number(row.FailedCount));
        }

        return new ReportContent(csv.ToBytes(), csv.RowCount);
    }

    private async Task<ReportContent> SupplierPerformanceAsync(
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken)
    {
        var rows = await _db.SupplierDailyAggregates.AsNoTracking()
            .Where(row => row.Day >= from && row.Day <= to)
            .OrderBy(row => row.Day)
            .ToListAsync(cancellationToken);

        var supplierIds = rows.Select(row => row.SupplierId).Distinct().ToList();

        var names = await _db.Suppliers.AsNoTracking()
            .Where(supplier => supplierIds.Contains(supplier.Id))
            .ToDictionaryAsync(supplier => supplier.Id, supplier => supplier.Name, cancellationToken);

        var csv = new CsvWriter();

        csv.WriteHeader(
            "Day",
            "Supplier",
            "Searches",
            "Price confirmations",
            "Issue attempts",
            "Tickets issued",
            "Status polls",
            "Total calls",
            "Errors",
            "Timeouts",
            "Search-to-book",
            "Error rate",
            "Average latency (ms)",
            "Max latency (ms)");

        foreach (var row in rows)
        {
            csv.WriteRow(
                CsvWriter.Date(row.Day),
                names.TryGetValue(row.SupplierId, out var name) ? name : "Unknown supplier",
                CsvWriter.Number(row.SearchCount),
                CsvWriter.Number(row.ConfirmPriceCount),
                CsvWriter.Number(row.IssueCount),
                CsvWriter.Number(row.BookedCount),
                CsvWriter.Number(row.StatusCount),
                CsvWriter.Number(row.TotalCalls),
                CsvWriter.Number(row.ErrorCount),
                CsvWriter.Number(row.TimeoutCount),
                CsvWriter.Percentage(Ratio(row.BookedCount, row.SearchCount)),
                CsvWriter.Percentage(Ratio(row.ErrorCount, row.TotalCalls)),
                CsvWriter.Number(row.AverageLatencyMs),
                CsvWriter.Number(row.MaxLatencyMs));
        }

        return new ReportContent(csv.ToBytes(), csv.RowCount);
    }

    private async Task<ReportContent> PlatformAgencySalesAsync(
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken)
    {
        var rows = await _db.AgencyDailyAggregates.AsNoTracking()
            .Where(row => row.Day >= from && row.Day <= to)
            .ToListAsync(cancellationToken);

        var agencyIds = rows.Select(row => row.AgencyId).Distinct().ToList();

        var names = await _db.Agencies.AsNoTracking()
            .Where(agency => agencyIds.Contains(agency.Id))
            .ToDictionaryAsync(agency => agency.Id, agency => agency.LegalName, cancellationToken);

        var csv = new CsvWriter();
        var currency = rows.FirstOrDefault()?.Currency ?? "NGN";

        csv.WriteHeader(
            "Agency",
            "Currency",
            "Orders",
            "Bookings",
            $"Gross sales ({currency})",
            $"Supplier cost ({currency})",
            $"Agent markup ({currency})",
            $"Trips fee revenue ({currency})",
            "Refunds",
            "Failures");

        var byAgency = rows
            .GroupBy(row => new { row.AgencyId, row.Currency })
            .OrderByDescending(group => group.Sum(row => row.GrossSalesMinor.AmountMinor));

        foreach (var group in byAgency)
        {
            csv.WriteRow(
                names.TryGetValue(group.Key.AgencyId, out var name) ? name : group.Key.AgencyId.ToString(),
                group.Key.Currency,
                CsvWriter.Number(group.Sum(row => row.OrdersCount)),
                CsvWriter.Number(group.Sum(row => row.BookingsCount)),
                CsvWriter.Money(group.Sum(row => row.GrossSalesMinor.AmountMinor)),
                CsvWriter.Money(group.Sum(row => row.NetCostMinor.AmountMinor)),
                CsvWriter.Money(group.Sum(row => row.MarkupMinor.AmountMinor)),
                CsvWriter.Money(group.Sum(row => row.PlatformFeeMinor.AmountMinor)),
                CsvWriter.Number(group.Sum(row => row.RefundedCount)),
                CsvWriter.Number(group.Sum(row => row.FailedCount)));
        }

        return new ReportContent(csv.ToBytes(), csv.RowCount);
    }

    /// <summary>A ratio in basis points, or null when there was no denominator.</summary>
    private static int? Ratio(int numerator, int denominator) =>
        denominator == 0 ? null : (int)((long)numerator * 10_000L / denominator);
}
