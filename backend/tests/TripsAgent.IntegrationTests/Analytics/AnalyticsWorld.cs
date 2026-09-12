using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using TripsAgent.Application.Analytics;
using TripsAgent.Domain.Common;
using TripsAgent.Domain.Orders;
using TripsAgent.Domain.Pricing;
using TripsAgent.Domain.Suppliers;
using TripsAgent.Domain.Tenancy;
using TripsAgent.Infrastructure.Persistence;
using TripsAgent.IntegrationTests.Persistence;

namespace TripsAgent.IntegrationTests.Analytics;

/// <summary>A clock a test moves by hand, so a rollup's watermark can be reasoned about exactly.</summary>
internal sealed class MovableClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now = _now.Add(by);

    public void MoveTo(DateTimeOffset instant) => _now = instant;
}

/// <summary>
/// A migrated database with agencies and orders in it, and a rollup pointed at them.
/// </summary>
/// <remarks>
/// The rollup runs with no tenant of its own, which is what a Hangfire job looks like: it opens a
/// platform scope itself and that is the only thing that lets it read across agencies. A world is
/// deliberately built through the domain — real quotes, real orders — rather than by inserting
/// fact rows, because the whole question these tests ask is whether the derived numbers match the
/// source.
/// </remarks>
internal sealed class AnalyticsWorld : IAsyncDisposable
{
    private readonly PostgresFixture _postgres;
    private readonly List<AppDbContext> _contexts = [];

    private AnalyticsWorld(PostgresFixture postgres, string database, MovableClock clock)
    {
        _postgres = postgres;
        Database = database;
        Clock = clock;
    }

    /// <summary>The VAT rate the quote records. 7.5% in Nigeria, in basis points.</summary>
    private const int VatRateBasisPoints = 750;

    public string Database { get; }

    public MovableClock Clock { get; }

    /// <summary>The database's owner. Used for setup and for reading past a policy on purpose.</summary>
    public AppDbContext Owner { get; private set; } = null!;

    /// <summary>The rollup's own context: no tenant, policed application role, exactly like a job.</summary>
    public AppDbContext JobDb { get; private set; } = null!;

    public AnalyticsRollup Rollup { get; private set; } = null!;

    public static async Task<AnalyticsWorld> CreateAsync(
        PostgresFixture postgres,
        DateTimeOffset now,
        string prefix = "analytics")
    {
        var database = $"{prefix}_{Guid.NewGuid():N}";
        var clock = new MovableClock(now);
        var world = new AnalyticsWorld(postgres, database, clock);

        await using (var setup = await postgres.CreateEmptyDatabaseAsync(database))
        {
            await setup.Database.MigrateAsync();
        }

        world.Owner = world.Track(postgres.Connect(database, asApplicationRole: false, clock: clock));

        var jobTenancy = TestTenancy.None();
        world.JobDb = world.Track(postgres.Connect(database, jobTenancy.Tenant, jobTenancy.Scope, clock));

        world.Rollup = new AnalyticsRollup(
            world.JobDb,
            jobTenancy.Scope,
            clock,
            NullLogger<AnalyticsRollup>.Instance);

        return world;
    }

    /// <summary>A context acting as one agency, under row-level security, as production connects.</summary>
    public AppDbContext AsAgency(Guid agencyId)
    {
        var tenancy = TestTenancy.For(agencyId);
        return Track(_postgres.Connect(Database, tenancy.Tenant, tenancy.Scope, Clock));
    }

    /// <summary>A context with no tenant but a platform scope open — a back-office read.</summary>
    public AppDbContext AsPlatform(string reason = "test")
    {
        var tenancy = TestTenancy.None();
        var context = Track(_postgres.Connect(Database, tenancy.Tenant, tenancy.Scope, Clock));
        tenancy.Scope.Enter(reason);
        return context;
    }

    public async Task<Guid> AddAgencyAsync(string name, string slug, DateTimeOffset? createdAt = null)
    {
        var agency = Agency.RegisterPrincipal(name, slug, "NG", "NGN", "Africa/Lagos");
        Owner.Agencies.Add(agency);
        await Owner.SaveChangesAsync();

        if (createdAt is not null)
        {
            // CreatedAt is stamped on save, so a test that needs a signup on a particular day sets
            // it afterwards. Raw SQL: the stamping interceptor would put it back.
            await Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions.ExecuteSqlRawAsync(
                Owner.Database,
                "UPDATE tenancy.agencies SET created_at = {0} WHERE id = {1}",
                createdAt.Value,
                agency.Id);
        }

        return agency.Id;
    }

    public async Task<Guid> AddSupplierAsync(string code, string name)
    {
        var supplier = Supplier.Register(code, name, SupplierKind.Multi, "https://example.test/api");
        Owner.Suppliers.Add(supplier);
        await Owner.SaveChangesAsync();
        return supplier.Id;
    }

