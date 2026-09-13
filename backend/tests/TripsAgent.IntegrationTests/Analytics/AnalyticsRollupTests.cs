using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using TripsAgent.Domain.Analytics;
using TripsAgent.Domain.Orders;
using TripsAgent.Domain.Suppliers;
using TripsAgent.IntegrationTests.Persistence;

namespace TripsAgent.IntegrationTests.Analytics;

/// <summary>
/// The rollup, against a real database.
/// </summary>
/// <remarks>
/// The criterion issue 67 cares about most is that rebuilding from source reproduces identical
/// numbers, and it is the one that cannot be argued from the code — a rebuild that silently doubles
/// a day looks exactly like a correct one until somebody reconciles. So it is asserted here three
/// ways: the same run twice, an incremental run against a full rebuild, and an order that moves
/// between days.
/// </remarks>
[Collection(PostgresCollection.Name)]
public sealed class AnalyticsRollupTests
{
    /// <summary>A Tuesday, mid-morning in Lagos, so nothing sits on a day boundary by accident.</summary>
    private static readonly DateTimeOffset Now = new(2026, 3, 10, 9, 0, 0, TimeSpan.Zero);

    private readonly PostgresFixture _postgres;

    public AnalyticsRollupTests(PostgresFixture postgres) => _postgres = postgres;

    // --------------------------------------------------------------- the numbers, from the source

    [Fact]
    public async Task A_days_aggregate_is_the_sum_of_its_lines()
    {
        await using var world = await AnalyticsWorld.CreateAsync(_postgres, Now, "rollup_sum");
        var agency = await world.AddAgencyAsync("Lagos Travel Limited", "lagos-travel");

        await world.PlaceOrderAsync(agency, "ORD-1", Now.AddHours(-2));
        await world.PlaceOrderAsync(agency, "ORD-2", Now.AddHours(-1));

        await world.Rollup.RunFullRebuildAsync();

        await using var platform = world.AsPlatform();
        var day = await platform.AgencyDailyAggregates.SingleAsync(row => row.AgencyId == agency);

        day.Day.Should().Be(LagosDay.Of(Now));
        day.BookingsCount.Should().Be(2);
        day.OrdersCount.Should().Be(2);
        day.GrossSalesMinor.AmountMinor.Should().Be(2 * 110_750);
        day.NetCostMinor.AmountMinor.Should().Be(2 * 100_000);
        day.MarkupMinor.AmountMinor.Should().Be(2 * 10_000);
        day.PlatformFeeMinor.AmountMinor.Should().Be(2 * 500);

        // Margin is what the agency keeps: markup less the platform's cut. Integer arithmetic all
        // the way down — CLAUDE.md rule 2.
        day.MarginMinor.AmountMinor.Should().Be(2 * (10_000 - 500));
    }

    [Fact]
    public async Task A_booking_is_counted_on_the_Lagos_day_the_agent_sold_it()
    {
        // 23:30 UTC on 9 March is 00:30 on 10 March in Lagos. The agent sold it on the 10th, and
        // that is the day the number has to appear on, or it will never match their own books.
        var justAfterMidnightInLagos = new DateTimeOffset(2026, 3, 9, 23, 30, 0, TimeSpan.Zero);

        await using var world = await AnalyticsWorld.CreateAsync(_postgres, Now, "rollup_lagos");
        var agency = await world.AddAgencyAsync("Lagos Travel Limited", "lagos-travel");

        await world.PlaceOrderAsync(agency, "ORD-1", justAfterMidnightInLagos);

        await world.Rollup.RunFullRebuildAsync();

        await using var platform = world.AsPlatform();
        var fact = await platform.BookingFacts.SingleAsync();

        fact.BookingDay.Should().Be(new DateOnly(2026, 3, 10));
    }

