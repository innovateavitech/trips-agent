using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using TripsAgent.Application.Catalog;
using TripsAgent.Application.Messaging;
using TripsAgent.Application.Notifications;
using TripsAgent.Domain.Catalog;
using TripsAgent.Domain.Common;
using TripsAgent.Domain.Orders;
using TripsAgent.Domain.Tenancy;
using TripsAgent.Infrastructure.Persistence;
using TripsAgent.IntegrationTests.Persistence;

namespace TripsAgent.IntegrationTests.Catalog;

/// <summary>
/// The acceptance criterion for issue #57: <b>parallel reservations never oversell a departure.</b>
/// </summary>
/// <remarks>
/// <para>
/// Against a real PostgreSQL, because this claim is about PostgreSQL. Seats are taken with one
/// UPDATE whose WHERE re-checks the capacity; under READ COMMITTED the server re-evaluates that
/// WHERE against the row as the previous transaction left it, so of two checkouts racing for the
/// last seat exactly one updates a row.
/// </para>
/// <para>
/// Behind that sits <c>ck_departures_no_oversell</c>, proved separately here with raw SQL — because
/// the guarantee has to hold for code nobody has written yet, including a hand-typed UPDATE.
/// </para>
/// </remarks>
[Collection(PostgresCollection.Name)]
public sealed class DepartureConcurrencyTests : IAsyncLifetime
{
    private const string CheckViolation = "23514";
    private const int Capacity = 20;

    private readonly PostgresFixture _postgres;

    private string _database = string.Empty;
    private Guid _agencyId;
    private Guid _departureId;
    private Guid[] _carts = [];

    public DepartureConcurrencyTests(PostgresFixture postgres) => _postgres = postgres;

