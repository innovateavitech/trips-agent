using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TripsAgent.Application.Messaging;
using TripsAgent.Application.Orders;
using TripsAgent.Application.Persistence;
using TripsAgent.Application.Pricing;
using TripsAgent.Application.Suppliers;
using TripsAgent.Application.Tenancy;
using TripsAgent.Domain.Common;
using TripsAgent.Domain.Orders;
using TripsAgent.Domain.Payments;
using TripsAgent.Domain.Pricing;
using TripsAgent.Domain.Suppliers;
using TripsAgent.Domain.Tenancy;

namespace TripsAgent.Application.Checkout;

/// <summary>A traveller, as the agent entered them.</summary>
/// <param name="PassportNumber">In clear only on its way through: the order keeps ciphertext.</param>
public sealed record CheckoutTraveller(
    PassengerType Type,
    string? Title,
    string FirstName,
    string LastName,
    DateOnly? BirthDate = null,
    string? Gender = null,
    string? Email = null,
    string? Phone = null,
    string? PassportNumber = null,
    DateOnly? PassportExpiry = null,
    string? Nationality = null)
{
    /// <summary>A record's generated ToString prints every property — and one log line would leak a passport.</summary>
    public override string ToString() => $"CheckoutTraveller {{ Type = {Type}, Name = {FirstName} {LastName}, Passport = [redacted] }}";
}

/// <summary>What the supplier confirmed, priced for the agency.</summary>
/// <param name="Reference">The order this confirmation created, which paying for it names.</param>
/// <param name="Sell">What the traveller pays now: the confirmed net, with the agency's markup and tax.</param>
/// <param name="SearchedSell">What the search showed. Differs only when the supplier moved the price.</param>
/// <param name="TicketTimeLimit">Pay and issue before this, or the supplier releases the fare.</param>
public sealed record CheckoutPriceConfirmation(
    string Reference,
    Money Sell,
    Money SearchedSell,
    string Currency,
    DateTimeOffset TicketTimeLimit);

/// <summary>
/// The console's checkout (#42): confirm a fare's price with the supplier, then pay for it and start the
/// ticket on its way.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two steps, because the supplier works that way.</b> Confirming the price holds the fare and returns
/// the price the supplier will really charge, with a deadline — the ticket time limit. The agent sees
/// that price, and pays for exactly it: a price that moved has to be accepted again (the console's price
/// change dialog) before it can be paid for.
/// </para>
/// <para>
/// <b>Confirming creates the order</b>, pending payment, at the confirmed price — frozen there from that
/// moment (CLAUDE.md rule 5) — with the supplier booking behind its line. A confirmation nobody pays for
/// lapses at its ticket time limit and the order is cancelled; nothing was charged.
/// </para>
/// <para>
/// <b>The wallet is held before the supplier is asked</b> (decision Q3 in docs/BUILD_PLAN.md). Confirming
/// first holds the searched price — so a short wallet fails at once, before any supplier call — and then,
/// in the same transaction that records the order, swaps that provisional hold for the booking's own, at
/// the confirmed price. Every failure on the way gives the provisional hold back. The money is only taken
/// when the ticket exists (<see cref="CheckoutCompletion"/>), and given back if it never does
/// (<see cref="PaymentReversalService"/>).
/// </para>
/// <para>
/// <b>Paying is one transaction</b>: the order's move to Paid and the message asking a Worker to issue the
/// ticket commit together or not at all. Only a rise in price has to be accepted again; a fall passes
/// through (decision Q9).
/// </para>
/// <para>
/// <b>The same key twice is the same booking.</b> The console sends one idempotency key per payment
/// attempt, kept on the order under a unique index — so a double press, or a retry after a lost answer,
/// finds the booking it already made and never charges again.
/// </para>
/// </remarks>
public sealed partial class CheckoutService
{
    /// <summary>
    /// How long after the ticket time limit a wallet hold may stand. A ticket still pending with the
    /// supplier keeps its money held (#37), and the nightly ledger audit flags a hold outstanding past
    /// its expiry — so the grace is generous, and a hold that outlives it is worth a person's look.
    /// </summary>
    public static readonly TimeSpan HoldGrace = TimeSpan.FromDays(1);