    [Fact]
    public async Task Money_that_was_given_back_is_not_revenue()
    {
        await using var world = await AnalyticsWorld.CreateAsync(_postgres, Now, "rollup_refund");
        var agency = await world.AddAgencyAsync("Lagos Travel Limited", "lagos-travel");

        await world.PlaceOrderAsync(agency, "ORD-1", Now.AddHours(-2));
        await world.PlaceOrderAsync(agency, "ORD-2", Now.AddHours(-1), status: OrderStatus.Refunded);

        await world.Rollup.RunFullRebuildAsync();

        await using var platform = world.AsPlatform();
        var day = await platform.AgencyDailyAggregates.SingleAsync(row => row.AgencyId == agency);

        day.BookingsCount.Should().Be(1, "a refunded line is not a sale");
        day.GrossSalesMinor.AmountMinor.Should().Be(110_750);
        day.RefundedCount.Should().Be(1);
        day.RefundedGrossMinor.AmountMinor.Should().Be(110_750);
    }

    [Fact]
    public async Task An_order_nobody_has_paid_for_is_not_a_sale()
    {
        await using var world = await AnalyticsWorld.CreateAsync(_postgres, Now, "rollup_pending");
        var agency = await world.AddAgencyAsync("Lagos Travel Limited", "lagos-travel");

        await world.PlaceOrderAsync(agency, "ORD-1", Now.AddHours(-1), status: OrderStatus.PendingPayment);

        await world.Rollup.RunFullRebuildAsync();

        await using var platform = world.AsPlatform();

        // The fact row exists — the line happened — but it counts as nothing.
        (await platform.BookingFacts.CountAsync()).Should().Be(1);

        var day = await platform.AgencyDailyAggregates.SingleOrDefaultAsync(row => row.AgencyId == agency);
        day.Should().NotBeNull();
        day!.BookingsCount.Should().Be(0);
        day.GrossSalesMinor.Should().Be(TripsAgent.Domain.Common.Money.Zero);
    }

    // ------------------------------------------------------- rebuilding reproduces the same numbers

    [Fact]
    public async Task Rebuilding_from_source_reproduces_identical_numbers()
    {
        await using var world = await AnalyticsWorld.CreateAsync(_postgres, Now, "rollup_identical");
        var lagos = await world.AddAgencyAsync("Lagos Travel Limited", "lagos-travel");
        var kano = await world.AddAgencyAsync("Kano Journeys", "kano-journeys");

        await world.PlaceOrderAsync(lagos, "ORD-1", Now.AddDays(-2));
        await world.PlaceOrderAsync(lagos, "ORD-2", Now.AddDays(-1), netMinor: 250_000, markupMinor: 25_000);
        await world.PlaceOrderAsync(kano, "ORD-3", Now.AddDays(-1));
        await world.PlaceOrderAsync(kano, "ORD-4", Now, status: OrderStatus.Refunded);

        await world.Rollup.RunFullRebuildAsync();
        var first = await SnapshotAsync(world);

        // Nothing about the source changed, so nothing about the derived numbers may change either.
        await world.Rollup.RunFullRebuildAsync();
        var second = await SnapshotAsync(world);

        second.Should().BeEquivalentTo(first);
    }

    [Fact]
    public async Task An_incremental_run_lands_where_a_full_rebuild_would()
    {
        await using var world = await AnalyticsWorld.CreateAsync(_postgres, Now, "rollup_incremental");
        var lagos = await world.AddAgencyAsync("Lagos Travel Limited", "lagos-travel");
        var kano = await world.AddAgencyAsync("Kano Journeys", "kano-journeys");

        // Three days of trading, each one rolled up incrementally as it happened — which is what
        // production looks like.
        await world.PlaceOrderAsync(lagos, "ORD-1", Now.AddDays(-2));
        await world.Rollup.RunIncrementalAsync();

        world.Clock.Advance(TimeSpan.FromMinutes(10));
        await world.PlaceOrderAsync(lagos, "ORD-2", Now.AddDays(-1), netMinor: 250_000, markupMinor: 25_000);
        await world.TouchOrderAsync((await OrderIdAsync(world, "ORD-2")), world.Clock.GetUtcNow());
        await world.Rollup.RunIncrementalAsync();

        world.Clock.Advance(TimeSpan.FromMinutes(10));
        await world.PlaceOrderAsync(kano, "ORD-3", Now);
        await world.TouchOrderAsync((await OrderIdAsync(world, "ORD-3")), world.Clock.GetUtcNow());
        await world.Rollup.RunIncrementalAsync();

        var incremental = await SnapshotAsync(world);

        // The nightly rebuild reads no watermark at all. If the incremental runs drifted, this is
        // where it shows.
        await world.Rollup.RunFullRebuildAsync();
        var rebuilt = await SnapshotAsync(world);

        rebuilt.Should().BeEquivalentTo(incremental);
    }

