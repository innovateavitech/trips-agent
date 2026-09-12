using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using TripsAgent.Application.Catalog;
using TripsAgent.Application.Persistence;
using TripsAgent.Contracts.Commerce;
using TripsAgent.Domain.Orders;

namespace TripsAgent.Application.Commerce;

/// <summary>
/// The traveller's cart on an agency's storefront (build plan F5, issue 61).
/// </summary>
/// <remarks>
/// <para>
/// <b>A session token, not an account.</b> There are no traveller accounts (decision 21), so a cart
/// is found by a secret the server issues on the first add and the browser sends back afterwards. It
/// is 256 random bits: it names nothing, and guessing one is not a thing anybody can do.
/// </para>
/// <para>
/// <b>Mixed carts are the point.</b> One cart holds a flight, a visa and a seat on a group departure
/// at once, and checkout turns all of them into one order with a line each — which is what makes
/// partial failure a real case rather than a theoretical one.
/// </para>
/// <para>
/// <b>Prices here are indicative</b>, and the class documentation on <c>Cart</c> explains why at
/// length. Nothing in a cart is a price anybody has been promised; checkout re-quotes every line.
/// </para>
/// <para>
/// <b>Seats are not held while something sits in a cart.</b> They are held when checkout starts and
/// given back if nobody pays — see <see cref="StorefrontCheckoutService"/>. Holding on add would let
/// an abandoned tab keep a departure sold out.
/// </para>
/// </remarks>
public sealed class CartService
{
    /// <summary>The length of every session token: 32 bytes, base64url without padding.</summary>
    public const int SessionTokenLength = 43;

    private readonly IAppDbContext _db;
    private readonly StorefrontTenant _storefront;
    private readonly CartPricing _pricing;
    private readonly DepartureSeats _seats;
    private readonly CommerceOptions _options;
    private readonly TimeProvider _clock;

    public CartService(
        IAppDbContext db,
        StorefrontTenant storefront,
        CartPricing pricing,
        DepartureSeats seats,
        CommerceOptions options,
        TimeProvider clock)
    {
        _db = db;
        _storefront = storefront;
        _pricing = pricing;
        _seats = seats;
        _options = options;
        _clock = clock;
    }

    /// <summary>The cart behind a session token, as the storefront renders it.</summary>
    public async Task<StoreResult<CartResponse>> GetAsync(
        string? host,
        string? sessionToken,
        CancellationToken cancellationToken = default)
    {
        if (await _storefront.EnterAsync(host, cancellationToken) is null)
        {
            return Store.NotFound<CartResponse>(NoSuchShop);
        }

        var cart = await FindAsync(sessionToken, cancellationToken);

        return cart is null
            ? Store.NotFound<CartResponse>(NoSuchCart)
            : new StoreResult<CartResponse>.Done(View(cart));
    }

    /// <summary>
    /// Puts something in the cart, opening one when there is none yet.
    /// </summary>
    /// <param name="host">The host name the traveller's browser used.</param>
    /// <param name="sessionToken">Their cart's token, or null to start a cart.</param>
    /// <param name="request">What to add, and for how many people.</param>
    /// <param name="cancellationToken">Cancels the work; nothing is saved.</param>
    public async Task<StoreResult<CartResponse>> AddAsync(
        string? host,
        string? sessionToken,
        AddCartItemRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var agency = await _storefront.EnterAsync(host, cancellationToken);

        if (agency is null)
        {
            return Store.NotFound<CartResponse>(NoSuchShop);
        }

        var party = new PartySize(request.Adults, request.Children, request.Infants);

        if (Check(request, party) is { } problem)
        {
            return problem;
        }

        var now = _clock.GetUtcNow();
        var cart = await FindAsync(sessionToken, cancellationToken);
        string token;

        if (cart is null)
        {
            token = NewSessionToken();
            cart = Cart.Open(agency.Id, agency.Currency, now, _options.CartLifetime, sessionToken: token);
            _db.Carts.Add(cart);
        }
        else
        {
            token = cart.SessionToken!;

            if (cart.Items.Count >= _options.MaxCartItems)
            {
                return new StoreResult<CartResponse>.Refused(
                    "There is no room for another item.",
                    $"A cart holds up to {_options.MaxCartItems} items. Check out what is in it, then start another.");
            }
        }

        var priced = await PriceAsync(request, party, cancellationToken);

        if (priced is null)
        {
            // Not on sale, sold out, or another agency's. A traveller is told the same thing for all
            // three: there is nothing here they can buy.
            return Store.NotFound<CartResponse>("We could not add that — it is no longer available.");
        }

        if (!string.Equals(priced.Quote.Currency, cart.Currency, StringComparison.Ordinal))
        {
            // Every agency sells in one currency (decision 17), so this means something is mis-set up
            // rather than something the traveller did.
            return new StoreResult<CartResponse>.Refused(
                "We could not add that to your cart.",
                "It is priced in a different currency from the rest of your cart.");
        }

        cart.Add(
            CartItem.FromQuote(
                priced.Quote,
                priced.Title,
                party.ToJson(),
                party.Total,
                now,
                request.DepartureId,
                request.OfferId),
            now);

        cart.KeepAlive(now, _options.CartLifetime);

        await _db.SaveChangesAsync(cancellationToken);

        return new StoreResult<CartResponse>.Done(View(cart, token));
    }

