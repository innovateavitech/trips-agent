using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TripsAgent.Application.Checkout;
using TripsAgent.Application.Commerce;
using TripsAgent.Contracts.Commerce;
using TripsAgent.Domain.Assets;
using TripsAgent.Domain.Catalog;
using TripsAgent.Domain.Common;
using TripsAgent.Domain.Orders;
using TripsAgent.Domain.Payments;
using TripsAgent.Domain.Pricing;
using TripsAgent.IntegrationTests.Persistence;
using TripsAgent.IntegrationTests.Suppliers;

namespace TripsAgent.IntegrationTests.Commerce;

/// <summary>
/// The traveller's buying flow against real PostgreSQL (build plan F5, issue 61): a cart on an
/// agency's storefront, guest checkout, a card payment, and what each of those does to the money.
/// </summary>
/// <remarks>
/// <para>
/// The gateway is faked — no test may reach a card network — but nothing else is. The wallet, the
/// ledger, the holds, the seats and row-level security are all the real ones, because the claims
/// being made here are claims about them.
/// </para>
/// <para>
/// Every read and write happens inside the agency the host name resolved to, through the ordinary
/// tenant filter. There is no <c>IgnoreQueryFilters</c> anywhere in this file, and there must not be.
/// </para>
/// </remarks>
[Collection(PostgresCollection.Name)]
public sealed class StorefrontCheckoutTests : IAsyncLifetime
{
    private const string Host = FixedStorefrontDirectory.Host;

    /// <summary>₦150,000 a head, as the agent typed it on the product.</summary>
    private static readonly Money TourPrice = StorefrontSeed.TourPrice;

    private readonly PostgresFixture _postgres;
    private TripsAfricaStub _stub = null!;

    public StorefrontCheckoutTests(PostgresFixture postgres) => _postgres = postgres;

    public async Task InitializeAsync() => _stub = await TripsAfricaStub.StartAsync();

    public async Task DisposeAsync() => await _stub.DisposeAsync();

    // --------------------------------------------------------------------------------- the cart

    [Fact]
    public async Task A_traveller_fills_a_cart_without_signing_in_to_anything()
    {
        await using var harness = await BookingPipelineHarness.CreateAsync(_postgres, _stub);
        var productId = await SeedTourAsync(harness);

        var cart = await AddAsync(harness, null, new AddCartItemRequest(ProductId: productId, Adults: 2));

        cart.SessionToken.Should().NotBeNullOrWhiteSpace("the cart is found by its token, not by an account");
        cart.Items.Should().ContainSingle();
        cart.Items[0].Adults.Should().Be(2);

        // Two adults at ₦150,000, and no markup rule, so the traveller pays what the agent typed.
        cart.TotalMinor.Should().Be(TourPrice.AmountMinor * 2);
        cart.Currency.Should().Be("NGN");
    }

    [Fact]
    public async Task A_cart_holds_a_flight_a_visa_and_a_departure_at_once()
    {
        await using var harness = await BookingPipelineHarness.CreateAsync(_postgres, _stub);
        var productId = await SeedTourAsync(harness);
        var departureId = await SeedDepartureAsync(harness, productId);
        var offerId = await harness.SeedFlightOfferAsync();

        var cart = await AddAsync(harness, null, new AddCartItemRequest(ProductId: productId));
        cart = await AddAsync(harness, cart.SessionToken, new AddCartItemRequest(DepartureId: departureId, Adults: 2));
        cart = await AddAsync(harness, cart.SessionToken, new AddCartItemRequest(OfferId: offerId));

        cart.Items.Select(item => item.ItemType).Should().BeEquivalentTo(["Tour", "GroupDeparture", "Flight"]);

        // Nothing is held yet: a seat a browser tab is sitting on is a seat nobody else can buy.
        cart.Items.Should().OnlyContain(item => item.HoldExpiresAt == null);
        await SeatsLeftShouldBe(harness, departureId, 10);
    }

