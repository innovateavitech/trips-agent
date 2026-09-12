using FluentAssertions;
using TripsAgent.Application.Commerce;
using TripsAgent.Domain.Common;
using TripsAgent.Domain.Orders;
using TripsAgent.Domain.Pricing;

namespace TripsAgent.UnitTests.Commerce;

/// <summary>
/// The rules the traveller's buying flow rests on, tested where they live: in the domain and in the
/// small pure helpers around it, with no database in sight.
/// </summary>
public class CommerceDomainTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 12, 10, 0, 0, TimeSpan.Zero);

    // ------------------------------------------------------------------------------ party sizes

    [Fact]
    public void A_party_counts_everybody_and_survives_a_round_trip_through_json()
    {
        var party = new PartySize(2, 1, 1);

        party.Total.Should().Be(4);
        PartySize.FromJson(party.ToJson()).Should().Be(party);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("""{"adults":-3}""")]
    public void An_unreadable_pax_breakdown_counts_as_one_adult_rather_than_throwing(string? json)
    {
        // A cart row written before this shape existed, or corrupted by hand, must not crash a page
        // the traveller is looking at. One adult is the smallest sellable party.
        PartySize.FromJson(json).Should().Be(new PartySize(1, 0, 0));
    }

    // ---------------------------------------------------------------- what a line costs the agency

    [Theory]
    [InlineData(OrderLineItemType.Tour)]
    [InlineData(OrderLineItemType.Visa)]
    [InlineData(OrderLineItemType.Package)]
    [InlineData(OrderLineItemType.GroupDeparture)]
    public void An_agency_hosted_line_costs_the_agency_the_platform_fee_and_nothing_else(OrderLineItemType itemType)
    {
        // There is no supplier behind a tour the agency runs itself, so there is no net rate to owe
        // anybody. The whole of what the traveller paid stays in the agency's wallet bar our fee.
        CartPricing.IsAgencyHosted(itemType).Should().BeTrue();
        CartPricing.CostToAgency(Line(itemType)).Should().Be(new Money(500));
    }

    [Theory]
    [InlineData(OrderLineItemType.Flight)]
    [InlineData(OrderLineItemType.Bus)]
    public void A_supplier_line_costs_the_agency_the_net_rate_and_the_platform_fee(OrderLineItemType itemType)
    {
        CartPricing.IsAgencyHosted(itemType).Should().BeFalse();
        CartPricing.CostToAgency(Line(itemType)).Should().Be(new Money(100_500));
    }

    // ----------------------------------------------------------------------------- booking links

    [Fact]
    public void A_booking_link_stores_only_the_hash_of_its_secret()
    {
        var (token, secret) = BookingAccessToken.Issue(Guid.CreateVersion7(), Guid.CreateVersion7(), Now);

        // The secret exists once, in the email. A dump of this table opens nobody's booking.
        token.TokenHash.Should().NotContain(secret);
        token.TokenHash.Should().Be(BookingAccessToken.HashOf(secret));
        token.TokenHash.Should().HaveLength(64);
    }

    [Fact]
    public void A_booking_link_works_until_it_expires_and_not_after()
    {
        var (token, _) = BookingAccessToken.Issue(Guid.CreateVersion7(), Guid.CreateVersion7(), Now, TimeSpan.FromDays(2));

        token.IsUsableAt(Now.AddDays(1)).Should().BeTrue();
        token.IsUsableAt(Now.AddDays(2)).Should().BeFalse();
    }

    [Fact]
    public void A_revoked_booking_link_opens_nothing_even_before_it_expires()
    {
        var (token, _) = BookingAccessToken.Issue(Guid.CreateVersion7(), Guid.CreateVersion7(), Now);

        token.Revoke(Now);

        token.IsUsableAt(Now.AddMinutes(1)).Should().BeFalse();
    }

    [Fact]
    public void Two_links_never_share_a_secret()
    {
        var orderId = Guid.CreateVersion7();
        var agencyId = Guid.CreateVersion7();

        var (_, first) = BookingAccessToken.Issue(agencyId, orderId, Now);
        var (_, second) = BookingAccessToken.Issue(agencyId, orderId, Now);

        first.Should().NotBe(second);
        BookingAccessLinks.LooksLikeSecret(first).Should().BeTrue();
    }

    // ------------------------------------------------------------------------------ cart session

    [Fact]
    public void A_session_token_is_url_safe_and_the_length_the_endpoints_check_for()
    {
        var token = CartService.NewSessionToken();

        token.Should().HaveLength(CartService.SessionTokenLength);
        CartService.LooksLikeToken(token).Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("too-short")]
    [InlineData("' OR 1=1 --aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public void Anything_that_is_not_shaped_like_a_token_is_turned_away_before_the_database(string? token) =>
        CartService.LooksLikeToken(token).Should().BeFalse();

    // -------------------------------------------------------------------------------- host names

    [Theory]
    [InlineData("Lekki-Horizon.com", "lekki-horizon.com")]
    [InlineData("lekki-horizon.com:443", "lekki-horizon.com")]
    [InlineData("lekki-horizon.com.", "lekki-horizon.com")]
    [InlineData("  LEKKI-HORIZON.COM  ", "lekki-horizon.com")]
    public void A_host_name_is_read_the_same_way_however_the_browser_sent_it(string sent, string expected) =>
        StorefrontTenant.NormaliseHost(sent).Should().Be(expected);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_request_with_no_host_belongs_to_no_shop(string? sent) =>
        StorefrontTenant.NormaliseHost(sent).Should().BeNull();

    // -------------------------------------------------------------------------------- cart items

    [Fact]
    public void A_cart_line_remembers_the_seats_checkout_held_for_it_and_forgets_them_afterwards()
    {
        var item = CartItem.FromQuote(Quote(), "Kilimanjaro, 12 March 2027", """{"adults":2}""", 2, Now);
        var holdId = Guid.CreateVersion7();

        item.AttachHold(holdId, Now.AddMinutes(20));
        item.DepartureHoldId.Should().Be(holdId);
        item.HoldExpiresAt.Should().Be(Now.AddMinutes(20));

        item.ForgetHold();
        item.DepartureHoldId.Should().BeNull();
        item.HoldExpiresAt.Should().BeNull();
    }

    [Fact]
    public void Re_pricing_a_cart_line_moves_it_onto_the_new_quote()
    {
        var item = CartItem.FromQuote(Quote(gross: 110_750), "Kilimanjaro", """{"adults":1}""", 1, Now);
        var fresh = Quote(gross: 120_000);

        item.RepriceTo(fresh);

        // The indicative price the traveller sees, and the quote an order line will be frozen from.
        item.PriceQuoteId.Should().Be(fresh.Id);
        item.IndicativeGrossMinor.Should().Be(new Money(120_000));
    }

    [Fact]
    public void Touching_a_cart_gives_it_its_full_life_again()
    {
        var cart = Cart.Open(Guid.CreateVersion7(), "NGN", Now, TimeSpan.FromHours(1), sessionToken: "session");

        cart.KeepAlive(Now.AddMinutes(50), TimeSpan.FromHours(1));

        cart.ExpiresAt.Should().Be(Now.AddMinutes(50).AddHours(1));
        cart.IsOpenAt(Now.AddMinutes(90)).Should().BeTrue();
    }

    [Fact]
    public void Keeping_a_cart_alive_never_shortens_it()
    {
        var cart = Cart.Open(Guid.CreateVersion7(), "NGN", Now, TimeSpan.FromHours(6), sessionToken: "session");

        cart.KeepAlive(Now.AddMinutes(1), TimeSpan.FromMinutes(5));

        cart.ExpiresAt.Should().Be(Now.AddHours(6));
    }

    [Fact]
    public void A_converted_cart_is_no_longer_open()
    {
        var cart = Cart.Open(Guid.CreateVersion7(), "NGN", Now, TimeSpan.FromHours(1), sessionToken: "session");

        cart.Convert(Guid.CreateVersion7(), Now);

        cart.IsOpenAt(Now).Should().BeFalse();
    }

    // ------------------------------------------------------------------------------- wallet holds

    [Fact]
    public void A_hold_can_name_the_one_line_it_reserves_money_for()
    {
        var wallet = TripsAgent.Domain.Payments.Wallet.OpenFor(Guid.CreateVersion7(), "NGN");
        wallet.Credit(new Money(500_000));

        var orderId = Guid.CreateVersion7();
        var lineId = Guid.CreateVersion7();

        var hold = wallet.PlaceHold(new Money(100_000), Now, TimeSpan.FromDays(1), orderId, lineId);

        // What lets one line of a mixed cart be captured, failed or refunded on its own.
        hold.OrderId.Should().Be(orderId);
        hold.OrderLineId.Should().Be(lineId);
    }

    private static OrderLine Line(OrderLineItemType itemType)
    {
        var productType = itemType switch
        {
            OrderLineItemType.Flight => PricedProductType.Flight,
            OrderLineItemType.Bus => PricedProductType.Bus,
            OrderLineItemType.Tour => PricedProductType.Tour,
            OrderLineItemType.Visa => PricedProductType.Visa,
            OrderLineItemType.GroupDeparture => PricedProductType.GroupDeparture,
            _ => PricedProductType.Package,
        };

        return OrderLine.FromQuote(Quote(productType: productType), "A thing somebody bought", """{"adults":1}""", Now);
    }

    private static PriceQuote Quote(long gross = 110_750, PricedProductType productType = PricedProductType.Tour)
    {
        var subject = new PricingSubject(productType, "NGN");

        var price = new PriceBreakdown(
            new Money(100_000),
            new Money(10_000),
            new Money(750),
            new Money(500),
            new Money(gross),
            "NGN",
            MarkupRule: null,
            MarkupRuleInherited: false,
            VatRateBasisPoints: 750,
            PlatformFeeBasisPoints: 50);

        return PriceQuote.Record(Guid.CreateVersion7(), subject, price, Now, TimeSpan.FromMinutes(30));
    }
}
