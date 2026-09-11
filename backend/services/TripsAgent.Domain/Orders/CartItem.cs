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
    public static CartItem FromQuote(PriceQuote quote, string titleSnapshot, string paxBreakdown, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(quote);
        ArgumentException.ThrowIfNullOrWhiteSpace(titleSnapshot);
        ArgumentException.ThrowIfNullOrWhiteSpace(paxBreakdown);

        return new CartItem
        {
            AgencyId = quote.AgencyId,
            ItemType = OrderLine.ItemTypeFor(quote.ProductType),
            ProductId = quote.ProductId,
            PriceQuoteId = quote.Id,
            TitleSnapshot = titleSnapshot.Trim(),
            PaxBreakdown = paxBreakdown,
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

    /// <summary>The quote this was priced from — re-checked, and possibly replaced, at checkout.</summary>
    public Guid PriceQuoteId { get; private set; }

    public string TitleSnapshot { get; private set; }

    /// <summary>Who is travelling, as jsonb: <c>{"adults":2,"children":1}</c>.</summary>
    public string PaxBreakdown { get; private set; }

    public string Currency { get; private set; }

    /// <summary>For display only. Not a price we have promised anybody — see the class remarks.</summary>
    public Money IndicativeGrossMinor { get; private set; }

    public DateTimeOffset AddedAt { get; private set; }

    internal void AttachTo(Guid cartId, Guid agencyId)
    {
        CartId = cartId;
        AgencyId = agencyId;
    }
}
