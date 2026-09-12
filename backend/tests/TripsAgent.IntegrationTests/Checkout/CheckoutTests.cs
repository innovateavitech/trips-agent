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
/// The console's checkout against real PostgreSQL and a real HTTP stand-in for the supplier (#42):
/// confirm a price, pay for it, and follow it to a confirmed booking — with the money held, then taken
/// exactly once, and the books balanced.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class CheckoutTests : IAsyncLifetime
{
    private readonly PostgresFixture _postgres;
    private TripsAfricaStub _stub = null!;

    public CheckoutTests(PostgresFixture postgres) => _postgres = postgres;

    public async Task InitializeAsync() => _stub = await TripsAfricaStub.StartAsync();

    public async Task DisposeAsync() => await _stub.DisposeAsync();

    [Fact]
    public async Task Confirming_a_price_records_the_order_pending_payment_with_a_verified_supplier_booking_behind_it()
    {
        await using var harness = await BookingPipelineHarness.CreateAsync(_postgres, _stub);

        var confirmation = await harness.ConfirmPriceAsync(await harness.SeedFlightOfferAsync());

        confirmation.Sell.Should().Be(confirmation.SearchedSell, "the supplier held the searched price");
        confirmation.TicketTimeLimit.Should().Be(BookingPipelineHarness.Start.AddMinutes(45));
        _stub.Count(TripsAfricaStub.ConfirmPath).Should().Be(1);

        await using var db = harness.AsAgency();
        var order = await db.Orders.AsNoTracking().Include(candidate => candidate.Lines).SingleAsync(candidate => candidate.OrderNumber == confirmation.Reference);
        order.Status.Should().Be(OrderStatus.PendingPayment);
        order.PaidAt.Should().BeNull();

        var line = order.Lines.Single();
        line.GrossAmountMinor.Should().Be(confirmation.Sell, "the price is frozen on the line from the confirmation");

        var booking = await db.SupplierBookings.AsNoTracking().SingleAsync(candidate => candidate.OrderLineId == line.Id);
        booking.Status.Should().Be(SupplierBookingStatus.PriceConfirmed);
        booking.HashVerified.Should().BeTrue();
        booking.TripType.Should().Be("International");
        booking.TripMode.Should().Be("Flight");
        (await db.SupplierBookingPassengers.CountAsync(passenger => passenger.SupplierBookingId == booking.Id)).Should().Be(1);
        (await db.OrderTravellers.CountAsync(traveller => traveller.OrderLineId == line.Id)).Should().Be(1);
    }

    [Fact]
    public async Task Paying_holds_the_wallet_marks_the_order_paid_and_queues_the_issue_in_one_transaction()
    {
        await using var harness = await BookingPipelineHarness.CreateAsync(_postgres, _stub);
        var confirmation = await harness.ConfirmPriceAsync(await harness.SeedFlightOfferAsync());

        (await harness.PayAsync(confirmation, "attempt-1")).Should().Be(confirmation.Reference);

        await using var db = harness.AsAgency();
        var order = await db.Orders.AsNoTracking().Include(candidate => candidate.Lines).SingleAsync(candidate => candidate.OrderNumber == confirmation.Reference);
        order.Status.Should().Be(OrderStatus.Paid);
        order.PaidFrom.Should().Be(OrderPaymentMethod.Wallet);
        order.PaymentIdempotencyKey.Should().Be("attempt-1");

        var line = order.Lines.Single();
        line.FulfilmentStatus.Should().Be(FulfilmentStatus.Confirming);

        var hold = await db.WalletHolds.AsNoTracking().SingleAsync(candidate => candidate.OrderId == order.Id);
        hold.Status.Should().Be(WalletHoldStatus.Held);
        hold.AmountMinor.Should().Be(line.NetAmountMinor + line.PlatformFeeMinor, "the agency owes Trips the net rate and the fee");
        (await harness.WalletAsync()).ReservedMinor.Should().Be(hold.AmountMinor);

        (await harness.OutboxAsync<IssueSupplierTicket>()).Should().ContainSingle().Which.OrderLineId.Should().Be(line.Id);
        _stub.Count(TripsAfricaStub.IssuePath).Should().Be(0, "a Worker issues it once the payment has committed");
    }

    [Fact]
    public async Task The_same_key_is_the_same_booking_never_a_second_charge()
    {
        await using var harness = await BookingPipelineHarness.CreateAsync(_postgres, _stub);
        var confirmation = await harness.ConfirmPriceAsync(await harness.SeedFlightOfferAsync());

        var references = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => Task.Run(() => harness.PayAsync(confirmation, "attempt-1"))));

        references.Should().OnlyContain(reference => reference == confirmation.Reference);
        await using (var db = harness.AsAgency())
        {
            (await db.WalletHolds.CountAsync(hold => hold.Status == WalletHoldStatus.Held)).Should().Be(1, "five presses, one payment");
        }

        (await harness.OutboxAsync<IssueSupplierTicket>()).Should().ContainSingle();

        var anotherAttempt = () => harness.PayAsync(confirmation, "attempt-2");
        (await anotherAttempt.Should().ThrowAsync<CheckoutRefusedException>()).Which.Refusal.Should().Be(CheckoutRefusal.Conflict);
    }

    [Fact]
    public async Task Not_enough_in_the_wallet_fails_before_anything_reaches_the_supplier()
    {
        // Q3: the wallet is held before the supplier is asked, so a short one stops the checkout first.
        await using var harness = await BookingPipelineHarness.CreateAsync(_postgres, _stub);
        var offer = await harness.SeedFlightOfferAsync();
        await harness.DrainWalletAsync(leaveMinor: 1_000);

        var confirm = () => harness.ConfirmPriceAsync(offer);

        (await confirm.Should().ThrowAsync<CheckoutRefusedException>()).Which.Refusal.Should().Be(CheckoutRefusal.Unprocessable);
        _stub.Journal.Should().BeEmpty("not even the price confirmation was sent");

        await using var db = harness.AsAgency();
        (await db.WalletHolds.CountAsync()).Should().Be(0);
        (await db.Orders.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task The_wallet_is_held_before_the_supplier_is_asked_and_the_hold_follows_the_confirmed_price()
    {
        await using var harness = await BookingPipelineHarness.CreateAsync(_postgres, _stub);
        var offer = await harness.SeedFlightOfferAsync();
        long? reservedWhenAsked = null;

        _stub.OnConfirm = async (_, _) =>
        {
            reservedWhenAsked = (await harness.WalletAsync()).ReservedMinor.AmountMinor;
            return TripsAfricaStub.Confirmed("36516|12QFDT", 1_000, BookingPipelineHarness.Start.AddMinutes(45));
        };

        await harness.ConfirmPriceAsync(offer);

        reservedWhenAsked.Should().Be(100_000, "the searched net was held before the confirmation left");

        await using var db = harness.AsAgency();
        var holds = await db.WalletHolds.AsNoTracking().ToListAsync();
        holds.Should().ContainSingle(hold => hold.OrderId == null).Which.Status.Should().Be(WalletHoldStatus.Released);
        holds.Should().ContainSingle(hold => hold.OrderId != null).Which.Status.Should().Be(WalletHoldStatus.Held);
        (await harness.WalletAsync()).ReservedMinor.AmountMinor.Should().Be(100_000, "held once, never twice");
    }

    [Fact]
    public async Task A_rise_the_wallet_cannot_cover_is_refused_and_the_money_held_for_it_goes_back()
    {
        await using var harness = await BookingPipelineHarness.CreateAsync(_postgres, _stub);
        var offer = await harness.SeedFlightOfferAsync();
        await harness.DrainWalletAsync(leaveMinor: 101_000);
        _stub.OnConfirm = (_, _) => Task.FromResult(
            TripsAfricaStub.Confirmed("36516|12QFDT", 1_035, BookingPipelineHarness.Start.AddMinutes(45), oldPriceWhole: 1_000));

        var confirm = () => harness.ConfirmPriceAsync(offer);

        (await confirm.Should().ThrowAsync<CheckoutRefusedException>()).Which.Refusal.Should().Be(CheckoutRefusal.Unprocessable);
        (await harness.WalletAsync()).ReservedMinor.Should().Be(Money.Zero);

        await using var db = harness.AsAgency();
        (await db.Orders.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task A_price_that_fell_passes_through_without_asking_again()
    {
        // Q9: only a rise needs re-consent.
        await using var harness = await BookingPipelineHarness.CreateAsync(_postgres, _stub);
        _stub.OnConfirm = (_, _) => Task.FromResult(
            TripsAfricaStub.Confirmed("36516|12QFDT", 950, BookingPipelineHarness.Start.AddMinutes(45), oldPriceWhole: 1_000));

        var confirmation = await harness.ConfirmPriceAsync(await harness.SeedFlightOfferAsync());
        confirmation.Sell.Should().BeLessThan(confirmation.SearchedSell);

        (await harness.PayAsync(confirmation, "attempt-1", acceptedSellMinor: confirmation.SearchedSell.AmountMinor))
            .Should().Be(confirmation.Reference);
    }

    [Fact]
    public async Task A_price_the_supplier_moved_must_be_accepted_again_before_it_can_be_paid()
    {
        await using var harness = await BookingPipelineHarness.CreateAsync(_postgres, _stub);
        _stub.OnConfirm = (_, _) => Task.FromResult(
            TripsAfricaStub.Confirmed("36516|12QFDT", 1_035, BookingPipelineHarness.Start.AddMinutes(45), oldPriceWhole: 1_000));

        var confirmation = await harness.ConfirmPriceAsync(await harness.SeedFlightOfferAsync());
        confirmation.Sell.Should().BeGreaterThan(confirmation.SearchedSell);

        var atTheOldPrice = () => harness.PayAsync(confirmation, "attempt-1", acceptedSellMinor: confirmation.SearchedSell.AmountMinor);
        (await atTheOldPrice.Should().ThrowAsync<CheckoutRefusedException>()).Which.Refusal.Should().Be(CheckoutRefusal.Conflict);

        (await harness.PayAsync(confirmation, "attempt-2")).Should().Be(confirmation.Reference, "the new price, accepted, can be paid");
    }

    [Fact]
    public async Task A_price_whose_hash_does_not_verify_is_never_booked_and_is_reported()
    {
        await using var harness = await BookingPipelineHarness.CreateAsync(_postgres, _stub);
        _stub.OnConfirm = (_, _) => Task.FromResult(
            TripsAfricaStub.Confirmed("36516|12QFDT", 1_000, BookingPipelineHarness.Start.AddMinutes(45), hash: "tampered"));

        var offer = await harness.SeedFlightOfferAsync();
        var confirm = () => harness.ConfirmPriceAsync(offer);

        (await confirm.Should().ThrowAsync<CheckoutRefusedException>()).Which.Refusal.Should().Be(CheckoutRefusal.SupplierFailed);
        harness.Alerts.Raised.Should().ContainSingle().Which.Severity.Should().Be(AlertSeverity.P1);
        (await harness.WalletAsync()).ReservedMinor.Should().Be(Money.Zero, "the money held for it went back");

        await using var db = harness.AsAgency();
        (await db.Orders.CountAsync()).Should().Be(0, "a price nobody can vouch for is never sold");
    }

    [Fact]
    public async Task Card_payment_is_refused_until_the_storefront_checkout_exists()
    {
        await using var harness = await BookingPipelineHarness.CreateAsync(_postgres, _stub);
        var confirmation = await harness.ConfirmPriceAsync(await harness.SeedFlightOfferAsync());

        var pay = () => harness.PayAsync(confirmation, "attempt-1", method: OrderPaymentMethod.Card);

        (await pay.Should().ThrowAsync<CheckoutRefusedException>()).Which.Refusal.Should().Be(CheckoutRefusal.Unprocessable);
    }

    [Fact]
    public async Task A_fare_past_its_ticket_time_limit_cannot_be_paid_for()
    {
        await using var harness = await BookingPipelineHarness.CreateAsync(_postgres, _stub);
        var confirmation = await harness.ConfirmPriceAsync(await harness.SeedFlightOfferAsync());

        harness.Clock.Advance(TimeSpan.FromMinutes(46));
        var pay = () => harness.PayAsync(confirmation, "attempt-1");

        (await pay.Should().ThrowAsync<CheckoutRefusedException>()).Which.Refusal.Should().Be(CheckoutRefusal.Gone);
    }

    [Fact]
    public async Task From_confirm_to_confirmed_the_money_is_held_then_taken_exactly_once_and_the_books_balance()
    {
        await using var harness = await BookingPipelineHarness.CreateAsync(_postgres, _stub);
        var before = (await harness.WalletAsync()).BalanceMinor;
        _stub.OnIssue = (_, _) => Task.FromResult(TripsAfricaStub.Issued("TicketIssued"));

        var confirmation = await harness.ConfirmPriceAsync(await harness.SeedFlightOfferAsync());
        await harness.PayAsync(confirmation, "attempt-1");
        (await harness.IssueLineAsync((await harness.OutboxAsync<IssueSupplierTicket>()).Single())).Should().Be(TicketIssueOutcome.Sent);

        var ticketed = (await harness.OutboxAsync<BookingTicketed>()).Should().ContainSingle().Subject;
        (await harness.CompleteAsync(ticketed)).Should().BeTrue();
        (await harness.CompleteAsync(ticketed)).Should().BeFalse("a redelivered event does nothing more");

        await using var db = harness.AsAgency();
        var order = await db.Orders.AsNoTracking().Include(candidate => candidate.Lines).SingleAsync();
        order.Status.Should().Be(OrderStatus.Confirmed);
        order.Lines.Single().FulfilmentStatus.Should().Be(FulfilmentStatus.Confirmed);

        var hold = await db.WalletHolds.AsNoTracking().SingleAsync(candidate => candidate.OrderId != null);
        hold.Status.Should().Be(WalletHoldStatus.Captured, "captured only on Ticketed");

        var wallet = await harness.WalletAsync();
        wallet.BalanceMinor.Should().Be(before - hold.AmountMinor);
        wallet.ReservedMinor.Should().Be(Money.Zero);

        var payment = await db.WalletTransactions.AsNoTracking().SingleAsync(transaction => transaction.Type == WalletTransactionType.BookingPayment);
        payment.AmountMinor.Should().Be(new Money(-hold.AmountMinor.AmountMinor));

        var entries = await harness.LedgerAsync(payment.TransactionGroupId);
        Sum(entries, LedgerDirection.Debit).Should().Be(hold.AmountMinor.AmountMinor);
        Sum(entries, LedgerDirection.Credit).Should().Be(hold.AmountMinor.AmountMinor, "every capture balances");

        (await harness.OutboxAsync<BookingConfirmed>()).Should().ContainSingle().Which.Pnr.Should().Be("RE6MIK");

        var listed = await harness.InAgencyScopeAsync(provider => provider.GetRequiredService<BookingQueries>().ListAsync());
        listed.Should().ContainSingle().Which.State.Should().Be(BookingState.Ticketed);

        var detail = await harness.InAgencyScopeAsync(provider => provider.GetRequiredService<BookingQueries>().FindAsync(confirmation.Reference));
        detail!.Timeline.Select(entry => entry.State).Should().Equal(BookingState.AwaitingTicket, BookingState.Ticketed);
        detail.Summary.Pnr.Should().Be("RE6MIK");
    }

    internal static long Sum(IEnumerable<LedgerEntry> entries, LedgerDirection direction) =>
        entries.Where(entry => entry.Direction == direction).Sum(entry => entry.AmountMinor.AmountMinor);
}
