using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using TripsAgent.Application.Suppliers;
using TripsAgent.Domain.Suppliers;
using TripsAgent.IntegrationTests.Persistence;
using TripsAgent.IntegrationTests.Pricing;

namespace TripsAgent.IntegrationTests.Suppliers;

/// <summary>
/// Issuing a ticket against real PostgreSQL, a real Redis and a real HTTP stand-in for the supplier
/// (#36). Read docs/adr/0003-never-retry-ticket-issuance.md first: every test here is a way a second
/// real ticket could be issued, and is not.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class TicketIssuanceTests : IClassFixture<RedisFixture>, IAsyncLifetime
{
    private readonly PostgresFixture _postgres;
    private readonly RedisFixture _redis;
    private TripsAfricaStub _stub = null!;

    public TicketIssuanceTests(PostgresFixture postgres, RedisFixture redis)
    {
        _postgres = postgres;
        _redis = redis;
    }

    public async Task InitializeAsync() => _stub = await TripsAfricaStub.StartAsync();

    public async Task DisposeAsync() => await _stub.DisposeAsync();

    // --------------------------------------------------------------------- the chaos test

    [Fact]
    public async Task Twenty_concurrent_issue_messages_for_one_order_line_reach_the_supplier_exactly_once()
    {
        await using var harness = await BookingPipelineHarness.CreateAsync(_postgres, _stub, _redis.Connection);
        var booking = await harness.SeedConfirmedBookingAsync();

        // Slow enough that all twenty are in flight together.
        _stub.OnIssue = async (_, token) =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds(400), token);
            return TripsAfricaStub.Issued("TicketPending");
        };

        var outcomes = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(() => harness.IssueAsync(booking))));

        _stub.Count(TripsAfricaStub.IssuePath).Should().Be(1, "the issue call is not idempotent: a second one is a second ticket");
        outcomes.Count(outcome => outcome == TicketIssueOutcome.Sent).Should().Be(1);
        outcomes.Should().OnlyContain(outcome =>
            outcome == TicketIssueOutcome.Sent
            || outcome == TicketIssueOutcome.AlreadyInProgress
            || outcome == TicketIssueOutcome.AlreadyStarted);

        var stored = await harness.BookingAsync(booking);
        stored.Status.Should().Be(SupplierBookingStatus.TicketPending);
        stored.Pnr.Should().Be("RE6MIK");
    }

    [Fact]
    public async Task Without_Redis_the_database_guards_alone_still_let_exactly_one_through()
    {
        // The lock is the outermost guard, and Redis can be down. The row lock and the booking's own
        // state are what hold then.
        await using var harness = await BookingPipelineHarness.CreateAsync(_postgres, _stub, redis: null);
        var booking = await harness.SeedConfirmedBookingAsync();

        _stub.OnIssue = async (_, token) =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds(400), token);
            return TripsAfricaStub.Issued("TicketPending");
        };

        var outcomes = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(() => harness.IssueAsync(booking))));

        _stub.Count(TripsAfricaStub.IssuePath).Should().Be(1);
        outcomes.Count(outcome => outcome == TicketIssueOutcome.Sent).Should().Be(1);
        outcomes.Count(outcome => outcome == TicketIssueOutcome.AlreadyStarted).Should().Be(19);
    }

    [Fact]
    public async Task One_order_line_can_only_ever_have_one_supplier_booking()
    {
        // The guard that survives everything — a process dying, a lock expiring, a bug above it.
        await using var harness = await BookingPipelineHarness.CreateAsync(_postgres, _stub);
        var booking = await harness.SeedConfirmedBookingAsync();

        await using var db = harness.AsAgency();
        db.SupplierBookings.Add(SupplierBooking.Create(
            harness.AgencyId, harness.SupplierId, booking.OrderLineId, null, SupplierProductType.Flight,
            "Domestic", "Flight", "another-session", "NGN", $"another-key:{Guid.CreateVersion7():N}"));

        var save = () => db.SaveChangesAsync();

        (await save.Should().ThrowAsync<DbUpdateException>())
            .Which.InnerException.Should().BeOfType<PostgresException>()
            .Which.SqlState.Should().Be(PostgresErrorCodes.UniqueViolation);
    }

    // ------------------------------------------------------------------------ the kill test

    [Fact]
    public async Task A_worker_killed_mid_issue_is_recovered_by_asking_the_supplier_never_by_issuing_again()
    {
        await using var harness = await BookingPipelineHarness.CreateAsync(_postgres, _stub);
        var booking = await harness.SeedConfirmedBookingAsync();

        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _stub.OnIssue = async (_, token) =>
        {
            arrived.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            return TripsAfricaStub.Issued("TicketPending");
        };

        // The worker dies once the supplier has the request: nothing after that point runs.
        using var kill = new CancellationTokenSource();
        var worker = Task.Run(() => harness.IssueAsync(booking, cancellationToken: kill.Token));
        await arrived.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await kill.CancelAsync();

        await worker.Invoking(task => task).Should().ThrowAsync<OperationCanceledException>();

        var orphaned = await harness.BookingAsync(booking);
        orphaned.Status.Should().Be(SupplierBookingStatus.Issuing, "it was saved as Issuing before the call left, and nothing recorded an answer");

        // The restart: the poller takes the booking over once the recovery delay has passed.
        _stub.OnStatus = (_, _) => Task.FromResult(TripsAfricaStub.Status(2, "TicketIssued"));
        harness.Clock.Advance(SupplierPollSchedule.IssueRecoveryDelay);

        (await harness.PollAsync()).Should().Be(1);

        _stub.Count(TripsAfricaStub.IssuePath).Should().Be(1, "recovery never re-issues");
        _stub.Count(TripsAfricaStub.StatusPath).Should().Be(1, "it asks GetBookingStatus instead");

        var recovered = await harness.BookingAsync(booking);
        recovered.Status.Should().Be(SupplierBookingStatus.Ticketed);
        (await harness.PollsAsync(booking)).Should().ContainSingle().Which.ActionTaken.Should().Be(SupplierPollAction.MarkedTicketed);
        (await harness.OutboxPayloadsAsync<BookingTicketed>()).Should().ContainSingle();
    }

    [Fact]
    public async Task Before_the_recovery_delay_the_poller_leaves_an_issuing_booking_alone()
    {
        // A slow issue call still waiting for its answer must not be overtaken.
        await using var harness = await BookingPipelineHarness.CreateAsync(_postgres, _stub);
        var booking = await harness.SeedConfirmedBookingAsync();

        await using (var db = harness.AsAgency())
        {
            var issuing = await db.SupplierBookings.SingleAsync(candidate => candidate.Id == booking.SupplierBookingId);
            issuing.BeginIssue(harness.Clock.GetUtcNow());
            await db.SaveChangesAsync();
        }

        harness.Clock.Advance(SupplierPollSchedule.IssueRecoveryDelay - TimeSpan.FromSeconds(1));

        (await harness.PollAsync()).Should().Be(0);
        _stub.Count(TripsAfricaStub.StatusPath).Should().Be(0);
    }

    // ---------------------------------------------------------------- timeouts and refusals

    [Fact]
    public async Task A_timed_out_issue_call_is_an_unknown_outcome_handed_to_the_poller()
    {
        await using var harness = await BookingPipelineHarness.CreateAsync(_postgres, _stub, issueTimeoutSeconds: 1);
        var booking = await harness.SeedConfirmedBookingAsync();

        _stub.OnIssue = async (_, token) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(10), token);
            return TripsAfricaStub.Issued("TicketIssued");
        };

        (await harness.IssueAsync(booking)).Should().Be(TicketIssueOutcome.Sent);

        var unknown = await harness.BookingAsync(booking);
        unknown.Status.Should().Be(SupplierBookingStatus.IssueOutcomeUnknown);
        unknown.NextPollAt.Should().Be(harness.Clock.GetUtcNow().AddSeconds(30));
        _stub.Count(TripsAfricaStub.IssuePath).Should().Be(1);

        // The poller settles it — with a status query.
        _stub.OnStatus = (_, _) => Task.FromResult(TripsAfricaStub.Status(3, "TicketPending"));
        harness.Clock.Advance(TimeSpan.FromSeconds(30));
        await harness.PollAsync();

        (await harness.BookingAsync(booking)).Status.Should().Be(SupplierBookingStatus.TicketPending);
        _stub.Count(TripsAfricaStub.IssuePath).Should().Be(1);
    }

    [Fact]
    public async Task A_supplier_that_is_down_gets_one_issue_call_and_the_outcome_is_left_to_the_poller()
    {
        await using var harness = await BookingPipelineHarness.CreateAsync(_postgres, _stub);
        var booking = await harness.SeedConfirmedBookingAsync();

        _stub.OnIssue = (_, _) => Task.FromResult(StubAnswer.Json("<html>Service Unavailable</html>", status: 503));

        await harness.IssueAsync(booking);

        _stub.Count(TripsAfricaStub.IssuePath).Should().Be(1, "no retry of any kind sits on the issue call");
        (await harness.BookingAsync(booking)).Status.Should().Be(SupplierBookingStatus.IssueOutcomeUnknown);
    }

    // -------------------------------------------------------------------- what is sent, and when not

    [Theory]
    [InlineData(SupplierProductType.Flight, "International", "Flight")]
    [InlineData(SupplierProductType.Flight, "Domestic", "Flight")]
    [InlineData(SupplierProductType.Bus, "Domestic", "Road")]
    public async Task The_issue_call_carries_the_session_and_the_trip_type_and_mode(SupplierProductType product, string tripType, string tripMode)
    {
        await using var harness = await BookingPipelineHarness.CreateAsync(_postgres, _stub);
        var booking = await harness.SeedConfirmedBookingAsync(product, tripType: tripType, tripMode: tripMode);

        await harness.IssueAsync(booking);

        var sent = _stub.Journal.Should().ContainSingle(request => request.Path == TripsAfricaStub.IssuePath).Subject;
        using var body = JsonDocument.Parse(sent.Body);
        body.RootElement.GetProperty("SessionId").GetString().Should().Be("8646790ccb9a4d0997a6b52693287256");
        body.RootElement.GetProperty("TripType").GetString().Should().Be(tripType);
        body.RootElement.GetProperty("TripMode").GetString().Should().Be(tripMode);
    }

    [Fact]
    public async Task A_message_with_the_wrong_idempotency_key_sends_nothing()
    {
        await using var harness = await BookingPipelineHarness.CreateAsync(_postgres, _stub);
        var booking = await harness.SeedConfirmedBookingAsync();

        (await harness.IssueAsync(booking, idempotencyKey: "order-line:someone-else")).Should().Be(TicketIssueOutcome.NotIssuable);

        _stub.Count(TripsAfricaStub.IssuePath).Should().Be(0);
        (await harness.BookingAsync(booking)).Status.Should().Be(SupplierBookingStatus.PriceConfirmed);
    }

    [Fact]
    public async Task A_booking_past_its_ticket_time_limit_is_never_sent()
    {
        await using var harness = await BookingPipelineHarness.CreateAsync(_postgres, _stub);
        var booking = await harness.SeedConfirmedBookingAsync(ticketTimeLimitIn: TimeSpan.FromMinutes(5));

        harness.Clock.Advance(TimeSpan.FromMinutes(5));

        (await harness.IssueAsync(booking)).Should().Be(TicketIssueOutcome.NotIssuable);
        _stub.Count(TripsAfricaStub.IssuePath).Should().Be(0);
    }

    [Fact]
    public async Task A_redelivered_message_after_the_ticket_is_issued_sends_nothing()
    {
        await using var harness = await BookingPipelineHarness.CreateAsync(_postgres, _stub);
        var booking = await harness.SeedConfirmedBookingAsync();
        _stub.OnIssue = (_, _) => Task.FromResult(TripsAfricaStub.Issued("TicketIssued"));

        (await harness.IssueAsync(booking)).Should().Be(TicketIssueOutcome.Sent);
        (await harness.IssueAsync(booking)).Should().Be(TicketIssueOutcome.AlreadyStarted);

        _stub.Count(TripsAfricaStub.IssuePath).Should().Be(1);
        (await harness.BookingAsync(booking)).Status.Should().Be(SupplierBookingStatus.Ticketed);
        (await harness.OutboxPayloadsAsync<BookingTicketed>()).Should().ContainSingle();
    }
}
