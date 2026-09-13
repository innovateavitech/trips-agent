using TripsAgent.Domain.Common;
using TripsAgent.Domain.Pricing;

namespace TripsAgent.Domain.Orders;

/// <summary>
/// One thing somebody is thinking about buying, at a price that may no longer be available.
/// </summary>
/// <remarks>
/// <see cref="IndicativeGrossMinor"/> is for display, nothing else. It is named that way so no
/// future reader mistakes it for a price we have committed to. Checkout builds the real line from
/// the quote — see the remarks on <see cref="Cart"/>.
/// </remarks>
public sealed class CartItem : Entity, ITenantScoped
{
    private CartItem()
    {
        Currency = string.Empty;
        TitleSnapshot = string.Empty;
        PaxBreakdown = "{}";
    }

    /// <summary>
    /// Puts a quoted thing in a cart. Unlike <see cref="OrderLine.FromQuote"/> this does NOT refuse
    /// an expired quote: a cart is allowed to hold a stale price, and checkout is where that is
    /// settled. Refusing here would empty somebody's cart while they were still looking at it.
    /// </summary>
    public static CartItem FromQuote(
        PriceQuote quote,
        string titleSnapshot,
        string paxBreakdown,
        int paxCount,
        DateTimeOffset now,
        Guid? departureId = null,
        Guid? supplierOfferId = null)
    {
        ArgumentNullException.ThrowIfNull(quote);
        ArgumentException.ThrowIfNullOrWhiteSpace(titleSnapshot);
        ArgumentException.ThrowIfNullOrWhiteSpace(paxBreakdown);
        ArgumentOutOfRangeException.ThrowIfLessThan(paxCount, 1);

        return new CartItem
        {
            AgencyId = quote.AgencyId,
            ItemType = OrderLine.ItemTypeFor(quote.ProductType),
            ProductId = quote.ProductId,
            DepartureId = departureId,
            SupplierOfferId = supplierOfferId,
            PriceQuoteId = quote.Id,
            TitleSnapshot = titleSnapshot.Trim(),
            PaxBreakdown = paxBreakdown,
            PaxCount = paxCount,
            Currency = quote.Currency,
            IndicativeGrossMinor = quote.GrossAmountMinor,
            AddedAt = now,
        };
    }

    /// <inheritdoc />
    public Guid AgencyId { get; private set; }

    public Guid CartId { get; private set; }

    public OrderLineItemType ItemType { get; private set; }

    /// <summary>The catalog product, for a tour or visa. Null for a flight or bus.</summary>
    public Guid? ProductId { get; private set; }

    /// <summary>The dated departure whose seats this is for. Only a group departure has one.</summary>
    public Guid? DepartureId { get; private set; }

    /// <summary>The searched fare this is for. Only a flight or a bus has one.</summary>
    public Guid? SupplierOfferId { get; private set; }

    /// <summary>
    /// The seats held on <see cref="DepartureId"/> while this cart is being paid for. Null until
    /// checkout takes them, and null again once the hold is given back or converted.
    /// </summary>
    /// <remarks>
    /// Seats are held at checkout rather than when the item goes in the cart: a seat a browser tab
    /// has been sitting on for an hour is a seat nobody else could buy. See <c>DepartureSeats</c>.
    /// </remarks>
    public Guid? DepartureHoldId { get; private set; }

    /// <summary>When that hold lapses, so the cart can say how long there is left to pay.</summary>
    public DateTimeOffset? HoldExpiresAt { get; private set; }

    /// <summary>The quote this was priced from — re-checked, and possibly replaced, at checkout.</summary>
    public Guid PriceQuoteId { get; private set; }

    public string TitleSnapshot { get; private set; }

    /// <summary>Who is travelling, as jsonb: <c>{"adults":2,"children":1}</c>.</summary>
    public string PaxBreakdown { get; private set; }

    /// <summary>How many people this is for, across every type. What a seat hold counts.</summary>
    public int PaxCount { get; private set; }

    public string Currency { get; private set; }

    /// <summary>For display only. Not a price we have promised anybody — see the class remarks.</summary>
    public Money IndicativeGrossMinor { get; private set; }

    public DateTimeOffset AddedAt { get; private set; }

    internal void AttachTo(Guid cartId, Guid agencyId)
    {
        CartId = cartId;
        AgencyId = agencyId;
    }

    /// <summary>Records the seats checkout has just held for this item.</summary>
    public void AttachHold(Guid holdId, DateTimeOffset expiresAt)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(holdId, Guid.Empty);

        DepartureHoldId = holdId;
        HoldExpiresAt = expiresAt;
    }

    /// <summary>Forgets a hold that has been given back or turned into seats. Idempotent.</summary>
    public void ForgetHold()
    {
        DepartureHoldId = null;
        HoldExpiresAt = null;
    }

    /// <summary>Re-prices this item from a fresh quote, keeping everything else about it.</summary>
    /// <remarks>
    /// A cart may sit open for days, and its prices go stale with it (see the remarks on
    /// <see cref="Cart"/>). Checkout re-quotes every item and lands the new figure here, so what the
    /// traveller is asked to pay is what the cart last showed them.
    /// </remarks>
    public void RepriceTo(PriceQuote quote)
    {
        ArgumentNullException.ThrowIfNull(quote);

        PriceQuoteId = quote.Id;
        IndicativeGrossMinor = quote.GrossAmountMinor;
    }
}
