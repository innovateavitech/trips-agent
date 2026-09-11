using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;
using TripsAgent.Application.Concurrency;
using TripsAgent.Application.Messaging;
using TripsAgent.Application.Notifications;
using TripsAgent.Application.Persistence;
using TripsAgent.Application.Suppliers;
using TripsAgent.Application.Tenancy;
using TripsAgent.Domain.Common;
using TripsAgent.Domain.Identity;
using TripsAgent.Domain.Orders;
using TripsAgent.Domain.Payments;
using TripsAgent.Domain.Pricing;
using TripsAgent.Domain.Suppliers;
using TripsAgent.Domain.Tenancy;
using TripsAgent.Infrastructure.Concurrency;
using TripsAgent.Infrastructure.Messaging;
using TripsAgent.Infrastructure.Persistence;
using TripsAgent.Infrastructure.Suppliers;
using TripsAgent.Infrastructure.Tenancy;
using TripsAgent.Integrations.TripsAfrica;
using TripsAgent.IntegrationTests.Identity;
using TripsAgent.IntegrationTests.Persistence;

namespace TripsAgent.IntegrationTests.Suppliers;

/// <summary>What a seeded booking is called.</summary>
internal sealed record SeededBooking(Guid OrderId, string OrderNumber, Guid OrderLineId, Guid SupplierBookingId, string IdempotencyKey);

/// <summary>
/// The booking pipeline as the Worker composes it — the issuer, the poller and the time limit monitor
/// — over a real PostgreSQL (as the policed application role), a real HTTP stand-in for Trips Africa,
/// and a real Redis when a test asks for one.
/// </summary>
/// <remarks>
/// Every unit of work gets its own DI scope, and so its own DbContext and connection, exactly like a
/// message or a job run. "Twenty concurrent issue messages" is twenty scopes running at once.
/// </remarks>
internal sealed class BookingPipelineHarness : IAsyncDisposable
{
    /// <summary>When every test's clock starts: UTC, as the database insists.</summary>
    public static readonly DateTimeOffset Start = new(2026, 9, 11, 9, 0, 0, TimeSpan.Zero);

    private readonly PostgresFixture _postgres;
    private readonly ServiceProvider _services;
    private int _orders;

    private BookingPipelineHarness(
        PostgresFixture postgres,
        string database,
        Guid agencyId,
        Guid supplierId,
        Guid walletId,
        ManualClock clock,
        TripsAfricaStub stub,
        ServiceProvider services,
        RecordingAlerter alerts)
    {
        _postgres = postgres;
        _services = services;
        Database = database;
        AgencyId = agencyId;
        SupplierId = supplierId;
        WalletId = walletId;
        Clock = clock;
        Stub = stub;
        Alerts = alerts;
    }

    public string Database { get; }

    public Guid AgencyId { get; }

    public Guid SupplierId { get; }

    /// <summary>The agency's NGN wallet, opened with ₦10,000,000 in it.</summary>
    public Guid WalletId { get; }

    public ManualClock Clock { get; }

    public TripsAfricaStub Stub { get; }

    public RecordingAlerter Alerts { get; }

    public static async Task<BookingPipelineHarness> CreateAsync(
        PostgresFixture postgres,
        TripsAfricaStub stub,
        IConnectionMultiplexer? redis = null,
        int issueTimeoutSeconds = 45)
    {
        var database = $"pipeline_{Guid.NewGuid():N}";
        var clock = new ManualClock(Start);

        Guid agencyId;
        Guid supplierId;
        Guid walletId;

        await using (var setup = await postgres.CreateEmptyDatabaseAsync(database, clock: clock))
        {
            await setup.Database.MigrateAsync();

            var agency = Agency.RegisterPrincipal("Lagos Travel Limited", "lagos-travel", "NG", "NGN", "Africa/Lagos");
            var supplier = Supplier.Register(TripsAfricaOptions.SupplierCode, "Trips Africa", SupplierKind.Multi, stub.BaseAddress.ToString());
            setup.Agencies.Add(agency);
            setup.Suppliers.Add(supplier);
            await setup.SaveChangesAsync();

            // The owner, who the time limit emails go to.
            setup.Users.Add(User.ForAgency(agency.Id, "ada@lagos-travel.test", "not-a-real-hash", "Ada", "Okafor"));

            var wallet = Wallet.OpenFor(agency.Id, "NGN");
            wallet.Credit(new Money(10_000_000_00));
            setup.Wallets.Add(wallet);
            await setup.SaveChangesAsync();

            agencyId = agency.Id;
            supplierId = supplier.Id;
            walletId = wallet.Id;
        }

        var alerts = new RecordingAlerter();
        var services = Compose(postgres, database, clock, stub, redis, alerts, issueTimeoutSeconds);

        return new BookingPipelineHarness(postgres, database, agencyId, supplierId, walletId, clock, stub, services, alerts);
    }