    /// <summary>
    /// How long the provisional hold — placed before the supplier is asked — may stand. The booking's own
    /// hold replaces it within the supplier's timeout; one left behind by a crash is released by the
    /// checkout sweeper once this passes.
    /// </summary>
    public static readonly TimeSpan ProvisionalHoldLifetime = TimeSpan.FromMinutes(10);

    /// <summary>The longest idempotency key accepted: the column's width.</summary>
    public const int MaxIdempotencyKeyLength = 100;

    private const int MaxPlaceAttempts = 3;

    private const int MaxWalletAttempts = 3;

    private readonly IAppDbContext _db;
    private readonly ITransactionRunner _transactions;
    private readonly ITenantContext _tenant;
    private readonly ISupplierAdapterRegistry _adapters;
    private readonly PricingService _pricing;
    private readonly PlaceOrderHandler _placeOrder;
    private readonly PriceConfirmationService _priceConfirmation;
    private readonly ISupplierBookingLocks _bookingLocks;
    private readonly IOutbox _outbox;
    private readonly IUniqueViolationDetector _uniqueViolations;
    private readonly TimeProvider _clock;
    private readonly ILogger<CheckoutService> _logger;

    public CheckoutService(
        IAppDbContext db,
        ITransactionRunner transactions,
        ITenantContext tenant,
        ISupplierAdapterRegistry adapters,
        PricingService pricing,
        PlaceOrderHandler placeOrder,
        PriceConfirmationService priceConfirmation,
        ISupplierBookingLocks bookingLocks,
        IOutbox outbox,
        IUniqueViolationDetector uniqueViolations,
        TimeProvider clock,
        ILogger<CheckoutService> logger)
    {
        _db = db;
        _transactions = transactions;
        _tenant = tenant;
        _adapters = adapters;
        _pricing = pricing;
        _placeOrder = placeOrder;
        _priceConfirmation = priceConfirmation;
        _bookingLocks = bookingLocks;
        _outbox = outbox;
        _uniqueViolations = uniqueViolations;
        _clock = clock;
        _logger = logger;
    }

    // ------------------------------------------------------------------------ step 1: confirm

    /// <summary>
    /// Confirms a searched fare's price for these travellers with the supplier, and records the order
    /// it will be, pending payment.
    /// </summary>
    /// <exception cref="CheckoutRefusedException">The fare is gone, or the supplier's answer could not be trusted.</exception>
    public async Task<CheckoutPriceConfirmation> ConfirmPriceAsync(
        Guid offerId,
        IReadOnlyList<CheckoutTraveller> travellers,
        string? correlationId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(travellers);

        var agencyId = RequireAgency();
        await RefuseIfNotSellingAsync(agencyId, cancellationToken);
        RequireTravellers(travellers);

        var offer = await _db.SupplierOffers.AsNoTracking().SingleOrDefaultAsync(candidate => candidate.Id == offerId, cancellationToken)
            ?? throw new CheckoutRefusedException(
                CheckoutRefusal.NotFound,
                "We could not find that fare.",
                "Fares from a search are kept for a few minutes only. Search again and choose one.");

        var supplierSessionId = await _db.SearchSessions.AsNoTracking()
            .Where(session => session.Id == offer.SearchSessionId)
            .Select(session => session.SupplierSessionId)
            .SingleAsync(cancellationToken);

        var supplierCode = await _db.Suppliers.AsNoTracking()
            .Where(supplier => supplier.Id == offer.SupplierId)
            .Select(supplier => supplier.Code)
            .SingleAsync(cancellationToken);

        var adapter = _adapters.Resolve(supplierCode, offer.ProductType);
        var subject = new PricingSubject(PricedTypeOf(offer.ProductType), offer.Currency, supplierCode: supplierCode);
        var searched = await _pricing.PriceAsync(subject, offer.TotalFareMinor, cancellationToken);

        // Q3: the wallet first. A short wallet fails here, before any supplier call.
        var provisionalHoldId = await PlaceProvisionalHoldAsync(
            offer.Currency, searched.NetAmountMinor + searched.PlatformFeeMinor, cancellationToken);

        try
        {
            return await ConfirmHeldAsync(
                agencyId, offer, supplierSessionId, supplierCode, adapter, subject, searched.GrossAmountMinor,
                travellers, provisionalHoldId, correlationId, cancellationToken);
        }
        catch
        {
            // Nothing was bought, so the money held for it goes straight back.
            await ReleaseProvisionalHoldAsync(provisionalHoldId);
            throw;
        }
    }