    /// <summary>Takes a line out of the cart, giving back any seats it was holding.</summary>
    public async Task<StoreResult<CartResponse>> RemoveAsync(
        string? host,
        string? sessionToken,
        Guid cartItemId,
        CancellationToken cancellationToken = default)
    {
        if (await _storefront.EnterAsync(host, cancellationToken) is null)
        {
            return Store.NotFound<CartResponse>(NoSuchShop);
        }

        var cart = await FindAsync(sessionToken, cancellationToken);

        if (cart is null)
        {
            return Store.NotFound<CartResponse>(NoSuchCart);
        }

        var item = cart.Items.FirstOrDefault(candidate => candidate.Id == cartItemId);

        if (item is null)
        {
            return Store.NotFound<CartResponse>("That is not in your cart.");
        }

        // Before the row goes: seats it was sitting on belong to whoever wants them next. Released
        // outside the cart's own save, because releasing runs its own transaction.
        if (item.DepartureHoldId is { } holdId)
        {
            await _seats.ReleaseAsync(holdId, cancellationToken);
        }

        var now = _clock.GetUtcNow();

        cart.Remove(cartItemId, now);
        cart.KeepAlive(now, _options.CartLifetime);

        await _db.SaveChangesAsync(cancellationToken);

        return new StoreResult<CartResponse>.Done(View(cart));
    }

    /// <summary>The traveller's active cart, with its lines. Null when there is none to find.</summary>
    /// <remarks>
    /// Read inside the agency's own tenant filter, so a token from one agency's shop finds nothing on
    /// another's. An expired cart is not found either: it is as good as gone, and the sweeper will
    /// say so formally.
    /// </remarks>
    internal async Task<Cart?> FindAsync(string? sessionToken, CancellationToken cancellationToken)
    {
        var token = sessionToken?.Trim();

        if (!LooksLikeToken(token))
        {
            return null;
        }

        var cart = await _db.Carts
            .Include(candidate => candidate.Items)
            .FirstOrDefaultAsync(candidate => candidate.SessionToken == token, cancellationToken);

        return cart is not null && cart.IsOpenAt(_clock.GetUtcNow()) ? cart : null;
    }

    /// <summary>256 random bits, URL-safe: unguessable, and it names nothing about the traveller.</summary>
    internal static string NewSessionToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>True for something shaped like a token, so a malformed one never reaches the database.</summary>
    internal static bool LooksLikeToken(string? token) =>
        token is { Length: SessionTokenLength }
        && token.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    /// <summary>The cart as the storefront shows it: what is in it, and what it comes to.</summary>
    internal static CartResponse View(Cart cart, string? sessionToken = null)
    {
        var items = cart.Items
            .OrderBy(item => item.AddedAt)
            .Select(item =>
            {
                var party = PartySize.FromJson(item.PaxBreakdown);

                return new CartItemResponse(
                    item.Id,
                    item.ItemType.ToString(),
                    item.TitleSnapshot,
                    party.Adults,
                    party.Children,
                    party.Infants,
                    item.IndicativeGrossMinor.AmountMinor,
                    item.Currency,
                    item.ProductId,
                    item.DepartureId,
                    item.HoldExpiresAt);
            })
            .ToList();

        return new CartResponse(
            cart.Id,
            sessionToken ?? cart.SessionToken ?? string.Empty,
            cart.Currency,
            items,
            cart.IndicativeTotalMinor.AmountMinor,
            cart.ExpiresAt);
    }

    private Task<PricedCartItem?> PriceAsync(AddCartItemRequest request, PartySize party, CancellationToken cancellationToken) =>
        request switch
        {
            { DepartureId: { } departureId } => _pricing.ForDepartureAsync(departureId, party, cancellationToken),
            { OfferId: { } offerId } => _pricing.ForOfferAsync(offerId, party, cancellationToken),
            { ProductId: { } productId } => _pricing.ForProductAsync(productId, party, cancellationToken),
            _ => Task.FromResult<PricedCartItem?>(null),
        };

    /// <summary>Everything wrong with an add request, or null when there is nothing wrong with it.</summary>
    private StoreResult<CartResponse>? Check(AddCartItemRequest request, PartySize party)
    {
        var named = new[] { request.ProductId, request.DepartureId, request.OfferId }.Count(id => id is not null);

        if (named != 1)
        {
            return Store.Invalid<CartResponse>(
                "We could not add that to your cart.",
                "item",
                "Say exactly one of a product, a departure or a fare.");
        }

        if (party.Adults < 1)
        {
            return Store.Invalid<CartResponse>(
                "We could not add that to your cart.",
                "adults",
                "At least one adult has to be travelling.");
        }

        if (party.Children < 0 || party.Infants < 0)
        {
            return Store.Invalid<CartResponse>(
                "We could not add that to your cart.",
                "children",
                "Numbers of travellers cannot be negative.");
        }

        if (party.Total > _options.MaxPaxPerItem)
        {
            return Store.Invalid<CartResponse>(
                "We could not add that to your cart.",
                "adults",
                $"Up to {_options.MaxPaxPerItem} travellers on one booking. Get in touch for a larger group.");
        }

        // An infant travels on an adult's lap, so there cannot be more of them than there are laps.
        if (party.Infants > party.Adults)
        {
            return Store.Invalid<CartResponse>(
                "We could not add that to your cart.",
                "infants",
                "Each infant travels with an adult, so there cannot be more infants than adults.");
        }

        return null;
    }

    private const string NoSuchShop = "We could not find that site.";

    private const string NoSuchCart = "We could not find your cart.";
}
