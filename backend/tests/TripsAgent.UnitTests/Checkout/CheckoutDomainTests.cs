using FluentAssertions;
using Hangfire;
using Hangfire.Common;
using TripsAgent.Application.Checkout;
using TripsAgent.Application.Messaging;
using TripsAgent.Domain.Common;
using TripsAgent.Domain.Orders;
using TripsAgent.Domain.Payments;
using TripsAgent.Domain.Pricing;
using TripsAgent.Domain.Suppliers;
using TripsAgent.Infrastructure.Checkout;
using TripsAgent.Infrastructure.Messaging;

namespace TripsAgent.UnitTests.Checkout;

/// <summary>The checkout's rules that need no database (#42, #43, #44).</summary>
public class CheckoutDomainTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 11, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void An_order_is_paid_for_once_and_says_how_when_and_with_which_key()
    {
        var order = PlacedOrder();
        var agent = Guid.CreateVersion7();

        order.RecordPayment(OrderPaymentMethod.Wallet, " attempt-1 ", Now, agent);

        order.Status.Should().Be(OrderStatus.Paid);
        order.PaidFrom.Should().Be(OrderPaymentMethod.Wallet);
        order.PaidAt.Should().Be(Now);
        order.PaymentIdempotencyKey.Should().Be("attempt-1");
        order.StatusHistory[^1].ChangedByUserId.Should().Be(agent);

        var again = () => order.RecordPayment(OrderPaymentMethod.Wallet, "attempt-2", Now);
        again.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Moving_an_order_to_the_status_it_already_has_records_nothing()
    {
        var order = PlacedOrder();

        order.ChangeStatus(OrderStatus.PendingPayment, Now);

        order.StatusHistory.Should().ContainSingle("the trail's own CHECK refuses a move to the same status");
    }

    [Fact]
    public void A_supplier_reversal_cannot_be_recorded_without_the_poll_behind_it()
    {
        var noEvidence = () => Refund(RefundReason.SupplierReversal, RefundMethod.WalletHoldReleased, withPoll: false);
        var noAgent = () => Refund(RefundReason.AgentResolution, RefundMethod.WalletHoldReleased, user: null);
        var noLedger = () => Refund(RefundReason.AgentResolution, RefundMethod.WalletCredited, user: Guid.CreateVersion7());
        var negative = () => Refund(RefundReason.SupplierReversal, RefundMethod.WalletHoldReleased, amount: -1);

        noEvidence.Should().Throw<ArgumentException>().WithMessage("*evidence*");
        noAgent.Should().Throw<ArgumentException>();
        noLedger.Should().Throw<ArgumentException>();
        negative.Should().Throw<ArgumentOutOfRangeException>();

        var refund = Refund(RefundReason.SupplierReversal, RefundMethod.WalletHoldReleased);
        refund.Currency.Should().Be("NGN");
        refund.SupplierStatusPollId.Should().NotBeNull();
    }

    [Fact]
    public void A_failed_line_enters_the_resolution_queue_once_with_the_time_it_did()
    {
        var order = PlacedOrder();
        var line = order.Lines[0];
        line.RecordFulfilment(FulfilmentStatus.Confirming, Now);
        var outbox = new RecordingOutbox();

        FailedLines.FlagForResolution(order, line, "The supplier reported status 0.", Now, outbox).Should().BeTrue();
        FailedLines.FlagForResolution(order, line, "Again.", Now.AddMinutes(1), outbox).Should().BeFalse();

        line.FulfilmentStatus.Should().Be(FulfilmentStatus.FailedNeedsResolution);
        line.ResolutionStatus.Should().Be(ResolutionStatus.Open);
        line.ResolutionOpenedAt.Should().Be(Now);
        order.Status.Should().Be(OrderStatus.PartiallyFailed);
        outbox.Messages.Should().ContainSingle().Which.Should().BeOfType<BookingNeedsResolution>();
    }

    [Fact]
    public void A_ticketed_line_is_never_sent_to_the_resolution_queue()
    {
        var order = PlacedOrder();
        order.Lines[0].RecordFulfilment(FulfilmentStatus.Confirmed, Now);

        FailedLines.FlagForResolution(order, order.Lines[0], "Too late.", Now, new RecordingOutbox()).Should().BeFalse();
    }

    [Theory]
    [InlineData(FulfilmentStatus.Confirming, SupplierBookingStatus.TicketPending, BookingState.AwaitingTicket)]
    [InlineData(FulfilmentStatus.Confirming, SupplierBookingStatus.Ticketed, BookingState.Ticketed)]
    [InlineData(FulfilmentStatus.Confirmed, SupplierBookingStatus.Ticketed, BookingState.Ticketed)]
    [InlineData(FulfilmentStatus.FailedNeedsResolution, SupplierBookingStatus.Failed, BookingState.Failed)]
    [InlineData(FulfilmentStatus.Refunded, SupplierBookingStatus.Failed, BookingState.Cancelled)]
    [InlineData(FulfilmentStatus.Cancelled, SupplierBookingStatus.Expired, BookingState.Cancelled)]
    public void The_console_reads_a_booking_from_its_line_and_its_supplier_booking(
        FulfilmentStatus fulfilment, SupplierBookingStatus booking, BookingState expected)
    {
        var order = PlacedOrder();
        var line = order.Lines[0];

        if (fulfilment == FulfilmentStatus.FailedNeedsResolution)
        {
            line.RecordFulfilment(fulfilment, Now, "Failed.");
        }
        else
        {
            line.RecordFulfilment(fulfilment, Now);
        }

        BookingQueries.StateOf(line, booking).Should().Be(expected);
    }

    [Fact]
    public void Tickets_and_reversals_are_consumed_where_the_checkout_lives()
    {
        MessagingRegistration.ConsumerRoutes.Should().Contain((MessageQueue.BookingSaga, typeof(BookingTicketedConsumer)));
        MessagingRegistration.ConsumerRoutes.Should().Contain((MessageQueue.PaymentsReversal, typeof(PaymentReversalRequiredConsumer)));
    }

    [Fact]
    public void The_checkout_sweeper_runs_every_minute_in_UTC()
    {
        var jobs = new RecordingRecurringJobManager();

        CheckoutSweepSchedule.Register(jobs);

        var registration = jobs.Registrations.Should().ContainSingle().Subject;
        registration.Cron.Should().Be("* * * * *");
        registration.Options.TimeZone.Should().Be(TimeZoneInfo.Utc);
        registration.Job.Type.Should().Be<CheckoutSweepJob>();
    }

    private static Order PlacedOrder()
    {
        var agencyId = Guid.CreateVersion7();
        var rule = MarkupRule.Create(agencyId, new MarkupRuleTerms
        {
            Scope = MarkupScope.Global,
            Currency = "NGN",
            CalculationType = MarkupCalculationType.Percentage,
            PercentBasisPoints = 1_000,
            EffectiveFrom = Now.AddDays(-1),
        });

        var quote = PriceQuote.Record(
            agencyId,
            new PricingSubject(PricedProductType.Flight, "NGN"),
            new PriceBreakdown(
                new Money(100_000), new Money(10_000), new Money(750), new Money(500), new Money(110_750),
                "NGN", new MarkupRuleDefinition(rule.Id, agencyId, rule.Terms), false, 750, 0),
            Now,
            TimeSpan.FromMinutes(30));

        var line = OrderLine.FromQuote(quote, "LOS → LHR, P4 7121", """{"adults":1}""", Now);
        return Order.Place(agencyId, "ORD-2026-000001", "NGN", BuyerType.AgentAssisted, OrderChannel.Console, null, [line], Now);
    }

    private static Refund Refund(RefundReason reason, RefundMethod method, bool withPoll = true, Guid? user = null, long amount = 100_000) =>
        TripsAgent.Domain.Payments.Refund.Record(
            Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), reason, method, new Money(amount), "ngn", Now,
            supplierStatusPollId: withPoll ? Guid.CreateVersion7() : null,
            refundedByUserId: user);

    private sealed class RecordingOutbox : IOutbox
    {
        public List<object> Messages { get; } = [];

        public void Enqueue<TMessage>(TMessage message, Guid? agencyId = null)
            where TMessage : class => Messages.Add(message);
    }

    private sealed class RecordingRecurringJobManager : IRecurringJobManager
    {
        public List<(string Id, Job Job, string Cron, RecurringJobOptions Options)> Registrations { get; } = [];

        public void AddOrUpdate(string recurringJobId, Job job, string cronExpression, RecurringJobOptions options) =>
            Registrations.Add((recurringJobId, job, cronExpression, options));

        public void Trigger(string recurringJobId) => throw new NotSupportedException();

        public void RemoveIfExists(string recurringJobId) => throw new NotSupportedException();
    }
}