    private async Task<CheckoutPriceConfirmation> ConfirmHeldAsync(
        Guid agencyId,
        SupplierOffer offer,
        string supplierSessionId,
        string supplierCode,
        ISupplierAdapter adapter,
        PricingSubject subject,
        Money searchedSell,
        IReadOnlyList<CheckoutTraveller> travellers,
        Guid provisionalHoldId,
        string? correlationId,
        CancellationToken cancellationToken)
    {
        SupplierPriceConfirmation confirmation;

        try
        {
            // Holds the fare with the supplier. Sent once: asking twice would hold it twice.
            confirmation = await adapter.ConfirmPriceAsync(
                new SupplierCallContext(agencyId, SupplierBookingId: null, correlationId),
                new SupplierPriceConfirmationRequest(
                    offer.ProductType,
                    supplierSessionId,
                    offer.OfferRef,
                    offer.Reference,
                    travellers.Select(ToPassenger).ToList()),
                cancellationToken);
        }
        catch (SupplierRequestRejectedException)
        {
            throw new CheckoutRefusedException(
                CheckoutRefusal.Conflict,
                "The supplier no longer offers this fare.",
                "Nothing was booked or charged. Search again for a fresh fare.");
        }
        catch (SupplierCallOutcomeUnknownException)
        {
            throw new CheckoutRefusedException(
                CheckoutRefusal.SupplierFailed,
                "The supplier did not confirm the price in time.",
                "Nothing was booked or charged. Try again in a moment.");
        }

        var ticketTimeLimit = await VerifyAsync(agencyId, supplierCode, confirmation, cancellationToken);
        var confirmedNet = new Money(confirmation.Lines.Sum(line => line.NewPrice.AmountMinor));
        var title = await TitleAsync(offer.Id, offer.ProductType, cancellationToken);

        var (reference, sell) = await WithWalletRetryAsync(() => _transactions.RunAsync(
            async token =>
            {
                var now = _clock.GetUtcNow();

                // The price the traveller will pay, frozen from here: the quote, then the order line
                // copied from it (CLAUDE.md rule 5).
                var quote = await _pricing.QuoteAsync(subject, confirmedNet, token);

                var order = await _placeOrder.HandleAsync(
                    new PlaceOrderCommand(
                        BuyerType.AgentAssisted,
                        OrderChannel.Console,
                        CustomerId: null,
                        [
                            new PlaceOrderLine(
                                quote.Id,
                                title,
                                PaxBreakdown(travellers),
                                SupplierOfferId: offer.Id,
                                Travellers: travellers.Select(ToOrderTraveller).ToList()),
                        ]),
                    token);

                var line = order.Lines[0];

                // After the order is saved: EF does not know supplier_bookings.order_line_id references
                // order_lines (that key was added in SQL), so it could not order the two inserts itself.
                var booking = SupplierBooking.Create(
                    agencyId,
                    offer.SupplierId,
                    line.Id,
                    offer.Id,
                    offer.ProductType,
                    confirmation.TripType,
                    confirmation.TripMode,
                    confirmation.SupplierSessionId,
                    offer.Currency,
                    $"order-line:{line.Id:N}");

                booking.RecordPriceConfirmation(confirmation.Lines, now);
                line.AttachSupplierBooking(booking.Id);
                _db.SupplierBookings.Add(booking);

                foreach (var traveller in travellers)
                {
                    _db.SupplierBookingPassengers.Add(SupplierBookingPassenger.Add(
                        agencyId,
                        booking.Id,
                        traveller.Type,
                        traveller.FirstName,
                        traveller.LastName,
                        title: traveller.Title,
                        birthDate: traveller.BirthDate,
                        gender: traveller.Gender,
                        email: traveller.Email,
                        phoneNumber: traveller.Phone));
                }

                // The provisional hold gives way to the booking's own, for exactly what the confirmed fare
                // costs the agency — in one step, so the money is never unheld in between.
                var provisional = await _db.WalletHolds.SingleAsync(hold => hold.Id == provisionalHoldId, token);
                var wallet = await _db.Wallets.SingleAsync(candidate => candidate.Id == provisional.WalletId, token);
                wallet.ReleaseHold(provisional, now);

                var amount = line.NetAmountMinor + line.PlatformFeeMinor;

                if (wallet.AvailableMinor < amount)
                {
                    throw NotEnough(amount, wallet.AvailableMinor, "The supplier's price rose above what the wallet can cover.");
                }

                _db.WalletHolds.Add(wallet.PlaceHold(amount, now, ticketTimeLimit - now + HoldGrace, order.Id));

                await _db.SaveChangesAsync(token);

                return (order.OrderNumber, line.GrossAmountMinor);
            },
            cancellationToken));

        LogConfirmed(_logger, reference, sell.AmountMinor, ticketTimeLimit);

        return new CheckoutPriceConfirmation(reference, sell, searchedSell, offer.Currency, ticketTimeLimit);
    }