    [Fact]
    public async Task An_order_that_moves_to_another_day_is_counted_once()
    {
        await using var world = await AnalyticsWorld.CreateAsync(_postgres, Now, "rollup_moved");
        var agency = await world.AddAgencyAsync("Lagos Travel Limited", "lagos-travel");

        var order = await world.PlaceOrderAsync(agency, "ORD-1", Now.AddDays(-1));
        await world.Rollup.RunIncrementalAsync();

        // Paid for the next day: the order's placed_at moves, and with it the day it belongs on.
        world.Clock.Advance(TimeSpan.FromMinutes(10));
        await world.MoveOrderToAsync(order.Id, Now);
        await world.TouchOrderAsync(order.Id, world.Clock.GetUtcNow());

        await world.Rollup.RunIncrementalAsync();

        await using var platform = world.AsPlatform();

        // One fact row, on the new day. Rebuilding only the new day would have left yesterday still
        // counting it, and the agency's month would be one booking too long.
        var facts = await platform.BookingFacts.ToListAsync();
        facts.Should().ContainSingle();
        facts[0].BookingDay.Should().Be(LagosDay.Of(Now));

        var days = await platform.AgencyDailyAggregates.ToListAsync();
        days.Should().ContainSingle();
        days[0].Day.Should().Be(LagosDay.Of(Now));
    }

    [Fact]
    public async Task A_run_that_finds_nothing_changes_nothing()
    {
        await using var world = await AnalyticsWorld.CreateAsync(_postgres, Now, "rollup_idle");
        var agency = await world.AddAgencyAsync("Lagos Travel Limited", "lagos-travel");
        await world.PlaceOrderAsync(agency, "ORD-1", Now);

        await world.Rollup.RunIncrementalAsync();
        var before = await SnapshotAsync(world);

        // Two more runs. The first still sees the order, because every run rewinds its window by
        // the overlap and the order sits inside that rewind — rebuilding it again, to exactly the
        // same numbers, which is the point of the overlap being safe. The second run's window has
        // finally moved past it, so there is nothing at all to do.
        world.Clock.Advance(RollupRun.WatermarkOverlap + TimeSpan.FromMinutes(5));
        await world.Rollup.RunIncrementalAsync();

        world.Clock.Advance(RollupRun.WatermarkOverlap + TimeSpan.FromMinutes(5));
        var idle = await world.Rollup.RunIncrementalAsync();

        idle.FactRows.Should().Be(0, "nothing changed, so nothing was rebuilt");
        (await SnapshotAsync(world)).Should().BeEquivalentTo(before);
    }

    // ------------------------------------------------------------------------- the platform's view

    [Fact]
    public async Task Platform_GMV_is_every_agencys_sales_added_up()
    {
        await using var world = await AnalyticsWorld.CreateAsync(_postgres, Now, "rollup_gmv");
        var lagos = await world.AddAgencyAsync("Lagos Travel Limited", "lagos-travel");
        var kano = await world.AddAgencyAsync("Kano Journeys", "kano-journeys");

        await world.PlaceOrderAsync(lagos, "ORD-1", Now);
        await world.PlaceOrderAsync(kano, "ORD-2", Now);

        await world.Rollup.RunFullRebuildAsync();

        await using var platform = world.AsPlatform();
        var day = await platform.PlatformDailyAggregates.SingleAsync(row => row.Day == LagosDay.Of(Now));

        day.GmvMinor.AmountMinor.Should().Be(2 * 110_750);
        day.SellingAgenciesCount.Should().Be(2);
        day.BookingsCount.Should().Be(2);

        // Trips' own revenue is the fee taken from margins, not the GMV. The two are constantly
        // confused, so they are separate columns and this asserts they stay separate.
        day.PlatformFeeMinor.AmountMinor.Should().Be(2 * 500);
    }

