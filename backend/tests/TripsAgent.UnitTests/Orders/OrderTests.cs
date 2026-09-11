using FluentAssertions;
using TripsAgent.Domain.Common;
using TripsAgent.Domain.Orders;
using TripsAgent.Domain.Pricing;

namespace TripsAgent.UnitTests.Orders;

/// <summary>
/// What an order does with the prices it is given: copies them, and refuses a price that has expired.
/// </summary>
public class OrderTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 11, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Agency = Guid.CreateVersion7();

    [Fact]
    public void A_line_copies_every_figure_from_the_quote()
    {
        var quote = Quote(net: 100_000, markup: 10_000, tax: 750, fee: 500, gross: 110_750);

        var line = OrderLine.FromQuote(quote, "LOS → ABV, Air Peace", """{"adults":1}""", Now);

        line.NetAmountMinor.Should().Be(quote.NetAmountMinor);
        line.MarkupAmountMinor.Should().Be(quote.MarkupAmountMinor);
        line.TaxAmountMinor.Should().Be(quote.TaxAmountMinor);
        line.PlatformFeeMinor.Should().Be(quote.PlatformFeeMinor);
        line.GrossAmountMinor.Should().Be(quote.GrossAmountMinor);
        line.Currency.Should().Be(quote.Currency);
        line.PriceQuoteId.Should().Be(quote.Id);
        line.MarkupRuleId.Should().Be(quote.MarkupRuleId);
        line.FulfilmentStatus.Should().Be(FulfilmentStatus.Pending);
    }

    [Fact]
    public void A_line_cannot_be_built_from_an_expired_quote()
    {
        var quote = Quote(net: 100_000, markup: 0, tax: 0, fee: 0, gross: 100_000, validity: TimeSpan.FromMinutes(30));

        var act = () => OrderLine.FromQuote(quote, "LOS → ABV", """{"adults":1}""", Now.AddMinutes(31));

        act.Should().Throw<PriceQuoteExpiredException>(
            "a price nobody re-confirmed is not a price we can sell at — issue #29");
    }

    [Fact]
    public void A_line_priced_at_the_last_moment_is_still_usable()
    {
        var quote = Quote(net: 100_000, markup: 0, tax: 0, fee: 0, gross: 100_000, validity: TimeSpan.FromMinutes(30));

        var act = () => OrderLine.FromQuote(quote, "LOS → ABV", """{"adults":1}""", Now.AddMinutes(29));

        act.Should().NotThrow();
    }

    [Fact]
    public void Placing_sums_the_lines_and_freezes_them()
    {
        var first = OrderLine.FromQuote(Quote(100_000, 10_000, 750, 500, 110_750), "LOS → ABV", """{"adults":1}""", Now);
        var second = OrderLine.FromQuote(Quote(50_000, 5_000, 375, 250, 55_375), "ABV → LOS", """{"adults":1}""", Now);

        var order = Order.Place(Agency, "ORD-2026-000001", "NGN", BuyerType.AgentAssisted, OrderChannel.Console, null, [first, second], Now);

        order.TotalNetMinor.Should().Be(new Money(150_000));
        order.TotalMarkupMinor.Should().Be(new Money(15_000));
        order.TotalTaxMinor.Should().Be(new Money(1_125));
        order.TotalPlatformFeeMinor.Should().Be(new Money(750));
        order.TotalGrossMinor.Should().Be(new Money(166_125));
        order.Status.Should().Be(OrderStatus.PendingPayment);
        order.PlacedAt.Should().Be(Now);
        order.Lines.Should().HaveCount(2);
        order.Lines.Should().OnlyContain(line => line.OrderId == order.Id && line.PlacedAt == Now);
    }

    [Fact]
    public void The_agency_keeps_the_markup_less_the_platform_fee()
    {
        var line = OrderLine.FromQuote(Quote(100_000, 10_000, 750, 500, 110_750), "LOS → ABV", """{"adults":1}""", Now);

        var order = Order.Place(Agency, "ORD-2026-000002", "NGN", BuyerType.Customer, OrderChannel.Storefront, null, [line], Now);

        line.AgentMarginMinor.Should().Be(new Money(9_500));
        order.AgentMarginMinor.Should().Be(new Money(9_500));
    }

    [Fact]
    public void An_order_refuses_a_line_in_another_currency()
    {
        var naira = OrderLine.FromQuote(Quote(100_000, 0, 0, 0, 100_000), "LOS → ABV", """{"adults":1}""", Now);
        var dollars = OrderLine.FromQuote(Quote(100_000, 0, 0, 0, 100_000, currency: "USD"), "LHR → JFK", """{"adults":1}""", Now);

        var act = () => Order.Place(Agency, "ORD-2026-000003", "NGN", BuyerType.Customer, OrderChannel.Storefront, null, [naira, dollars], Now);

        act.Should().Throw<ArgumentException>().WithMessage("*currency*");
    }

    [Fact]
    public void An_order_needs_at_least_one_line()
    {
        var act = () => Order.Place(Agency, "ORD-2026-000004", "NGN", BuyerType.Customer, OrderChannel.Storefront, null, [], Now);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void An_order_that_is_finished_cannot_move_on()
    {
        var line = OrderLine.FromQuote(Quote(100_000, 0, 0, 0, 100_000), "LOS → ABV", """{"adults":1}""", Now);
        var order = Order.Place(Agency, "ORD-2026-000005", "NGN", BuyerType.Customer, OrderChannel.Storefront, null, [line], Now);

        order.ChangeStatus(OrderStatus.Cancelled, Now);
        var act = () => order.ChangeStatus(OrderStatus.Paid, Now);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void A_failed_line_has_to_say_why_and_opens_a_resolution()
    {
        var line = OrderLine.FromQuote(Quote(100_000, 0, 0, 0, 100_000), "LOS → ABV", """{"adults":1}""", Now);

        var withoutReason = () => line.RecordFulfilment(FulfilmentStatus.FailedNeedsResolution, Now);
        withoutReason.Should().Throw<ArgumentException>();

        line.RecordFulfilment(FulfilmentStatus.FailedNeedsResolution, Now, "the airline released the seat");

        line.FailureReason.Should().Be("the airline released the seat");
        line.ResolutionStatus.Should().Be(ResolutionStatus.Open, "somebody has to decide what happens to the money");
    }


    // ------------------------------------------------------------------------ the status trail

    [Fact]
    public void A_new_order_already_has_its_first_trail_entry()
    {
        var order = Place();

        order.StatusHistory.Should().ContainSingle();
        order.StatusHistory[0].FromStatus.Should().BeNull("the order did not come from anywhere");
        order.StatusHistory[0].ToStatus.Should().Be(OrderStatus.PendingPayment);
        order.StatusHistory[0].OrderId.Should().Be(order.Id);
        order.StatusHistory[0].AgencyId.Should().Be(Agency);
    }

    [Fact]
    public void Every_status_change_writes_itself_into_the_trail()
    {
        var order = Place();

        order.ChangeStatus(OrderStatus.Paid, Now.AddMinutes(2));
        order.ChangeStatus(OrderStatus.Confirmed, Now.AddMinutes(9), "supplier confirmed", Agency);

        order.StatusHistory.Should().HaveCount(3);
        order.StatusHistory[1].FromStatus.Should().Be(OrderStatus.PendingPayment);
        order.StatusHistory[1].ToStatus.Should().Be(OrderStatus.Paid);
        order.StatusHistory[2].Reason.Should().Be("supplier confirmed");
        order.StatusHistory[2].ChangedByUserId.Should().Be(Agency);
    }

    [Fact]
    public void A_refused_status_change_leaves_no_trace_in_the_trail()
    {
        var order = Place();
        order.ChangeStatus(OrderStatus.Cancelled, Now.AddMinutes(1));
        var before = order.StatusHistory.Count;

        var act = () => order.ChangeStatus(OrderStatus.Paid, Now.AddMinutes(2));

        act.Should().Throw<InvalidOperationException>();
        order.StatusHistory.Should().HaveCount(before, "a change that did not happen is not history");
    }

    // ------------------------------------------------------------------------------ the cart

    [Fact]
    public void A_cart_may_hold_a_price_that_has_expired_but_an_order_line_may_not()
    {
        // The whole difference between the two, in one test. Rule 5 freezes a price at purchase,
        // not when somebody first clicked "add" — so the cart keeps showing it and checkout re-prices.
        var quote = Quote(net: 100_000, markup: 0, tax: 0, fee: 0, gross: 100_000, validity: TimeSpan.FromMinutes(30));
        var tooLate = Now.AddMinutes(31);

        var item = CartItem.FromQuote(quote, "LOS → ABV", """{"adults":1}""", tooLate);
        var line = () => OrderLine.FromQuote(quote, "LOS → ABV", """{"adults":1}""", tooLate);

        item.IndicativeGrossMinor.Should().Be(new Money(100_000));
        line.Should().Throw<PriceQuoteExpiredException>();
    }

    [Fact]
    public void A_guest_cart_needs_something_to_find_it_by()
    {
        var act = () => Cart.Open(Agency, "NGN", Now, TimeSpan.FromDays(7));

        act.Should().Throw<ArgumentException>("a cart nobody can find again is litter");
    }

    [Fact]
    public void A_cart_totals_what_is_in_it()
    {
        var cart = Cart.Open(Agency, "NGN", Now, TimeSpan.FromDays(7), sessionToken: "sess-abc");

        cart.Add(CartItem.FromQuote(Quote(100_000, 10_000, 750, 500, 110_750), "LOS → ABV", """{"adults":1}""", Now), Now);
        cart.Add(CartItem.FromQuote(Quote(50_000, 5_000, 375, 0, 55_375), "ABV → KAN", """{"adults":1}""", Now), Now);

        cart.IndicativeTotalMinor.Should().Be(new Money(166_125));
        cart.Items.Should().HaveCount(2);
    }

    [Fact]
    public void A_cart_refuses_an_item_in_another_currency()
    {
        var cart = Cart.Open(Agency, "NGN", Now, TimeSpan.FromDays(7), sessionToken: "sess-abc");
        var usd = CartItem.FromQuote(Quote(100, 0, 0, 0, 100, currency: "USD"), "LOS → JFK", """{"adults":1}""", Now);

        var act = () => cart.Add(usd, Now);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void An_expired_cart_cannot_be_added_to()
    {
        var cart = Cart.Open(Agency, "NGN", Now, TimeSpan.FromHours(1), sessionToken: "sess-abc");
        var item = CartItem.FromQuote(Quote(100_000, 0, 0, 0, 100_000), "LOS → ABV", """{"adults":1}""", Now);

        var act = () => cart.Add(item, Now.AddHours(2));

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void A_converted_cart_is_closed_for_editing()
    {
        var cart = Cart.Open(Agency, "NGN", Now, TimeSpan.FromDays(7), sessionToken: "sess-abc");
        var orderId = Guid.CreateVersion7();

        cart.Convert(orderId, Now.AddMinutes(5));

        cart.Status.Should().Be(CartStatus.Converted);
        cart.ConvertedOrderId.Should().Be(orderId);

        var act = () => cart.Add(
            CartItem.FromQuote(Quote(1_000, 0, 0, 0, 1_000), "LOS → ABV", """{"adults":1}""", Now),
            Now.AddMinutes(6));
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Walking_away_and_timing_out_are_recorded_differently()
    {
        // Different marketing follow-ups: "you left something behind" versus "your cart expired".
        var walked = Cart.Open(Agency, "NGN", Now, TimeSpan.FromDays(7), sessionToken: "a");
        var timedOut = Cart.Open(Agency, "NGN", Now, TimeSpan.FromHours(1), sessionToken: "b");

        walked.Abandon(Now.AddMinutes(30));
        timedOut.Abandon(Now.AddHours(2));

        walked.Status.Should().Be(CartStatus.Abandoned);
        timedOut.Status.Should().Be(CartStatus.Expired);
    }

    private static Order Place()
    {
        var line = OrderLine.FromQuote(
            Quote(net: 100_000, markup: 10_000, tax: 750, fee: 500, gross: 110_750),
            "LOS → ABV, Air Peace",
            """{"adults":1}""",
            Now);

        return Order.Place(Agency, "ORD-2026-000001", "NGN", BuyerType.Customer, OrderChannel.Storefront, null, [line], Now);
    }

    private static PriceQuote Quote(
        long net,
        long markup,
        long tax,
        long fee,
        long gross,
        string currency = "NGN",
        TimeSpan? validity = null)
    {
        var subject = new PricingSubject(PricedProductType.Flight, currency);
        var price = new PriceBreakdown(
            new Money(net),
            new Money(markup),
            new Money(tax),
            new Money(fee),
            new Money(gross),
            currency,
            MarkupRule: null,
            MarkupRuleInherited: false,
            VatRateBasisPoints: 750,
            PlatformFeeBasisPoints: 0);

        return PriceQuote.Record(Agency, subject, price, Now, validity ?? TimeSpan.FromMinutes(30));
    }
}