    // --------------------------------------------------------------------------- step 2: pay

    /// <summary>
    /// Pays for a confirmed booking from the agency's wallet and asks a Worker to issue the ticket.
    /// </summary>
    /// <param name="reference">The order the price confirmation created.</param>
    /// <param name="payment">Where the money comes from. Only the wallet, for now.</param>
    /// <param name="acceptedSell">The price the agent accepted. Must be the confirmed one.</param>
    /// <param name="idempotencyKey">One per payment attempt: the same key again is the same booking.</param>
    /// <param name="paidByUserId">The agent who pressed Pay.</param>
    /// <param name="correlationId">Carried to the issue call's audit row.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The booking's reference.</returns>
    public async Task<string> PlaceAsync(
        string reference,
        OrderPaymentMethod payment,
        Money acceptedSell,
        string idempotencyKey,
        Guid? paidByUserId = null,
        string? correlationId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);
        await RefuseIfNotSellingAsync(RequireAgency(), cancellationToken);

        var key = idempotencyKey?.Trim();

        if (string.IsNullOrEmpty(key) || key.Length > MaxIdempotencyKeyLength)
        {
            throw new CheckoutRefusedException(
                CheckoutRefusal.Unprocessable,
                "The payment needs an idempotency key.",
                $"Send one key, at most {MaxIdempotencyKeyLength} characters, per payment attempt — and the same key when retrying it.");
        }

