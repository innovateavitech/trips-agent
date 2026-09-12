using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TripsAgent.Application.Persistence;
using TripsAgent.Application.Tenancy;
using TripsAgent.Domain.Analytics;
using TripsAgent.Domain.Common;
using TripsAgent.Domain.Orders;
using TripsAgent.Domain.Suppliers;

namespace TripsAgent.Application.Analytics;

/// <summary>
/// Rebuilds <c>analytics.fact_bookings</c> and the three <c>agg_*</c> tables from the tables they
/// are derived from.
/// </summary>
/// <remarks>
/// <para>
/// <b>The shape of it.</b> Everything is a rebuild of whole Lagos days. A day is loaded from
/// source, the existing rows for that day are deleted, and new ones are inserted — in one save, so
/// a day is never half-built. Nothing is incremented and nothing is merged, which is why running
/// the rollup twice, or running an incremental run and then a full rebuild, leaves exactly the
/// same numbers. That is issue 67's last criterion, and it is a property of this design rather
/// than something the tests have to keep catching.
/// </para>
/// <para>
/// <b>Why a day at a time.</b> One save per day means one transaction per day. A run that dies
/// half way through leaves the days it finished correct and the days it did not touch untouched,
/// and because a failed run never becomes the watermark, the next run does the rest. Batching every
/// day into one transaction would buy nothing and would hold a write lock on the fact table for the
/// length of a nightly rebuild.
/// </para>
/// <para>
/// <b>Why a platform scope.</b> The rollup reads and writes every agency's rows: that is what a
/// platform aggregate is. It goes through <see cref="IPlatformScope"/> with a reason, like every
/// other cross-tenant read in the codebase, and never <c>IgnoreQueryFilters</c> (CLAUDE.md rule 3,
/// analyser TRIPS002). Row-level security enforces the same rule underneath.
/// </para>
/// <para>
/// <b>Money.</b> Every figure is summed as a <see cref="Money"/> over <c>long</c> minor units. A
/// day's takings are added up in kobo and stored in kobo, so the sum of the days equals the sum of
/// the rows exactly. Nothing here divides, averages or rounds money.
/// </para>
/// </remarks>
public sealed partial class AnalyticsRollup : IAnalyticsRollup
{
    /// <summary>
    /// How far back the nightly rebuild reaches.
    /// </summary>
    /// <remarks>
    /// Fourteen months, so a year-on-year comparison always has both ends. Older days are still
    /// correct — they were built when they were current and nothing rewrites a placed order's money
    /// (CLAUDE.md rule 5) — they are simply not checked again every night for ever.
    /// </remarks>
    public const int FullRebuildWindowDays = 425;

    /// <summary>
    /// The earliest a first run will reach back if there is no successful run to carry on from.
    /// </summary>
    /// <remarks>
    /// A first incremental run on a database with history would otherwise have no watermark and
    /// rebuild nothing at all. It falls back to the same window as the nightly rebuild, so the very
    /// first run after a deployment produces a complete picture rather than an empty one.
    /// </remarks>
    public const int FirstRunWindowDays = FullRebuildWindowDays;

    private readonly IAppDbContext _db;
    private readonly IPlatformScope _platformScope;
    private readonly TimeProvider _clock;
    private readonly ILogger<AnalyticsRollup> _logger;