    [Fact]
    public async Task A_cart_token_from_one_shop_finds_nothing_at_another_host()
    {
        await using var harness = await BookingPipelineHarness.CreateAsync(_postgres, _stub);
        var productId = await SeedTourAsync(harness);
        var cart = await AddAsync(harness, null, new AddCartItemRequest(ProductId: productId));

        var elsewhere = await harness.InAgencyScopeAsync(provider =>
            provider.GetRequiredService<CartService>().GetAsync("someone-elses-shop.test", cart.SessionToken));

        // The same 404 a wrong token gets: nobody anonymous learns which agencies are on the platform.
        elsewhere.Should().BeOfType<StoreResult<CartResponse>.NotFound>();
    }

    [Fact]
    public async Task A_cart_refuses_more_infants_than_there_are_laps_to_put_them_on()
    {
        await using var harness = await BookingPipelineHarness.CreateAsync(_postgres, _stub);
        var productId = await SeedTourAsync(harness);

        var outcome = await harness.InAgencyScopeAsync(provider =>
            provider.GetRequiredService<CartService>()
                .AddAsync(Host, null, new AddCartItemRequest(ProductId: productId, Adults: 1, Infants: 2)));

        outcome.Should().BeOfType<StoreResult<CartResponse>.Invalid>()
            .Which.Problems.Should().ContainSingle().Which.Field.Should().Be("infants");
    }

    // ----------------------------------------------------------------------------- the checkout

    [Fact]
    public async Task Checking_out_holds_the_seats_places_the_order_and_charges_nothing_yet()
    {
        await using var harness = await BookingPipelineHarness.CreateAsync(_postgres, _stub);
        var productId = await SeedTourAsync(harness);
        var departureId = await SeedDepartureAsync(harness, productId);

        var cart = await AddAsync(harness, null, new AddCartItemRequest(DepartureId: departureId, Adults: 2));
        var started = await CheckOutAsync(harness, cart);

        started.AuthorizationUrl.Should().StartWith("https://pay.test/", "cards are entered on the gateway's page");
        started.TotalMinor.Should().Be(TourPrice.AmountMinor * 2);

        // The seats are ours for the length of the payment window.
        await SeatsLeftShouldBe(harness, departureId, 8);

        await using var db = harness.AsAgency();
        var order = await db.Orders.AsNoTracking().Include(candidate => candidate.Lines).SingleAsync();
        order.Status.Should().Be(OrderStatus.PendingPayment);
        order.Channel.Should().Be(OrderChannel.Storefront);
        order.BuyerType.Should().Be(BuyerType.Customer);
        order.PaidAt.Should().BeNull("nothing is charged until the gateway says the payment went through");

        // The guest is a customer record, created by this booking (FRD §2.8 RS-1).
        var customer = await db.Customers.AsNoTracking().SingleAsync();
        customer.Email.Should().Be("traveller@example.com");
        order.CustomerId.Should().Be(customer.Id);

        // The wallet has not moved: the agency funds nothing on a storefront sale.
        (await harness.WalletAsync()).AvailableMinor.Should().Be(Money.FromMajor(10_000_000));
    }

    [Fact]
    public async Task A_checkout_that_fails_gives_every_seat_it_held_straight_back()
    {
        await using var harness = await BookingPipelineHarness.CreateAsync(_postgres, _stub);
        var productId = await SeedTourAsync(harness);
        var departureId = await SeedDepartureAsync(harness, productId);

        var cart = await AddAsync(harness, null, new AddCartItemRequest(DepartureId: departureId, Adults: 2));

        // The agent closes the departure between the cart and the checkout. Re-pricing refuses it, and
        // the seats this checkout had already taken have to go back.
        await using (var db = harness.AsAgency())
        {
            var departure = await db.Departures.SingleAsync(candidate => candidate.Id == departureId);
            departure.Close(harness.Clock.GetUtcNow());
            await db.SaveChangesAsync();
        }

        var outcome = await harness.InAgencyScopeAsync(provider =>
            provider.GetRequiredService<StorefrontCheckoutService>()
                .BeginAsync(Host, cart.SessionToken, Checkout(cart)));

        outcome.Should().BeOfType<StoreResult<BeginCheckoutResponse>.Refused>();
        await SeatsLeftShouldBe(harness, departureId, 10);
    }