        if (payment != OrderPaymentMethod.Wallet)
        {
            throw new CheckoutRefusedException(
                CheckoutRefusal.Unprocessable,
                "Card payment is not available for console bookings yet.",
                "Nothing was booked or charged. Top the wallet up by card from the Wallet page, then pay from the wallet.");
        }

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await PlaceOnceAsync(reference.Trim(), acceptedSell, key, paidByUserId, correlationId, cancellationToken);
            }
            catch (DbUpdateConcurrencyException) when (attempt < MaxPlaceAttempts)
            {
                // The wallet moved at the same moment — a top-up, another booking. Read it again.
                _db.ChangeTracker.Clear();
            }
            catch (DbUpdateException ex) when (_uniqueViolations.IsUniqueViolation(ex))
            {
                // The same key committed first, on another request: that is this booking, already paid.
                _db.ChangeTracker.Clear();
                return reference.Trim();
            }
        }
    }

    private Task<string> PlaceOnceAsync(
        string reference,
        Money acceptedSell,
        string key,
        Guid? paidByUserId,
        string? correlationId,
        CancellationToken cancellationToken) =>
        _transactions.RunAsync(
            async token =>
            {
                var now = _clock.GetUtcNow();

                var target = await _db.Orders.AsNoTracking()
                    .Where(order => order.OrderNumber == reference)
                    .Select(order => new { order.Id, BookingId = order.Lines.Select(line => line.SupplierBookingId).FirstOrDefault() })
                    .SingleOrDefaultAsync(token)
                    ?? throw NotFound();

                if (target.BookingId is not { } bookingId)
                {
                    throw new CheckoutRefusedException(
                        CheckoutRefusal.Conflict,
                        "This order has no supplier booking to pay for.",
                        "Nothing was charged. Confirm the price again from the search.");
                }

                // One payment at a time for this booking: a second press, or the time limit monitor
                // lapsing it at this very moment, waits here and then sees what this one did.
                await _bookingLocks.LockAsync(bookingId, token);

                var order = await _db.Orders.Include(candidate => candidate.Lines).SingleAsync(candidate => candidate.Id == target.Id, token);

                if (string.Equals(order.PaymentIdempotencyKey, key, StringComparison.Ordinal))
                {
                    return order.OrderNumber;
                }

                if (order.PaidAt is not null || order.Status != OrderStatus.PendingPayment)
                {
                    throw new CheckoutRefusedException(
                        CheckoutRefusal.Conflict,
                        "This booking has already been paid for.",
                        "Nothing more was charged. Open it from Bookings to see where it has got to.");
                }

                var line = order.Lines.Single();
                var booking = await _db.SupplierBookings.SingleAsync(candidate => candidate.Id == bookingId, token);

                if (booking.Status == SupplierBookingStatus.Expired || booking.TicketTimeLimit is not { } limit || limit <= now)
                {
                    throw new CheckoutRefusedException(
                        CheckoutRefusal.Gone,
                        "The supplier's hold on this fare has ended.",
                        "Nothing was charged. Search again for a fresh fare.");
                }

                if (booking.Status != SupplierBookingStatus.PriceConfirmed)
                {
                    throw new CheckoutRefusedException(
                        CheckoutRefusal.Conflict,
                        "This fare can no longer be booked.",
                        "Nothing was charged. Search again for a fresh fare.");
                }

                // Price change gate (#42, decision Q9): a rise the agent has not accepted stops here. A fall
                // passes through — they pay the lower, confirmed price.
                if (line.GrossAmountMinor > acceptedSell)
                {
                    throw new CheckoutRefusedException(
                        CheckoutRefusal.Conflict,
                        "The price has changed.",
                        $"The confirmed price is {line.Currency} {line.GrossAmountMinor.AmountMinor} (in kobo). Nothing was charged: "
                        + "accept the new price, then pay.");
                }

                // The hold placed when the price was confirmed (Q3). Should it be gone, the booking takes a new
                // one now — still before anything is sent to the supplier.
                var held = await _db.WalletHolds.AnyAsync(
                    hold => hold.OrderId == order.Id && hold.Status == WalletHoldStatus.Held, token);

                if (!held)
                {
                    // What the agency owes Trips for this line: the supplier's net rate and the platform fee.
                    // The markup and tax are the agency's to collect from its customer (decision Q4).
                    var amount = line.NetAmountMinor + line.PlatformFeeMinor;
                    var wallet = await WalletAsync(order.Currency, token);

                    if (wallet.AvailableMinor < amount)
                    {
                        throw NotEnough(amount, wallet.AvailableMinor);
                    }

                    _db.WalletHolds.Add(wallet.PlaceHold(amount, now, limit - now + HoldGrace, order.Id));
                }

                order.RecordPayment(OrderPaymentMethod.Wallet, key, now, paidByUserId);
                line.RecordFulfilment(FulfilmentStatus.Confirming, now);

                // The next step, in this same transaction: a Worker issues the ticket once this commits,
                // and never if it does not.
                _outbox.Enqueue(
                    new IssueSupplierTicket(line.Id, order.AgencyId, booking.IdempotencyKey, correlationId),
                    order.AgencyId);

                await _db.SaveChangesAsync(token);

                LogPaid(_logger, order.OrderNumber);
                return order.OrderNumber;
            },
            cancellationToken);

    // ------------------------------------------------------------------------------ helpers

    /// <summary>
    /// Every element of the confirmation must verify on its own (#35), and there must be a deadline.
    /// </summary>
    private async Task<DateTimeOffset> VerifyAsync(
        Guid agencyId,
        string supplierCode,
        SupplierPriceConfirmation confirmation,
        CancellationToken cancellationToken)
    {
        if (confirmation.Lines.Count == 0)
        {
            throw new CheckoutRefusedException(
                CheckoutRefusal.SupplierFailed,
                "The supplier's confirmation was empty.",
                "Nothing was booked or charged. Search again.");
        }

        if (confirmation.Lines.Any(line => !SupplierBookingConfirmation.HashesMatch(line.HashExpected, line.HashReceived)))
        {
            await _priceConfirmation.RaiseIntegrityAlertAsync(agencyId, supplierBookingId: null, supplierCode, confirmation.Lines, cancellationToken);

            throw new CheckoutRefusedException(
                CheckoutRefusal.SupplierFailed,
                "The supplier's price could not be verified.",
                "Nothing was booked or charged, and the fare has been reported. Search again for another.");
        }

        var limit = confirmation.Lines.Min(line => line.TicketTimeLimit)
            ?? throw new CheckoutRefusedException(
                CheckoutRefusal.SupplierFailed,
                "The supplier did not say how long it will hold the fare.",
                "Nothing was booked or charged. Search again.");

        if (limit <= _clock.GetUtcNow())
        {
            throw new CheckoutRefusedException(
                CheckoutRefusal.Gone,
                "The supplier's hold on this fare has already ended.",
                "Nothing was booked or charged. Search again.");
        }

        return limit;
    }

    /// <summary>What the line reads as, for a traveller and an agent: the route and who flies or drives it.</summary>
    private async Task<string> TitleAsync(Guid offerId, SupplierProductType product, CancellationToken cancellationToken)
    {
        if (product == SupplierProductType.Bus)
        {
            var bus = await _db.BusSegments.AsNoTracking()
                .Where(segment => segment.SupplierOfferId == offerId)
                .OrderBy(segment => segment.DepartureAt)
                .Select(segment => new { segment.OperatorName, segment.DepartureTerminalId, segment.ArrivalTerminalId })
                .FirstOrDefaultAsync(cancellationToken);

            return bus is null
                ? "Bus trip"
                : $"{bus.OperatorName}, terminal {bus.DepartureTerminalId} → {bus.ArrivalTerminalId}";
        }

        var segments = await _db.FlightSegments.AsNoTracking()
            .Where(segment => segment.SupplierOfferId == offerId)
            .OrderBy(segment => segment.LegIndex)
            .ThenBy(segment => segment.SegmentIndex)
            .Select(segment => new { segment.LegIndex, segment.OriginIata, segment.DestinationIata, segment.FlightNumber })
            .ToListAsync(cancellationToken);

        if (segments.Count == 0)
        {
            return "Flight";
        }

        var outbound = segments.Where(segment => segment.LegIndex == segments[0].LegIndex).ToList();
        var returns = segments.Any(segment => segment.LegIndex != segments[0].LegIndex);

        return $"{outbound[0].OriginIata} {(returns ? "⇄" : "→")} {outbound[^1].DestinationIata}, {outbound[0].FlightNumber}";
    }

    private static string PaxBreakdown(IReadOnlyList<CheckoutTraveller> travellers) =>
        JsonSerializer.Serialize(new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["adults"] = travellers.Count(traveller => traveller.Type == PassengerType.Adult),
            ["children"] = travellers.Count(traveller => traveller.Type == PassengerType.Child),
            ["infants"] = travellers.Count(traveller => traveller.Type == PassengerType.Infant),
        });

    private static SupplierPassenger ToPassenger(CheckoutTraveller traveller) =>
        new(
            traveller.Type,
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

    private static PlaceOrderTraveller ToOrderTraveller(CheckoutTraveller traveller) =>
        new(
            traveller.Type switch
            {
                PassengerType.Child => TravellerType.Child,
                PassengerType.Infant => TravellerType.Infant,
                _ => TravellerType.Adult,
            },
            traveller.FirstName.Trim(),
            traveller.LastName.Trim(),
            traveller.BirthDate,
            traveller.PassportNumber,
            traveller.PassportExpiry,
            traveller.Nationality?.Trim().ToUpperInvariant());

    private static PricedProductType PricedTypeOf(SupplierProductType product) =>
        product == SupplierProductType.Bus ? PricedProductType.Bus : PricedProductType.Flight;

    private static void RequireTravellers(IReadOnlyList<CheckoutTraveller> travellers)
    {
        if (travellers.Count == 0 || travellers.All(traveller => traveller.Type != PassengerType.Adult))
        {
            throw new CheckoutRefusedException(
                CheckoutRefusal.Unprocessable,
                "A booking needs at least one adult traveller.",
                "Add the travellers, then confirm the price again.");
        }

        if (travellers.Any(traveller => string.IsNullOrWhiteSpace(traveller.FirstName) || string.IsNullOrWhiteSpace(traveller.LastName)))
        {
            throw new CheckoutRefusedException(
                CheckoutRefusal.Unprocessable,
                "Every traveller needs a first and last name.",
                "Names must match the travel document exactly.");
        }
    }

    private Guid RequireAgency() =>
        _tenant.AgencyId ?? throw new InvalidOperationException("A checkout is always for an agency, and this request has none.");

    /// <summary>
    /// Refuses a new booking when the agency is not allowed to sell.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Build-plan decision 14: a suspended agency's existing bookings stand, but it takes no new
    /// ones. The rule itself is in <see cref="AgencyAccess"/>, so the storefront and this checkout
    /// answer the same question the same way; all this does is ask it, at the two points where a
    /// booking begins.
    /// </para>
    /// <para>
    /// Only the entry points check. Everything after a price is confirmed — paying, issuing,
    /// resolving — belongs to a booking that already exists, and suspending an agency mid-flight
    /// must not strand a traveller who has already paid.
    /// </para>
    /// </remarks>
    private async Task RefuseIfNotSellingAsync(Guid agencyId, CancellationToken cancellationToken)
    {
        var status = await _db.Agencies.AsNoTracking()
            .Where(agency => agency.Id == agencyId)
            .Select(agency => agency.Status)
            .SingleAsync(cancellationToken);

        if (AgencyAccess.CanTakeNewBookings(status))
        {
            return;
        }

        throw new CheckoutRefusedException(
            CheckoutRefusal.Unprocessable,
            "This account cannot take new bookings.",
            AgencyAccess.WhyNewBookingsAreRefused(status) ?? "Contact Trips support.");
    }

    private static CheckoutRefusedException NotFound() =>
        new(
            CheckoutRefusal.NotFound,
            "We could not find that booking.",
            "It may belong to another agency, or the reference may be mistyped.");

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Confirmed booking {Reference} at {SellMinor} kobo; the supplier holds it until {TicketTimeLimit}.")]
    private static partial void LogConfirmed(ILogger logger, string reference, long sellMinor, DateTimeOffset ticketTimeLimit);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Booking {Reference} paid for; its wallet hold stands until the ticket is issued.")]
    private static partial void LogPaid(ILogger logger, string reference);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "Could not release provisional wallet hold {HoldId}; the checkout sweeper will once it expires.")]
    private static partial void LogReleaseFailed(ILogger logger, Guid holdId, Exception exception);

    // ------------------------------------------------------------------------- wallet holds

    /// <summary>Holds <paramref name="amount"/> before the supplier is asked, or refuses — having asked nothing.</summary>
    private Task<Guid> PlaceProvisionalHoldAsync(string currency, Money amount, CancellationToken cancellationToken) =>
        WithWalletRetryAsync(() => _transactions.RunAsync(
            async token =>
            {
                var wallet = await WalletAsync(currency, token);

                if (wallet.AvailableMinor < amount)
                {
                    throw NotEnough(amount, wallet.AvailableMinor);
                }

                var hold = wallet.PlaceHold(amount, _clock.GetUtcNow(), ProvisionalHoldLifetime);
                _db.WalletHolds.Add(hold);
                await _db.SaveChangesAsync(token);

                return hold.Id;
            },
            cancellationToken));

    /// <summary>
    /// Gives the provisional hold back after a checkout that went no further. Never throws: if it cannot,
    /// the sweeper releases the hold once it expires.
    /// </summary>
    private async Task ReleaseProvisionalHoldAsync(Guid holdId)
    {
        // First, forget whatever the failed step had staged — a quote, an order — so this save cannot
        // write it outside the transaction that was rolled back.
        _db.ChangeTracker.Clear();

        try
        {
            await WithWalletRetryAsync(() => _transactions.RunAsync(
                async token =>
                {
                    var hold = await _db.WalletHolds.SingleOrDefaultAsync(candidate => candidate.Id == holdId, token);

                    if (hold is null || hold.Status != WalletHoldStatus.Held)
                    {
                        return false;
                    }

                    var wallet = await _db.Wallets.SingleAsync(candidate => candidate.Id == hold.WalletId, token);
                    wallet.ReleaseHold(hold, _clock.GetUtcNow());
                    await _db.SaveChangesAsync(token);

                    return true;
                },
                CancellationToken.None));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogReleaseFailed(_logger, holdId, ex);
        }
    }

    /// <summary>Retries <paramref name="work"/> when the wallet moved underneath it — a top-up, another booking.</summary>
    private async Task<T> WithWalletRetryAsync<T>(Func<Task<T>> work)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await work();
            }
            catch (DbUpdateConcurrencyException) when (attempt < MaxWalletAttempts)
            {
                _db.ChangeTracker.Clear();
            }
        }
    }

    private async Task<Domain.Payments.Wallet> WalletAsync(string currency, CancellationToken cancellationToken) =>
        await _db.Wallets.SingleOrDefaultAsync(candidate => candidate.Currency == currency, cancellationToken)
        ?? throw new CheckoutRefusedException(
            CheckoutRefusal.Unprocessable,
            $"This agency has no {currency} wallet.",
            "A wallet is opened when KYB is approved. Nothing was booked or charged.");

    private static CheckoutRefusedException NotEnough(Money needed, Money available, string? why = null) =>
        new(
            CheckoutRefusal.Unprocessable,
            "There is not enough in the wallet for this booking.",
            (why is null ? string.Empty : why + " ")
            + $"It needs {needed.AmountMinor} and {available.AmountMinor} is available (in kobo). Nothing was booked or charged. "
            + "Top up, then try again.");
}