    /// <summary>
    /// An order placed by one agency, through the domain, at an exact instant.
    /// </summary>
    /// <remarks>
    /// <paramref name="placedAt"/> is written afterwards with raw SQL for the same reason a
    /// signup date is: the placing instant comes from the clock, and a test needs orders spread
    /// over several days without waiting for them.
    /// </remarks>
    public async Task<Order> PlaceOrderAsync(
        Guid agencyId,
        string orderNumber,
        DateTimeOffset placedAt,
        long netMinor = 100_000,
        long markupMinor = 10_000,
        long taxMinor = 750,
        long platformFeeMinor = 500,
        OrderStatus status = OrderStatus.Confirmed)
    {
        var db = AsAgency(agencyId);

        // Built at the clock's "now", not at placedAt: a quote's expiry is checked against the
        // row's own created_at, and a quote issued two days ago with a thirty-minute life is
        // refused by the database before the rollup ever sees it. The order is filed under
        // placedAt afterwards, which is the only thing these tests care about.
        var now = Clock.GetUtcNow();

        var rule = MarkupRule.Create(agencyId, new MarkupRuleTerms
        {
            Scope = MarkupScope.Global,
            Currency = "NGN",
            CalculationType = MarkupCalculationType.Percentage,
            PercentBasisPoints = 1_000,
            EffectiveFrom = now.AddDays(-1),
        });
        db.MarkupRules.Add(rule);
        await db.SaveChangesAsync();

        var gross = netMinor + markupMinor + taxMinor;

        var quote = PriceQuote.Record(
            agencyId,
            new PricingSubject(PricedProductType.Flight, "NGN"),
            new PriceBreakdown(
                new Money(netMinor),
                new Money(markupMinor),
                new Money(taxMinor),
                new Money(platformFeeMinor),
                new Money(gross),
                "NGN",
                new MarkupRuleDefinition(rule.Id, agencyId, rule.Terms),
                false,
                VatRateBasisPoints,
                0),
            now,
            TimeSpan.FromMinutes(30));
        db.PriceQuotes.Add(quote);
        await db.SaveChangesAsync();

        var line = OrderLine.FromQuote(quote, "LOS → ABV, Air Peace", """{"adults":1}""", now);
        var order = Order.Place(
            agencyId, orderNumber, "NGN", BuyerType.AgentAssisted, OrderChannel.Console, null, [line], now);

        if (status != OrderStatus.PendingPayment)
        {
            order.ChangeStatus(status, now);
        }

        db.Orders.Add(order);
        await db.SaveChangesAsync();

        await MoveOrderToAsync(order.Id, placedAt);

        return order;
    }

    /// <summary>Files an order at a different instant, as though it had been placed then.</summary>
    public Task MoveOrderToAsync(Guid orderId, DateTimeOffset placedAt) =>
        Owner.Database.ExecuteSqlRawAsync(
            // Only the order moves. A placed line's placed_at is frozen by the money trigger, and
            // the rollup reads the order's instant anyway — which is the right one: a booking
            // belongs to the day the order was placed, not to whenever a line row was written.
            "UPDATE orders.orders SET placed_at = {0}, created_at = {0} WHERE id = {1}",
            placedAt,
            orderId);

    /// <summary>Marks the source rows changed, as an ordinary update would.</summary>
    public Task TouchOrderAsync(Guid orderId, DateTimeOffset updatedAt) =>
        Owner.Database.ExecuteSqlRawAsync(
            """
            UPDATE orders.orders SET updated_at = {0} WHERE id = {1};
            UPDATE orders.order_lines SET updated_at = {0} WHERE order_id = {1};
            """,
            updatedAt,
            orderId);

    /// <summary>
    /// Creates the monthly partition a supplier call needs before it can be inserted.
    /// </summary>
    /// <remarks>
    /// <c>supplier.supplier_api_calls</c> is partitioned by month and a maintenance job keeps the
    /// partitions ahead of the calendar. A test that writes a call on a fixed date has to make its
    /// own, or the insert is refused with "no partition of relation found for row".
    /// </remarks>
    public Task<string> EnsureSupplierCallPartitionAsync(DateOnly month) =>
        Owner.Database
            .SqlQuery<string>($"SELECT supplier.create_supplier_api_call_partition({month}) AS \"Value\"")
            .SingleAsync();

    /// <summary>One recorded supplier call, for the supplier-performance aggregate.</summary>
    public async Task RecordSupplierCallAsync(
        Guid supplierId,
        Guid? agencyId,
        SupplierOperation operation,
        SupplierCallOutcome outcome,
        int latencyMs,
        DateTimeOffset occurredAt)
    {
        await Owner.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO supplier.supplier_api_calls
                (id, occurred_at, agency_id, supplier_id, supplier_booking_id, operation, http_method,
                 endpoint, request_headers, request_body, response_status_code, response_body,
                 latency_ms, outcome, error_message, correlation_id)
            VALUES (gen_random_uuid(), {0}, {1}, {2}, NULL, {3}, 'POST',
                    'https://example.test/search', '{{}}', NULL, 200, NULL,
                    {4}, {5}, NULL, NULL)
            """,
            occurredAt,
            (object?)agencyId ?? DBNull.Value,
            supplierId,
            operation.ToString(),
            latencyMs,
            outcome.ToString());
    }

    private AppDbContext Track(AppDbContext context)
    {
        _contexts.Add(context);
        return context;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var context in _contexts)
        {
            await context.DisposeAsync();
        }
    }
}