    /// <summary>A unit of work as a message for the agency gets it: its own scope, the agency as tenant.</summary>
    public AsyncServiceScope ScopeForAgency()
    {
        var scope = _services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().SetTenant(AgencyId);
        return scope;
    }

    /// <summary>A unit of work as a Hangfire job gets it: no tenant. The job opens the platform scope itself.</summary>
    public AsyncServiceScope ScopeForJob() => _services.CreateAsyncScope();

    /// <summary>A context acting as the agency, under the policed application role, for reading results.</summary>
    public AppDbContext AsAgency()
    {
        var tenancy = TestTenancy.For(AgencyId);
        return _postgres.Connect(Database, tenancy.Tenant, tenancy.Scope, Clock);
    }

    /// <summary>The schema owner, for the tests about what the database itself refuses.</summary>
    public AppDbContext AsOwner() => _postgres.Connect(Database, clock: Clock, asApplicationRole: false);

    public async Task<TicketIssueOutcome> IssueAsync(SeededBooking booking, string? idempotencyKey = null, CancellationToken cancellationToken = default)
    {
        await using var scope = ScopeForAgency();

        return await scope.ServiceProvider.GetRequiredService<TicketIssuanceService>()
            .IssueAsync(booking.OrderLineId, idempotencyKey ?? booking.IdempotencyKey, cancellationToken: cancellationToken);
    }

    public async Task<int> PollAsync()
    {
        await using var scope = ScopeForJob();
        return await scope.ServiceProvider.GetRequiredService<SupplierBookingStatusPoller>().RunAsync();
    }

    public async Task<TicketTimeLimitRun> MonitorAsync()
    {
        await using var scope = ScopeForJob();
        return await scope.ServiceProvider.GetRequiredService<TicketTimeLimitMonitor>().RunAsync();
    }

    public async Task<SupplierBooking> BookingAsync(SeededBooking seeded)
    {
        await using var db = AsAgency();
        return await db.SupplierBookings.AsNoTracking().SingleAsync(booking => booking.Id == seeded.SupplierBookingId);
    }

    public async Task<List<SupplierStatusPoll>> PollsAsync(SeededBooking seeded)
    {
        await using var db = AsAgency();
        return await db.SupplierStatusPolls.AsNoTracking()
            .Where(poll => poll.SupplierBookingId == seeded.SupplierBookingId)
            .OrderBy(poll => poll.PolledAt)
            .ToListAsync();
    }

    /// <summary>The outbox messages of a type — what the Worker will publish.</summary>
    public async Task<List<string>> OutboxPayloadsAsync<TMessage>()
    {
        await using var db = AsOwner();
        var type = typeof(TMessage).AssemblyQualifiedName!;

        // Compared on the type's name, since the stored name is whatever the serialiser wrote.
        var rows = await db.OutboxMessages.AsNoTracking().Select(message => new { message.MessageType, message.Payload }).ToListAsync();
        return rows
            .Where(row => row.MessageType.Split(',')[0] == type.Split(',')[0])
            .Select(row => row.Payload)
            .ToList();
    }

