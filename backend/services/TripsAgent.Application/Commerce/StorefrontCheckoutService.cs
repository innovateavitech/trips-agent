using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TripsAgent.Application.Catalog;
using TripsAgent.Application.Checkout;
using TripsAgent.Application.Crm;
using TripsAgent.Application.Orders;
using TripsAgent.Application.Payments;
using TripsAgent.Application.Persistence;
using TripsAgent.Application.Pricing;
using TripsAgent.Application.Suppliers;
using TripsAgent.Contracts.Commerce;
using TripsAgent.Domain.Catalog;
using TripsAgent.Domain.Common;
using TripsAgent.Domain.Orders;
using TripsAgent.Domain.Payments;
using TripsAgent.Domain.Suppliers;

namespace TripsAgent.Application.Commerce;

/// <summary>
/// Guest checkout on an agency's storefront (build plan F5, issue 61).
/// </summary>
/// <remarks>
/// <para>
/// <b>What this does, in order.</b> Seats on any group departure in the cart are held; every line is
/// re-priced, and a flight or bus line has its price confirmed with the supplier, which also holds
/// the fare; the whole cart becomes one order, pending payment, with its prices frozen from that
/// moment (CLAUDE.md rule 5); and the traveller is sent to the gateway's hosted page to pay.
/// Anything that goes wrong on the way gives every hold back before it returns.
/// </para>
/// <para>
/// <b>The card never touches us</b> (decision 18). The traveller types it on the gateway's page,
/// which is what keeps the platform in PCI SAQ-A. Nothing in this file could accept a card number
/// even by accident.
/// </para>
/// <para>
/// <b>One order, many lines, one payment.</b> A mixed cart — a flight, a visa and a seat on a tour —
/// is one order with a line each, paid for once. Each line is then fulfilled on its own, and a line
/// that fails goes to the agent's resolution queue while the rest stand
/// (<see cref="CustomerOrderPayments"/>). That is the whole point of the feature.
/// </para>
/// <para>
/// <b>Nothing is charged here.</b> This creates an order and a payment attempt; the money only
/// exists once the gateway says so, server-side, and <see cref="CustomerOrderPayments"/> acts on it.
/// A checkout nobody pays for lapses at its deadline and the sweeper gives the seats and fares back.
/// </para>
/// </remarks>
public sealed partial class StorefrontCheckoutService
{
    /// <summary>Where a traveller pays from, on the order and in the refund path.</summary>
    public const OrderPaymentMethod PaidBy = OrderPaymentMethod.Card;

    private readonly IAppDbContext _db;
    private readonly StorefrontTenant _storefront;
    private readonly CartService _carts;
    private readonly CartPricing _pricing;
    private readonly PricingService _prices;
    private readonly PlaceOrderHandler _placeOrder;
    private readonly SupplierFareConfirmation _fares;
    private readonly DepartureSeats _seats;
    private readonly DepartureInstallments _installments;
    private readonly CustomerDirectory _customers;
    private readonly BookingAccessLinks _links;
    private readonly IPaymentGateway _gateway;
    private readonly CheckoutReturnUrl _returnUrl;
    private readonly CommerceOptions _options;
    private readonly ITransactionRunner _transactions;
    private readonly TimeProvider _clock;
    private readonly ILogger<StorefrontCheckoutService> _logger;

