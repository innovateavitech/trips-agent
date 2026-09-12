using TripsAgent.Api.Crm;
using TripsAgent.Api.RateLimiting;
using TripsAgent.Application.Commerce;
using TripsAgent.Application.RateLimiting;
using TripsAgent.Contracts.Commerce;

namespace TripsAgent.Api.Commerce;

/// <summary>
/// The traveller's buying flow on an agency's storefront (build plan F5, issue 61): the cart, guest
/// checkout, and the link that manages the booking afterwards.
/// </summary>
/// <remarks>
/// <para>
/// <b>No token, and no tenant of its own.</b> A traveller is signed in to nothing, so the agency is
/// worked out from the host name their browser used — <c>X-Storefront-Host</c> where the storefront
/// sets it, and otherwise the request's own <c>Host</c> — and every read and write after that
/// happens inside that agency's tenant filter. This is the same shape the CRM's public routes use,
/// deliberately: one way to resolve a storefront request, not two.
/// </para>
/// <para>
/// <b>The cart is found by a session token</b>, sent as <c>X-Cart-Session</c> and issued by the
/// server on the first add. There are no traveller accounts (decision 21), so this is the whole of
/// the traveller's identity here — which is why it is 256 random bits and why nothing about it is
/// guessable.
/// </para>
/// <para>
/// <b>Nothing here mentions Trips</b> (CLAUDE.md rule 4), and nothing here returns an agent-facing
/// figure: a traveller sees the sell price and never the net rate, the markup or anybody's margin.
/// </para>
/// <para>
/// Rate-limited per calling address under the <c>Storefront</c> policy, like every other route on
/// the internet's side of the platform.
/// </para>
/// </remarks>
public static class PublicCommerceEndpoints
{
    /// <summary>The header the storefront sends the traveller's cart session token in.</summary>
    public const string CartSessionHeader = "X-Cart-Session";

    public static IEndpointRouteBuilder MapPublicCommerceEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/v1/public")
            .WithTags("Storefront commerce")
            .AllowAnonymous()
            .RequireRateLimitPolicy(RateLimitPolicyNames.Storefront);

        MapDepartures(group);
        MapCart(group);
        MapCheckout(group);
        MapBookings(group);

