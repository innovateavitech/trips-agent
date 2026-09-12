using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using TripsAgent.Application.Checkout;
using TripsAgent.Application.Notifications;
using TripsAgent.Application.Persistence;
using TripsAgent.Application.Suppliers;
using TripsAgent.Domain.Common;
using TripsAgent.Domain.Orders;
using TripsAgent.Domain.Payments;
using TripsAgent.Domain.Suppliers;
using TripsAgent.IntegrationTests.Persistence;
using TripsAgent.IntegrationTests.Suppliers;

namespace TripsAgent.IntegrationTests.Checkout;

/// <summary>
/// Payment reversal against real PostgreSQL (#43): each of the supplier's documented reversal rules
/// produces exactly one refund, resting on a recorded poll, with a flagged order line — and money that
/// had been taken goes back through balanced ledger entries.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class PaymentReversalTests : IAsyncLifetime
{
    private readonly PostgresFixture _postgres;
    private TripsAfricaStub _stub = null!;

    public PaymentReversalTests(PostgresFixture postgres) => _postgres = postgres;

    public async Task InitializeAsync() => _stub = await TripsAfricaStub.StartAsync();

    public async Task DisposeAsync() => await _stub.DisposeAsync();

    [Theory]
    [InlineData(200)]
    [InlineData(400)]
    public async Task Each_documented_rule_reverses_exactly_once_on_the_evidence_of_a_recorded_poll(int issueStatus)
    {
        // Rule 1: HTTP 200 with status 0, 1 or 11. Rule 2: HTTP 400, and a status query then says 0, 1 or 11.
        await using var harness = await BookingPipelineHarness.CreateAsync(_postgres, _stub);
        var (reference, request) = await ReversalRequestedAsync(harness, issueStatus);

        (await harness.ReverseAsync(request)).Should().Be(ReversalOutcome.Reversed);
        (await harness.ReverseAsync(request)).Should().Be(ReversalOutcome.AlreadyReversed, "a redelivered message refunds nothing more");

        await using var db = harness.AsAgency();
        var refund = await db.Refunds.AsNoTracking().SingleAsync();
        refund.Reason.Should().Be(RefundReason.SupplierReversal);
        refund.Method.Should().Be(RefundMethod.WalletHoldReleased, "the money was only ever held");
        refund.SupplierStatusPollId.Should().Be(request.SupplierStatusPollId);

        var hold = await db.WalletHolds.AsNoTracking().SingleAsync(candidate => candidate.OrderId != null);
        hold.Status.Should().Be(WalletHoldStatus.Released);
        refund.AmountMinor.Should().Be(hold.AmountMinor);
        (await harness.WalletAsync()).ReservedMinor.Should().Be(Money.Zero);

        var order = await db.Orders.AsNoTracking().Include(candidate => candidate.Lines).SingleAsync(candidate => candidate.OrderNumber == reference);
        order.Status.Should().Be(OrderStatus.PartiallyFailed);
        var line = order.Lines.Single();
        line.FulfilmentStatus.Should().Be(FulfilmentStatus.FailedNeedsResolution, "the agent decides what to offer the traveller");
        line.ResolutionStatus.Should().Be(ResolutionStatus.Open);
        line.ResolutionOpenedAt.Should().NotBeNull();

        (await harness.OutboxAsync<BookingNeedsResolution>()).Should().ContainSingle();
        (await harness.OutboxAsync<PaymentReversed>()).Should().ContainSingle();
    }

    [Fact]
    public async Task A_reversal_without_evidence_moves_nothing_and_tells_a_person()
    {
        await using var harness = await BookingPipelineHarness.CreateAsync(_postgres, _stub);
        var (_, request) = await ReversalRequestedAsync(harness, issueStatus: 200);

        (await harness.ReverseAsync(request with { SupplierStatusPollId = Guid.CreateVersion7() })).Should().Be(ReversalOutcome.NoEvidence);

        harness.Alerts.Raised.Should().ContainSingle(alert => alert.Source == PaymentReversalService.AlertSource)
            .Which.Severity.Should().Be(AlertSeverity.P1);

        await using var db = harness.AsAgency();
        (await db.Refunds.CountAsync()).Should().Be(0);
        (await db.WalletHolds.AsNoTracking().SingleAsync(hold => hold.OrderId != null)).Status.Should().Be(WalletHoldStatus.Held);
    }

    [Fact]
    public async Task Money_already_taken_goes_back_through_balanced_reversing_entries()
    {
        await using var harness = await BookingPipelineHarness.CreateAsync(_postgres, _stub);
        var before = (await harness.WalletAsync()).BalanceMinor;
        var reference = await ConfirmedBookingAsync(harness);

        var refund = await harness.InAgencyScopeAsync(async provider =>
        {
            var db = provider.GetRequiredService<IAppDbContext>();
            var order = await db.Orders.Include(candidate => candidate.Lines).SingleAsync(candidate => candidate.OrderNumber == reference);

            // As its callers do: the scope open from the money to the save.
            using var scope = provider.GetRequiredService<TripsAgent.Application.Tenancy.IPlatformScope>().Enter("test — refunds a captured booking");
            var returned = await provider.GetRequiredService<WalletRefunds>().ReturnAsync(
                order, order.Lines[0], RefundReason.AgentResolution, harness.Clock.GetUtcNow(), refundedByUserId: harness.OwnerUserId);
            await db.SaveChangesAsync();
            return returned!;
        });

        refund.Method.Should().Be(RefundMethod.WalletCredited);

        var entries = await harness.LedgerAsync(refund.LedgerTransactionGroupId!.Value);
        CheckoutTests.Sum(entries, LedgerDirection.Debit).Should().Be(refund.AmountMinor.AmountMinor);
        CheckoutTests.Sum(entries, LedgerDirection.Credit).Should().Be(refund.AmountMinor.AmountMinor, "the reversal balances like the capture did");
        (await harness.WalletAsync()).BalanceMinor.Should().Be(before, "the wallet is whole again");
    }

    [Fact]
    public async Task A_refund_can_never_be_edited_or_deleted()
    {
        await using var harness = await BookingPipelineHarness.CreateAsync(_postgres, _stub);
        var (_, request) = await ReversalRequestedAsync(harness, issueStatus: 200);
        await harness.ReverseAsync(request);

        await using var app = harness.AsAgency();
        var refundId = (await app.Refunds.AsNoTracking().SingleAsync()).Id;
        var asApp = () => app.Database.ExecuteSqlRawAsync("UPDATE payments.refunds SET note = 'edited' WHERE id = {0}", refundId);
        (await asApp.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);

        await using var owner = harness.AsOwner();
        var asOwner = () => owner.Database.ExecuteSqlRawAsync("DELETE FROM payments.refunds WHERE id = {0}", refundId);
        (await asOwner.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.RestrictViolation);
    }

    /// <summary>Confirm, pay, issue — answered with <paramref name="issueStatus"/> — then the poll that says 0.</summary>
    internal static async Task<(string Reference, PaymentReversalRequired Request)> ReversalRequestedAsync(BookingPipelineHarness harness, int issueStatus)
    {
        harness.Stub.OnIssue = (_, _) => Task.FromResult(issueStatus == 200
            ? TripsAfricaStub.Issued("Booking")
            : StubAnswer.Json("""{ "Pnr": "null", "IsSuccessful": false, "Message": "Invalid sessionId", "BookingStatus": "null" }""", status: 400));
        harness.Stub.OnStatus = (_, _) => Task.FromResult(TripsAfricaStub.Status(0, "Booking"));

        var confirmation = await harness.ConfirmPriceAsync(await harness.SeedFlightOfferAsync());
        await harness.PayAsync(confirmation, "attempt-1");
        await harness.IssueLineAsync((await harness.OutboxAsync<IssueSupplierTicket>()).Single());
        await harness.PollAsync();

        return (confirmation.Reference, (await harness.OutboxAsync<PaymentReversalRequired>()).Single());
    }

    /// <summary>Confirm, pay, issue with a ticket, and complete: the money has been taken.</summary>
    internal static async Task<string> ConfirmedBookingAsync(BookingPipelineHarness harness)
    {
        harness.Stub.OnIssue = (_, _) => Task.FromResult(TripsAfricaStub.Issued("TicketIssued"));

        var confirmation = await harness.ConfirmPriceAsync(await harness.SeedFlightOfferAsync());
        await harness.PayAsync(confirmation, "attempt-1");
        await harness.IssueLineAsync((await harness.OutboxAsync<IssueSupplierTicket>()).Single());
        await harness.CompleteAsync((await harness.OutboxAsync<BookingTicketed>()).Single());

        return confirmation.Reference;
    }
}
