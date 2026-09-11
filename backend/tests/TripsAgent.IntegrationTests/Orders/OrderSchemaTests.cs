using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using TripsAgent.Domain.Common;
using TripsAgent.Domain.Orders;
using TripsAgent.Domain.Pricing;
using TripsAgent.Domain.Tenancy;
using TripsAgent.Infrastructure.Persistence;
using TripsAgent.Infrastructure.Security;
using TripsAgent.IntegrationTests.Persistence;

namespace TripsAgent.IntegrationTests.Orders;

/// <summary>
/// What PostgreSQL itself refuses about an order, whatever the application does. Raw SQL and the
/// table owner on purpose: these are the writes that would get past the domain — a script, a hand-fix
/// in psql, a future bug — and rule 5 says a price already given never moves.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class OrderSchemaTests
{
    private const string RestrictViolation = "23001";
    private const string CheckViolation = "23514";
    private const string UniqueViolation = "23505";
    private const string InsufficientPrivilege = "42501";

    private readonly PostgresFixture _postgres;

    public OrderSchemaTests(PostgresFixture postgres) => _postgres = postgres;

    // ------------------------------------------------------------------ the price is final

    [Theory]
    [InlineData("net_amount_minor = net_amount_minor + 1")]
    [InlineData("markup_amount_minor = 0")]
    [InlineData("tax_amount_minor = 0")]
    [InlineData("platform_fee_minor = 0")]
    [InlineData("gross_amount_minor = 1")]
    [InlineData("markup_rule_id = NULL")]
    [InlineData("price_quote_id = gen_random_uuid()")]
    [InlineData("currency = 'USD'")]
    [InlineData("placed_at = NULL")]
    public async Task A_placed_lines_price_is_final(string change)
    {
        await using var world = await WorldAsync();
        var order = await world.PlaceOrderAsync();

        // EF1002 guards against user input reaching raw SQL. `change` is one of the constants in
        // [InlineData] above — a SET clause cannot be a parameter, and the test needs a real one.
#pragma warning disable EF1002
        var act = () => world.Owner.Database.ExecuteSqlRawAsync(
            $"UPDATE orders.order_lines SET {change} WHERE id = {{0}}", order.Lines[0].Id);
#pragma warning restore EF1002

        (await act.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be(RestrictViolation, "the money on a placed line is frozen, even for the owner");
    }

    [Fact]
    public async Task Everything_else_about_a_placed_line_can_still_change()
    {
        await using var world = await WorldAsync();
        var order = await world.PlaceOrderAsync();

        var changed = await world.Owner.Database.ExecuteSqlRawAsync(
            "UPDATE orders.order_lines SET fulfilment_status = 'Confirmed' WHERE id = {0}", order.Lines[0].Id);

        changed.Should().Be(1, "fulfilment moves on long after the price stops moving");
    }

    [Fact]
    public async Task A_line_that_was_never_placed_can_still_be_corrected()
    {
        await using var world = await WorldAsync();
        var order = await world.PlaceOrderAsync();
        var draft = await world.AddRawLineAsync(order, placed: false);

        // Coherently: net 90,000 + markup 10,000 + tax 750. ck_order_lines_gross holds on UPDATE as
        // well as INSERT, so even a draft cannot be left with a gross that is not the sum of its parts.
        var changed = await world.Owner.Database.ExecuteSqlRawAsync(
            "UPDATE orders.order_lines SET net_amount_minor = 90000, gross_amount_minor = 100750 WHERE id = {0}",
            draft);

        changed.Should().Be(1, "the trigger is scoped to placed lines — a draft is not a sale");
    }

    // ------------------------------------------------------------------ shape

    [Fact]
    public async Task The_gross_must_be_the_sum_of_its_parts()
    {
        await using var world = await WorldAsync();
        var order = await world.PlaceOrderAsync();

        var act = () => world.AddRawLineAsync(order, placed: false, gross: 999);

        (await act.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(CheckViolation);
    }

    [Fact]
    public async Task A_failed_line_must_say_why_and_what_is_being_done()
    {
        await using var world = await WorldAsync();
        var order = await world.PlaceOrderAsync();

        var act = () => world.Owner.Database.ExecuteSqlRawAsync(
            "UPDATE orders.order_lines SET fulfilment_status = 'FailedNeedsResolution' WHERE id = {0}",
            order.Lines[0].Id);

        (await act.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be(CheckViolation, "money taken and nothing delivered needs a reason and an owner");
    }

    [Fact]
    public async Task An_order_number_is_unique_per_agency_but_not_across_them()
    {
        await using var world = await WorldAsync();
        var order = await world.PlaceOrderAsync();

        var again = () => world.Owner.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO orders.orders
                (id, agency_id, order_number, buyer_type, channel, status, currency,
                 total_net_minor, total_markup_minor, total_tax_minor, total_platform_fee_minor,
                 total_gross_minor, placed_at, created_at, updated_at)
            VALUES (gen_random_uuid(), {0}, {1}, 'Customer', 'Storefront', 'PendingPayment', 'NGN',
                    0, 0, 0, 0, 0, now(), now(), now())
            """,
            world.AgencyId, order.OrderNumber);

        (await again.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(UniqueViolation);

        var otherAgency = await world.AddAgencyAsync("Abuja Tours Limited", "abuja-tours");
        var elsewhere = await world.Owner.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO orders.orders
                (id, agency_id, order_number, buyer_type, channel, status, currency,
                 total_net_minor, total_markup_minor, total_tax_minor, total_platform_fee_minor,
                 total_gross_minor, placed_at, created_at, updated_at)
            VALUES (gen_random_uuid(), {0}, {1}, 'Customer', 'Storefront', 'PendingPayment', 'NGN',
                    0, 0, 0, 0, 0, now(), now(), now())
            """,
            otherAgency, order.OrderNumber);

        elsewhere.Should().Be(1, "each agency numbers its own orders from one");
    }

    // ------------------------------------------------------------------ tenancy and privilege

    [Fact]
    public async Task The_application_role_cannot_delete_an_order()
    {
        await using var world = await WorldAsync();
        var order = await world.PlaceOrderAsync();

        var act = () => world.Db.Database.ExecuteSqlRawAsync("DELETE FROM orders.orders WHERE id = {0}", order.Id);

        (await act.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be(InsufficientPrivilege, "an order is cancelled or refunded, never deleted");
    }

    [Fact]
    public async Task Another_agency_sees_none_of_it()
    {
        await using var world = await WorldAsync();
        await world.PlaceOrderAsync();
        var otherAgency = await world.AddAgencyAsync("Abuja Tours Limited", "abuja-tours");

        var tenancy = TestTenancy.For(otherAgency);
        await using var asOther = _postgres.Connect(world.Database, tenancy.Tenant, tenancy.Scope);

        (await asOther.Orders.CountAsync()).Should().Be(0);
        (await asOther.OrderLines.CountAsync()).Should().Be(0);
        (await asOther.OrderTravellers.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task A_supplier_booking_has_to_point_at_a_real_order_line()
    {
        await using var world = await WorldAsync();

        await using var connection = new NpgsqlConnection(_postgres.ConnectionStringFor(world.Database, asApplicationRole: false));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT count(*) FROM pg_constraint
             WHERE conname = 'fk_supplier_bookings_order_lines_order_line_id'
               AND contype = 'f'
            """;

        var found = (long)(await command.ExecuteScalarAsync())!;

        found.Should().Be(1, "order_line_id was unique but unenforced until order_lines existed");
    }

    [Fact]
    public async Task A_passport_number_is_never_stored_in_clear()
    {
        await using var world = await WorldAsync();
        var order = await world.PlaceOrderAsync();

        const string passport = "A01234567";
        var protector = new AesGcmSecretProtector(RandomNumberGenerator.GetBytes(AesGcmSecretProtector.KeyBytes));
        var traveller = OrderTraveller.Record(
            world.AgencyId,
            order.Lines[0].Id,
            TravellerType.Adult,
            "Ngozi",
            "Adeyemi",
            passportNumberEncrypted: protector.Protect(passport, "orders.order_travellers.passport_number"));

        world.Db.OrderTravellers.Add(traveller);
        await world.Db.SaveChangesAsync();

        await using var connection = new NpgsqlConnection(_postgres.ConnectionStringFor(world.Database, asApplicationRole: false));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT passport_number_encrypted FROM orders.order_travellers WHERE id = $1";
        command.Parameters.AddWithValue(traveller.Id);

        var stored = (byte[])(await command.ExecuteScalarAsync())!;

        Encoding.UTF8.GetString(stored).Should().NotContain(passport);
        stored.Should().NotEqual(Encoding.UTF8.GetBytes(passport));
    }


    // ------------------------------------------------------------------ the trail is append-only

    [Fact]
    public async Task The_status_trail_cannot_be_rewritten_even_by_the_owner()
    {
        await using var world = await WorldAsync();
        var order = await world.PlaceOrderAsync();

        var entryId = order.StatusHistory[0].Id;

        var edit = () => world.Owner.Database.ExecuteSqlRawAsync(
            "UPDATE orders.order_status_history SET to_status = 'Refunded' WHERE id = {0}", entryId);
        var erase = () => world.Owner.Database.ExecuteSqlRawAsync(
            "DELETE FROM orders.order_status_history WHERE id = {0}", entryId);

        (await edit.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(RestrictViolation);
        (await erase.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(RestrictViolation);
    }

    [Fact]
    public async Task Placing_an_order_writes_its_first_trail_entry()
    {
        await using var world = await WorldAsync();
        var order = await world.PlaceOrderAsync();

        var trail = await world.Db.OrderStatusHistory.Where(entry => entry.OrderId == order.Id).ToListAsync();

        trail.Should().ContainSingle();
        trail[0].FromStatus.Should().BeNull();
        trail[0].ToStatus.Should().Be(OrderStatus.PendingPayment);
    }

    [Fact]
    public async Task A_trail_entry_that_changes_nothing_is_refused()
    {
        await using var world = await WorldAsync();
        var order = await world.PlaceOrderAsync();

        var act = () => world.Owner.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO orders.order_status_history (id, agency_id, order_id, from_status, to_status, changed_at)
            VALUES (gen_random_uuid(), {0}, {1}, 'Paid', 'Paid', now())
            """,
            world.AgencyId, order.Id);

        (await act.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(CheckViolation);
    }

    // ------------------------------------------------------------------------------- the cart

    [Fact]
    public async Task A_cart_that_identifies_nobody_is_refused()
    {
        await using var world = await WorldAsync();

        var act = () => world.Owner.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO orders.carts (id, agency_id, customer_id, session_token, status, currency,
                                      expires_at, created_at, updated_at)
            VALUES (gen_random_uuid(), {0}, NULL, NULL, 'Active', 'NGN', now() + interval '7 days', now(), now())
            """,
            world.AgencyId);

        (await act.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(CheckViolation);
    }

    [Fact]
    public async Task A_converted_cart_has_to_name_the_order_it_became()
    {
        await using var world = await WorldAsync();

        var act = () => world.Owner.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO orders.carts (id, agency_id, session_token, status, currency,
                                      expires_at, created_at, updated_at)
            VALUES (gen_random_uuid(), {0}, 'sess-abc', 'Converted', 'NGN', now() + interval '7 days', now(), now())
            """,
            world.AgencyId);

        (await act.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(CheckViolation);
    }

    [Fact]
    public async Task Two_guests_at_the_same_agency_cannot_share_a_session_token()
    {
        await using var world = await WorldAsync();

        await world.OpenCartAsync("sess-abc");

        // EF wraps the driver's exception, so unwrap before asking PostgreSQL what it objected to.
        var again = await Record.ExceptionAsync(() => world.OpenCartAsync("sess-abc"));

        Innermost(again!).Should().BeOfType<PostgresException>()
            .Which.SqlState.Should().Be(UniqueViolation);
    }

    [Fact]
    public async Task A_cart_can_be_emptied_unlike_an_order()
    {
        // Carts are the one thing here the application may DELETE: a cart is not a record of
        // anything that happened, and somebody removing an item expects it to be gone.
        await using var world = await WorldAsync();
        var cart = await world.OpenCartAsync("sess-abc");

        var removed = await world.Db.Database.ExecuteSqlRawAsync(
            "DELETE FROM orders.carts WHERE id = {0}", cart.Id);

        removed.Should().Be(1);
    }

    private static Exception Innermost(Exception exception)
    {
        while (exception.InnerException is { } inner)
        {
            exception = inner;
        }

        return exception;
    }

    private async Task<World> WorldAsync()
    {
        // A fresh name every time, including for each case of a [Theory]. See RegistrationEndToEndTests.
        var database = $"orders_schema_{Guid.NewGuid():N}";

        Guid agencyId;
        await using (var setup = await _postgres.CreateEmptyDatabaseAsync(database))
        {
            await setup.Database.MigrateAsync();

            var agency = Agency.RegisterPrincipal("Lagos Travel Limited", "lagos-travel", "NG", "NGN", "Africa/Lagos");
            setup.Agencies.Add(agency);
            await setup.SaveChangesAsync();
            agencyId = agency.Id;
        }

        var tenancy = TestTenancy.For(agencyId);
        return new World(
            database,
            agencyId,
            _postgres.Connect(database, tenancy.Tenant, tenancy.Scope),
            _postgres.Connect(database, asApplicationRole: false));
    }

    private sealed class World : IAsyncDisposable
    {
        public World(string database, Guid agencyId, AppDbContext db, AppDbContext owner)
        {
            Database = database;
            AgencyId = agencyId;
            Db = db;
            Owner = owner;
        }

        public string Database { get; }

        public Guid AgencyId { get; }

        /// <summary>As the tenant, under the policed application role — how production connects.</summary>
        public AppDbContext Db { get; }

        /// <summary>The same database as its owner, for the tests that isolate one layer of protection.</summary>
        public AppDbContext Owner { get; }

        /// <summary>A rule, a quote and an order placed from it — the whole chain, through the domain.</summary>
        public async Task<Order> PlaceOrderAsync(string orderNumber = "ORD-2026-000001")
        {
            var now = DateTimeOffset.UtcNow;

            var rule = MarkupRule.Create(AgencyId, new MarkupRuleTerms
            {
                Scope = MarkupScope.Global,
                Currency = "NGN",
                CalculationType = MarkupCalculationType.Percentage,
                PercentBasisPoints = 1_000,
                EffectiveFrom = now.AddDays(-1),
            });
            Db.MarkupRules.Add(rule);
            await Db.SaveChangesAsync();

            var quote = PriceQuote.Record(
                AgencyId,
                new PricingSubject(PricedProductType.Flight, "NGN"),
                new PriceBreakdown(
                    new Money(100_000), new Money(10_000), new Money(750), new Money(500), new Money(110_750),
                    "NGN", new MarkupRuleDefinition(rule.Id, AgencyId, rule.Terms), false, 750, 0),
                now,
                TimeSpan.FromMinutes(30));
            Db.PriceQuotes.Add(quote);
            await Db.SaveChangesAsync();

            var line = OrderLine.FromQuote(quote, "LOS → ABV, Air Peace", """{"adults":1}""", now);
            var order = Order.Place(AgencyId, orderNumber, "NGN", BuyerType.AgentAssisted, OrderChannel.Console, null, [line], now);

            Db.Orders.Add(order);
            await Db.SaveChangesAsync();

            return order;
        }

        /// <summary>A line written past the domain, for the constraints the domain would never reach.</summary>
        public async Task<Guid> AddRawLineAsync(Order order, bool placed, long gross = 110_750)
        {
            var id = Guid.CreateVersion7();

            await Owner.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO orders.order_lines
                    (id, order_id, agency_id, item_type, price_quote_id, title_snapshot, pax_breakdown,
                     currency, net_amount_minor, markup_amount_minor, tax_amount_minor, platform_fee_minor,
                     gross_amount_minor, markup_rule_id, fulfilment_status, placed_at, created_at, updated_at)
                SELECT {0}, {1}, agency_id, item_type, price_quote_id, title_snapshot, pax_breakdown,
                       currency, net_amount_minor, markup_amount_minor, tax_amount_minor, platform_fee_minor,
                       {2}, markup_rule_id, 'Pending', CASE WHEN {3} THEN now() END, now(), now()
                  FROM orders.order_lines WHERE id = {4}
                """,
                id, order.Id, gross, placed, order.Lines[0].Id);

            return id;
        }

        /// <summary>A guest cart, through the domain, as the tenant.</summary>
        public async Task<Cart> OpenCartAsync(string sessionToken)
        {
            var cart = Cart.Open(AgencyId, "NGN", DateTimeOffset.UtcNow, TimeSpan.FromDays(7), sessionToken: sessionToken);
            Db.Carts.Add(cart);
            await Db.SaveChangesAsync();
            return cart;
        }

        public async Task<Guid> AddAgencyAsync(string name, string slug)
        {
            var agency = Agency.RegisterPrincipal(name, slug, "NG", "NGN", "Africa/Lagos");
            Owner.Agencies.Add(agency);
            await Owner.SaveChangesAsync();
            return agency.Id;
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await Owner.DisposeAsync();
        }
    }
}
