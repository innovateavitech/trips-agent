using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using TripsAgent.Application.Suppliers;
using TripsAgent.Domain.Payments;
using TripsAgent.Domain.Suppliers;
using TripsAgent.IntegrationTests.Persistence;

namespace TripsAgent.IntegrationTests.Suppliers;

/// <summary>
/// The status poller against real PostgreSQL and a real HTTP stand-in for the supplier (#37). There are
/// no webhooks: this job is the only way a booking's outcome is learned, and the only thing that asks
/// for a payment to be reversed.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class SupplierBookingStatusPollerTests : IAsyncLifetime
{
    private readonly PostgresFixture _postgres;
    private TripsAfricaStub _stub = null!;

    public SupplierBookingStatusPollerTests(PostgresFixture postgres) => _postgres = postgres;

    public async Task InitializeAsync() => _stub = await TripsAfricaStub.StartAsync();

    public async Task DisposeAsync() => await _stub.DisposeAsync();

    // ------------------------------------------------------------------------ what each code does

    [Fact]
    public async Task Status_2_marks_the_booking_ticketed_and_announces_it()
    {
        await using var harness = await BookingPipelineHarness.CreateAsync(_postgres, _stub);
        var booking = await harness.SeedPendingBookingAsync();
        _stub.OnStatus = (_, _) => Task.FromResult(TripsAfricaStub.Status(2, "TicketIssued"));

        (await harness.PollAsync()).Should().Be(1);

        (await harness.BookingAsync(booking)).Status.Should().Be(SupplierBookingStatus.Ticketed);

        var poll = (await harness.PollsAsync(booking)).Should().ContainSingle().Subject;
        poll.Outcome.Should().Be(SupplierPollOutcome.Answered);
        poll.SupplierStatusCode.Should().Be(2);
        poll.HttpStatusCode.Should().Be(200);
        poll.ActionTaken.Should().Be(SupplierPollAction.MarkedTicketed);

        var ticketed = (await harness.OutboxPayloadsAsync<BookingTicketed>()).Should().ContainSingle().Subject;
        Read(ticketed, "supplierStatusPollId").Should().Be(poll.Id.ToString());

        // Asked the documented way: the confirmation code and the lead passenger's surname.
        var asked = _stub.Journal.Should().ContainSingle(request => request.Path == TripsAfricaStub.StatusPath).Subject;
        asked.Body.Should().Contain("\"ConfirmationCode\":\"36516|12QFDT\"").And.Contain("\"Surname\":\"Adeyemi\"");
    }

    [Theory]
    [InlineData(0, SupplierBookingStatus.Failed)]
    [InlineData(1, SupplierBookingStatus.Cancelled)]
    [InlineData(11, SupplierBookingStatus.Failed)]
    public async Task Statuses_0_1_and_11_ask_for_a_payment_reversal_resting_on_the_recorded_poll(int code, SupplierBookingStatus expected)
    {
        await using var harness = await BookingPipelineHarness.CreateAsync(_postgres, _stub);
        var booking = await harness.SeedPendingBookingAsync();
        _stub.OnStatus = (_, _) => Task.FromResult(TripsAfricaStub.Status(code));

        await harness.PollAsync();

        (await harness.BookingAsync(booking)).Status.Should().Be(expected);

        var poll = (await harness.PollsAsync(booking)).Should().ContainSingle().Subject;
        poll.ActionTaken.Should().Be(SupplierPollAction.ReversalRequested);

        var reversal = (await harness.OutboxPayloadsAsync<PaymentReversalRequired>()).Should().ContainSingle().Subject;
        Read(reversal, "supplierStatusPollId").Should().Be(poll.Id.ToString(), "never a reversal without the poll behind it");
        Read(reversal, "supplierStatusCode").Should().Be(code.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task Status_100_raises_one_admin_alert_and_polling_carries_on()
    {
        await using var harness = await BookingPipelineHarness.CreateAsync(_postgres, _stub);
        var booking = await harness.SeedPendingBookingAsync();
        _stub.OnStatus = (_, _) => Task.FromResult(StubAnswer.Json(
            """{ "StatusCode": 100, "StatusDescription": "Error", "ErrorList": ["No Valid booking found for the given record"] }"""));

        await harness.PollAsync();
        harness.Clock.Advance(TimeSpan.FromMinutes(1));
        await harness.PollAsync();

        var alert = harness.Alerts.Raised.Should().ContainSingle().Subject;
        alert.Source.Should().Be(SupplierBookingStatusPoller.AlertSource);
        alert.AgencyId.Should().Be(harness.AgencyId);
        alert.Detail.Should().Contain("No Valid booking found");

        var polls = await harness.PollsAsync(booking);
        polls.Select(poll => poll.ActionTaken).Should().Equal(SupplierPollAction.AlertRaised, SupplierPollAction.Rescheduled);

        var stored = await harness.BookingAsync(booking);
        stored.Status.Should().Be(SupplierBookingStatus.TicketPending, "nothing is decided on an error");
        stored.NextPollAt.Should().NotBeNull();
        (await harness.OutboxPayloadsAsync<PaymentReversalRequired>()).Should().BeEmpty();
    }

    [Fact]
    public async Task A_pending_ticket_is_never_resolved_by_a_timeout_and_the_money_stays_held()
    {
        await using var harness = await BookingPipelineHarness.CreateAsync(_postgres, _stub);
        var booking = await harness.SeedPendingBookingAsync(ticketTimeLimitIn: TimeSpan.FromMinutes(10));
        _stub.OnStatus = (_, _) => Task.FromResult(TripsAfricaStub.Status(3, "TicketPending"));

        for (var poll = 0; poll < 9; poll++)
        {
            var due = (await harness.BookingAsync(booking)).NextPollAt!.Value;
            harness.Clock.Advance(due - harness.Clock.GetUtcNow());
            (await harness.PollAsync()).Should().Be(1);
        }

        harness.Clock.GetUtcNow().Should().BeAfter(BookingPipelineHarness.Start.AddHours(2), "well past the limit and its buffer");

        var stored = await harness.BookingAsync(booking);
        stored.Status.Should().Be(SupplierBookingStatus.TicketPending);
        stored.NextPollAt.Should().NotBeNull("it is still being asked about");

        harness.Alerts.Raised.Should().ContainSingle().Which.Source.Should().Be(SupplierBookingStatusPoller.TimeLimitAlertSource);
        (await harness.OutboxPayloadsAsync<PaymentReversalRequired>()).Should().BeEmpty();

        await using var db = harness.AsAgency();
        (await db.WalletHolds.SingleAsync(hold => hold.OrderId == booking.OrderId)).Status.Should().Be(WalletHoldStatus.Held);
    }

    [Fact]
    public async Task The_back_off_widens_after_each_poll()
    {
        await using var harness = await BookingPipelineHarness.CreateAsync(_postgres, _stub);
        var booking = await harness.SeedPendingBookingAsync(ticketTimeLimitIn: TimeSpan.FromDays(1));
        _stub.OnStatus = (_, _) => Task.FromResult(TripsAfricaStub.Status(3));

        var gaps = new List<TimeSpan>();

        for (var poll = 0; poll < 4; poll++)
        {
            await harness.PollAsync();
            var next = (await harness.BookingAsync(booking)).NextPollAt!.Value;
            gaps.Add(next - harness.Clock.GetUtcNow());
            harness.Clock.Advance(next - harness.Clock.GetUtcNow());
        }

        // The first thirty seconds were spent before the first poll, after the issue call.
        gaps.Should().Equal(TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15));
    }

    [Fact]
    public async Task A_supplier_that_does_not_answer_is_recorded_as_a_poll_and_asked_again()
    {
        await using var harness = await BookingPipelineHarness.CreateAsync(_postgres, _stub);
        var booking = await harness.SeedPendingBookingAsync();
        _stub.OnStatus = (_, _) => Task.FromResult(StubAnswer.Json("<html>Bad gateway</html>", status: 502));

        await harness.PollAsync();

        var poll = (await harness.PollsAsync(booking)).Should().ContainSingle().Subject;
        poll.Outcome.Should().Be(SupplierPollOutcome.HttpError);
        poll.HttpStatusCode.Should().Be(502);
        poll.ActionTaken.Should().Be(SupplierPollAction.Rescheduled);
        (await harness.BookingAsync(booking)).Status.Should().Be(SupplierBookingStatus.TicketPending);
    }

    [Fact]
    public async Task A_booking_that_is_not_due_is_left_alone()
    {
        await using var harness = await BookingPipelineHarness.CreateAsync(_postgres, _stub);
        await harness.SeedPendingBookingAsync();

        (await harness.PollAsync()).Should().Be(1);
        (await harness.PollAsync()).Should().Be(0, "the next poll is a minute away");
        _stub.Count(TripsAfricaStub.StatusPath).Should().Be(1);
    }

    // ------------------------------------------------------------------------- many workers

    [Fact]
    public async Task Many_workers_polling_at_once_ask_about_each_due_booking_exactly_once()
    {
        await using var harness = await BookingPipelineHarness.CreateAsync(_postgres, _stub);
        var bookings = new List<SeededBooking>();

        for (var index = 0; index < 12; index++)
        {
            bookings.Add(await harness.SeedPendingBookingAsync());
        }

        _stub.OnStatus = async (_, token) =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds(150), token);
            return TripsAfricaStub.Status(3);
        };

        var recorded = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => Task.Run(harness.PollAsync)));

        recorded.Sum().Should().Be(12);
        _stub.Count(TripsAfricaStub.StatusPath).Should().Be(12, "SKIP LOCKED hands each booking to one worker");

        foreach (var booking in bookings)
        {
            (await harness.PollsAsync(booking)).Should().ContainSingle();
        }
    }

    // ------------------------------------------------------------ the supplier's reversal rules

    [Fact]
    public async Task Rule_one_HTTP_200_with_status_0_is_reversed_only_after_a_status_query_says_so()
    {
        await using var harness = await BookingPipelineHarness.CreateAsync(_postgres, _stub);
        var booking = await harness.SeedConfirmedBookingAsync(paidFromWallet: true);
        _stub.OnIssue = (_, _) => Task.FromResult(TripsAfricaStub.Issued("Booking"));
        _stub.OnStatus = (_, _) => Task.FromResult(TripsAfricaStub.Status(0, "Booking"));

        await harness.IssueAsync(booking);

        (await harness.OutboxPayloadsAsync<PaymentReversalRequired>()).Should().BeEmpty("the issue answer alone is not evidence");
        (await harness.BookingAsync(booking)).Status.Should().Be(SupplierBookingStatus.IssueOutcomeUnknown);

        await harness.PollAsync();

        (await harness.OutboxPayloadsAsync<PaymentReversalRequired>()).Should().ContainSingle();
        _stub.Count(TripsAfricaStub.IssuePath).Should().Be(1);
    }

    [Fact]
    public async Task Rule_two_HTTP_400_is_reversed_when_the_status_query_then_says_0()
    {
        await using var harness = await BookingPipelineHarness.CreateAsync(_postgres, _stub);
        var booking = await harness.SeedConfirmedBookingAsync(paidFromWallet: true);
        _stub.OnIssue = (_, _) => Task.FromResult(StubAnswer.Json(
            """{ "Pnr": "null", "IsSuccessful": false, "Message": "Invalid sessionId", "BookingStatus": "null" }""", status: 400));
        _stub.OnStatus = (_, _) => Task.FromResult(TripsAfricaStub.Status(0, "Booking"));

        await harness.IssueAsync(booking);
        await harness.PollAsync();

        var reversal = (await harness.OutboxPayloadsAsync<PaymentReversalRequired>()).Should().ContainSingle().Subject;
        Read(reversal, "supplierStatusCode").Should().Be("0");
        (await harness.BookingAsync(booking)).Status.Should().Be(SupplierBookingStatus.Failed);
        _stub.Count(TripsAfricaStub.IssuePath).Should().Be(1, "a refusal is never followed by a second issue call");
    }

    // ------------------------------------------------------------------------- the evidence

    [Fact]
    public async Task A_poll_can_never_be_edited_or_deleted_not_even_by_the_owner()
    {
        await using var harness = await BookingPipelineHarness.CreateAsync(_postgres, _stub);
        var booking = await harness.SeedPendingBookingAsync();
        await harness.PollAsync();
        var poll = (await harness.PollsAsync(booking)).Single();

        await using var app = harness.AsAgency();
        var asApp = () => app.Database.ExecuteSqlRawAsync("UPDATE supplier.supplier_status_polls SET note = 'edited' WHERE id = {0}", poll.Id);
        (await asApp.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);

        await using var owner = harness.AsOwner();
        var edit = () => owner.Database.ExecuteSqlRawAsync("UPDATE supplier.supplier_status_polls SET note = 'edited' WHERE id = {0}", poll.Id);
        var delete = () => owner.Database.ExecuteSqlRawAsync("DELETE FROM supplier.supplier_status_polls WHERE id = {0}", poll.Id);

        (await edit.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.RestrictViolation);
        (await delete.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.RestrictViolation);
    }

    private static string? Read(string payload, string property)
    {
        using var json = JsonDocument.Parse(payload);
        var value = json.RootElement.GetProperty(property);
        return value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText();
    }
}
