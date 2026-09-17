using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TripsAgent.Application.Commerce;
using TripsAgent.Application.Persistence;
using TripsAgent.Contracts.Commerce;
using TripsAgent.Domain.Tenancy;
using TripsAgent.IntegrationTests.Persistence;
using TripsAgent.IntegrationTests.Suppliers;

namespace TripsAgent.IntegrationTests.Commerce;

/// <summary>
/// The storefront, attacked rather than used (issue 110's internal adversarial pass).
/// </summary>
/// <remarks>
/// <para>
/// Every test here is a hole that was open, written so that closing it stays closed. They are the
/// two kinds of mistake this system can least afford: handing a traveller's booking to somebody who
/// only knows its number, and letting an agency that has been switched off go on selling.
/// </para>
/// <para>
/// The same fixtures as <see cref="StorefrontCheckoutTests"/> — real PostgreSQL, real row-level
/// security, a faked gateway — because the claims are about the real flow.
/// </para>
/// </remarks>
[Collection(PostgresCollection.Name)]
public sealed class StorefrontAdversarialTests : IAsyncLifetime
{
    private const string Host = FixedStorefrontDirectory.Host;

    private readonly PostgresFixture _postgres;
    private TripsAfricaStub _stub = null!;

    public StorefrontAdversarialTests(PostgresFixture postgres) => _postgres = postgres;

    public async Task InitializeAsync() => _stub = await TripsAfricaStub.StartAsync();

    public async Task DisposeAsync() => await _stub.DisposeAsync();

    // ------------------------------------------------------- guessing at somebody else's booking

    [Fact]
    public async Task A_booking_cannot_be_opened_by_guessing_its_order_number()
    {
        await using var harness = await BookingPipelineHarness.CreateAsync(_postgres, _stub);
        var (reference, sessionToken) = await BuyAsync(harness);

        // The browser that bought it sees its own receipt, and the link to manage the booking.
        var mine = await StatusAsync(harness, reference, sessionToken);
        var receipt = mine.Should().BeOfType<StoreResult<CheckoutStatusResponse>.Done>().Which.Value;
        receipt.Status.Should().Be("paid");
        receipt.ManageUrl.Should().NotBeNullOrWhiteSpace();

        var linksBefore = await LinkCountAsync(harness);

        // Somebody who has only counted up to the order number is told nothing — order numbers are
        // gapless per agency, so knowing one proves nothing at all.
        (await StatusAsync(harness, reference, sessionToken: null))
            .Should().BeOfType<StoreResult<CheckoutStatusResponse>.NotFound>("an order number is not a secret");

        (await StatusAsync(harness, reference, sessionToken: new string('b', 43)))
            .Should().BeOfType<StoreResult<CheckoutStatusResponse>.NotFound>();

        (await LinkCountAsync(harness)).Should().Be(
            linksBefore, "a refused attempt must not mint a manage-booking link either");
    }

    // ------------------------------------------------------------- selling while switched off

    [Fact]
    public async Task A_suspended_agencys_storefront_sells_nothing_even_when_called_directly()
    {
        await using var harness = await BookingPipelineHarness.CreateAsync(_postgres, _stub);
        var productId = await StorefrontSeed.TourAsync(harness);

        // A traveller who already has a cart when the agency is suspended mid-visit.
        var cart = await AddAsync(harness, null, new AddCartItemRequest(ProductId: productId, Adults: 2));

        await SetStatusAsync(harness, AgencyStatus.Suspended);

        // The site itself reports "offline"; these are the endpoints underneath it, called the way
        // an attacker would call them — with the host name and nothing else.
        (await TryAddAsync(harness, cart.SessionToken, new AddCartItemRequest(ProductId: productId)))
            .Should().BeOfType<StoreResult<CartResponse>.NotFound>("decision 14: no new bookings");

        var checkout = await harness.InAgencyScopeAsync(provider =>
            provider.GetRequiredService<StorefrontCheckoutService>().BeginAsync(
                Host,
                cart.SessionToken,
                new BeginCheckoutRequest(
                    new CheckoutContactRequest("Ngozi Adeyemi", "traveller@example.com", "0803 000 1122"),
                    cart.Items.Select(item => new CheckoutLineRequest(item.Id, [])).ToList())));

        checkout.Should().BeOfType<StoreResult<BeginCheckoutResponse>.NotFound>(
            "a suspended agency must not be able to take a traveller's money");

        await using var db = harness.AsAgency();
        (await db.Orders.AsNoTracking().CountAsync()).Should().Be(0, "nothing was sold");
        (await db.PaymentTransactions.AsNoTracking().CountAsync()).Should().Be(0, "nobody was charged");
    }