    [Fact]
    public async Task Growth_counts_the_agencies_that_signed_up_that_day()
    {
        await using var world = await AnalyticsWorld.CreateAsync(_postgres, Now, "rollup_growth");
        await world.AddAgencyAsync("Lagos Travel Limited", "lagos-travel", createdAt: Now.AddHours(-3));
        await world.AddAgencyAsync("Kano Journeys", "kano-journeys", createdAt: Now.AddHours(-2));
        await world.AddAgencyAsync("Abuja Tours", "abuja-tours", createdAt: Now.AddDays(-5));

        await world.Rollup.RunFullRebuildAsync();

        await using var platform = world.AsPlatform();
        var today = await platform.PlatformDailyAggregates.SingleAsync(row => row.Day == LagosDay.Of(Now));

        today.NewAgenciesCount.Should().Be(2);
        today.GmvMinor.AmountMinor.Should().Be(0, "signing up is not selling");
    }

    [Fact]
    public async Task Supplier_conversion_and_errors_come_from_the_call_log()
    {
        await using var world = await AnalyticsWorld.CreateAsync(_postgres, Now, "rollup_supplier");
        var agency = await world.AddAgencyAsync("Lagos Travel Limited", "lagos-travel");
        var supplier = await world.AddSupplierAsync("trips_africa", "Trips Africa");
        await world.EnsureSupplierCallPartitionAsync(new DateOnly(Now.Year, Now.Month, 1));

        // Four searches, two confirms, one issued ticket and one that timed out.
        for (var i = 0; i < 4; i++)
        {
            await world.RecordSupplierCallAsync(
                supplier, agency, SupplierOperation.Search, SupplierCallOutcome.Succeeded, 400 + i, Now.AddHours(-2));
        }

        await world.RecordSupplierCallAsync(
            supplier, agency, SupplierOperation.ConfirmPrice, SupplierCallOutcome.Succeeded, 300, Now.AddHours(-2));
        await world.RecordSupplierCallAsync(
            supplier, agency, SupplierOperation.ConfirmPrice, SupplierCallOutcome.HttpError, 900, Now.AddHours(-2));
        await world.RecordSupplierCallAsync(
            supplier, agency, SupplierOperation.Issue, SupplierCallOutcome.Succeeded, 1_200, Now.AddHours(-1));
        await world.RecordSupplierCallAsync(
            supplier, agency, SupplierOperation.Issue, SupplierCallOutcome.Timeout, 20_000, Now.AddHours(-1));

        await world.Rollup.RunFullRebuildAsync();

        await using var platform = world.AsPlatform();
        var day = await platform.SupplierDailyAggregates.SingleAsync();

        day.SearchCount.Should().Be(4);
        day.ConfirmPriceCount.Should().Be(2);
        day.IssueCount.Should().Be(2);
        day.BookedCount.Should().Be(1, "only the issue the supplier confirmed counts as a booking");
        day.ErrorCount.Should().Be(2, "the HTTP error and the timeout");
        day.TimeoutCount.Should().Be(1);
        day.TotalCalls.Should().Be(8);
        day.MaxLatencyMs.Should().Be(20_000);
    }

    // --------------------------------------------------------------------------- the run's own log

    [Fact]
    public async Task A_run_records_what_it_rebuilt()
    {
        await using var world = await AnalyticsWorld.CreateAsync(_postgres, Now, "rollup_runs");
        var agency = await world.AddAgencyAsync("Lagos Travel Limited", "lagos-travel");
        await world.PlaceOrderAsync(agency, "ORD-1", Now);

        var result = await world.Rollup.RunIncrementalAsync();

        result.Kind.Should().Be(RollupKind.Incremental);
        result.FactRows.Should().Be(1);

        await using var platform = world.AsPlatform();
        var run = await platform.RollupRuns.OrderByDescending(entry => entry.StartedAt).FirstAsync();

        run.Status.Should().Be(RollupRunStatus.Succeeded);
        run.FactRows.Should().Be(1);
        run.CompletedAt.Should().NotBeNull();
    }

