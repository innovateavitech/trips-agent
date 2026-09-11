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
/// The agent's resolution queue against real PostgreSQL (#44): what a failed booking shows, and what
/// each decision does — recorded with who made it.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ResolutionTests : IAsyncLifetime
{
    private readonly PostgresFixture _postgres;
    private TripsAfricaStub _stub = null!;

    public ResolutionTests(PostgresFixture postgres) => _postgres = postgres;

    public async Task InitializeAsync() => _stub = await TripsAfricaStub.StartAsync();

    public async Task DisposeAsync() => await _stub.DisposeAsync();

    [Fact]
    public async Task A_failed_booking_shows_what_failed_why_and_what_was_paid()
    {
        await using var harness = await BookingPipelineHarness.CreateAsync(_postgres, _stub);
        var (reference, request) = await PaymentReversalTests.ReversalRequestedAsync(harness, issueStatus: 200);
        await harness.ReverseAsync(request);

        var queries = await harness.InAgencyScopeAsync(async provider =>
        {
            var bookings = provider.GetRequiredService<BookingQueries>();
            return (List: await bookings.ListAsync(), Detail: await bookings.FindAsync(reference));
        });

        queries.List.Should().ContainSingle().Which.State.Should().Be(BookingState.Failed);

        var failure = queries.Detail!.Failure!;
        failure.Reason.Should().Contain("status 0");
        failure.AtRiskMinor.Should().Be(queries.Detail.Summary.SellMinor);
        failure.PaidFrom.Should().Be(OrderPaymentMethod.Wallet);
        failure.OpenedAt.Should().NotBeNull("how long it has waited is what escalation reads");
        queries.Detail.Timeline[^1].State.Should().Be(BookingState.Failed);
    }

    [Fact]
    public async Task Refunding_closes_the_line_on_the_trail_with_who_decided_and_never_refunds_twice()
    {
        await using var harness = await BookingPipelineHarness.CreateAsync(_postgres, _stub);
        var (reference, request) = await PaymentReversalTests.ReversalRequestedAsync(harness, issueStatus: 200);
        await harness.ReverseAsync(request);

        await harness.ResolveAsync(reference, ResolutionChoice.Refund);

        await using var db = harness.AsAgency();
        var order = await db.Orders.AsNoTracking().Include(candidate => candidate.Lines).SingleAsync();
        order.Status.Should().Be(OrderStatus.Refunded);

        var line = order.Lines.Single();
        line.ResolutionStatus.Should().Be(ResolutionStatus.ResolvedRefunded);
        line.FulfilmentStatus.Should().Be(FulfilmentStatus.Refunded);
        line.ResolvedBy.Should().Be(harness.OwnerUserId);

        var decision = await db.OrderStatusHistory.AsNoTracking().SingleAsync(entry => entry.ToStatus == OrderStatus.Refunded);
        decision.ChangedByUserId.Should().Be(harness.OwnerUserId);
        decision.Reason.Should().Contain("already gone back", "the supplier reversal had returned it");

        var lineId = line.Id.ToString();
        var audited = await db.AuditLogs.AsNoTracking()
            .Where(entry => entry.EntityType == nameof(OrderLine) && entry.EntityId == lineId)
            .ToListAsync();
        audited.Should().Contain(
            entry => entry.ActorUserId == harness.OwnerUserId && entry.AfterState!.Contains("ResolvedRefunded"),
            "the decision is on the audit log too, with the agent who made it");

        (await db.Refunds.CountAsync()).Should().Be(1, "the reversal's refund, and no second one");
        (await harness.OutboxAsync<BookingResolved>()).Should().ContainSingle();

        var again = () => harness.ResolveAsync(reference, ResolutionChoice.Refund);
        (await again.Should().ThrowAsync<CheckoutRefusedException>()).Which.Refusal.Should().Be(CheckoutRefusal.Conflict);
    }

    [Fact]
    public async Task Retry_is_refused_with_the_way_forward()
    {
        await using var harness = await BookingPipelineHarness.CreateAsync(_postgres, _stub);
        var (reference, request) = await PaymentReversalTests.ReversalRequestedAsync(harness, issueStatus: 200);
        await harness.ReverseAsync(request);

        var retry = () => harness.ResolveAsync(reference, ResolutionChoice.Retry);

        var refused = (await retry.Should().ThrowAsync<CheckoutRefusedException>()).Which;
        refused.Refusal.Should().Be(CheckoutRefusal.Conflict);
        refused.Detail.Should().Contain("Substitute");
    }

    [Fact]
    public async Task A_booking_that_needs_no_decision_cannot_be_resolved()
    {
        await using var harness = await BookingPipelineHarness.CreateAsync(_postgres, _stub);
        var reference = await PaymentReversalTests.ConfirmedBookingAsync(harness);

        var refund = () => harness.ResolveAsync(reference, ResolutionChoice.Refund);

        (await refund.Should().ThrowAsync<CheckoutRefusedException>()).Which.Refusal.Should().Be(CheckoutRefusal.Conflict);
    }

    [Fact]
    public async Task A_fare_that_lapsed_after_payment_goes_to_the_queue_and_one_never_paid_for_is_simply_closed()
    {
        await using var harness = await BookingPipelineHarness.CreateAsync(_postgres, _stub);
        var paid = await harness.ConfirmPriceAsync(await harness.SeedFlightOfferAsync());
        await harness.PayAsync(paid, "attempt-1");
        var unpaid = await harness.ConfirmPriceAsync(await harness.SeedFlightOfferAsync());

        harness.Clock.Advance(TimeSpan.FromMinutes(46));
        (await harness.MonitorAsync()).Expired.Should().Be(2);

        await using var db = harness.AsAgency();
        var paidOrder = await db.Orders.AsNoTracking().Include(candidate => candidate.Lines).SingleAsync(candidate => candidate.OrderNumber == paid.Reference);
        paidOrder.Lines.Single().FulfilmentStatus.Should().Be(FulfilmentStatus.FailedNeedsResolution);

        var unpaidOrder = await db.Orders.AsNoTracking().Include(candidate => candidate.Lines).SingleAsync(candidate => candidate.OrderNumber == unpaid.Reference);
        unpaidOrder.Status.Should().Be(OrderStatus.Cancelled, "nothing was paid, so there is nothing to resolve");
        unpaidOrder.Lines.Single().FulfilmentStatus.Should().Be(FulfilmentStatus.Cancelled);

        (await harness.OutboxAsync<BookingNeedsResolution>()).Should().ContainSingle();
        (await db.WalletHolds.CountAsync(hold => hold.Status == WalletHoldStatus.Held)).Should().Be(0, "neither order's money is still held");
    }
}
