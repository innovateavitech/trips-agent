using TripsAgent.Domain.Common;

namespace TripsAgent.Domain.Orders;

/// <summary>
/// What somebody has chosen but not yet bought.
/// </summary>
/// <remarks>
/// <para>
/// <strong>A cart price is indicative, an order line's is final.</strong> This is the single most
/// important thing to know about this class, and the exact inverse of <see cref="OrderLine"/>.
/// Rule 5 freezes a price at <em>purchase</em>, not at the moment somebody adds an item to a cart.
/// A cart that has sat open for two days holds quotes that have long expired; checkout (#42)
/// re-checks every quote and re-prices what has lapsed. Nothing should ever copy a cart item's
/// money onto an order line — build the line from the quote, through <see cref="OrderLine.FromQuote"/>,
/// which refuses an expired one.
/// </para>
/// <para>
/// <see cref="CustomerId"/> is null for guest checkout (FRD §2.4 RS-2). A guest's cart is found by
/// <see cref="SessionToken"/> instead, so somebody can fill a cart and only identify themselves at
/// the end — which is most of the traffic a storefront gets.
/// </para>
/// </remarks>
public sealed class Cart : Entity, IAuditableEntity, ITenantScoped
{
    private readonly List<CartItem> _items = [];

    private Cart()
    {
        Currency = string.Empty;
    }

    /// <summary>Opens a cart. <paramref name="customerId"/> is null for a guest.</summary>
    public static Cart Open(
        Guid agencyId,
        string currency,
        DateTimeOffset now,
        TimeSpan lifetime,
        Guid? customerId = null,
        string? sessionToken = null)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(agencyId, Guid.Empty);
        ArgumentException.ThrowIfNullOrWhiteSpace(currency);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(lifetime, TimeSpan.Zero);

        // One of the two has to identify the cart, or nobody can find it again.
        if (customerId is null && string.IsNullOrWhiteSpace(sessionToken))
        {
            throw new ArgumentException("A guest cart needs a session token.", nameof(sessionToken));
        }

        return new Cart
        {
            AgencyId = agencyId,
            Currency = currency.Trim().ToUpperInvariant(),
            CustomerId = customerId,
            SessionToken = string.IsNullOrWhiteSpace(sessionToken) ? null : sessionToken.Trim(),
            Status = CartStatus.Active,
            ExpiresAt = now + lifetime,
        };
    }

    /// <inheritdoc />
    public Guid AgencyId { get; private set; }

    /// <summary>Null for guest checkout; then <see cref="SessionToken"/> identifies the cart.</summary>
    public Guid? CustomerId { get; private set; }

    /// <summary>How a guest's browser finds its own cart again. Null once somebody signs in.</summary>
    public string? SessionToken { get; private set; }

    public CartStatus Status { get; private set; }

    public string Currency { get; private set; }

    public DateTimeOffset ExpiresAt { get; private set; }

    /// <summary>The order this cart turned into, once it did.</summary>
    public Guid? ConvertedOrderId { get; private set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public IReadOnlyList<CartItem> Items => _items;

    /// <summary>
    /// What the cart says it costs, right now. Indicative only — see the remarks on this class.
    /// </summary>
    public Money IndicativeTotalMinor
    {
        get
        {
            var total = default(Money);

            foreach (var item in _items)
            {
                total += item.IndicativeGrossMinor;
            }

            return total;
        }
    }

    public bool HasExpiredAt(DateTimeOffset now) => now >= ExpiresAt;

    /// <summary>Adds an item. Refuses a currency the cart cannot total.</summary>
    public CartItem Add(CartItem item, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(item);
        EnsureActive(now);

        if (!string.Equals(item.Currency, Currency, StringComparison.Ordinal))
        {
            throw new ArgumentException($"Item currency {item.Currency} does not match the cart's {Currency}.", nameof(item));
        }

        item.AttachTo(Id, AgencyId);
        _items.Add(item);
        UpdatedAt = now;

        return item;
    }

    public void Remove(Guid cartItemId, DateTimeOffset now)
    {
        EnsureActive(now);
        _items.RemoveAll(item => item.Id == cartItemId);
        UpdatedAt = now;
    }

    /// <summary>Marks the cart as the order it became. A converted cart is closed for editing.</summary>
    public void Convert(Guid orderId, DateTimeOffset now)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(orderId, Guid.Empty);
        EnsureActive(now);

        Status = CartStatus.Converted;
        ConvertedOrderId = orderId;
        UpdatedAt = now;
    }

    /// <summary>Gives up on a cart: the buyer walked away, or the sweeper found it expired.</summary>
    public void Abandon(DateTimeOffset now)
    {
        if (Status != CartStatus.Active)
        {
            return;
        }

        Status = HasExpiredAt(now) ? CartStatus.Expired : CartStatus.Abandoned;
        UpdatedAt = now;
    }

    private void EnsureActive(DateTimeOffset now)
    {
        if (Status != CartStatus.Active)
        {
            throw new InvalidOperationException($"Cart {Id} is {Status} and cannot be changed.");
        }

        if (HasExpiredAt(now))
        {
            throw new InvalidOperationException($"Cart {Id} expired at {ExpiresAt:O}.");
        }
    }
}
