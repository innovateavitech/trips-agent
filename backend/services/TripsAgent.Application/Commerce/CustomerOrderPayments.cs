using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TripsAgent.Application.Checkout;
using TripsAgent.Application.Messaging;
using TripsAgent.Application.Notifications;
using TripsAgent.Application.Payments;
using TripsAgent.Application.Persistence;
using TripsAgent.Application.Suppliers;
using TripsAgent.Application.Tenancy;
using TripsAgent.Domain.Orders;
using TripsAgent.Domain.Payments;
using TripsAgent.Domain.Suppliers;

namespace TripsAgent.Application.Commerce;

/// <summary>What one attempt to settle a traveller's card payment came to.</summary>
public enum CustomerPaymentOutcome
{
    /// <summary>The money is in, and the booking is on its way.</summary>
    Settled = 1,

    /// <summary>Somebody — a webhook, the return page, an earlier retry — had already settled it.</summary>
    AlreadySettled = 2,

    /// <summary>The gateway has no final answer yet. Nothing changed; ask again later.</summary>
    StillPending = 3,

    /// <summary>The gateway says it finally failed. Nothing was charged, and the holds will lapse.</summary>
    Failed = 4,

    /// <summary>The traveller was charged, but not in a way we can credit. Held for a person.</summary>
    UnderReview = 5,

    /// <summary>No order payment of ours has that reference.</summary>
    UnknownReference = 6,
}

/// <summary>
/// Turns a traveller's confirmed card payment into a funded booking (build plan F5).
/// </summary>
/// <remarks>
/// <para>
/// <b>One money path, not two</b> (decision 2, decision 3). The platform is merchant of record, so a
/// traveller's payment settles into the agency's wallet exactly as a top-up does
/// (<see cref="WalletTopUpService"/>). The booking then holds from that wallet and captures when it
/// is fulfilled, which is the same path an agent's own booking takes — so there is one place money
/// leaves a wallet, one place it is captured, and one place it is given back.
/// </para>
/// <para>
/// <b>What is held is what the line costs the agency</b>, never what the traveller paid. For a
/// flight that is the supplier's net rate plus the platform's fee; for a tour the agency hosts
/// itself there is no supplier, so it is the platform's fee alone. The markup and the tax stay in
/// the wallet, which is the agency's margin (decision 4, decision 25).
/// </para>
/// <para>
/// <b>Per line, because a cart is mixed.</b> Each line gets its own hold, so one line failing gives
/// back its own money and leaves the rest of the booking alone —
/// <see cref="CheckoutCompletion"/> captures a confirmed line and <see cref="FailedLines"/> sends a
/// failed one to the agent's resolution queue.
/// </para>
/// <para>
/// <b>Safe to call again, from anywhere.</b> The webhook and the traveller's return page routinely
/// both arrive for the same payment, and either may be first. Crediting is idempotent through
/// <c>PaymentTransaction.LedgerTransactionGroupId</c>; funding is idempotent through
/// <c>Order.PaidAt</c>.
/// </para>
/// </remarks>
public sealed partial class CustomerOrderPayments : IOrderPaymentSettlement
{
    private const int MaxAttempts = 3;

    private readonly IAppDbContext _db;
    private readonly ITransactionRunner _transactions;
    private readonly IPaymentGateway _gateway;
    private readonly IPlatformScope _platformScope;
    private readonly WalletTopUpService _credits;
    private readonly AgencyLineFulfilment _agencyLines;
    private readonly IOutbox _outbox;
    private readonly IPlatformAlerter _alerter;
    private readonly IUniqueViolationDetector _uniqueViolations;
    private readonly TimeProvider _clock;
    private readonly ILogger<CustomerOrderPayments> _logger;

    public CustomerOrderPayments(
        IAppDbContext db,
        ITransactionRunner transactions,
        IPaymentGateway gateway,
        IPlatformScope platformScope,
        WalletTopUpService credits,
        AgencyLineFulfilment agencyLines,
        IOutbox outbox,
        IPlatformAlerter alerter,
        IUniqueViolationDetector uniqueViolations,
        TimeProvider clock,
        ILogger<CustomerOrderPayments> logger)
    {
        _db = db;
        _transactions = transactions;
        _gateway = gateway;
        _platformScope = platformScope;
        _credits = credits;
        _agencyLines = agencyLines;
        _outbox = outbox;
        _alerter = alerter;
        _uniqueViolations = uniqueViolations;
        _clock = clock;
        _logger = logger;
    }

    /// <inheritdoc />
    async Task<bool> IOrderPaymentSettlement.SettleAsync(string reference, CancellationToken cancellationToken) =>
        await SettleAsync(reference, cancellationToken) == CustomerPaymentOutcome.StillPending;

    /// <summary>
    /// Asks the gateway what happened to one order payment, and acts on the answer.
    /// </summary>
    /// <param name="reference">Our own reference for the attempt, which the gateway quotes back.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <exception cref="PaymentGatewayException">The gateway could not be asked, or answered unusably.</exception>
    public async Task<CustomerPaymentOutcome> SettleAsync(string reference, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);