    public StorefrontCheckoutService(
        IAppDbContext db,
        StorefrontTenant storefront,
        CartService carts,
        CartPricing pricing,
        PricingService prices,
        PlaceOrderHandler placeOrder,
        SupplierFareConfirmation fares,
        DepartureSeats seats,
        DepartureInstallments installments,
        CustomerDirectory customers,
        BookingAccessLinks links,
        IPaymentGateway gateway,
        CheckoutReturnUrl returnUrl,
        CommerceOptions options,
        ITransactionRunner transactions,
        TimeProvider clock,
        ILogger<StorefrontCheckoutService> logger)
    {
        _db = db;
        _storefront = storefront;
        _carts = carts;
        _pricing = pricing;
        _prices = prices;
        _placeOrder = placeOrder;
        _fares = fares;
        _seats = seats;
        _installments = installments;
        _customers = customers;
        _links = links;
        _gateway = gateway;
        _returnUrl = returnUrl;
        _options = options;
        _transactions = transactions;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>
    /// Turns the traveller's cart into an order and hands back where to pay for it.
    /// </summary>
    /// <param name="host">The host name their browser used, which says whose shop this is.</param>
    /// <param name="sessionToken">Their cart's session token.</param>
    /// <param name="request">Who is buying, and who is travelling on each line.</param>
    /// <param name="cancellationToken">Cancels the work. Holds already taken are given back.</param>
    public async Task<StoreResult<BeginCheckoutResponse>> BeginAsync(
        string? host,
        string? sessionToken,
        BeginCheckoutRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var agency = await _storefront.EnterAsync(host, cancellationToken);

        if (agency is null)
        {
            return Store.NotFound<BeginCheckoutResponse>("We could not find that site.");
        }

        var cart = await _carts.FindAsync(sessionToken, cancellationToken);

        if (cart is null || cart.Items.Count == 0)
        {
            return Store.NotFound<BeginCheckoutResponse>("Your cart is empty, or we could not find it.");
        }

        if (Check(request, cart) is { } invalid)
        {
            return invalid;
        }

        // Everything held from here has to be given back if anything below fails, so the ids are
        // collected as they are taken rather than found again afterwards.
        var held = new List<Guid>();

        try
        {
            return await StartAsync(agency, cart, request, held, cancellationToken);
        }
        catch (CheckoutRefusedException refused)
        {
            await ReleaseAsync(held);

            // The traveller reads the same words an agent would, because they describe the same
            // thing: which is to say what happened, and that nothing was charged.
            return refused.Refusal is CheckoutRefusal.NotFound
                ? Store.NotFound<BeginCheckoutResponse>(refused.Title)
                : new StoreResult<BeginCheckoutResponse>.Refused(refused.Title, refused.Detail);
        }
        catch
        {
            await ReleaseAsync(held);
            throw;
        }
    }

    /// <summary>Where a payment has got to, for the page the gateway returns the traveller to.</summary>
    /// <remarks>
    /// Read-only and idempotent. It reports what the database says; the gateway's own answer is
    /// taken by <see cref="CustomerOrderPayments"/>, through the webhook and the verify path, so a
    /// traveller refreshing this page can never move any money.
    /// </remarks>
    public async Task<StoreResult<CheckoutStatusResponse>> StatusAsync(
        string? host,
        string reference,
        CancellationToken cancellationToken = default)
    {
        var agency = await _storefront.EnterAsync(host, cancellationToken);

        if (agency is null)
        {
            return Store.NotFound<CheckoutStatusResponse>("We could not find that site.");
        }

        var order = await _db.Orders.AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.OrderNumber == reference, cancellationToken);

        if (order is null)
        {
            return Store.NotFound<CheckoutStatusResponse>("We could not find that booking.");
        }

        var payment = await _db.PaymentTransactions.AsNoTracking()
            .Where(candidate => candidate.OrderId == order.Id)
            .OrderByDescending(candidate => candidate.Id)
            .FirstOrDefaultAsync(cancellationToken);

        var status = order.PaidAt is not null
            ? "paid"
            : payment is { Status: PaymentStatus.Failed or PaymentStatus.Abandoned }
                ? "failed"
                : "pending";

        var manageUrl = order.PaidAt is null
            ? null
            : await _links.UrlForAsync(order.AgencyId, order.Id, cancellationToken);

        return new StoreResult<CheckoutStatusResponse>.Done(new CheckoutStatusResponse(
            order.OrderNumber,
            status,
            payment?.AmountMinor.AmountMinor ?? order.TotalGrossMinor.AmountMinor,
            order.Currency,
            manageUrl));
    }

    // ---------------------------------------------------------------------------------- the flow