    // --------------------------------------------------------------------------------- helpers

    private static async Task<Guid> OrderIdAsync(AnalyticsWorld world, string orderNumber)
    {
        await using var platform = world.AsPlatform();
        return await platform.Orders.Where(order => order.OrderNumber == orderNumber)
            .Select(order => order.Id)
            .SingleAsync();
    }

    /// <summary>
    /// Every derived number, in a shape two runs can be compared by.
    /// </summary>
    /// <remarks>
    /// Ids and <c>built_at</c> are deliberately left out. A rebuild deletes and re-inserts, so the
    /// rows are new objects with new ids and a later timestamp; what must not change is what they
    /// say. Nothing references an analytics row by id, which is why that is a safe thing to ignore.
    /// </remarks>
    private static async Task<object> SnapshotAsync(AnalyticsWorld world)
    {
        await using var platform = world.AsPlatform();

        var facts = await platform.BookingFacts.AsNoTracking()
            .OrderBy(fact => fact.OrderLineId)
            .Select(fact => new
            {
                fact.OrderLineId,
                fact.AgencyId,
                fact.RootAgencyId,
                fact.BookingDay,
                fact.OrderStatus,
                fact.FulfilmentStatus,
                fact.Currency,
                Net = fact.NetAmountMinor.AmountMinor,
                Markup = fact.MarkupAmountMinor.AmountMinor,
                Tax = fact.TaxAmountMinor.AmountMinor,
                Fee = fact.PlatformFeeMinor.AmountMinor,
                Gross = fact.GrossAmountMinor.AmountMinor,
                fact.IsSale,
                fact.IsRefunded,
                fact.IsCancelled,
                fact.IsFailed,
            })
            .ToListAsync();

        var agencyDays = await platform.AgencyDailyAggregates.AsNoTracking()
            .OrderBy(row => row.AgencyId).ThenBy(row => row.Day)
            .Select(row => new
            {
                row.AgencyId,
                row.RootAgencyId,
                row.Day,
                row.Currency,
                row.OrdersCount,
                row.BookingsCount,
                Gross = row.GrossSalesMinor.AmountMinor,
                Net = row.NetCostMinor.AmountMinor,
                Markup = row.MarkupMinor.AmountMinor,
                Tax = row.TaxMinor.AmountMinor,
                Fee = row.PlatformFeeMinor.AmountMinor,
                row.RefundedCount,
                Refunded = row.RefundedGrossMinor.AmountMinor,
                row.CancelledCount,
                row.FailedCount,
            })
            .ToListAsync();

        var platformDays = await platform.PlatformDailyAggregates.AsNoTracking()
            .OrderBy(row => row.Day).ThenBy(row => row.Currency)
            .Select(row => new
            {
                row.Day,
                row.Currency,
                row.SellingAgenciesCount,
                row.NewAgenciesCount,
                row.OrdersCount,
                row.BookingsCount,
                Gmv = row.GmvMinor.AmountMinor,
                Net = row.NetCostMinor.AmountMinor,
                Markup = row.MarkupMinor.AmountMinor,
                Fee = row.PlatformFeeMinor.AmountMinor,
                row.RefundedCount,
                row.CancelledCount,
                row.FailedCount,
            })
            .ToListAsync();

        var supplierDays = await platform.SupplierDailyAggregates.AsNoTracking()
            .OrderBy(row => row.Day).ThenBy(row => row.SupplierId)
            .Select(row => new
            {
                row.Day,
                row.SupplierId,
                row.SearchCount,
                row.ConfirmPriceCount,
                row.IssueCount,
                row.StatusCount,
                row.OtherCount,
                row.ErrorCount,
                row.TimeoutCount,
                row.BookedCount,
                row.AverageLatencyMs,
                row.MaxLatencyMs,
            })
            .ToListAsync();

        return new { facts, agencyDays, platformDays, supplierDays };
    }
}