    /// <summary>
    /// A held booking: a rule, a quote, an order with one line, and a supplier booking whose price was
    /// confirmed and verified — ready to issue.
    /// </summary>
    public async Task<SeededBooking> SeedConfirmedBookingAsync(
        SupplierProductType product = SupplierProductType.Flight,
        TimeSpan? ticketTimeLimitIn = null,
        long netMinor = 100_000,
        bool paidFromWallet = false,
        string? tripType = "Domestic",
        string? tripMode = null)
    {
        var now = Clock.GetUtcNow();
        await using var db = AsAgency();

        var rule = MarkupRule.Create(AgencyId, new MarkupRuleTerms
        {
            Scope = MarkupScope.Global,
            Currency = "NGN",
            CalculationType = MarkupCalculationType.Percentage,
            PercentBasisPoints = 1_000,
            EffectiveFrom = now.AddDays(-1),
        });
        db.MarkupRules.Add(rule);
        await db.SaveChangesAsync();

        var priced = product == SupplierProductType.Bus ? PricedProductType.Bus : PricedProductType.Flight;
        var quote = PriceQuote.Record(
            AgencyId,
            new PricingSubject(priced, "NGN"),
            new PriceBreakdown(
                new Money(100_000), new Money(10_000), new Money(750), new Money(500), new Money(110_750),
                "NGN", new MarkupRuleDefinition(rule.Id, AgencyId, rule.Terms), false, 750, 0),
            now,
            TimeSpan.FromMinutes(30));
        db.PriceQuotes.Add(quote);
        await db.SaveChangesAsync();

        var number = string.Create(CultureInfo.InvariantCulture, $"ORD-2026-{Interlocked.Increment(ref _orders):D6}");
        var line = OrderLine.FromQuote(quote, "LOS → ABV, Air Peace", """{"adults":1}""", now);
        var order = TripsAgent.Domain.Orders.Order.Place(AgencyId, number, "NGN", BuyerType.AgentAssisted, OrderChannel.Console, null, [line], now);
        db.Orders.Add(order);

        // EF does not know supplier_bookings.order_line_id references order_lines — that foreign key was
        // added in SQL (AddOrders) — so it cannot order the inserts itself: the line has to exist first.
        await db.SaveChangesAsync();

        var booking = SupplierBooking.Create(
            AgencyId,
            SupplierId,
            line.Id,
            supplierOfferId: null,
            product,
            tripType,
            tripMode ?? (product == SupplierProductType.Bus ? "Road" : "Flight"),
            "8646790ccb9a4d0997a6b52693287256",
            "NGN",
            $"order-line:{line.Id:N}");

        booking.RecordPriceConfirmation(
            [new PriceConfirmationLine("36516|12QFDT", new Money(netMinor), new Money(netMinor), now + (ticketTimeLimitIn ?? TimeSpan.FromMinutes(45)), "ab12", "ab12")],
            now);

        line.AttachSupplierBooking(booking.Id);
        db.SupplierBookings.Add(booking);
        db.SupplierBookingPassengers.Add(SupplierBookingPassenger.Add(AgencyId, booking.Id, PassengerType.Adult, "Ngozi", "Adeyemi"));

        if (paidFromWallet)
        {
            var wallet = await db.Wallets.SingleAsync(candidate => candidate.Id == WalletId);
            db.WalletHolds.Add(wallet.PlaceHold(new Money(netMinor), now, TimeSpan.FromDays(2), order.Id));
            order.RecordPayment(OrderPaymentMethod.Wallet, $"pay:{line.Id:N}", now);
        }

        await db.SaveChangesAsync();

        return new SeededBooking(order.Id, order.OrderNumber, line.Id, booking.Id, booking.IdempotencyKey);
    }

    /// <summary>A booking whose issue call answered "TicketPending", with its first poll due now.</summary>
    public async Task<SeededBooking> SeedPendingBookingAsync(TimeSpan? ticketTimeLimitIn = null, bool paidFromWallet = true)
    {
        var seeded = await SeedConfirmedBookingAsync(ticketTimeLimitIn: ticketTimeLimitIn, paidFromWallet: paidFromWallet);
        var now = Clock.GetUtcNow();

        await using var db = AsAgency();
        var booking = await db.SupplierBookings.SingleAsync(candidate => candidate.Id == seeded.SupplierBookingId);
        booking.BeginIssue(now.AddSeconds(-30));
        booking.RecordIssuePending("RE6MIK", 3, now.AddSeconds(-30));
        await db.SaveChangesAsync();

        return seeded;
    }