        return app;
    }

    private static void MapDepartures(RouteGroupBuilder group)
    {
        // The dated departures on one trip. Priced for the party asked about, because a departure's
        // price bands are per head and depend on how many people are going.
        group.MapGet("/trips/{productSlug}/departures", async (
                string productSlug,
                HttpContext http,
                PublicDepartureQueries departures,
                int? adults,
                int? children,
                int? infants,
                CancellationToken cancellationToken) =>
                ToResult(await departures.ForProductAsync(
                    HostOf(http), productSlug, PartyOf(adults, children, infants), cancellationToken)))
            .WithName("ListPublicDepartures")
            .Produces<IReadOnlyList<PublicDepartureResponse>>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/departures/{departureId:guid}", async (
                Guid departureId,
                HttpContext http,
                PublicDepartureQueries departures,
                int? adults,
                int? children,
                int? infants,
                CancellationToken cancellationToken) =>
                ToResult(await departures.FindAsync(
                    HostOf(http), departureId, PartyOf(adults, children, infants), cancellationToken)))
            .WithName("GetPublicDeparture")
            .Produces<PublicDepartureResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);
    }

    /// <summary>Who is travelling, from the query string. One adult when nobody said.</summary>
    /// <remarks>
    /// Clamped rather than refused: a party size in a URL is something a traveller can type, and a
    /// negative one should quietly become the smallest sensible party rather than a 422 on a page
    /// they are only browsing. Checkout validates the real thing.
    /// </remarks>
    private static PartySize PartyOf(int? adults, int? children, int? infants) =>
        new(Math.Clamp(adults ?? 1, 1, 20), Math.Clamp(children ?? 0, 0, 20), Math.Clamp(infants ?? 0, 0, 20));

    private static void MapCart(RouteGroupBuilder group)
    {
        group.MapGet("/cart", async (
                HttpContext http,
                CartService carts,
                CancellationToken cancellationToken) =>
                ToResult(await carts.GetAsync(HostOf(http), SessionOf(http), cancellationToken)))
            .WithName("GetCart")
            .Produces<CartResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        // Adding opens a cart when there is none, and the response carries the session token to send
        // back from then on — so a traveller's first add needs no round trip to start a cart.
        group.MapPost("/cart/items", async (
                HttpContext http,
                AddCartItemRequest request,
                CartService carts,
                CancellationToken cancellationToken) =>
                ToResult(await carts.AddAsync(HostOf(http), SessionOf(http), request, cancellationToken)))
            .WithName("AddCartItem")
            .Produces<CartResponse>()
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapDelete("/cart/items/{cartItemId:guid}", async (
                Guid cartItemId,
                HttpContext http,
                CartService carts,
                CancellationToken cancellationToken) =>
                ToResult(await carts.RemoveAsync(HostOf(http), SessionOf(http), cartItemId, cancellationToken)))
            .WithName("RemoveCartItem")
            .Produces<CartResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);
    }

    private static void MapCheckout(RouteGroupBuilder group)
    {
        group.MapPost("/checkout", async (
                HttpContext http,
                BeginCheckoutRequest request,
                StorefrontCheckoutService checkout,
                CancellationToken cancellationToken) =>
                ToResult(await checkout.BeginAsync(HostOf(http), SessionOf(http), request, cancellationToken)))
            .WithName("BeginCheckout")
            .Produces<BeginCheckoutResponse>()
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        // Where the gateway returns the traveller to. Asking the gateway what happened is this
        // endpoint's job and not the browser's: a redirect says what the payer's browser was told,
        // which is not the gateway answering a question we asked.
        group.MapGet("/checkout/{reference}", async (
                string reference,
                HttpContext http,
                StorefrontCheckoutService checkout,
                CustomerOrderPayments payments,
                CancellationToken cancellationToken) =>
            {
                await SettleQuietlyAsync(http, payments, cancellationToken);

                return ToResult(await checkout.StatusAsync(HostOf(http), reference, cancellationToken));
            })
            .WithName("GetCheckoutStatus")
            .Produces<CheckoutStatusResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);
    }

    private static void MapBookings(RouteGroupBuilder group)
    {
        group.MapGet("/bookings/{token}", async (
                string token,
                HttpContext http,
                ManageBookingQueries bookings,
                CancellationToken cancellationToken) =>
                ToResult(await bookings.OpenAsync(HostOf(http), token, cancellationToken)))
            .WithName("OpenBookingByLink")
            .Produces<ManageBookingResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);
    }

    /// <summary>
    /// Asks the gateway about the payment the traveller has just come back from, if they named one.
    /// </summary>
    /// <remarks>
    /// Best effort, and deliberately swallowed. The webhook is what guarantees a payment is settled;
    /// this only makes the traveller's own page tell the truth a few seconds sooner. A gateway that
    /// cannot be reached must not turn into an error on a page somebody has just been charged on —
    /// that invites them to pay again.
    /// </remarks>
    private static async Task SettleQuietlyAsync(
        HttpContext http,
        CustomerOrderPayments payments,
        CancellationToken cancellationToken)
    {
        var paymentReference = http.Request.Query["payment"].ToString();

        if (string.IsNullOrWhiteSpace(paymentReference))
        {
            return;
        }

        try
        {
            await payments.SettleAsync(paymentReference.Trim(), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            http.RequestServices
                .GetRequiredService<ILoggerFactory>()
                .CreateLogger(typeof(PublicCommerceEndpoints))
                .LogWarning(ex, "Could not settle payment from the return page; the webhook will.");
        }
    }

    /// <summary>Each outcome to exactly one status, the same mapping the CRM's public routes use.</summary>
    private static IResult ToResult<T>(StoreResult<T> outcome) => outcome switch
    {
        StoreResult<T>.Done done => Results.Ok(done.Value),

        StoreResult<T>.NotFound missing => Results.Problem(
            statusCode: StatusCodes.Status404NotFound,
            title: missing.Title),

        StoreResult<T>.Invalid invalid => Results.ValidationProblem(
            invalid.Problems
                .GroupBy(problem => problem.Field, StringComparer.Ordinal)
                .ToDictionary(
                    field => field.Key,
                    field => field.Select(problem => problem.Message).ToArray(),
                    StringComparer.Ordinal),
            title: invalid.Title,
            statusCode: StatusCodes.Status422UnprocessableEntity),

        StoreResult<T>.Refused refused => Results.Problem(
            statusCode: StatusCodes.Status409Conflict,
            title: refused.Title,
            detail: refused.Detail),

        _ => throw new InvalidOperationException($"Unhandled storefront outcome {outcome?.GetType().Name}."),
    };

    /// <summary>
    /// The host name the traveller's browser used: the storefront's header where it set one, and
    /// otherwise this request's own <c>Host</c>.
    /// </summary>
    /// <remarks>
    /// The storefront renders pages server-side, so its own <c>Host</c> when it calls this API is the
    /// API's rather than the traveller's. The header is how it passes the one that matters. It
    /// decides only <em>which agency's</em> shop is served — never whether the caller may buy — so a
    /// forged header buys nothing that asking that agency's own domain would not.
    /// </remarks>
    private static string? HostOf(HttpContext http)
    {
        var header = http.Request.Headers[PublicCrmEndpoints.StorefrontHostHeader].ToString();

        return StorefrontTenant.NormaliseHost(
            string.IsNullOrWhiteSpace(header) ? http.Request.Host.Value : header);
    }

    /// <summary>The traveller's cart token, from its header. Null when their browser has no cart yet.</summary>
    private static string? SessionOf(HttpContext http)
    {
        var header = http.Request.Headers[CartSessionHeader].ToString();

        return string.IsNullOrWhiteSpace(header) ? null : header.Trim();
    }
}