    private async Task<StoreResult<BeginCheckoutResponse>> StartAsync(
        StorefrontAgency agency,
        Cart cart,
        BeginCheckoutRequest request,
        List<Guid> held,
        CancellationToken cancellationToken)
    {
        var now = _clock.GetUtcNow();
        var travellersByItem = request.Lines.ToDictionary(line => line.CartItemId, line => line.Travellers);
        var deadline = now + _options.PaymentWindow;
        var prepared = new List<PreparedLine>(cart.Items.Count);

        foreach (var item in cart.Items.OrderBy(candidate => candidate.AddedAt))
        {
            var party = PartySize.FromJson(item.PaxBreakdown);
            var travellers = travellersByItem.TryGetValue(item.Id, out var given) ? given : [];

            // Seats first, and only for what needs them: a departure that has sold out while the
            // traveller was filling in names stops the checkout before the supplier is ever called.
            if (item.DepartureId is { } departureId && item.DepartureHoldId is null)
            {
                var outcome = await _seats.HoldAsync(
                    departureId, cart.Id, party.Total, _options.PaymentWindow, cancellationToken);

                if (outcome is not SeatHoldOutcome.Held(var hold, _))
                {
                    return new StoreResult<BeginCheckoutResponse>.Refused(
                        $"We could not hold seats on {item.TitleSnapshot}.",
                        outcome switch
                        {
                            SeatHoldOutcome.NoSeats seats when seats.SeatsLeft > 0 =>
                                $"Only {seats.SeatsLeft} seats are left. Change the number of travellers and try again.",
                            SeatHoldOutcome.NoSeats => "It has just sold out. Nothing was charged.",
                            SeatHoldOutcome.NotSellable reason => reason.Reason + " Nothing was charged.",
                            _ => "It is no longer on sale. Nothing was charged.",
                        });
                }

                held.Add(hold.Id);
                item.AttachHold(hold.Id, hold.ExpiresAt);
                deadline = Earliest(deadline, hold.ExpiresAt);
            }
            else if (item.DepartureHoldId is { } standing && item.HoldExpiresAt is { } expires)
            {
                // A checkout begun and abandoned a moment ago; its seats are still ours.
                held.Add(standing);
                deadline = Earliest(deadline, expires);
            }

            prepared.Add(item.SupplierOfferId is { } offerId
                ? await PrepareSupplierLineAsync(agency, item, party, travellers, offerId, cancellationToken)
                : await PrepareAgencyLineAsync(item, party, travellers, cancellationToken));
        }

        foreach (var line in prepared.Where(line => line.TicketTimeLimit is not null))
        {
            deadline = Earliest(deadline, line.TicketTimeLimit!.Value);
        }

        var order = await RecordAsync(agency, cart, request.Contact, prepared, deadline, now, cancellationToken);
        var due = await AmountDueNowAsync(order, prepared, cancellationToken);

        var started = await StartPaymentAsync(agency, order, due, request.ReturnUrl, cancellationToken);

        if (started is null)
        {
            return new StoreResult<BeginCheckoutResponse>.Refused(
                "We could not start the payment.",
                "Nothing was charged. Try again in a moment — your booking is held until the deadline shown.");
        }

        LogStarted(_logger, order.OrderNumber, order.Lines.Count, due.AmountMinor);

        return new StoreResult<BeginCheckoutResponse>.Done(new BeginCheckoutResponse(
            order.OrderNumber,
            started,
            due.AmountMinor,
            order.TotalGrossMinor.AmountMinor,
            order.Currency,
            deadline));
    }

    /// <summary>A line the agency fulfils itself: a tour, a visa, a package or a seat on a departure.</summary>
    private async Task<PreparedLine> PrepareAgencyLineAsync(
        CartItem item,
        PartySize party,
        IReadOnlyList<CheckoutTravellerRequest> travellers,
        CancellationToken cancellationToken)
    {
        // Re-priced now, so the order is frozen from a price quoted seconds ago rather than from
        // whatever the cart was showing yesterday.
        var priced = item.DepartureId is { } departureId
            ? await _pricing.ForDepartureAsync(departureId, party, cancellationToken)
            : await _pricing.ForProductAsync(item.ProductId!.Value, party, cancellationToken);

        if (priced is null)
        {
            throw new CheckoutRefusedException(
                CheckoutRefusal.Gone,
                $"{item.TitleSnapshot} is no longer available.",
                "Nothing was charged. Take it out of your cart, then check out again.");
        }

        item.RepriceTo(priced.Quote);

        return new PreparedLine(
            item,
            new PlaceOrderLine(
                priced.Quote.Id,
                priced.Title,
                party.ToJson(),
                ProductId: item.ProductId,
                Travellers: travellers.Select(ToOrderTraveller).ToList()),
            travellers,
            Fare: null,
            Confirmed: null,
            TicketTimeLimit: null);
    }