    [Fact]
    public async Task A_departure_sold_on_a_deposit_asks_for_the_deposit_and_writes_the_schedule()
    {
        await using var harness = await BookingPipelineHarness.CreateAsync(_postgres, _stub);
        var productId = await SeedTourAsync(harness);
        var departureId = await SeedDepartureAsync(harness, productId, depositPercentBasisPoints: 2_500);

        var cart = await AddAsync(harness, null, new AddCartItemRequest(DepartureId: departureId, Adults: 2));
        var started = await CheckOutAsync(harness, cart);

        // A quarter of ₦300,000 up front; the rest is billed by the schedule at the cutoff.
        started.TotalMinor.Should().Be(TourPrice.AmountMinor * 2);
        started.AmountDueMinor.Should().Be(TourPrice.AmountMinor * 2 / 4);

        await using var db = harness.AsAgency();
        var schedule = await db.BookingPaymentSchedules.AsNoTracking().Include(item => item.Items).SingleAsync();
        schedule.PaxCount.Should().Be(2);
        schedule.Items.Should().HaveCount(2);
        schedule.Items.OrderBy(item => item.Sequence).First().Label.Should().Be("Deposit");
        schedule.TotalMinor.Should().Be(new Money(TourPrice.AmountMinor * 2), "the lines add up to the price exactly");
    }

    // ------------------------------------------------------------------------------ the payment

