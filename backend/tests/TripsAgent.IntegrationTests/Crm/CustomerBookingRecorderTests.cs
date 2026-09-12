using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using TripsAgent.Application.Checkout;
using TripsAgent.Application.Crm;
using TripsAgent.Application.Storefront;
using TripsAgent.Domain.Common;
using TripsAgent.Domain.Crm;
using TripsAgent.Domain.Orders;
using TripsAgent.Domain.Pricing;
using TripsAgent.Domain.Suppliers;
using TripsAgent.Domain.Tenancy;
using TripsAgent.Infrastructure.Persistence;
using TripsAgent.Infrastructure.Storefront;
using TripsAgent.IntegrationTests.Persistence;

namespace TripsAgent.IntegrationTests.Crm;

/// <summary>
/// The booking half of the customer 360 (#62): a confirmed booking finds the customer it belongs to,
/// or starts one, and the customer's record adds it up — against PostgreSQL as the policed
/// application role.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class CustomerBookingRecorderTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 9, 12, 10, 0, 0, TimeSpan.Zero);

    private readonly PostgresFixture _postgres;

    private string _database = string.Empty;
    private Guid _agencyId;
    private Guid _supplierId;
    private Guid _markupRuleId;

    public CustomerBookingRecorderTests(PostgresFixture postgres) => _postgres = postgres;

    public async Task InitializeAsync()
    {
        _database = $"crm_bookings_{Guid.NewGuid():N}";

        await using var setup = await _postgres.CreateEmptyDatabaseAsync(_database);
        await setup.Database.MigrateAsync();

        var agency = Agency.RegisterPrincipal("Lagos Travel Limited", "lagos-travel", "NG", "NGN", "Africa/Lagos");
        var supplier = Supplier.Register("trips_africa", "Trips Africa", SupplierKind.Multi, "https://api.staging.trips.ng");

        setup.Agencies.Add(agency);
        setup.Suppliers.Add(supplier);
        await setup.SaveChangesAsync();

        _agencyId = agency.Id;
        _supplierId = supplier.Id;

        var rule = MarkupRule.Create(agency.Id, new MarkupRuleTerms
        {
            Scope = MarkupScope.Global,
            Currency = "NGN",
            CalculationType = MarkupCalculationType.Percentage,
            PercentBasisPoints = 1_000,
            EffectiveFrom = Now.AddYears(-1),
        });

        setup.MarkupRules.Add(rule);
        await setup.SaveChangesAsync();

        _markupRuleId = rule.Id;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task A_confirmed_booking_starts_the_customer_it_belongs_to()
    {
        var confirmed = await PlaceAsync("ORD-2026-000001", "Ngozi", "Adeyemi", "ngozi@example.test", null);

        await RecordAsync(confirmed);

        await using var db = AsAgency();

        var customer = await db.Customers.SingleAsync();

        customer.Name.Should().Be("Ngozi Adeyemi");
        customer.Email.Should().Be("ngozi@example.test");
        (await db.Orders.SingleAsync(order => order.Id == confirmed.OrderId)).CustomerId.Should().Be(customer.Id);
    }

    [Fact]
    public async Task A_booking_finds_the_customer_who_already_inquired()
    {
        // She asked in March and booked in April. One record, not two (FRD §2.8 RS-1).
        await using (var db = AsAgency())
        {
            db.Customers.Add(Customer.Create(_agencyId, "Ngozi A.", "ngozi@example.test", null, Now.AddMonths(-1)));
            await db.SaveChangesAsync();
        }

        var confirmed = await PlaceAsync("ORD-2026-000002", "Ngozi", "Adeyemi", "ngozi@example.test", null);
        await RecordAsync(confirmed);

        await using var check = AsAgency();

        check.Customers.Should().HaveCount(1);
        (await check.Orders.SingleAsync(order => order.Id == confirmed.OrderId)).CustomerId
            .Should().Be((await check.Customers.SingleAsync()).Id);
    }

    [Fact]
    public async Task A_traveller_known_only_by_phone_is_matched_on_it()
    {
        await using (var db = AsAgency())
        {
            db.Customers.Add(Customer.Create(_agencyId, "Ngozi A.", null, "+234 803 000 1122", Now.AddMonths(-1)));
            await db.SaveChangesAsync();
        }

        var confirmed = await PlaceAsync("ORD-2026-000003", "Ngozi", "Adeyemi", null, "0803 000 1122");
        await RecordAsync(confirmed);

        await using var check = AsAgency();

        check.Customers.Should().HaveCount(1, "the same number written differently is the same person");
    }

    [Fact]
    public async Task The_same_event_twice_records_the_booking_once()
    {
        var confirmed = await PlaceAsync("ORD-2026-000004", "Ngozi", "Adeyemi", "ngozi@example.test", null);

        await RecordAsync(confirmed);
        await RecordAsync(confirmed);

        await using var check = AsAgency();

        check.Customers.Should().HaveCount(1);
    }

    [Fact]
    public async Task A_traveller_with_no_way_to_reach_them_makes_no_customer_record()
    {
        var confirmed = await PlaceAsync("ORD-2026-000005", "Ngozi", "Adeyemi", null, null);

        await RecordAsync(confirmed);

        await using var check = AsAgency();

        check.Customers.Should().BeEmpty("a name alone cannot recognise the same person later");
        (await check.Orders.SingleAsync()).CustomerId.Should().BeNull("the booking still stands");
    }

    [Fact]
    public async Task The_customers_record_counts_paid_bookings_and_drops_refunded_ones()
    {
        var kept = await PlaceAsync("ORD-2026-000006", "Ngozi", "Adeyemi", "ngozi@example.test", null);
        var refunded = await PlaceAsync("ORD-2026-000007", "Ngozi", "Adeyemi", "ngozi@example.test", null);

        await RecordAsync(kept);
        await RecordAsync(refunded);

        await using (var db = AsAgency())
        {
            var order = await db.Orders.SingleAsync(candidate => candidate.Id == refunded.OrderId);
            order.ChangeStatus(OrderStatus.Refunded, Now, "the customer cancelled");
            await db.SaveChangesAsync();
        }

        await using var reading = AsAgency();
        var customers = CustomersFor(reading);

        var summaries = await customers.ListAsync();

        summaries.Should().ContainSingle();
        summaries[0].TotalBookings.Should().Be(1, "a refunded booking is money the customer got back");
        summaries[0].LifetimeValueMinor.Should().Be(110_750);

        var full = await customers.GetAsync(summaries[0].Id);

        full!.Bookings.Should().HaveCount(2, "both still show on the record");
        full.Bookings.Select(booking => booking.Status).Should().Contain("Refunded");
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>Places a paid order with one line, its supplier booking and its lead traveller.</summary>
    private async Task<BookingConfirmed> PlaceAsync(
        string orderNumber,
        string firstName,
        string lastName,
        string? email,
        string? phone)
    {
        await using var db = AsAgency();

        var quote = PriceQuote.Record(
            _agencyId,
            new PricingSubject(PricedProductType.Flight, "NGN"),
            new PriceBreakdown(
                new Money(100_000), new Money(10_000), new Money(750), new Money(500), new Money(110_750),
                "NGN", new MarkupRuleDefinition(_markupRuleId, _agencyId, RuleTerms()), false, 750, 0),
            Now,
            TimeSpan.FromMinutes(30));

        db.PriceQuotes.Add(quote);
        await db.SaveChangesAsync();

        var line = OrderLine.FromQuote(quote, "LOS → ABV, Air Peace", """{"adults":1}""", Now);
        var order = Order.Place(_agencyId, orderNumber, "NGN", BuyerType.AgentAssisted, OrderChannel.Console, null, [line], Now);

        order.RecordPayment(OrderPaymentMethod.Wallet, $"pay-{orderNumber}", Now);
        db.Orders.Add(order);
        await db.SaveChangesAsync();

        var booking = SupplierBooking.Create(
            _agencyId, _supplierId, line.Id, null, SupplierProductType.Flight, "Domestic", "Flight",
            "session-1", "NGN", $"idem-{Guid.NewGuid():N}");

        db.SupplierBookings.Add(booking);
        await db.SaveChangesAsync();

        db.SupplierBookingPassengers.Add(SupplierBookingPassenger.Add(
            _agencyId, booking.Id, PassengerType.Adult, firstName, lastName, email: email, phoneNumber: phone));

        await db.SaveChangesAsync();

        return new BookingConfirmed(_agencyId, order.Id, orderNumber, line.Id, booking.Id, "ABC123", Now);
    }

    private static MarkupRuleTerms RuleTerms() => new()
    {
        Scope = MarkupScope.Global,
        Currency = "NGN",
        CalculationType = MarkupCalculationType.Percentage,
        PercentBasisPoints = 1_000,
        EffectiveFrom = Now.AddYears(-1),
    };

    /// <summary>One delivery of the event, in its own unit of work, as the Worker's consumer runs it.</summary>
    private async Task RecordAsync(BookingConfirmed confirmed)
    {
        await using var db = AsAgency();

        var recorder = new CustomerBookingRecorder(
            db,
            new CustomerDirectory(db),
            new PostgresUniqueViolationDetector(),
            new FixedClock(Now),
            NullLogger<CustomerBookingRecorder>.Instance);

        await recorder.RecordAsync(confirmed);
    }

    private CustomerService CustomersFor(AppDbContext db)
    {
        var (tenant, scope) = TestTenancy.For(_agencyId);

        return new CustomerService(
            db,
            new CrmContext(db, tenant, new FixedClock(Now)),
            new CrmReader(db, new SiteDomainDirectory(db, scope, new UncachedStorefrontHostCache(), new StorefrontOptions())));
    }

    /// <summary>A clock stopped at one instant, so every row a test writes carries the same time.</summary>
    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private AppDbContext AsAgency()
    {
        var (tenant, scope) = TestTenancy.For(_agencyId);
        return _postgres.Connect(_database, tenant, scope, new FixedClock(Now));
    }
}
