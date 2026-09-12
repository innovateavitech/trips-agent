using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using TripsAgent.Application.Catalog;
using TripsAgent.Application.Messaging;
using TripsAgent.Application.Notifications;
using TripsAgent.Application.Tenancy;
using TripsAgent.Domain.Catalog;
using TripsAgent.Domain.Common;
using TripsAgent.Domain.Identity;
using TripsAgent.Domain.Orders;
using TripsAgent.Domain.Pricing;
using TripsAgent.Domain.Tenancy;
using TripsAgent.Infrastructure.Persistence;
using TripsAgent.IntegrationTests.Identity;
using TripsAgent.IntegrationTests.Persistence;

namespace TripsAgent.IntegrationTests.Catalog;

/// <summary>
/// The clock's share of a departure's life (plan §3 jobs 6, 9, 10 and 11): the checkout that walked
/// away, the offer nobody answered, the payment nobody made, and the nightly proof that every status
/// still matches its seats.
/// </summary>
/// <remarks>
/// Each job runs across agencies, so each opens a platform scope. Run here against a real
/// PostgreSQL as the policed application role, because the scope and the policies are the thing.
/// </remarks>
[Collection(PostgresCollection.Name)]
public sealed class DepartureJobTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Start = new(2026, 3, 1, 9, 0, 0, TimeSpan.Zero);

    private readonly PostgresFixture _postgres;
    private readonly ManualClock _clock = new(Start);

    private string _database = string.Empty;
    private Guid _agencyId;
    private Guid _productId;
    private Guid _departureId;
    private Guid[] _carts = [];

    public DepartureJobTests(PostgresFixture postgres) => _postgres = postgres;

    public async Task InitializeAsync()
    {
        _database = $"departure_jobs_{Guid.NewGuid():N}";

        await using var setup = await _postgres.CreateEmptyDatabaseAsync(_database);
        await setup.Database.MigrateAsync();

        var agency = Agency.RegisterPrincipal("Lagos Travel Limited", "lagos-travel", "NG", "NGN", "Africa/Lagos");
        var owner = User.ForAgency(agency.Id, "owner@lagos-travel.test", "not-a-real-hash", "Ada", "Obi");
        var product = Product.CreateDraft(agency.Id, Tour(), "kilimanjaro");

        var departure = Departure.Create(
            agency.Id,
            product.Id,
            new DepartureTerms
            {
                DepartureDate = DateOnly.FromDateTime(Start.UtcDateTime).AddDays(180),
                IsGroupDeparture = true,
                MinPax = 2,
                CapacityTotal = 4,
                CutoffDaysBefore = 14,
                DepositType = DepositType.None,
                PriceTiers = [new PriceTierTerms(1, null, new Money(100_000))],
            },
            Start.AddDays(166),
            Start);

        var carts = Enumerable.Range(0, 4)
            .Select(index => Cart.Open(agency.Id, "NGN", Start, TimeSpan.FromHours(2), sessionToken: $"job-{index}"))
            .ToList();

        setup.Agencies.Add(agency);
        setup.Users.Add(owner);
        setup.Products.Add(product);
        setup.Departures.Add(departure);
        setup.Carts.AddRange(carts);
        await setup.SaveChangesAsync();

        (_agencyId, _productId, _departureId) = (agency.Id, product.Id, departure.Id);
        _carts = [.. carts.Select(cart => cart.Id)];
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ------------------------------------------------------------------ job 6

    [Fact]
    public async Task A_hold_whose_time_ran_out_gives_its_seats_back_and_the_next_person_waiting_is_offered_one()
    {
        await using (var world = World())
        {
            (await world.Seats.HoldAsync(_departureId, _carts[0], 4, TimeSpan.FromMinutes(20)))
                .Should().BeOfType<SeatHoldOutcome.Held>();

            await world.Waitlist.OfferFreedSeatsAsync(_departureId);
            await Join(world, "Ada Obi", "ada@example.test", 2);
        }

        // Sold out, so nobody is offered anything yet.
        await using (var before = World())
        {
            (await before.Db.DepartureWaitlist.SingleAsync()).Status.Should().Be(WaitlistStatus.Waiting);
            (await before.Db.Departures.Select(departure => departure.Status).SingleAsync())
                .Should().Be(DepartureStatus.SoldOut);
        }

        _clock.Advance(TimeSpan.FromMinutes(21));

        await using (var world = World())
        {
            var run = await world.Holds.RunAsync();

            run.Released.Should().Be(1);
            run.SeatsReturned.Should().Be(4);
        }

        await using var after = World();

        var seats = await after.Db.Departures
            .Select(departure => new { departure.CapacityReserved, departure.Status })
            .SingleAsync();

        seats.CapacityReserved.Should().Be(0);
        seats.Status.Should().Be(DepartureStatus.Open, "the seats are back and nobody has paid");

        var entry = await after.Db.DepartureWaitlist.SingleAsync();
        entry.Status.Should().Be(WaitlistStatus.Offered);
        entry.ExpiresAt.Should().Be(_clock.GetUtcNow() + DepartureWaitlistService.DefaultOfferTimeToLive);

        (await after.Db.Notifications.CountAsync(
            notification => notification.TemplateKey == NotificationTemplateCatalog.DepartureWaitlistOffer))
            .Should().Be(1);
    }

    [Fact]
    public async Task A_hold_that_is_still_good_is_left_alone()
    {
        await using (var world = World())
        {
            await world.Seats.HoldAsync(_departureId, _carts[0], 2, TimeSpan.FromMinutes(20));
        }

        _clock.Advance(TimeSpan.FromMinutes(19));

        await using var after = World();
        (await after.Holds.RunAsync()).Released.Should().Be(0);
        (await after.Db.Departures.Select(departure => departure.CapacityReserved).SingleAsync()).Should().Be(2);
    }

    // ------------------------------------------------------------------ job 10

    [Fact]
    public async Task An_offer_nobody_answers_expires_and_rolls_on_to_the_next_person()
    {
        await using (var world = World())
        {
            await Join(world, "Ada Obi", "ada@example.test", 4);
            await Join(world, "Bola Ade", "bola@example.test", 4);

            // Four seats free, so the first in the queue is offered them and the second waits.
            (await world.Waitlist.OfferFreedSeatsAsync(_departureId)).Should().Be(1);
        }

        await using (var before = World())
        {
            var queue = await before.Db.DepartureWaitlist.OrderBy(entry => entry.JoinedAt).ToListAsync();
            queue[0].Status.Should().Be(WaitlistStatus.Offered);
            queue[1].Status.Should().Be(WaitlistStatus.Waiting, "the same four seats cannot be offered twice");
        }

        _clock.Advance(DepartureWaitlistService.DefaultOfferTimeToLive + TimeSpan.FromMinutes(1));

        await using var after = World();
        var run = await after.Waitlist.SweepAsync();

        run.Expired.Should().Be(1);
        run.Offered.Should().Be(1);

        var rolled = await after.Db.DepartureWaitlist.OrderBy(entry => entry.JoinedAt).ToListAsync();
        rolled[0].Status.Should().Be(WaitlistStatus.Expired);
        rolled[1].Status.Should().Be(WaitlistStatus.Offered);

        (await after.Db.Notifications.CountAsync(
            notification => notification.TemplateKey == NotificationTemplateCatalog.DepartureWaitlistOffer))
            .Should().Be(2, "each person is emailed when their turn comes");
    }

    [Fact]
    public async Task Nothing_is_offered_on_a_departure_that_has_been_closed()
    {
        await using (var world = World())
        {
            await Join(world, "Ada Obi", "ada@example.test", 2);
            await world.Db.Departures.ExecuteUpdateAsync(setters =>
                setters.SetProperty(departure => departure.Status, DepartureStatus.Closed));
        }

        await using var after = World();

        (await after.Waitlist.OfferFreedSeatsAsync(_departureId)).Should().Be(0);
        (await after.Db.DepartureWaitlist.SingleAsync()).Status.Should().Be(WaitlistStatus.Waiting);
    }

    // ------------------------------------------------------------------ job 9

    [Fact]
    public async Task The_nightly_sweep_puts_a_status_that_drifted_back_in_line_with_its_seats()
    {
        await using (var world = World())
        {
            // Straight to the column, as an interrupted seat move would leave it: the seats say sold
            // out and the status still says open.
            await world.Db.Departures.ExecuteUpdateAsync(setters =>
                setters.SetProperty(departure => departure.CapacityConfirmed, 4));
        }

        await using var after = World();
        var run = await after.Sweep.RunAsync();

        run.Checked.Should().Be(1);
        run.Changed.Should().Be(1);
        (await after.Db.Departures.Select(departure => departure.Status).SingleAsync())
            .Should().Be(DepartureStatus.SoldOut);

        (await after.Sweep.RunAsync()).Changed.Should().Be(0, "there is nothing left to put right");
    }

    [Fact]
    public async Task The_sweep_never_touches_a_departure_the_agent_has_closed()
    {
        await using (var world = World())
        {
            await world.Db.Departures.ExecuteUpdateAsync(setters => setters
                .SetProperty(departure => departure.CapacityConfirmed, 4)
                .SetProperty(departure => departure.Status, DepartureStatus.Closed));
        }

        await using var after = World();

        (await after.Sweep.RunAsync()).Checked.Should().Be(0);
        (await after.Db.Departures.Select(departure => departure.Status).SingleAsync())
            .Should().Be(DepartureStatus.Closed);
    }

    // ------------------------------------------------------------------ job 11

    [Fact]
    public async Task Each_reminder_goes_out_once_and_the_agency_hears_about_it_a_week_after_it_was_due()
    {
        var due = DateOnly.FromDateTime(Start.UtcDateTime).AddDays(10);

        await using (var world = World())
        {
            await ScheduleAsync(world, due);
        }

        // Eleven days out: nothing is due within the week, so nothing is sent.
        await Run(0, 0);

        // T-7, T-3, T-1 and overdue, each once however many times the job runs that day.
        _clock.Advance(TimeSpan.FromDays(3));
        await Run(1, 0);
        await Run(0, 0);

        _clock.Advance(TimeSpan.FromDays(4));
        await Run(1, 0);

        _clock.Advance(TimeSpan.FromDays(2));
        await Run(1, 0);

        _clock.Advance(TimeSpan.FromDays(2));
        await Run(1, 0);
        await Run(0, 0);

        await using (var traveller = World())
        {
            (await traveller.Db.Notifications.CountAsync(
                notification => notification.TemplateKey == NotificationTemplateCatalog.DepartureInstallmentReminder))
                .Should().Be(4);
        }

        // A week past its due date, and nobody has paid: the agency is told, once, with what to do.
        _clock.Advance(TimeSpan.FromDays(6));
        await Run(0, 1);
        await Run(0, 0);

        await using var after = World();

        (await after.Db.Notifications.CountAsync(
            notification => notification.TemplateKey == NotificationTemplateCatalog.DepartureInstallmentOverdue))
            .Should().Be(1);

        var item = await after.Db.BookingInstallments.SingleAsync();
        item.State.Should().Be(InstallmentState.Pending, "decision 13: a booking is never cancelled automatically");
        item.FlaggedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task A_payment_that_has_been_settled_is_never_reminded_about_again()
    {
        var due = DateOnly.FromDateTime(Start.UtcDateTime).AddDays(2);

        await using (var world = World())
        {
            var schedule = await ScheduleAsync(world, due);
            (await world.Installments.MarkPaidAsync(schedule.Items[0].Id)).Should().BeTrue();
        }

        await Run(0, 0);

        _clock.Advance(TimeSpan.FromDays(30));
        await Run(0, 0);

        await using var after = World();
        (await after.Db.Notifications.CountAsync()).Should().Be(0);
    }

    // ------------------------------------------------------------------ helpers

    private async Task Run(int reminded, int flagged)
    {
        await using var world = World();
        var run = await world.Reminders.RunAsync();

        run.Reminded.Should().Be(reminded, $"on {_clock.GetUtcNow():yyyy-MM-dd}");
        run.Flagged.Should().Be(flagged, $"on {_clock.GetUtcNow():yyyy-MM-dd}");
    }

    /// <summary>A booking of two seats whose whole price falls due on <paramref name="due"/>.</summary>
    private async Task<BookingPaymentSchedule> ScheduleAsync(JobWorld world, DateOnly due)
    {
        var lineId = await BookAsync(world);

        await world.Db.Departures
            .Where(departure => departure.Id == _departureId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(
                departure => departure.CutoffDaysBefore,
                DateOnly.FromDateTime(Start.UtcDateTime).AddDays(180).DayNumber - due.DayNumber));

        var outcome = await world.Installments.ScheduleAsync(
            _departureId, lineId, 2, "Ada Obi", "ada@example.test", DateOnly.FromDateTime(Start.UtcDateTime));

        var schedule = outcome.Should().BeOfType<InstallmentScheduleOutcome.Scheduled>().Subject.Schedule;
        schedule.Items.Should().ContainSingle().Which.DueDate.Should().Be(due);

        return schedule;
    }

    private async Task<Guid> BookAsync(JobWorld world)
    {
        var now = _clock.GetUtcNow();

        var rule = MarkupRule.Create(_agencyId, new MarkupRuleTerms
        {
            Scope = MarkupScope.Global,
            Currency = "NGN",
            CalculationType = MarkupCalculationType.Percentage,
            PercentBasisPoints = 1_000,
            EffectiveFrom = now.AddDays(-1),
        });
        world.Db.MarkupRules.Add(rule);
        await world.Db.SaveChangesAsync();

        var quote = PriceQuote.Record(
            _agencyId,
            new PricingSubject(PricedProductType.GroupDeparture, "NGN"),
            new PriceBreakdown(
                new Money(200_000), new Money(20_000), new Money(1_500), new Money(1_000), new Money(221_500),
                "NGN", new MarkupRuleDefinition(rule.Id, _agencyId, rule.Terms), false, 1_500, 0),
            now,
            TimeSpan.FromMinutes(30));
        world.Db.PriceQuotes.Add(quote);
        await world.Db.SaveChangesAsync();

        var line = OrderLine.FromQuote(quote, "Kilimanjaro Trek", """{"adults":2}""", now);
        var order = Order.Place(
            _agencyId, "ORD-2026-000001", "NGN", BuyerType.AgentAssisted, OrderChannel.Console, null, [line], now);

        world.Db.Orders.Add(order);
        await world.Db.SaveChangesAsync();

        return line.Id;
    }

    private static async Task Join(JobWorld world, string name, string email, int paxCount)
    {
        world.Db.DepartureWaitlist.Add(DepartureWaitlistEntry.Join(
            world.AgencyId, world.DepartureId, name, email, paxCount, world.Clock.GetUtcNow()));

        await world.Db.SaveChangesAsync();

        // Version 7 ids order by when they were made, and so do the joined_at instants a manual
        // clock hands out — which are all the same. Nudge it so the queue has an order.
        world.Clock.Advance(TimeSpan.FromSeconds(1));
    }

    /// <summary>One caller's own unit of work, with the departure services it would be given.</summary>
    private JobWorld World()
    {
        var (tenant, scope) = TestTenancy.For(_agencyId);
        var db = _postgres.Connect(_database, tenant, scope, _clock);
        var notifier = new Notifier(db, new NullOutbox());

        var waitlist = new DepartureWaitlistService(
            db, scope, notifier, _clock, NullLogger<DepartureWaitlistService>.Instance);

        var seats = new DepartureSeats(db, new EfTransactionRunner(db), waitlist, _clock);

        return new JobWorld(
            db,
            _agencyId,
            _departureId,
            _clock,
            seats,
            waitlist,
            new DepartureHoldExpiry(db, seats, scope, _clock, NullLogger<DepartureHoldExpiry>.Instance),
            new DepartureStatusSweep(db, scope, _clock, NullLogger<DepartureStatusSweep>.Instance),
            new DepartureInstallments(db, _clock),
            new InstallmentReminders(db, scope, notifier, _clock, NullLogger<InstallmentReminders>.Instance));
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

    private sealed record JobWorld(
        AppDbContext Db,
        Guid AgencyId,
        Guid DepartureId,
        ManualClock Clock,
        DepartureSeats Seats,
        DepartureWaitlistService Waitlist,
        DepartureHoldExpiry Holds,
        DepartureStatusSweep Sweep,
        DepartureInstallments Installments,
        InstallmentReminders Reminders) : IAsyncDisposable
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