    public async ValueTask DisposeAsync() => await _services.DisposeAsync();

    private static ServiceProvider Compose(
        PostgresFixture postgres,
        string database,
        ManualClock clock,
        TripsAfricaStub stub,
        IConnectionMultiplexer? redis,
        RecordingAlerter alerts,
        int issueTimeoutSeconds)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TripsAfrica:BaseUrl"] = stub.BaseAddress.ToString(),
                ["TripsAfrica:IssueTimeoutSeconds"] = issueTimeoutSeconds.ToString(CultureInfo.InvariantCulture),
                ["TripsAfrica:TimeoutSeconds"] = "30",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(clock);

        // Tenancy, as AddInfrastructure registers it.
        services.AddScoped<TenantContext>();
        services.AddScoped<ITenantContext>(provider => provider.GetRequiredService<TenantContext>());
        services.AddScoped<IPlatformScope, PlatformScope>();

        // One context per scope, as the policed application role — how production connects.
        services.AddScoped(provider => postgres.Connect(
            database,
            provider.GetRequiredService<TenantContext>(),
            provider.GetRequiredService<IPlatformScope>(),
            clock));
        services.AddScoped<IAppDbContext>(provider => provider.GetRequiredService<AppDbContext>());
        services.AddScoped<ITransactionRunner, EfTransactionRunner>();
        services.AddScoped<IOutbox, EfOutbox>();
        services.AddScoped<INotifier, Notifier>();
        services.AddScoped<ISupplierBookingLocks, SupplierBookingLocks>();

        services.AddSingleton<IDistributedLock>(redis is null
            ? new NoDistributedLock(NullLogger<NoDistributedLock>.Instance)
            : new RedisDistributedLock(redis, NullLogger<RedisDistributedLock>.Instance));

        services.AddSingleton<IPlatformAlerter>(alerts);
        services.AddSingleton<ISupplierCallRecorder, DiscardingCallRecorder>();
        services.AddScoped<ISupplierCredentialStore, FixedCredentialStore>();

        // The real integration, pointed at the stub. Nothing between it and the socket is faked.
        services.AddTripsAfrica(configuration);
        services.AddScoped<ISupplierAdapterRegistry, SupplierAdapterRegistry>();

        services.AddScoped<TicketIssuanceService>();
        services.AddScoped<SupplierBookingStatusPoller>();
        services.AddScoped<TicketTimeLimitMonitor>();

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }
}

/// <summary>Keeps every platform alert raised, instead of emailing anyone.</summary>
internal sealed class RecordingAlerter : IPlatformAlerter
{
    private readonly ConcurrentQueue<PlatformAlert> _alerts = new();

    public IReadOnlyList<PlatformAlert> Raised => [.. _alerts];

    public Task RaiseAsync(PlatformAlert alert, CancellationToken cancellationToken = default)
    {
        _alerts.Enqueue(alert);
        return Task.CompletedTask;
    }
}

/// <summary>The audit log's writer, switched off: these tests are about bookings, not the call log.</summary>
internal sealed class DiscardingCallRecorder : ISupplierCallRecorder
{
    public void Record(SupplierCallCapture capture)
    {
    }
}

internal sealed class FixedCredentialStore : ISupplierCredentialStore
{
    public Task<SupplierCredentials?> FindAsync(
        string supplierCode,
        SupplierEnvironment environment,
        Guid? agencyId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<SupplierCredentials?>(new SupplierCredentials(
            Guid.CreateVersion7(), supplierCode, environment, AgencyId: null, "TESTCODE", "test-merchant-key", "flight-bearer-token"));

    public Task<Guid> SaveAsync(
        string supplierCode,
        SupplierEnvironment environment,
        Guid? agencyId,
        string merchantCode,
        string merchantKey,
        string? bearerToken,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
}