        // A gateway callback carries no session of its own; the reference identifies the tenant.
        using var scope = _platformScope.Enter(
            "customer payment settlement — a gateway callback identifies its own payment, not a signed-in agency");

        var payment = await LoadAsync(reference, cancellationToken);

        if (payment is null || payment.Purpose != PaymentPurpose.OrderPayment || payment.OrderId is null)
        {
            return CustomerPaymentOutcome.UnknownReference;
        }

        if (payment.LedgerTransactionGroupId is not null)
        {
            // Credited already. Funding may still be outstanding if the process died between the
            // two, so it is tried again rather than assumed — it does nothing when already done.
            await FundAsync(payment, cancellationToken);
            return CustomerPaymentOutcome.AlreadySettled;
        }

        if (payment.Status == PaymentStatus.UnderReview)
        {
            return CustomerPaymentOutcome.UnderReview;
        }

        var verification = await _gateway.VerifyAsync(reference, cancellationToken);
        var now = _clock.GetUtcNow();

        switch (verification.Outcome)
        {
            case GatewayPaymentOutcome.Pending:
                // Not failed: an abandoned page can be come back to, and a transfer settles later.
                return CustomerPaymentOutcome.StillPending;

            case GatewayPaymentOutcome.Failed:
                payment.MarkFailed(verification.FailureReason ?? verification.Status, now);
                await _db.SaveChangesAsync(cancellationToken);
                return CustomerPaymentOutcome.Failed;

            case GatewayPaymentOutcome.Succeeded:
                return await CreditAsync(payment, verification, now, cancellationToken);

            default:
                throw new InvalidOperationException($"Unhandled gateway outcome {verification.Outcome}.");
        }
    }

    private async Task<CustomerPaymentOutcome> CreditAsync(
        PaymentTransaction payment,
        GatewayVerification verification,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            if (payment.LedgerTransactionGroupId is not null)
            {
                await FundAsync(payment, cancellationToken);
                return CustomerPaymentOutcome.AlreadySettled;
            }

            payment.MarkSucceeded(
                verification.AmountMinor,
                verification.FeeMinor,
                verification.Currency,
                verification.GatewayReference,
                now);

            try
            {
                if (payment.Status == PaymentStatus.UnderReview)
                {
                    await _db.SaveChangesAsync(cancellationToken);
                    await AlertUnderReviewAsync(payment, cancellationToken);
                    return CustomerPaymentOutcome.UnderReview;
                }

                if (!await _credits.PostAsync(payment, cancellationToken))
                {
                    await FundAsync(payment, cancellationToken);
                    return CustomerPaymentOutcome.AlreadySettled;
                }
            }
            catch (DbUpdateException ex) when (ex is DbUpdateConcurrencyException || _uniqueViolations.IsUniqueViolation(ex))
            {
                // Another writer committed first. Discard what this one staged and read again.
                _db.ChangeTracker.Clear();
                LogPostingConflict(_logger, payment.Reference, attempt);

                if (attempt >= MaxAttempts)
                {
                    throw;
                }

                payment = await LoadAsync(payment.Reference, cancellationToken)
                          ?? throw new InvalidOperationException($"Payment {verification.GatewayReference} disappeared mid-settlement.");
                continue;
            }

            await FundAsync(payment, cancellationToken);
            return CustomerPaymentOutcome.Settled;
        }
    }

    /// <summary>
    /// Puts the credited money behind the booking: a hold per line, the order marked paid, and each
    /// line sent on its way.
    /// </summary>
    /// <remarks>
    /// A separate transaction from the crediting on purpose. The credit is money arriving and must
    /// stand on its own; funding is what the agency then does with it, and a failure here leaves the
    /// traveller paid and the agency credited rather than the money in limbo. The webhook's retries,
    /// and the traveller's own return page, run this again.
    /// </remarks>
    private async Task FundAsync(PaymentTransaction payment, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await _transactions.RunAsync(token => FundOnceAsync(payment, token), cancellationToken);
                return;
            }
            catch (DbUpdateConcurrencyException) when (attempt < MaxAttempts)
            {
                // The wallet moved at the same moment — another booking, another payment. Read again.
                _db.ChangeTracker.Clear();
            }
        }
    }

    private async Task<bool> FundOnceAsync(PaymentTransaction payment, CancellationToken cancellationToken)
    {
        var order = await _db.Orders.Include(candidate => candidate.Lines)
            .SingleOrDefaultAsync(candidate => candidate.Id == payment.OrderId, cancellationToken);

        if (order is null || order.PaidAt is not null || order.Status != OrderStatus.PendingPayment)
        {
            // Funded already, or cancelled while the payment was in flight. Either way there is
            // nothing to do, and doing it again would place a second set of holds.
            return false;
        }

        var now = _clock.GetUtcNow();

        var wallet = await _db.Wallets.SingleOrDefaultAsync(
            candidate => candidate.AgencyId == order.AgencyId && candidate.Currency == order.Currency,
            cancellationToken);

        if (wallet is null)
        {
            // The credit above would have thrown first; this is belt and braces for a hand-fixed row.
            throw new InvalidOperationException(
                $"Agency {order.AgencyId} has no {order.Currency} wallet to fund order {order.OrderNumber} from.");
        }

        // The payment reference is the idempotency key: it is unique platform-wide, and a second
        // settlement of the same payment is the same booking rather than a second one.
        order.RecordPayment(StorefrontCheckoutService.PaidBy, payment.Reference, now);

        var agencyLines = new List<OrderLine>();

        foreach (var line in order.Lines)
        {
            var cost = CartPricing.CostToAgency(line);

            if (cost.AmountMinor > 0)
            {
                if (wallet.AvailableMinor < cost)
                {
                    // The traveller's money was just credited, so this should be impossible. It is
                    // still handled rather than thrown: the booking is real, and a line nobody can
                    // fund is exactly what the resolution queue is for.
                    FailedLines.FlagForResolution(
                        order,
                        line,
                        "The agency's wallet could not cover what this item costs, so it was not booked. "
                        + "The traveller's payment is in the wallet.",
                        now,
                        _outbox);

                    continue;
                }

                _db.WalletHolds.Add(wallet.PlaceHold(cost, now, HoldLifetime(line), order.Id, line.Id));
            }

            if (CartPricing.IsAgencyHosted(line.ItemType))
            {
                agencyLines.Add(line);
                continue;
            }

            // A flight or a bus: the Worker issues the ticket once this commits, and never if it
            // does not. Issuing is never retried (ADR-0003); the pipeline already owns that rule.
            line.RecordFulfilment(FulfilmentStatus.Confirming, now);

            var booking = await _db.SupplierBookings.SingleOrDefaultAsync(
                candidate => candidate.OrderLineId == line.Id, cancellationToken);

            if (booking is null)
            {
                FailedLines.FlagForResolution(
                    order, line, "This item had no supplier booking to issue a ticket against.", now, _outbox);
                continue;
            }

            _outbox.Enqueue(
                new IssueSupplierTicket(line.Id, order.AgencyId, booking.IdempotencyKey, CorrelationId: null),
                order.AgencyId);
        }

        await _db.SaveChangesAsync(cancellationToken);

        // The agency's own products need no supplier, so they are confirmed here and now — inside
        // this same transaction, so a booking is never half-confirmed.
        foreach (var line in agencyLines)
        {
            await _agencyLines.ConfirmAsync(order, line, now, cancellationToken);
        }

        LogFunded(_logger, order.OrderNumber, order.Lines.Count);

        return true;
    }

    /// <summary>
    /// How long a line's money stays held before the sweeper may give it back.
    /// </summary>
    /// <remarks>
    /// A supplier line's money is held until its ticket exists, which can be days when the supplier
    /// is slow (issue 37), so the window is generous and an outstanding hold past it is worth a
    /// person's look. An agency's own line is captured in the same transaction, so its hold never
    /// outlives the request.
    /// </remarks>
    private static TimeSpan HoldLifetime(OrderLine line) =>
        CartPricing.IsAgencyHosted(line.ItemType) ? TimeSpan.FromMinutes(5) : TimeSpan.FromDays(7);

    private Task<PaymentTransaction?> LoadAsync(string reference, CancellationToken cancellationToken) =>
        _db.PaymentTransactions.FirstOrDefaultAsync(payment => payment.Reference == reference, cancellationToken);

    /// <summary>Tells the platform team a traveller was charged and nothing was credited.</summary>
    private async Task AlertUnderReviewAsync(PaymentTransaction payment, CancellationToken cancellationToken)
    {
        try
        {
            await _alerter.RaiseAsync(
                new PlatformAlert(
                    AlertSeverity.P1,
                    $"Customer payment {payment.Reference} was charged but cannot be credited automatically",
                    $"The gateway confirmed payment {payment.Reference} for order {payment.OrderId}, but not one the "
                    + $"wallet can be credited from: {payment.FailureReason}\n\n"
                    + $"Requested {payment.AmountMinor} {payment.Currency}; the gateway reports "
                    + $"{payment.VerifiedAmountMinor} paid.\n\n"
                    + "Nothing was credited and the booking is not funded. A traveller is out of pocket, so this needs "
                    + "a person today: check the payment in the gateway's dashboard, then either credit the agency by "
                    + "an adjustment or refund the traveller.",
                    nameof(CustomerOrderPayments),
                    payment.AgencyId),
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogAlertFailed(_logger, ex, payment.Reference);
        }
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Order {Reference} funded from the wallet: {LineCount} lines on their way.")]
    private static partial void LogFunded(ILogger logger, string reference, int lineCount);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Settling {Reference} lost a race with another writer (attempt {Attempt}); re-reading.")]
    private static partial void LogPostingConflict(ILogger logger, string reference, int attempt);

    [LoggerMessage(Level = LogLevel.Critical,
        Message = "Could not raise the alert for customer payment {Reference}, held for review.")]
    private static partial void LogAlertFailed(ILogger logger, Exception exception, string reference);
}