    [Fact]
    public async Task A_paid_booking_credits_the_wallet_captures_the_fee_and_confirms_the_seats()
    {
        await using var harness = await BookingPipelineHarness.CreateAsync(_postgres, _stub);
        var productId = await SeedTourAsync(harness);
        var departureId = await SeedDepartureAsync(harness, productId);

        var cart = await AddAsync(harness, null, new AddCartItemRequest(DepartureId: departureId, Adults: 2));
        var started = await CheckOutAsync(harness, cart);

        var before = (await harness.WalletAsync()).BalanceMinor;
        await PayAsync(harness, started.Reference);

        await using var db = harness.AsAgency();

        var order = await db.Orders.AsNoTracking().Include(candidate => candidate.Lines).SingleAsync();
        order.PaidFrom.Should().Be(OrderPaymentMethod.Card);
        order.Status.Should().Be(OrderStatus.Confirmed, "the agency hosts its own departure, so there is nothing to wait for");
        order.Lines.Single().FulfilmentStatus.Should().Be(FulfilmentStatus.Confirmed);

        // The traveller's money is in the agency's wallet, less the platform's fee — which this
        // agency does not pay, so the whole of it stays. One money path, not two.
        var wallet = await harness.WalletAsync();
        wallet.BalanceMinor.Should().Be(before + new Money(TourPrice.AmountMinor * 2));
        wallet.ReservedMinor.Should().Be(Money.Zero, "the hold was captured with the confirmation");

        // The seats are confirmed rather than merely held, which is what moves the departure's status.
        var departure = await db.Departures.AsNoTracking().SingleAsync(candidate => candidate.Id == departureId);
        departure.CapacityConfirmed.Should().Be(2);
        departure.CapacityReserved.Should().Be(0);

        // And the traveller has a way back in, with no account to sign in to.
        (await db.BookingAccessTokens.AsNoTracking().CountAsync()).Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Settling_the_same_payment_twice_credits_the_wallet_once()
    {
        await using var harness = await BookingPipelineHarness.CreateAsync(_postgres, _stub);
        var productId = await SeedTourAsync(harness);
        var cart = await AddAsync(harness, null, new AddCartItemRequest(ProductId: productId));
        var started = await CheckOutAsync(harness, cart);

        var before = (await harness.WalletAsync()).BalanceMinor;

        // The webhook and the traveller's own return page, which routinely both arrive.
        var first = await PayAsync(harness, started.Reference);
        var second = await SettleAsync(harness, started.Reference);

        first.Should().Be(CustomerPaymentOutcome.Settled);
        second.Should().Be(CustomerPaymentOutcome.AlreadySettled);

        (await harness.WalletAsync()).BalanceMinor.Should().Be(
            before + TourPrice, "the traveller paid once, so the wallet is credited once");

        await using var db = harness.AsAgency();
        (await db.PaymentTransactions.AsNoTracking().CountAsync(payment => payment.LedgerTransactionGroupId != null))
            .Should().Be(1, "one payment is posted to the ledger once, however many times it is settled");
    }

    [Fact]
    public async Task A_payment_the_gateway_declines_funds_nothing()
    {
        await using var harness = await BookingPipelineHarness.CreateAsync(_postgres, _stub);
        var productId = await SeedTourAsync(harness);
        var cart = await AddAsync(harness, null, new AddCartItemRequest(ProductId: productId));
        var started = await CheckOutAsync(harness, cart);

        var reference = await PaymentReferenceAsync(harness, started.Reference);
        harness.Gateway.Fail(reference);

        var outcome = await SettleAsync(harness, started.Reference);

        outcome.Should().Be(CustomerPaymentOutcome.Failed);

        await using var db = harness.AsAgency();
        (await db.Orders.AsNoTracking().SingleAsync()).PaidAt.Should().BeNull();
        (await db.WalletHolds.AsNoTracking().CountAsync()).Should().Be(0);
    }

    // ------------------------------------------------------------------- manage my booking

    [Fact]
    public async Task The_link_in_the_travellers_email_opens_their_booking_and_nobody_elses()
    {
        await using var harness = await BookingPipelineHarness.CreateAsync(_postgres, _stub);
        var productId = await SeedTourAsync(harness);
        var cart = await AddAsync(harness, null, new AddCartItemRequest(ProductId: productId, Adults: 2));
        var started = await CheckOutAsync(harness, cart);
        await PayAsync(harness, started.Reference);

        // The link the traveller was emailed. Issued through the same path the payment uses, which
        // is the only way to hold a secret: the row keeps nothing but its hash.
        var url = await harness.InAgencyScopeAsync(async provider =>
        {
            var db = provider.GetRequiredService<TripsAgent.Application.Persistence.IAppDbContext>();
            var orderId = await db.Orders.Select(order => order.Id).FirstAsync();

            return await provider.GetRequiredService<BookingAccessLinks>().UrlForAsync(harness.AgencyId, orderId);
        });

        var link = url ?? throw new InvalidOperationException("A paid booking always has a link.");
        link.Should().StartWith($"https://{Host}{BookingAccessLinks.Path}/", "the link is on the agency's own site");

        var secret = link[(link.LastIndexOf('/') + 1)..];

        var opened = await harness.InAgencyScopeAsync(provider =>
            provider.GetRequiredService<ManageBookingQueries>().OpenAsync(Host, secret));

        var booking = opened.Should().BeOfType<StoreResult<ManageBookingResponse>.Done>().Which.Value;
        booking.Reference.Should().Be(started.Reference);
        booking.AgencyName.Should().Be("Lagos Travel Limited", "the page is the agency's, and never ours");
        booking.Lines.Should().ContainSingle().Which.Status.Should().Be("Confirmed");
        booking.PaidMinor.Should().Be(TourPrice.AmountMinor * 2);

        // A secret that is not a secret of ours opens nothing, and says nothing about why.
        var wrong = await harness.InAgencyScopeAsync(provider =>
            provider.GetRequiredService<ManageBookingQueries>().OpenAsync(Host, new string('a', 43)));

        wrong.Should().BeOfType<StoreResult<ManageBookingResponse>.NotFound>();
    }

    // ------------------------------------------------------------------------------- the refund

    [Fact]
    public async Task Refunding_a_card_booking_sends_the_money_back_to_the_card_and_not_to_the_wallet()
    {
        await using var harness = await BookingPipelineHarness.CreateAsync(_postgres, _stub);
        var productId = await SeedTourAsync(harness);
        var cart = await AddAsync(harness, null, new AddCartItemRequest(ProductId: productId, Adults: 2));
        var started = await CheckOutAsync(harness, cart);
        await PayAsync(harness, started.Reference);

        var afterPayment = (await harness.WalletAsync()).BalanceMinor;

        // The agency cancels it, which is the resolution queue's job: the line is flagged, then
        // refunded by the agent.
        await FlagAsync(harness);
        await harness.ResolveAsync(started.Reference, ResolutionChoice.Refund);

        var sent = harness.Gateway.Refunds.Should().ContainSingle().Which;
        sent.Amount.Should().Be(new Money(TourPrice.AmountMinor * 2), "the traveller gets back what they paid");

        await using var db = harness.AsAgency();
        var refund = await db.Refunds.AsNoTracking().SingleAsync();
        refund.Method.Should().Be(RefundMethod.Gateway);

        // The money left the wallet again: it arrived from the traveller and has gone back to them.
        (await harness.WalletAsync()).BalanceMinor.Should().Be(afterPayment - new Money(TourPrice.AmountMinor * 2));

        var order = await db.Orders.AsNoTracking().Include(candidate => candidate.Lines).SingleAsync();
        order.Status.Should().Be(OrderStatus.Refunded);
        order.Lines.Single().ResolutionStatus.Should().Be(ResolutionStatus.ResolvedRefunded);
    }

    [Fact]
    public async Task A_gateway_that_refuses_a_refund_sends_nothing_and_tells_a_person()
    {
        await using var harness = await BookingPipelineHarness.CreateAsync(_postgres, _stub);
        var productId = await SeedTourAsync(harness);
        var cart = await AddAsync(harness, null, new AddCartItemRequest(ProductId: productId));
        var started = await CheckOutAsync(harness, cart);
        await PayAsync(harness, started.Reference);

        harness.Gateway.RefundAnswer = new TripsAgent.Application.Payments.GatewayRefund(
            TripsAgent.Application.Payments.GatewayRefundOutcome.Refused, "refused", null, "Transaction is too old to refund");

        var afterPayment = (await harness.WalletAsync()).BalanceMinor;

        await FlagAsync(harness);

        var act = () => harness.ResolveAsync(started.Reference, ResolutionChoice.Refund);

        // The agent is told, rather than the booking being closed as refunded with nothing sent.
        (await act.Should().ThrowAsync<CheckoutRefusedException>())
            .Which.Detail.Should().Contain("too old to refund");

        // Nothing was invented: the wallet still holds the traveller's money, and a person is told.
        (await harness.WalletAsync()).BalanceMinor.Should().Be(afterPayment);
        harness.Alerts.Raised.Should().Contain(alert => alert.Title.Contains("refuse", StringComparison.OrdinalIgnoreCase));

        await using var db = harness.AsAgency();
        (await db.Refunds.AsNoTracking().CountAsync()).Should().Be(0, "nothing reached a card, so nothing is recorded");

        var order = await db.Orders.AsNoTracking().Include(candidate => candidate.Lines).SingleAsync();
        order.Lines.Single().FulfilmentStatus.Should().Be(
            FulfilmentStatus.FailedNeedsResolution, "it stays in the queue until the money really goes back");
    }

    // ----------------------------------------------------------------------------------- helpers

    private static BeginCheckoutRequest Checkout(CartResponse cart) =>
        new(
            new CheckoutContactRequest("Ngozi Adeyemi", "traveller@example.com", "0803 000 1122"),
            cart.Items.Select(item => new CheckoutLineRequest(item.Id, [])).ToList());

    private static Task<CartResponse> AddAsync(BookingPipelineHarness harness, string? token, AddCartItemRequest request) =>
        harness.InAgencyScopeAsync(async provider =>
        {
            var outcome = await provider.GetRequiredService<CartService>().AddAsync(Host, token, request);

            return outcome.Should().BeOfType<StoreResult<CartResponse>.Done>().Which.Value;
        });

    private static Task<BeginCheckoutResponse> CheckOutAsync(BookingPipelineHarness harness, CartResponse cart) =>
        harness.InAgencyScopeAsync(async provider =>
        {
            var outcome = await provider.GetRequiredService<StorefrontCheckoutService>()
                .BeginAsync(Host, cart.SessionToken, Checkout(cart));

            return outcome.Should().BeOfType<StoreResult<BeginCheckoutResponse>.Done>().Which.Value;
        });

    /// <summary>Tells the gateway the payer paid, then settles it — the webhook's half of the flow.</summary>
    private static async Task<CustomerPaymentOutcome> PayAsync(BookingPipelineHarness harness, string orderReference)
    {
        harness.Gateway.Succeed(await PaymentReferenceAsync(harness, orderReference));

        return await SettleAsync(harness, orderReference);
    }

    private static async Task<CustomerPaymentOutcome> SettleAsync(BookingPipelineHarness harness, string orderReference)
    {
        var reference = await PaymentReferenceAsync(harness, orderReference);

        return await harness.InJobScopeAsync(provider =>
            provider.GetRequiredService<CustomerOrderPayments>().SettleAsync(reference));
    }

    /// <summary>Our reference for the payment attempt behind an order, which the gateway quotes back.</summary>
    private static async Task<string> PaymentReferenceAsync(BookingPipelineHarness harness, string orderReference)
    {
        await using var db = harness.AsAgency();

        return await db.PaymentTransactions.AsNoTracking()
            .Where(payment => db.Orders.Any(order => order.Id == payment.OrderId && order.OrderNumber == orderReference))
            .Select(payment => payment.Reference)
            .SingleAsync();
    }

    /// <summary>Puts the booking's line in the resolution queue, as a cancellation would.</summary>
    private static Task<bool> FlagAsync(BookingPipelineHarness harness) =>
        harness.InAgencyScopeAsync(async provider =>
        {
            var db = provider.GetRequiredService<TripsAgent.Application.Persistence.IAppDbContext>();
            var outbox = provider.GetRequiredService<TripsAgent.Application.Messaging.IOutbox>();

            var order = await db.Orders.Include(candidate => candidate.Lines).SingleAsync();

            FailedLines.FlagAgencyCancellation(
                order, order.Lines[0], "The agency called this departure off.", harness.Clock.GetUtcNow(), outbox);

            await db.SaveChangesAsync();
            return true;
        });

    private static async Task SeatsLeftShouldBe(BookingPipelineHarness harness, Guid departureId, int expected)
    {
        await using var db = harness.AsAgency();
        var departure = await db.Departures.AsNoTracking().SingleAsync(candidate => candidate.Id == departureId);

        departure.Seats.SeatsLeft.Should().Be(expected);
    }

    private static Task<Guid> SeedTourAsync(BookingPipelineHarness harness) =>
        StorefrontSeed.TourAsync(harness);

    private static Task<Guid> SeedDepartureAsync(
        BookingPipelineHarness harness,
        Guid productId,
        int depositPercentBasisPoints = 0) =>
        StorefrontSeed.DepartureAsync(harness, productId, depositPercentBasisPoints);
}