    /// <summary>A flight or a bus: the supplier confirms and holds the fare before anybody pays.</summary>
    private async Task<PreparedLine> PrepareSupplierLineAsync(
        StorefrontAgency agency,
        CartItem item,
        PartySize party,
        IReadOnlyList<CheckoutTravellerRequest> travellers,
        Guid offerId,
        CancellationToken cancellationToken)
    {
        var fare = await _fares.LoadAsync(offerId, cancellationToken);

        var confirmed = await _fares.ConfirmAsync(
            agency.Id,
            fare,
            travellers.Select(ToPassenger).ToList(),
            correlationId: null,
            cancellationToken);

        // The confirmed net, not the searched one: what the supplier has just said it will charge.
        var quote = await _prices.QuoteAsync(fare.Subject, confirmed.NetMinor, cancellationToken);

        item.RepriceTo(quote);

        return new PreparedLine(
            item,
            new PlaceOrderLine(
                quote.Id,
                confirmed.Title,
                party.ToJson(),
                SupplierOfferId: fare.Offer.Id,
                Travellers: travellers.Select(ToOrderTraveller).ToList()),
            travellers,
            fare,
            confirmed,
            confirmed.TicketTimeLimit);
    }

    /// <summary>
    /// Writes the order, its supplier bookings and its payment schedules — all in one transaction.
    /// </summary>
    /// <remarks>
    /// One transaction because a half-written order is worse than none: the order number comes from
    /// the gapless allocator, and a rolled-back order has to take its number back with it.
    /// </remarks>
    private Task<Order> RecordAsync(
        StorefrontAgency agency,
        Cart cart,
        CheckoutContactRequest contact,
        IReadOnlyList<PreparedLine> prepared,
        DateTimeOffset deadline,
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        _transactions.RunAsync(
            async token =>
            {
                // The customer record the CRM keeps: created by this booking if this is the first we
                // have heard of them, and found again by email or phone if it is not (FRD §2.8 RS-1).
                var customer = await _customers.FindOrAddAsync(
                    agency.Id, contact.Name, contact.Email, contact.Phone, now, token);

                var order = await _placeOrder.HandleAsync(
                    new PlaceOrderCommand(
                        BuyerType.Customer,
                        OrderChannel.Storefront,
                        customer.Id,
                        prepared.Select(line => line.Line).ToList()),
                    token);

                for (var index = 0; index < prepared.Count; index++)
                {
                    var line = order.Lines[index];
                    var source = prepared[index];

                    if (source is { Fare: { } fare, Confirmed: { } confirmed })
                    {
                        AttachSupplierBooking(agency.Id, line, fare, confirmed, source.Travellers, now);
                    }

                    if (source.Item.DepartureHoldId is { } seatHoldId)
                    {
                        // The seats follow the cart line into the order, so the paid line can find
                        // them again to convert. The cart itself is done with them.
                        var seats = await _db.DepartureHolds.SingleAsync(hold => hold.Id == seatHoldId, token);
                        seats.AttachToOrderLine(line.Id);
                    }

                    source.Item.ForgetHold();
                }

                cart.LinkCustomer(customer.Id);
                cart.Convert(order.Id, now);

                await _db.SaveChangesAsync(token);

                // After the save, because a schedule names the order line it bills, and the line
                // only has an id in the database once the order is written.
                for (var index = 0; index < prepared.Count; index++)
                {
                    var source = prepared[index];

                    if (source.Item.DepartureId is null)
                    {
                        continue;
                    }

                    var line = order.Lines[index];

                    await _installments.ScheduleAsync(
                        source.Item.DepartureId.Value,
                        line.Id,
                        PartySize.FromJson(line.PaxBreakdown).Total,
                        contact.Name,
                        contact.Email,
                        DateOnly.FromDateTime(CrmContext.InZone(now, agency.TimeZone).DateTime),
                        token);
                }

                LogPlaced(_logger, order.OrderNumber, deadline);

                return order;
            },
            cancellationToken);

    /// <summary>Records the supplier's confirmation behind a line, with the passengers it was made for.</summary>
    private void AttachSupplierBooking(
        Guid agencyId,
        OrderLine line,
        SupplierFare fare,
        ConfirmedFare confirmed,
        IReadOnlyList<CheckoutTravellerRequest> travellers,
        DateTimeOffset now)
    {
        var booking = SupplierBooking.Create(
            agencyId,
            fare.Offer.SupplierId,
            line.Id,
            fare.Offer.Id,
            fare.Offer.ProductType,
            confirmed.Confirmation.TripType,
            confirmed.Confirmation.TripMode,
            confirmed.Confirmation.SupplierSessionId,
            fare.Offer.Currency,
            $"order-line:{line.Id:N}");

        booking.RecordPriceConfirmation(confirmed.Confirmation.Lines, now);
        line.AttachSupplierBooking(booking.Id);
        _db.SupplierBookings.Add(booking);

        foreach (var traveller in travellers)
        {
            _db.SupplierBookingPassengers.Add(SupplierBookingPassenger.Add(
                agencyId,
                booking.Id,
                TypeOf(traveller.Type) switch
                {
                    TravellerType.Child => PassengerType.Child,
                    TravellerType.Infant => PassengerType.Infant,
                    _ => PassengerType.Adult,
                },
                traveller.FirstName.Trim(),
                traveller.LastName.Trim(),
                title: traveller.Title,
                birthDate: traveller.BirthDate,
                gender: traveller.Gender,
                email: traveller.Email,
                phoneNumber: traveller.Phone));
        }
    }

    /// <summary>
    /// What the traveller pays now: the whole order, less the part of a departure that its payment
    /// plan puts off until later.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A departure sold on deposit is billed by a <c>BookingPaymentSchedule</c>, whose figures are
    /// the agent's own seat prices. The agency's markup and the platform's fee are not in that
    /// schedule, so they are collected with the deposit: what is deferred is exactly the part of the
    /// seat price the schedule defers, and everything else is due now. That way the stored schedule
    /// stays the traveller's truth to the kobo, and no rounding is invented to split a margin.
    /// </para>
    /// <para>
    /// Automatic charging of the later payments is out of the MVP (build plan, "what the MVP leaves
    /// out"): the schedule drives reminders, and the agency takes the money.
    /// </para>
    /// </remarks>
    private async Task<Money> AmountDueNowAsync(
        Order order,
        IReadOnlyList<PreparedLine> prepared,
        CancellationToken cancellationToken)
    {
        var deferred = default(Money);

        for (var index = 0; index < prepared.Count; index++)
        {
            if (prepared[index].Item.DepartureId is not { } departureId)
            {
                continue;
            }

            // Only a departure the agent actually sells on a plan defers anything. One with no
            // deposit and no instalments has a schedule too — a single line due at the cutoff — and
            // a traveller buying it on the storefront pays for it there and then, like any other
            // product. Deferring that would be reading "when we would chase an agent's customer for
            // it" as "when the card is charged".
            var plan = await _db.Departures.AsNoTracking()
                .Where(candidate => candidate.Id == departureId)
                .Select(candidate => new
                {
                    candidate.DepositType,
                    HasInstallments = candidate.Installments != null && candidate.Installments.Items.Count > 0,
                })
                .FirstOrDefaultAsync(cancellationToken);

            if (plan is null || (plan.DepositType == DepositType.None && !plan.HasInstallments))
            {
                continue;
            }

            var line = order.Lines[index];

            var schedule = _db.BookingPaymentSchedules.Local
                .FirstOrDefault(candidate => candidate.OrderLineId == line.Id);

            if (schedule is null)
            {
                continue;
            }

            var later = schedule.Items
                .Where(item => item.DueDate > schedule.BookedOn)
                .Sum(item => item.AmountMinor.AmountMinor);

            // Never more than the line's own net: a schedule can only defer what the seats cost, and
            // the agency's markup and our fee are collected with the deposit.
            deferred += new Money(Math.Min(later, line.NetAmountMinor.AmountMinor));
        }

        return order.TotalGrossMinor - deferred;
    }

    /// <summary>Records the attempt, then asks the gateway for a page. Null when the gateway could not be asked.</summary>
    private async Task<string?> StartPaymentAsync(
        StorefrontAgency agency,
        Order order,
        Money due,
        string? requestedReturnUrl,
        CancellationToken cancellationToken)
    {
        // Ours, not the gateway's. No agency id in it: it ends up on the traveller's bank statement
        // and in the gateway's dashboard, and neither needs to carry a tenant identifier.
        var reference = $"TA-{Guid.CreateVersion7():N}";

        var payment = PaymentTransaction.Start(
            agency.Id,
            userId: null,
            PaymentPurpose.OrderPayment,
            due,
            order.Currency,
            reference,
            idempotencyKey: null,
            order.Id);

        _db.PaymentTransactions.Add(payment);

        // Saved before the gateway is called: if the call times out, the attempt still exists and
        // the webhook already on its way has a row to find.
        await _db.SaveChangesAsync(cancellationToken);

        var email = await _db.Customers.AsNoTracking()
            .Where(candidate => candidate.Id == order.CustomerId)
            .Select(candidate => candidate.Email)
            .FirstOrDefaultAsync(cancellationToken);

        try
        {
            var initialization = await _gateway.InitializeAsync(
                reference,
                due,
                order.Currency,
                email ?? $"bookings@{agency.Id:N}.invalid",
                await SafeReturnUrlAsync(agency.Id, requestedReturnUrl, order.OrderNumber, cancellationToken),
                cancellationToken);

            payment.RecordGatewayReference(initialization.GatewayReference);
            await _db.SaveChangesAsync(cancellationToken);

            return initialization.AuthorizationUrl;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogGatewayFailed(_logger, ex, reference);

            payment.MarkFailed("The payment gateway could not be reached.", _clock.GetUtcNow());
            await _db.SaveChangesAsync(cancellationToken);

            return null;
        }
    }

    /// <summary>
    /// Where the gateway may send the traveller back to: their own agency's site, and nowhere else.
    /// </summary>
    /// <remarks>
    /// A URL a caller supplies is honoured only when it is an absolute HTTPS address on the agency's
    /// own site. Anything else falls back to the site's own checkout-complete page. Without the
    /// check this endpoint would be an open redirect wearing the agency's domain — the shape a
    /// phishing page wants most.
    /// </remarks>
    internal async Task<string> SafeReturnUrlAsync(
        Guid agencyId,
        string? requested,
        string reference,
        CancellationToken cancellationToken)
    {
        var site = await _storefront.SiteUrlAsync(agencyId, cancellationToken);
        var fallback = new Uri(site, $"/checkout/complete?reference={Uri.EscapeDataString(reference)}").ToString();

        if (_returnUrl.Value is { } configured)
        {
            return configured;
        }

        if (string.IsNullOrWhiteSpace(requested)
            || !Uri.TryCreate(requested.Trim(), UriKind.Absolute, out var candidate)
            || candidate.Scheme != Uri.UriSchemeHttps
            || !string.Equals(candidate.Host, site.Host, StringComparison.OrdinalIgnoreCase))
        {
            return fallback;
        }

        return candidate.ToString();
    }

    // ------------------------------------------------------------------------------------ helpers

    /// <summary>Gives every hold back after a checkout that came to nothing. Never throws.</summary>
    private async Task ReleaseAsync(List<Guid> holdIds)
    {
        if (holdIds.Count == 0)
        {
            return;
        }

        // Whatever the failed attempt staged is forgotten first, so releasing cannot write it.
        _db.ChangeTracker.Clear();

        foreach (var holdId in holdIds)
        {
            try
            {
                await _seats.ReleaseAsync(holdId, CancellationToken.None);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The hold expiry job gives it back at its deadline anyway; a failure here must not
                // replace the refusal the traveller is about to read with a 500.
                LogReleaseFailed(_logger, ex, holdId);
            }
        }
    }

    private static DateTimeOffset Earliest(DateTimeOffset left, DateTimeOffset right) => left < right ? left : right;

    /// <summary>Everything wrong with the request, or null when there is nothing wrong with it.</summary>
    private static StoreResult<BeginCheckoutResponse>? Check(BeginCheckoutRequest request, Cart cart)
    {
        var problems = new List<StoreProblem>();

        if (string.IsNullOrWhiteSpace(request.Contact?.Name))
        {
            problems.Add(new StoreProblem("contact.name", "We need a name for the booking."));
        }

        if (string.IsNullOrWhiteSpace(request.Contact?.Email) || !request.Contact.Email.Contains('@', StringComparison.Ordinal))
        {
            problems.Add(new StoreProblem("contact.email", "We need an email address to send the booking to."));
        }

        var travellersByItem = request.Lines?.ToDictionary(line => line.CartItemId, line => line.Travellers)
                               ?? new Dictionary<Guid, IReadOnlyList<CheckoutTravellerRequest>>();

        foreach (var item in cart.Items)
        {
            var party = PartySize.FromJson(item.PaxBreakdown);
            var travellers = travellersByItem.TryGetValue(item.Id, out var given) ? given : [];

            // A ticket carries a name, so a flight or a bus needs one for everybody on it. A tour or
            // a visa can be booked against the lead contact and have names added later.
            if (item.SupplierOfferId is null)
            {
                continue;
            }

            if (travellers.Count != party.Total)
            {
                problems.Add(new StoreProblem(
                    $"lines.{item.Id}.travellers",
                    $"{item.TitleSnapshot} needs the names of all {party.Total} travellers, exactly as their travel documents show them."));
                continue;
            }

            if (travellers.Any(traveller =>
                    string.IsNullOrWhiteSpace(traveller.FirstName) || string.IsNullOrWhiteSpace(traveller.LastName)))
            {
                problems.Add(new StoreProblem(
                    $"lines.{item.Id}.travellers",
                    "Every traveller needs a first and last name, exactly as their travel document shows it."));
            }
        }

        return problems.Count == 0
            ? null
            : new StoreResult<BeginCheckoutResponse>.Invalid("We could not start your checkout.", problems);
    }

    private static SupplierPassenger ToPassenger(CheckoutTravellerRequest traveller) =>
        new(
            TypeOf(traveller.Type) switch
            {
                TravellerType.Child => PassengerType.Child,
                TravellerType.Infant => PassengerType.Infant,
                _ => PassengerType.Adult,
            },
            traveller.FirstName.Trim(),
            traveller.LastName.Trim(),
            MiddleName: null,
            traveller.Title,
            traveller.BirthDate,
            traveller.Gender,
            traveller.Email,
            traveller.Phone,
            Document: string.IsNullOrWhiteSpace(traveller.PassportNumber)
                ? null
                : new SupplierTravelDocument(
                    TravelDocumentKind.Passport,
                    traveller.PassportNumber.Trim(),
                    IssuingCountry: (traveller.Nationality ?? "NG").Trim().ToUpperInvariant(),
                    NationalityCountry: traveller.Nationality?.Trim().ToUpperInvariant(),
                    ExpiresOn: traveller.PassportExpiry));

    private static PlaceOrderTraveller ToOrderTraveller(CheckoutTravellerRequest traveller) =>
        new(
            TypeOf(traveller.Type),
            traveller.FirstName.Trim(),
            traveller.LastName.Trim(),
            traveller.BirthDate,
            traveller.PassportNumber,
            traveller.PassportExpiry,
            traveller.Nationality?.Trim().ToUpperInvariant());

    /// <summary>An unknown word means an adult: the safest reading, since an adult fare is the dearest.</summary>
    private static TravellerType TypeOf(string? type) =>
        Enum.TryParse<TravellerType>(type?.Trim(), ignoreCase: true, out var parsed) && Enum.IsDefined(parsed)
            ? parsed
            : TravellerType.Adult;

    /// <summary>One cart line on its way to being an order line, with whatever the supplier said about it.</summary>
    private sealed record PreparedLine(
        CartItem Item,
        PlaceOrderLine Line,
        IReadOnlyList<CheckoutTravellerRequest> Travellers,
        SupplierFare? Fare,
        ConfirmedFare? Confirmed,
        DateTimeOffset? TicketTimeLimit);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Storefront order {Reference} placed; it must be paid for by {Deadline}.")]
    private static partial void LogPlaced(ILogger logger, string reference, DateTimeOffset deadline);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Storefront checkout {Reference} sent to the gateway: {LineCount} lines, {DueMinor} kobo due now.")]
    private static partial void LogStarted(ILogger logger, string reference, int lineCount, long dueMinor);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "Could not start payment {Reference} with the gateway. Nothing was charged.")]
    private static partial void LogGatewayFailed(ILogger logger, Exception exception, string reference);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "Could not give seat hold {HoldId} back after a checkout that failed; the hold expiry job will.")]
    private static partial void LogReleaseFailed(ILogger logger, Exception exception, Guid holdId);
}