    public AnalyticsRollup(
        IAppDbContext db,
        IPlatformScope platformScope,
        TimeProvider clock,
        ILogger<AnalyticsRollup> logger)
    {
        _db = db;
        _platformScope = platformScope;
        _clock = clock;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<RollupResult> RunIncrementalAsync(CancellationToken cancellationToken = default)
    {
        using var scope = _platformScope.Enter(
            "Analytics rollup — reads every agency's orders to build the platform read models");

        var now = _clock.GetUtcNow();

        // Where the last successful run stopped, less an overlap. A row written inside a
        // transaction that committed after the previous run read the table carries a timestamp
        // from before that read; without the overlap it would be invisible until the nightly
        // rebuild. Rebuilding a day twice produces the same numbers, so the overlap costs nothing.
        var lastWatermark = await _db.RollupRuns.AsNoTracking()
            .Where(run => run.Status == RollupRunStatus.Succeeded)
            .OrderByDescending(run => run.WatermarkTo)
            .Select(run => (DateTimeOffset?)run.WatermarkTo)
            .FirstOrDefaultAsync(cancellationToken);

        var from = lastWatermark is null
            ? now.AddDays(-FirstRunWindowDays)
            : lastWatermark.Value - RollupRun.WatermarkOverlap;

        var days = await ChangedDaysAsync(from, now, cancellationToken);

        return await RunAsync(RollupKind.Incremental, from, now, days, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<RollupResult> RunFullRebuildAsync(CancellationToken cancellationToken = default)
    {
        var now = _clock.GetUtcNow();
        var today = LagosDay.Of(now);

        return await RebuildRangeAsync(
            today.AddDays(-FullRebuildWindowDays),
            today,
            RollupKind.FullRebuild,
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task<RollupResult> RebuildRangeAsync(
        DateOnly fromDay,
        DateOnly toDay,
        RollupKind kind,
        CancellationToken cancellationToken = default)
    {
        using var scope = _platformScope.Enter(
            "Analytics rollup — rebuilds the platform read models from every agency's orders");

        var now = _clock.GetUtcNow();
        var days = LagosDay.Range(fromDay, toDay).ToHashSet();

        // The whole range is the window, so a full rebuild answers the same question an incremental
        // run answers with a watermark: "everything that could possibly have changed".
        return await RunAsync(kind, LagosDay.StartOfUtc(fromDay), now, days, cancellationToken);
    }

    /// <summary>
    /// Opens a run row, rebuilds the days, and closes it either way.
    /// </summary>
    /// <remarks>
    /// The run row is saved before the work starts, so a process that is killed mid-rebuild leaves
    /// a <c>Running</c> row somebody can see rather than no trace at all. Only a
    /// <c>Succeeded</c> row is ever read as a watermark, so an abandoned run never causes the next
    /// one to skip what it did not finish.
    /// </remarks>
    private async Task<RollupResult> RunAsync(
        RollupKind kind,
        DateTimeOffset watermarkFrom,
        DateTimeOffset watermarkTo,
        HashSet<DateOnly> days,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        var run = RollupRun.Start(kind, watermarkFrom, watermarkTo, _clock.GetUtcNow());
        _db.RollupRuns.Add(run);
        await _db.SaveChangesAsync(cancellationToken);

        try
        {
            var factRows = 0;

            foreach (var day in days.OrderBy(day => day))
            {
                factRows += await RebuildDayAsync(day, cancellationToken);
            }

            stopwatch.Stop();

            var fromDay = days.Count == 0 ? (DateOnly?)null : days.Min();
            var toDay = days.Count == 0 ? (DateOnly?)null : days.Max();

            run.Succeeded(fromDay, toDay, days.Count, factRows, _clock.GetUtcNow());
            await _db.SaveChangesAsync(cancellationToken);

            LogCompleted(_logger, kind, days.Count, factRows, stopwatch.ElapsedMilliseconds);

            return new RollupResult(kind, days.Count, factRows, fromDay, toDay, stopwatch.Elapsed);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            stopwatch.Stop();

            // The context may be holding changes the failed save rejected. Clear them, or marking
            // the run failed would send the same rejected rows again and fail the same way — and
            // then nothing at all would record that the run went wrong.
            _db.ChangeTracker.Clear();

            var failed = await _db.RollupRuns.FirstOrDefaultAsync(
                candidate => candidate.Id == run.Id,
                cancellationToken);

            if (failed is not null)
            {
                failed.Failed(exception.Message, _clock.GetUtcNow());
                await _db.SaveChangesAsync(cancellationToken);
            }

            LogFailed(_logger, kind, exception);
            throw;
        }
    }

    /// <summary>
    /// The Lagos days touched by anything that changed in the window.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three sources, and all three matter:
    /// </para>
    /// <list type="bullet">
    ///   <item>
    ///     the day a changed order line now falls on — a new booking, or a status that moved;
    ///   </item>
    ///   <item>
    ///     the day its <i>existing</i> fact row falls on. An order created on Monday and paid for
    ///     on Tuesday moves from one day to the other, and rebuilding only Tuesday would leave
    ///     Monday still counting it. Without this, a single order can be counted twice.
    ///   </item>
    ///   <item>
    ///     the day a supplier call happened, since the supplier aggregate is built from the call
    ///     log rather than from orders and moves on its own.
    ///   </item>
    /// </list>
    /// </remarks>
    /// <remarks>
    /// The window is inclusive at both ends. A row written in the same instant the run started
    /// would fall outside an exclusive upper bound and wait for the next run to find it; including
    /// it costs one day rebuilt twice, which produces the same numbers, and excluding it costs a
    /// number that is wrong for five minutes. The overlap on the lower bound is the same trade
    /// made deliberately.
    /// </remarks>
    private async Task<HashSet<DateOnly>> ChangedDaysAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken)
    {
        var changedLines = await _db.OrderLines.AsNoTracking()
            .Where(line => line.UpdatedAt >= from && line.UpdatedAt <= to)
            .Select(line => new { line.Id, line.OrderId })
            .ToListAsync(cancellationToken);

        var changedOrderIds = await _db.Orders.AsNoTracking()
            .Where(order => order.UpdatedAt >= from && order.UpdatedAt <= to)
            .Select(order => order.Id)
            .ToListAsync(cancellationToken);

        var orderIds = changedLines.Select(line => line.OrderId)
            .Concat(changedOrderIds)
            .Distinct()
            .ToList();

        var days = new HashSet<DateOnly>();

        // Where those orders sit now.
        var occurredAt = await _db.Orders.AsNoTracking()
            .Where(order => orderIds.Contains(order.Id))
            .Select(order => order.PlacedAt ?? order.CreatedAt)
            .ToListAsync(cancellationToken);

        foreach (var instant in occurredAt)
        {
            days.Add(LagosDay.Of(instant));
        }

        // Where they were last time the rollup looked.
        var previousDays = await _db.BookingFacts.AsNoTracking()
            .Where(fact => orderIds.Contains(fact.OrderId))
            .Select(fact => fact.BookingDay)
            .Distinct()
            .ToListAsync(cancellationToken);

        foreach (var day in previousDays)
        {
            days.Add(day);
        }

        // And the supplier call log, which moves without any order moving.
        var callDays = await _db.SupplierApiCalls.AsNoTracking()
            .Where(call => call.OccurredAt >= from && call.OccurredAt <= to)
            .Select(call => call.OccurredAt)
            .ToListAsync(cancellationToken);

        foreach (var instant in callDays)
        {
            days.Add(LagosDay.Of(instant));
        }

        return days;
    }

    /// <summary>
    /// Throws one Lagos day away and builds it again from source. One save, so one transaction.
    /// </summary>
    /// <returns>How many fact rows the day now has.</returns>
    private async Task<int> RebuildDayAsync(DateOnly day, CancellationToken cancellationToken)
    {
        var builtAt = _clock.GetUtcNow();
        var start = LagosDay.StartOfUtc(day);
        var end = LagosDay.EndOfUtc(day);

        // ------------------------------------------------------------------ source, as it is now
        var lines = await _db.OrderLines.AsNoTracking()
            .Join(
                _db.Orders.AsNoTracking(),
                line => line.OrderId,
                order => order.Id,
                (line, order) => new { Line = line, Order = order })
            .Where(pair => (pair.Order.PlacedAt ?? pair.Order.CreatedAt) >= start
                && (pair.Order.PlacedAt ?? pair.Order.CreatedAt) < end)
            .ToListAsync(cancellationToken);

        // The supplier behind each line, where there is one. Read separately rather than joined so
        // a line with no supplier booking — a tour, a visa — is not dropped by an inner join.
        var lineIds = lines.Select(pair => pair.Line.Id).ToList();

        var suppliersByLine = await _db.SupplierBookings.AsNoTracking()
            .Where(booking => lineIds.Contains(booking.OrderLineId))
            .Select(booking => new { LineId = booking.OrderLineId, booking.SupplierId })
            .ToDictionaryAsync(entry => entry.LineId, entry => entry.SupplierId, cancellationToken);

        // Who each selling agency reports up to. Until the sub-agent network lands every agency is
        // its own root, which is exactly right for an agency with nobody beneath it.
        var agencyIds = lines.Select(pair => pair.Line.AgencyId).Distinct().ToList();

        var rootByAgency = await _db.Agencies.AsNoTracking()
            .Where(agency => agencyIds.Contains(agency.Id))
            .Select(agency => new { agency.Id, Root = agency.ParentAgencyId ?? agency.Id })
            .ToDictionaryAsync(entry => entry.Id, entry => entry.Root, cancellationToken);

        var facts = lines
            .Select(pair => BookingFact.From(
                pair.Order.Id,
                pair.Line.Id,
                pair.Line.AgencyId,
                rootByAgency.TryGetValue(pair.Line.AgencyId, out var root) ? root : pair.Line.AgencyId,
                pair.Order.PlacedAt ?? pair.Order.CreatedAt,
                pair.Order.Status,
                pair.Line.FulfilmentStatus,
                pair.Line.ItemType,
                pair.Order.Channel,
                pair.Order.BuyerType,
                suppliersByLine.TryGetValue(pair.Line.Id, out var supplierId) ? supplierId : null,
                pair.Line.Currency,
                pair.Line.NetAmountMinor,
                pair.Line.MarkupAmountMinor,
                pair.Line.TaxAmountMinor,
                pair.Line.PlatformFeeMinor,
                pair.Line.GrossAmountMinor,
                pair.Order.UpdatedAt > pair.Line.UpdatedAt ? pair.Order.UpdatedAt : pair.Line.UpdatedAt,
                builtAt))
            .ToList();

        // ----------------------------------------------------------------- throw the day away
        //
        // Two deletes, not one. The first clears the day itself. The second clears any fact row for
        // a line we are about to write that is currently filed under a DIFFERENT day — an order
        // created on Monday and placed on Tuesday moves, and leaving the stale row behind would
        // both double the booking and violate the unique index on order_line_id.
        var stale = await _db.BookingFacts
            .Where(fact => fact.BookingDay == day || lineIds.Contains(fact.OrderLineId))
            .ToListAsync(cancellationToken);

        _db.BookingFacts.RemoveRange(stale);

        var staleAgencyRows = await _db.AgencyDailyAggregates
            .Where(row => row.Day == day)
            .ToListAsync(cancellationToken);

        _db.AgencyDailyAggregates.RemoveRange(staleAgencyRows);

        var stalePlatformRows = await _db.PlatformDailyAggregates
            .Where(row => row.Day == day)
            .ToListAsync(cancellationToken);

        _db.PlatformDailyAggregates.RemoveRange(stalePlatformRows);

        var staleSupplierRows = await _db.SupplierDailyAggregates
            .Where(row => row.Day == day)
            .ToListAsync(cancellationToken);

        _db.SupplierDailyAggregates.RemoveRange(staleSupplierRows);

        // ----------------------------------------------------------------- and build it again
        _db.BookingFacts.AddRange(facts);

        foreach (var row in AgencyRowsFor(day, facts, builtAt))
        {
            _db.AgencyDailyAggregates.Add(row);
        }

        var newAgencies = await _db.Agencies.AsNoTracking()
            .CountAsync(agency => agency.CreatedAt >= start && agency.CreatedAt < end, cancellationToken);

        foreach (var row in PlatformRowsFor(day, facts, newAgencies, builtAt))
        {
            _db.PlatformDailyAggregates.Add(row);
        }

        foreach (var row in await SupplierRowsForAsync(day, start, end, builtAt, cancellationToken))
        {
            _db.SupplierDailyAggregates.Add(row);
        }

        await _db.SaveChangesAsync(cancellationToken);

        return facts.Count;
    }

    /// <summary>One row per agency, day and currency, summed in minor units.</summary>
    private static IEnumerable<AgencyDailyAggregate> AgencyRowsFor(
        DateOnly day,
        IReadOnlyCollection<BookingFact> facts,
        DateTimeOffset builtAt) =>
        facts
            .GroupBy(fact => new { fact.AgencyId, fact.RootAgencyId, fact.Currency })
            .Select(group => AgencyDailyAggregate.For(
                group.Key.AgencyId,
                group.Key.RootAgencyId,
                day,
                group.Key.Currency,
                group.Where(fact => fact.IsSale).Select(fact => fact.OrderId).Distinct().Count(),
                group.Count(fact => fact.IsSale),
                SumOf(group, fact => fact.GrossAmountMinor),
                SumOf(group, fact => fact.NetAmountMinor),
                SumOf(group, fact => fact.MarkupAmountMinor),
                SumOf(group, fact => fact.TaxAmountMinor),
                SumOf(group, fact => fact.PlatformFeeMinor),
                group.Count(fact => fact.IsRefunded),
                SumRefunds(group),
                group.Count(fact => fact.IsCancelled),
                group.Count(fact => fact.IsFailed),
                builtAt));

    /// <summary>One row per day and currency, across every agency.</summary>
    private static IEnumerable<PlatformDailyAggregate> PlatformRowsFor(
        DateOnly day,
        IReadOnlyCollection<BookingFact> facts,
        int newAgencies,
        DateTimeOffset builtAt)
    {
        var currencies = facts.Select(fact => fact.Currency).Distinct().ToList();

        // A day on which nobody sold anything but somebody signed up still has growth to report,
        // and a gap in the series reads as missing data rather than as a quiet day. The row is
        // written in the platform's own currency so the series is continuous.
        if (currencies.Count == 0)
        {
            if (newAgencies == 0)
            {
                yield break;
            }

            currencies = [PlatformBaseCurrency];
        }

        foreach (var currency in currencies)
        {
            var forCurrency = facts.Where(fact => fact.Currency == currency).ToList();

            yield return PlatformDailyAggregate.For(
                day,
                currency,

                // Agencies that sold something today — activity, not headcount.
                forCurrency.Where(fact => fact.IsSale).Select(fact => fact.AgencyId).Distinct().Count(),

                // Signups are counted once, against the first currency, or they would be multiplied
                // by the number of currencies that traded that day.
                currency == currencies[0] ? newAgencies : 0,
                forCurrency.Where(fact => fact.IsSale).Select(fact => fact.OrderId).Distinct().Count(),
                forCurrency.Count(fact => fact.IsSale),
                SumOf(forCurrency, fact => fact.GrossAmountMinor),
                SumOf(forCurrency, fact => fact.NetAmountMinor),
                SumOf(forCurrency, fact => fact.MarkupAmountMinor),
                SumOf(forCurrency, fact => fact.TaxAmountMinor),
                SumOf(forCurrency, fact => fact.PlatformFeeMinor),
                forCurrency.Count(fact => fact.IsRefunded),
                SumRefunds(forCurrency),
                forCurrency.Count(fact => fact.IsCancelled),
                forCurrency.Count(fact => fact.IsFailed),
                builtAt);
        }
    }

    /// <summary>One row per supplier, built from the call log rather than from orders.</summary>
    private async Task<IReadOnlyList<SupplierDailyAggregate>> SupplierRowsForAsync(
        DateOnly day,
        DateTimeOffset start,
        DateTimeOffset end,
        DateTimeOffset builtAt,
        CancellationToken cancellationToken)
    {
        var calls = await _db.SupplierApiCalls.AsNoTracking()
            .Where(call => call.OccurredAt >= start && call.OccurredAt < end)
            .Select(call => new
            {
                call.SupplierId,
                call.Operation,
                call.Outcome,
                call.LatencyMs,
            })
            .ToListAsync(cancellationToken);

        return calls
            .GroupBy(call => call.SupplierId)
            .Select(group => SupplierDailyAggregate.For(
                day,
                group.Key,
                group.Count(call => call.Operation == SupplierOperation.Search),
                group.Count(call => call.Operation == SupplierOperation.ConfirmPrice),
                group.Count(call => call.Operation == SupplierOperation.Issue),
                group.Count(call => call.Operation == SupplierOperation.Status),
                group.Count(call => call.Operation is SupplierOperation.Rules or SupplierOperation.Cancel),
                group.Count(call => call.Outcome != SupplierCallOutcome.Succeeded),
                group.Count(call => call.Outcome == SupplierCallOutcome.Timeout),

                // Search-to-book: an issue call that came back successful. A timed-out issue is
                // deliberately not counted as a booking here even though a ticket may exist —
                // conversion is measured on what the supplier confirmed, and the unknown case is
                // the poller's business, not the dashboard's (ADR-0003).
                group.Count(call => call.Operation == SupplierOperation.Issue
                    && call.Outcome == SupplierCallOutcome.Succeeded),
                // A group from GroupBy always has at least one call, so the mean is always
                // defined. Truncated to whole milliseconds: this is a latency, not money, and a
                // fractional millisecond is noise.
                (int)group.Average(call => call.LatencyMs),
                group.Max(call => call.LatencyMs),
                builtAt))
            .ToList();
    }

    /// <summary>
    /// The currency a platform row is written in on a day with signups and no sales.
    /// </summary>
    /// <remarks>Nigeria only for the MVP — see decision 17 in the build plan.</remarks>
    private const string PlatformBaseCurrency = "NGN";

    /// <summary>
    /// Adds money in minor units, as integers.
    /// </summary>
    /// <remarks>
    /// Written out rather than <c>Sum</c> over a projected <c>long</c> so there is one obvious
    /// place where money is added and no chance of a floating-point overload being selected by
    /// accident. <see cref="Money"/>'s operator is checked, so an impossible total throws rather
    /// than wrapping (CLAUDE.md rule 2).
    /// </remarks>
    private static Money SumOf(IEnumerable<BookingFact> facts, Func<BookingFact, Money> amount)
    {
        var total = Money.Zero;

        foreach (var fact in facts.Where(fact => fact.IsSale))
        {
            total += amount(fact);
        }

        return total;
    }

    /// <summary>What was handed back, which by definition is not in the sale totals above.</summary>
    private static Money SumRefunds(IEnumerable<BookingFact> facts)
    {
        var total = Money.Zero;

        foreach (var fact in facts.Where(fact => fact.IsRefunded))
        {
            total += fact.GrossAmountMinor;
        }

        return total;
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Analytics rollup ({Kind}) rebuilt {Days} day(s) and {FactRows} fact row(s) in {ElapsedMs}ms.")]
    private static partial void LogCompleted(ILogger logger, RollupKind kind, int days, int factRows, long elapsedMs);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Analytics rollup ({Kind}) failed. The run is recorded as failed, so the next run covers its window again.")]
    private static partial void LogFailed(ILogger logger, RollupKind kind, Exception exception);
}