    [Fact]
    public async Task A_traveller_who_already_paid_keeps_their_booking_when_the_agency_is_suspended()
    {
        await using var harness = await BookingPipelineHarness.CreateAsync(_postgres, _stub);
        var (reference, sessionToken) = await BuyAsync(harness);

        var receipt = (await StatusAsync(harness, reference, sessionToken))
            .Should().BeOfType<StoreResult<CheckoutStatusResponse>.Done>().Which.Value;
        var link = receipt.ManageUrl ?? throw new InvalidOperationException("A paid booking always has a link.");
        var secret = link[(link.LastIndexOf('/') + 1)..];

        await SetStatusAsync(harness, AgencyStatus.Suspended);

        // Decision 14 again, from the other side: the shop is shut, and the people who already
        // bought from it keep their booking and their documents.
        var opened = await harness.InAgencyScopeAsync(provider =>
            provider.GetRequiredService<ManageBookingQueries>().OpenAsync(Host, secret));

        opened.Should().BeOfType<StoreResult<ManageBookingResponse>.Done>()
            .Which.Value.Reference.Should().Be(reference);

        // Terminated is the end of the relationship, and of the link.
        await SetStatusAsync(harness, AgencyStatus.Terminated);

        (await harness.InAgencyScopeAsync(provider =>
                provider.GetRequiredService<ManageBookingQueries>().OpenAsync(Host, secret)))
            .Should().BeOfType<StoreResult<ManageBookingResponse>.NotFound>();
    }

    // ---------------------------------------------------------- making us call the gateway for free

    [Fact]
    public async Task The_return_page_asks_the_gateway_only_about_a_payment_of_the_booking_it_shows()
    {
        await using var harness = await BookingPipelineHarness.CreateAsync(_postgres, _stub);
        var productId = await StorefrontSeed.TourAsync(harness);

        // Two travellers on the same shop, both sent to the gateway and neither back yet.
        var mine = await BeginAsync(harness, productId);
        var theirs = await BeginAsync(harness, productId);

        // A reference that is nobody's. Before issue 173 the page put it to the gateway as it was.
        (await ReturnAsync(harness, mine.Reference, mine.SessionToken, "PAY-2026-NOT-OURS"))
            .Should().BeOfType<StoreResult<CheckoutStatusResponse>.Done>()
            .Which.Value.Status.Should().Be("pending");

        // Another traveller's real, pending payment, presented on this booking's page.
        (await ReturnAsync(harness, mine.Reference, mine.SessionToken, theirs.PaymentReference))
            .Should().BeOfType<StoreResult<CheckoutStatusResponse>.Done>()
            .Which.Value.Status.Should().Be("pending");

        // This booking's own payment, from a browser that did not buy it: refused before anything else.
        (await ReturnAsync(harness, mine.Reference, theirs.SessionToken, mine.PaymentReference))
            .Should().BeOfType<StoreResult<CheckoutStatusResponse>.NotFound>();

        harness.Gateway.Verified.Should().BeEmpty(
            "the gateway hears only about a payment of the booking on the page, from the browser that bought it");

        // The control: the traveller's own payment, from their own browser, is asked about — once.
        harness.Gateway.Succeed(mine.PaymentReference);

        (await ReturnAsync(harness, mine.Reference, mine.SessionToken, mine.PaymentReference))
            .Should().BeOfType<StoreResult<CheckoutStatusResponse>.Done>()
            .Which.Value.Status.Should().Be("paid");

        harness.Gateway.Verified.Should().Equal(mine.PaymentReference);

        // Settled now, so a refresh has nothing left to ask.
        await ReturnAsync(harness, mine.Reference, mine.SessionToken, mine.PaymentReference);

        harness.Gateway.Verified.Should().ContainSingle();
    }

    // ----------------------------------------------------------------------------------- helpers

    /// <summary>A traveller sent to the gateway and not back yet: their order, their browser, their payment.</summary>
    private sealed record Started(string Reference, string SessionToken, string PaymentReference);