    public async Task InitializeAsync()
    {
        _database = $"departures_race_{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;

        await using var setup = await _postgres.CreateEmptyDatabaseAsync(_database);
        await setup.Database.MigrateAsync();

        var agency = Agency.RegisterPrincipal("Lagos Travel Limited", "lagos-travel", "NG", "NGN", "Africa/Lagos");
        var product = Product.CreateDraft(agency.Id, Tour(), "kilimanjaro");

        var departure = Departure.Create(
            agency.Id,
            product.Id,
            new DepartureTerms
            {
                DepartureDate = DateOnly.FromDateTime(now.UtcDateTime).AddDays(180),
                IsGroupDeparture = true,
                MinPax = 4,
                CapacityTotal = Capacity,
                CutoffDaysBefore = 14,
                DepositType = DepositType.None,
                PriceTiers = [new PriceTierTerms(1, null, new Money(100_000))],
            },
            now.AddDays(166),
            now);

        // A hold belongs to a cart — the checkout it is part of — so each racing caller needs one
        // of its own. Without it the foreign key, not the capacity, is what refuses the second hold.
        var carts = Enumerable.Range(0, 64)
            .Select(index => Cart.Open(
                agency.Id, "NGN", now, TimeSpan.FromHours(2), sessionToken: $"race-{index}"))
            .ToList();

        setup.Agencies.Add(agency);
        setup.Products.Add(product);
        setup.Departures.Add(departure);
        setup.Carts.AddRange(carts);
        await setup.SaveChangesAsync();

        (_agencyId, _departureId) = (agency.Id, departure.Id);
        _carts = [.. carts.Select(cart => cart.Id)];
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Forty_checkouts_racing_for_twenty_seats_take_twenty_and_no_more()
    {
        var outcomes = await Task.WhenAll(
            Enumerable.Range(0, 40).Select(index => Task.Run(async () =>
            {
                await using var world = Seats();
                return await world.Seats.HoldAsync(_departureId, _carts[index], 1);
            })));

        outcomes.OfType<SeatHoldOutcome.Held>().Should().HaveCount(Capacity);
        outcomes.OfType<SeatHoldOutcome.NoSeats>().Should().HaveCount(40 - Capacity);

        var seats = await SeatCountAsync();
        seats.CapacityReserved.Should().Be(Capacity);
        seats.SeatsLeft.Should().Be(0);

        await using var reader = Seats();
        (await reader.Db.DepartureHolds.CountAsync(hold => hold.Status == DepartureHoldStatus.Held))
            .Should().Be(Capacity, "a hold exists only for a checkout that actually got its seats");

        (await reader.Db.Departures.Select(departure => departure.Status).SingleAsync())
            .Should().Be(DepartureStatus.SoldOut, "the status follows the seats — plan §3 job 9");
    }

    [Fact]
    public async Task Parties_racing_for_the_last_seats_never_take_more_than_are_left()
    {
        // Fifteen parties of three for twenty seats: six fit, and whichever six they are, the
        // eighteen seats they take can never become twenty-one.
        var outcomes = await Task.WhenAll(
            Enumerable.Range(0, 15).Select(index => Task.Run(async () =>
            {
                await using var world = Seats();
                return await world.Seats.HoldAsync(_departureId, _carts[index], 3);
            })));

        outcomes.OfType<SeatHoldOutcome.Held>().Should().HaveCount(6);

        var seats = await SeatCountAsync();
        seats.Taken.Should().Be(18);
        seats.Taken.Should().BeLessThanOrEqualTo(Capacity);
    }

    [Fact]
    public async Task A_released_hold_puts_its_seats_straight_back_on_sale()
    {
        await using var world = Seats();

        var held = (SeatHoldOutcome.Held)await world.Seats.HoldAsync(_departureId, _carts[0], Capacity);
        held.Status.Should().Be(DepartureStatus.SoldOut);
        (await world.Seats.HoldAsync(_departureId, _carts[1], 1)).Should().BeOfType<SeatHoldOutcome.NoSeats>();

        (await world.Seats.ReleaseAsync(held.Hold.Id)).Should().BeTrue();
        (await world.Seats.ReleaseAsync(held.Hold.Id)).Should().BeFalse("releasing is idempotent");

        var seats = await SeatCountAsync();
        seats.CapacityReserved.Should().Be(0);
        seats.SeatsLeft.Should().Be(Capacity);
    }

    [Fact]
    public async Task Converting_a_hold_moves_its_seats_from_reserved_to_confirmed_and_guarantees_the_departure()
    {
        await using var world = Seats();

        var held = (SeatHoldOutcome.Held)await world.Seats.HoldAsync(_departureId, _carts[0], 4);
        held.Status.Should().Be(DepartureStatus.Open, "holding a seat is not paying for it");

        (await world.Seats.ConfirmAsync(held.Hold.Id)).Should().BeTrue();
        (await world.Seats.ConfirmAsync(held.Hold.Id)).Should().BeFalse("converting is idempotent");

        var seats = await SeatCountAsync();
        seats.CapacityReserved.Should().Be(0);
        seats.CapacityConfirmed.Should().Be(4);

        await using var reader = Seats();
        (await reader.Db.Departures.Select(departure => departure.Status).SingleAsync())
            .Should().Be(DepartureStatus.Guaranteed, "the minimum party has paid");
    }

    [Fact]
    public async Task The_database_itself_refuses_an_oversell_however_it_is_written()
    {
        // Straight past the application, as the schema owner: no query filter, no guard in a WHERE,
        // nothing but the CHECK. This is the guarantee the acceptance criterion asks for.
        await using var owner = _postgres.Connect(_database, asApplicationRole: false);

        var act = () => owner.Database.ExecuteSqlRawAsync(
            "UPDATE catalog.departures SET capacity_reserved = {0} WHERE id = {1}", Capacity + 1, _departureId);

        (await act.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be(CheckViolation);

        var split = () => owner.Database.ExecuteSqlRawAsync(
            "UPDATE catalog.departures SET capacity_reserved = {0}, capacity_confirmed = {1} WHERE id = {2}",
            Capacity - 1,
            2,
            _departureId);

        (await split.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be(CheckViolation, "reserved + confirmed is what is bounded, not either alone");
    }

    [Fact]
    public async Task A_closed_or_cancelled_departure_sells_nothing()
    {
        await using var world = Seats();

        await world.Db.Departures.ExecuteUpdateAsync(setters =>
            setters.SetProperty(departure => departure.Status, DepartureStatus.Closed));

        (await world.Seats.HoldAsync(_departureId, _carts[0], 1))
            .Should().BeOfType<SeatHoldOutcome.NotSellable>();

        (await SeatCountAsync()).Taken.Should().Be(0);
    }

    [Fact]
    public async Task Another_agencys_departure_cannot_be_sold_and_does_not_appear_to_exist()
    {
        await using var world = Seats(Guid.CreateVersion7());

        (await world.Seats.HoldAsync(_departureId, _carts[0], 1))
            .Should().BeOfType<SeatHoldOutcome.NotFound>();

        (await SeatCountAsync()).Taken.Should().Be(0);
    }

    private async Task<SeatCount> SeatCountAsync()
    {
        await using var world = Seats();

        return await world.Db.Departures
            .Where(departure => departure.Id == _departureId)
            .Select(departure => new SeatCount(
                departure.CapacityTotal, departure.CapacityReserved, departure.CapacityConfirmed))
            .SingleAsync();
    }

    /// <summary>One caller's own unit of work, exactly as a request or a job would have.</summary>
    private SeatWorld Seats(Guid? agencyId = null)
    {
        var (tenant, scope) = TestTenancy.For(agencyId ?? _agencyId);
        var db = _postgres.Connect(_database, tenant, scope);
        var clock = TimeProvider.System;

        var waitlist = new DepartureWaitlistService(
            db, scope, new Notifier(db, new NullOutbox()), clock, NullLogger<DepartureWaitlistService>.Instance);

        return new SeatWorld(db, new DepartureSeats(db, new EfTransactionRunner(db), waitlist, clock));
    }

    private static ProductContent Tour() => new()
    {
        ProductType = ProductType.Tour,
        Title = "Kilimanjaro Trek",
        Summary = "Eight days on the Machame route.",
        Description = "Eight days on the Machame route, with a guide and a porter for every two climbers.",
        Currency = "NGN",
        BasePriceMinor = new Money(100_000),
    };

    private sealed record SeatWorld(AppDbContext Db, DepartureSeats Seats) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    /// <summary>Messages go nowhere here: nothing in this file asserts on what was published.</summary>
    private sealed class NullOutbox : IOutbox
    {
        public void Enqueue<TMessage>(TMessage message, Guid? agencyId = null)
            where TMessage : class
        {
        }
    }
}
