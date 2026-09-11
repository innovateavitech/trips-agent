using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using TripsAgent.Application.Notifications;
using TripsAgent.Application.Suppliers;
using TripsAgent.Domain.Notifications;
using TripsAgent.Domain.Orders;
using TripsAgent.Domain.Payments;
using TripsAgent.Domain.Suppliers;
using TripsAgent.IntegrationTests.Persistence;

namespace TripsAgent.IntegrationTests.Suppliers;

/// <summary>
/// The ticket time limit monitor against real PostgreSQL (#38): warnings at T-60 and T-15, and the
/// booking lapsing at the limit — exactly once, however many times or however many Workers run it.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class TicketTimeLimitMonitorTests : IAsyncLifetime
{
    private readonly PostgresFixture _postgres;
    private TripsAfricaStub _stub = null!;

    public TicketTimeLimitMonitorTests(PostgresFixture postgres) => _postgres = postgres;

    public async Task InitializeAsync() => _stub = await TripsAfricaStub.StartAsync();

    public async Task DisposeAsync() => await _stub.DisposeAsync();

    // ------------------------------------------------------------------------------ warnings

    [Fact]
    public async Task The_agent_is_warned_an_hour_and_a_quarter_hour_before_the_limit_once_each()
    {
        await using var harness = await BookingPipelineHarness.CreateAsync(_postgres, _stub);
        var booking = await harness.SeedConfirmedBookingAsync(ticketTimeLimitIn: TimeSpan.FromMinutes(50));

        var first = await RunThriceAsync(harness);
        harness.Clock.Advance(TimeSpan.FromMinutes(36));
        var second = await RunThriceAsync(harness);

        first.Sum(run => run.Warned).Should().Be(1);
        second.Sum(run => run.Warned).Should().Be(1);

        var warnings = await NotificationsAsync(harness, NotificationTemplateCatalog.BookingTimeLimitWarning);
        warnings.Select(warning => warning.DedupeKey).Should().BeEquivalentTo(
        [
            $"{NotificationTemplateCatalog.BookingTimeLimitWarning}:60:{booking.SupplierBookingId}",
            $"{NotificationTemplateCatalog.BookingTimeLimitWarning}:15:{booking.SupplierBookingId}",
        ]);
        warnings.Should().OnlyContain(warning => warning.RecipientAddress == "ada@lagos-travel.test", "the agency's owner is told");

        var hourWarning = Notifier.ReadPayload(warnings.Single(warning => warning.DedupeKey.Contains(":60:")).Payload);
        hourWarning["bookingReference"].Should().Be(booking.OrderNumber);
        hourWarning["minutesLeft"].Should().Be("50");
        hourWarning["deadline"].Should().Be("11 Sep 2026, 10:50 (Africa/Lagos)", "on the agency's own clock");

        var stored = await harness.BookingAsync(booking);
        stored.SixtyMinuteWarningSentAt.Should().Be(BookingPipelineHarness.Start);
        stored.FifteenMinuteWarningSentAt.Should().Be(BookingPipelineHarness.Start.AddMinutes(36));
        stored.Status.Should().Be(SupplierBookingStatus.PriceConfirmed, "a warning changes nothing but the record that it was sent");
    }

    [Fact]
    public async Task A_booking_confirmed_close_to_its_limit_gets_only_the_quarter_hour_warning()
    {
        await using var harness = await BookingPipelineHarness.CreateAsync(_postgres, _stub);
        var booking = await harness.SeedConfirmedBookingAsync(ticketTimeLimitIn: TimeSpan.FromMinutes(10));

        (await harness.MonitorAsync()).Warned.Should().Be(1);

        (await NotificationsAsync(harness, NotificationTemplateCatalog.BookingTimeLimitWarning))
            .Should().ContainSingle().Which.DedupeKey.Should().Contain(":15:");
        (await harness.BookingAsync(booking)).SixtyMinuteWarningSentAt.Should().BeNull();
    }

    // ------------------------------------------------------------------------------- expiry

    [Fact]
    public async Task At_the_limit_the_booking_lapses_exactly_once_however_many_times_the_job_runs()
    {
        await using var harness = await BookingPipelineHarness.CreateAsync(_postgres, _stub);
        var booking = await harness.SeedConfirmedBookingAsync(ticketTimeLimitIn: TimeSpan.FromMinutes(10), paidFromWallet: true);

        harness.Clock.Advance(TimeSpan.FromMinutes(10));
        var runs = await RunThriceAsync(harness);

        runs.Select(run => run.Expired).Should().Equal(1, 0, 0);

        var stored = await harness.BookingAsync(booking);
        stored.Status.Should().Be(SupplierBookingStatus.Expired);
        stored.FailureReason.Should().Contain("ticket time limit");

        await using var db = harness.AsAgency();

        var line = await db.OrderLines.AsNoTracking().SingleAsync(candidate => candidate.Id == booking.OrderLineId);
        line.FulfilmentStatus.Should().Be(FulfilmentStatus.FailedNeedsResolution, "it goes to the agent's resolution queue");
        line.ResolutionStatus.Should().Be(ResolutionStatus.Open);
        line.FailureReason.Should().NotBeNullOrWhiteSpace();

        var hold = await db.WalletHolds.AsNoTracking().SingleAsync(candidate => candidate.OrderId == booking.OrderId);
        hold.Status.Should().Be(WalletHoldStatus.Released);
        (await db.Wallets.AsNoTracking().SingleAsync(wallet => wallet.Id == harness.WalletId)).ReservedMinor.AmountMinor.Should().Be(0);

        (await NotificationsAsync(harness, NotificationTemplateCatalog.BookingExpired)).Should().ContainSingle();
        (await harness.OutboxPayloadsAsync<SupplierBookingExpired>()).Should().ContainSingle();
    }

    [Fact]
    public async Task Three_monitors_running_at_once_still_lapse_each_booking_exactly_once()
    {
        await using var harness = await BookingPipelineHarness.CreateAsync(_postgres, _stub);
        var bookings = new List<SeededBooking>();

        for (var index = 0; index < 5; index++)
        {
            bookings.Add(await harness.SeedConfirmedBookingAsync(ticketTimeLimitIn: TimeSpan.FromMinutes(10), paidFromWallet: true));
        }

        harness.Clock.Advance(TimeSpan.FromMinutes(11));

        var together = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => Task.Run(harness.MonitorAsync)));
        var expired = together.Sum(run => run.Expired);

        // All five share one wallet, so a monitor that loses a race on it stops its run and leaves the
        // rest for the next minute's. Keep running until there is nothing left, as the schedule would.
        for (var run = 0; run < 10 && expired < 5; run++)
        {
            expired += (await harness.MonitorAsync()).Expired;
        }

        expired.Should().Be(5);
        (await NotificationsAsync(harness, NotificationTemplateCatalog.BookingExpired)).Should().HaveCount(5);
        (await harness.OutboxPayloadsAsync<SupplierBookingExpired>()).Should().HaveCount(5);

        foreach (var booking in bookings)
        {
            (await harness.BookingAsync(booking)).Status.Should().Be(SupplierBookingStatus.Expired);
        }

        await using var db = harness.AsAgency();
        (await db.Wallets.AsNoTracking().SingleAsync(wallet => wallet.Id == harness.WalletId)).ReservedMinor.AmountMinor.Should().Be(0);
    }

    [Fact]
    public async Task A_booking_whose_confirmation_was_consumed_is_never_expired()
    {
        // Issuing, pending or ticketed: a real ticket may exist, and only the supplier can say otherwise.
        await using var harness = await BookingPipelineHarness.CreateAsync(_postgres, _stub);
        var limit = TimeSpan.FromMinutes(10);

        var issuing = await harness.SeedConfirmedBookingAsync(ticketTimeLimitIn: limit, paidFromWallet: true);
        var pending = await harness.SeedConfirmedBookingAsync(ticketTimeLimitIn: limit, paidFromWallet: true);
        var ticketed = await harness.SeedConfirmedBookingAsync(ticketTimeLimitIn: limit, paidFromWallet: true);

        await TransitionAsync(harness, issuing, (booking, at) => booking.BeginIssue(at));
        await TransitionAsync(harness, pending, (booking, at) =>
        {
            booking.BeginIssue(at);
            booking.RecordIssuePending("RE6MIK", 3, at);
        });
        await TransitionAsync(harness, ticketed, (booking, at) =>
        {
            booking.BeginIssue(at);
            booking.RecordIssueTicketed("RE6MIK", 2, at);
        });

        harness.Clock.Advance(limit + TimeSpan.FromMinutes(1));

        (await harness.MonitorAsync()).Expired.Should().Be(0);

        (await harness.BookingAsync(issuing)).Status.Should().Be(SupplierBookingStatus.Issuing);
        (await harness.BookingAsync(pending)).Status.Should().Be(SupplierBookingStatus.TicketPending);
        (await harness.BookingAsync(ticketed)).Status.Should().Be(SupplierBookingStatus.Ticketed);
        (await NotificationsAsync(harness, NotificationTemplateCatalog.BookingExpired)).Should().BeEmpty();

        await using var db = harness.AsAgency();
        (await db.WalletHolds.AsNoTracking().CountAsync(hold => hold.Status == WalletHoldStatus.Held))
            .Should().Be(3, "nobody's money was released out from under a ticket");
    }

    // ---------------------------------------------------------------------------- building

    private static async Task<TicketTimeLimitRun[]> RunThriceAsync(BookingPipelineHarness harness) =>
    [
        await harness.MonitorAsync(),
        await harness.MonitorAsync(),
        await harness.MonitorAsync(),
    ];

    private static async Task<List<Notification>> NotificationsAsync(BookingPipelineHarness harness, string templateKey)
    {
        await using var db = harness.AsAgency();
        return await db.Notifications.AsNoTracking().Where(notification => notification.TemplateKey == templateKey).ToListAsync();
    }

    private static async Task TransitionAsync(BookingPipelineHarness harness, SeededBooking seeded, Action<SupplierBooking, DateTimeOffset> change)
    {
        await using var db = harness.AsAgency();
        var booking = await db.SupplierBookings.SingleAsync(candidate => candidate.Id == seeded.SupplierBookingId);
        change(booking, harness.Clock.GetUtcNow());
        await db.SaveChangesAsync();
    }
}