    /// <summary>A tour in a new cart, checked out as far as the gateway's page, as a traveller would.</summary>
    private static async Task<Started> BeginAsync(BookingPipelineHarness harness, Guid productId)
    {
        var cart = await AddAsync(harness, null, new AddCartItemRequest(ProductId: productId, Adults: 2));

        var started = await harness.InAgencyScopeAsync(async provider =>
        {
            var outcome = await provider.GetRequiredService<StorefrontCheckoutService>().BeginAsync(
                Host,
                cart.SessionToken,
                new BeginCheckoutRequest(
                    new CheckoutContactRequest("Ngozi Adeyemi", "traveller@example.com", "0803 000 1122"),
                    cart.Items.Select(item => new CheckoutLineRequest(item.Id, [])).ToList()));

            return outcome.Should().BeOfType<StoreResult<BeginCheckoutResponse>.Done>().Which.Value;
        });

        return new Started(started.Reference, cart.SessionToken, await PaymentReferenceAsync(harness, started.Reference));
    }

    /// <summary>A tour, bought and paid for, as a traveller would.</summary>
    private static async Task<(string Reference, string SessionToken)> BuyAsync(BookingPipelineHarness harness)
    {
        var started = await BeginAsync(harness, await StorefrontSeed.TourAsync(harness));

        harness.Gateway.Succeed(started.PaymentReference);
        await harness.InJobScopeAsync(provider =>
            provider.GetRequiredService<CustomerOrderPayments>().SettleAsync(started.PaymentReference));

        return (started.Reference, started.SessionToken);
    }

    /// <summary>
    /// The gateway's return page, reached the way an anonymous browser reaches it: no tenant until
    /// the host names one.
    /// </summary>
    private static Task<StoreResult<CheckoutStatusResponse>> ReturnAsync(
        BookingPipelineHarness harness,
        string reference,
        string? sessionToken,
        string? paymentReference) =>
        harness.InJobScopeAsync(provider =>
            provider.GetRequiredService<StorefrontCheckoutService>().ReturnAsync(
                Host, reference, sessionToken, paymentReference));

    private static Task<StoreResult<CheckoutStatusResponse>> StatusAsync(
        BookingPipelineHarness harness,
        string reference,
        string? sessionToken) =>
        harness.InAgencyScopeAsync(provider =>
            provider.GetRequiredService<StorefrontCheckoutService>().StatusAsync(Host, reference, sessionToken));

    private static async Task<int> LinkCountAsync(BookingPipelineHarness harness)
    {
        await using var db = harness.AsAgency();
        return await db.BookingAccessTokens.AsNoTracking().CountAsync();
    }

    private static Task<StoreResult<CartResponse>> TryAddAsync(
        BookingPipelineHarness harness,
        string? token,
        AddCartItemRequest request) =>
        harness.InAgencyScopeAsync(provider =>
            provider.GetRequiredService<CartService>().AddAsync(Host, token, request));

    private static async Task<CartResponse> AddAsync(
        BookingPipelineHarness harness,
        string? token,
        AddCartItemRequest request) =>
        (await TryAddAsync(harness, token, request))
            .Should().BeOfType<StoreResult<CartResponse>.Done>().Which.Value;

    private static async Task<string> PaymentReferenceAsync(BookingPipelineHarness harness, string orderReference)
    {
        await using var db = harness.AsAgency();

        return await db.PaymentTransactions.AsNoTracking()
            .Where(payment => db.Orders.Any(order => order.Id == payment.OrderId && order.OrderNumber == orderReference))
            .Select(payment => payment.Reference)
            .SingleAsync();
    }

    /// <summary>
    /// Moves the agency's lifecycle on, as a Trips admin would. Written as SQL from the schema
    /// owner's connection because the point of the test is what the storefront does afterwards, not
    /// how the status got there.
    /// </summary>
    private static async Task SetStatusAsync(BookingPipelineHarness harness, AgencyStatus status)
    {
        await using var owner = harness.AsOwner();

        // status_changed_at and status_reason go together or not at all, which the database checks.
        await owner.Database.ExecuteSqlRawAsync(
            "update tenancy.agencies set status = {0}, status_reason = {1}, status_changed_at = {2} where id = {3}",
            status.ToString(),
            "Set by an adversarial test.",
            harness.Clock.GetUtcNow(),
            harness.AgencyId);
    }
}
