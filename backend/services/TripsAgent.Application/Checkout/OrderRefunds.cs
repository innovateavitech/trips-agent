using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TripsAgent.Application.Notifications;
using TripsAgent.Application.Payments;
using TripsAgent.Application.Persistence;
using TripsAgent.Application.Tenancy;
using TripsAgent.Domain.Common;
using TripsAgent.Domain.Orders;
using TripsAgent.Domain.Payments;

namespace TripsAgent.Application.Checkout;

/// <summary>
/// Gives an order line's money back to wherever it came from (issue 43, build plan F5).
/// </summary>
/// <remarks>
/// <para>
/// <b>Money goes back the way it came</b> — the rule the build plan sets for issue 43. An agent's
/// booking was paid from the agency's wallet, and the wallet is where it goes back
/// (<see cref="WalletRefunds"/>). A traveller's booking on a storefront was paid by card, so the
/// card is where it goes back, through the gateway. Refunding a traveller into their agent's wallet
/// would leave a person out of pocket and an agency holding money it did not earn.
/// </para>
/// <para>
/// <b>Both sides of a card booking are undone.</b> What the line cost the agency — the supplier's
/// net rate and the platform's fee — is released or credited back exactly as it is for a wallet
/// booking. What the <em>traveller</em> paid then leaves the agency's wallet again and goes out
/// through gateway clearing, which is the account it arrived in. The agency ends the day where it
/// started for that line: no margin, and no cost.
/// </para>
/// <para>
/// <b>The gateway is asked inside the caller's transaction</b>, which is normally something to
/// avoid. It is safe here, and safe only because refunding is idempotent at the gateway: a
/// transaction rolled back after the gateway accepted is retried, and the second request comes back
/// as already refunded rather than sending a second payment. A gateway that cannot be reached at
/// all throws, and nothing is recorded — an unknown outcome is never written down as a refund.
/// </para>
/// <para>
/// <b>One refund per line</b>, by the unique index on <c>refunds.order_line_id</c>. A reversal
/// message delivered twice, or a reversal racing an agent's own refund, moves nothing more.
/// </para>
/// </remarks>
public sealed partial class OrderRefunds
{
    /// <summary>Where this service's alerts say they came from.</summary>
    public const string AlertSource = nameof(OrderRefunds);

    private readonly IAppDbContext _db;
    private readonly WalletRefunds _wallet;
    private readonly IPaymentGateway _gateway;
    private readonly LedgerAccounts _accounts;
    private readonly IPlatformScope _platformScope;
    private readonly IPlatformAlerter _alerter;
    private readonly ILogger<OrderRefunds> _logger;

    public OrderRefunds(
        IAppDbContext db,
        WalletRefunds wallet,
        IPaymentGateway gateway,
        LedgerAccounts accounts,
        IPlatformScope platformScope,
        IPlatformAlerter alerter,
        ILogger<OrderRefunds> logger)
    {
        _db = db;
        _wallet = wallet;
        _gateway = gateway;
        _accounts = accounts;
        _platformScope = platformScope;
        _alerter = alerter;
        _logger = logger;
    }

    /// <summary>
    /// Returns a line's money to wherever it was paid from, and records the one refund for it.
    /// </summary>
    /// <param name="order">The booking. Its <c>PaidFrom</c> decides where the money goes.</param>
    /// <param name="line">The line being refunded.</param>
    /// <param name="reason">Why: a supplier reversal, or an agent's decision.</param>
    /// <param name="now">When.</param>
    /// <param name="supplierStatusPollId">The poll that justifies a supplier reversal. Required for one.</param>
    /// <param name="refundedByUserId">The agent who chose it. Required for a resolution refund.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <remarks>Call it inside an open <c>IPlatformScope</c>, and keep it open until the save.</remarks>
    /// <returns>The refund, or null when there was no money to give back.</returns>
    public async Task<Refund?> ReturnAsync(
        Order order,
        OrderLine line,
        RefundReason reason,
        DateTimeOffset now,
        Guid? supplierStatusPollId = null,
        Guid? refundedByUserId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(order);
        ArgumentNullException.ThrowIfNull(line);

        if (order.PaidFrom != OrderPaymentMethod.Card)
        {
            // An agent's booking, paid from the wallet. Unchanged from issue 43.
            return await _wallet.ReturnAsync(
                order, line, reason, now, supplierStatusPollId, refundedByUserId, cancellationToken);
        }

        var payment = await CreditedPaymentAsync(order, cancellationToken);

        if (payment is null)
        {
            // Marked as paid by card with no credited payment behind it. Nothing is invented: the
            // wallet path gives back what it can, and a person is told the rest is unaccounted for.
            await AlertAsync(
                AlertSeverity.P1,
                $"Booking {order.OrderNumber} is marked paid by card with no credited payment",
                $"Order line {line.Id} is being refunded, and order {order.OrderNumber} says it was paid by card — "
                + "but no payment of ours for it has been posted to the ledger. Whatever the wallet was holding has "
                + "gone back; nothing has been sent to a card. Check the gateway's dashboard before doing anything "
                + "by hand.",
                order.AgencyId,
                cancellationToken);

            return await _wallet.ReturnAsync(
                order, line, reason, now, supplierStatusPollId, refundedByUserId, cancellationToken);
        }

        return await ToCardAsync(
            order, line, payment, reason, now, supplierStatusPollId, refundedByUserId, cancellationToken);
    }

    private async Task<Refund?> ToCardAsync(
        Order order,
        OrderLine line,
        PaymentTransaction payment,
        RefundReason reason,
        DateTimeOffset now,
        Guid? supplierStatusPollId,
        Guid? refundedByUserId,
        CancellationToken cancellationToken)
    {
        // Never more than what is left of what the traveller actually paid: a departure bought on a
        // deposit has an order total well above its payment, and refunding the total would send back
        // money nobody gave us.
        var refundable = payment.AmountMinor - await AlreadyRefundedAsync(order, cancellationToken);
        var amount = line.GrossAmountMinor < refundable ? line.GrossAmountMinor : refundable;

        // Whatever the line cost the agency comes back first, whether or not the card refund works:
        // the agency should not be left paying a supplier's net rate for a booking being unwound.
        // Nothing below commits on its own, so a refusal rolls this back with everything else.
        var undone = await _wallet.UndoCostAsync(order, line, now, cancellationToken);

        if (amount.AmountMinor <= 0)
        {
            LogNothingToSend(_logger, order.OrderNumber);

            return undone is null
                ? null
                : Record(order, line, reason, RefundMethod.WalletHoldReleased, undone, now,
                    supplierStatusPollId, refundedByUserId,
                    "Nothing was sent to the card: this line's payments had already been refunded in full.");
        }

        var wallet = await _db.Wallets.SingleOrDefaultAsync(
            candidate => candidate.AgencyId == order.AgencyId && candidate.Currency == order.Currency,
            cancellationToken);

        if (wallet is null || wallet.AvailableMinor < amount)
        {
            // The agency has spent the traveller's money. Refusing is the honest answer: sending a
            // refund the platform has not got back from the agency would be the platform paying it.
            await AlertAsync(
                AlertSeverity.P1,
                $"Booking {order.OrderNumber} cannot be refunded to the traveller's card",
                $"Order line {line.Id} on {order.OrderNumber} should be refunded {order.Currency} "
                + $"{amount.AmountMinor} (in kobo) to the card it was paid from, and the agency's wallet does not "
                + "have it. Nothing was sent. The line is in the agency's resolution queue; recover the funds from "
                + "the agency, then refund from the gateway's dashboard and record it.",
                order.AgencyId,
                cancellationToken);

            // Nothing is closed as refunded when nothing was refunded. The line stays in the queue,
            // the agent sees why, and a retry once the funds are recovered still works. The alert
            // repeats on each retry, which is the right noise for a traveller who is owed money.
            throw new CheckoutRefusedException(
                CheckoutRefusal.Conflict,
                "This booking cannot be refunded yet.",
                $"It was paid by card, and {order.Currency} {amount.AmountMinor} (in kobo) has to go back to that "
                + "card — more than this agency's wallet holds. Nothing was sent. Top the wallet up, then refund it "
                + "again; we have told the team.");
        }

        var sent = await _gateway.RefundAsync(
            payment.Reference, amount, $"Booking {order.OrderNumber} — refund", cancellationToken);

        if (!sent.Accepted)
        {
            await AlertAsync(
                AlertSeverity.P1,
                $"The gateway refused to refund booking {order.OrderNumber}",
                $"Order line {line.Id} on {order.OrderNumber} should be refunded {order.Currency} "
                + $"{amount.AmountMinor} (in kobo) to the card it was paid from, and the gateway answered "
                + $"'{sent.Status}': {sent.FailureReason ?? "(no reason given)"}. Nothing was sent, and the "
                + "traveller is still owed it.",
                order.AgencyId,
                cancellationToken);

            throw new CheckoutRefusedException(
                CheckoutRefusal.Conflict,
                "The card refund was refused.",
                $"The payment gateway would not send {order.Currency} {amount.AmountMinor} (in kobo) back to the "
                + $"card this booking was paid with: {sent.FailureReason ?? sent.Status}. Nothing was sent, and the "
                + "booking is still waiting. We have told the team, who will refund it by hand.");
        }

        // The money leaves the agency's wallet and goes back out through the account it arrived in.
        // Row-level security lets only a platform scope write the clearing account.
        using var scope = _platformScope.Enter(
            "customer refund — sends a traveller's payment back out through the platform's gateway clearing account");

        var walletAccount = await _accounts.AgencyWalletAsync(order.AgencyId, order.Currency, cancellationToken);
        var clearing = await _accounts.PlatformAsync(LedgerAccountType.GatewayClearing, order.Currency, cancellationToken);

        var transaction = LedgerTransaction
            .Begin(now, nameof(Refund), line.Id)
            .Debit(walletAccount, amount, $"Booking {order.OrderNumber} — refunded to the traveller")
            .Credit(clearing, amount, $"Booking {order.OrderNumber} — refund sent to the gateway");

        _db.LedgerEntries.AddRange(transaction.Build());

        var before = wallet.BalanceMinor;
        wallet.Debit(amount);

        _db.WalletTransactions.Add(WalletTransaction.Record(
            wallet,
            WalletTransactionType.Refund,
            new Money(-amount.AmountMinor),
            before,
            $"Refund to the traveller — booking {order.OrderNumber}",
            transaction.TransactionGroupId,
            now));

        var refund = Refund.Record(
            order.AgencyId,
            order.Id,
            line.Id,
            reason,
            RefundMethod.Gateway,
            amount,
            order.Currency,
            now,
            line.SupplierBookingId,
            supplierStatusPollId,
            transaction.TransactionGroupId,
            refundedByUserId,
            sent.Outcome == GatewayRefundOutcome.AlreadyRefunded
                ? "The gateway had already refunded this payment."
                : $"Sent back to the card through the gateway (refund {sent.GatewayRefundReference ?? "pending"}).");

        _db.Refunds.Add(refund);

        LogRefunded(_logger, order.OrderNumber, amount.AmountMinor, sent.GatewayRefundReference);

        return refund;
    }

    /// <summary>Records a refund that gave the agency's cost back but sent nothing to a card.</summary>
    private Refund Record(
        Order order,
        OrderLine line,
        RefundReason reason,
        RefundMethod method,
        UndoneCost undone,
        DateTimeOffset now,
        Guid? supplierStatusPollId,
        Guid? refundedByUserId,
        string note)
    {
        var refund = Refund.Record(
            order.AgencyId,
            order.Id,
            line.Id,
            reason,
            undone.WasCaptured ? RefundMethod.WalletCredited : method,
            undone.Amount,
            order.Currency,
            now,
            line.SupplierBookingId,
            supplierStatusPollId,
            undone.LedgerTransactionGroupId,
            refundedByUserId,
            note);

        _db.Refunds.Add(refund);

        return refund;
    }

    /// <summary>The traveller's payment for this order that actually reached the ledger.</summary>
    private Task<PaymentTransaction?> CreditedPaymentAsync(Order order, CancellationToken cancellationToken) =>
        _db.PaymentTransactions
            .Where(payment => payment.OrderId == order.Id && payment.LedgerTransactionGroupId != null)
            .OrderByDescending(payment => payment.Id)
            .FirstOrDefaultAsync(cancellationToken);

    /// <summary>What has already gone back to the card for this order, across every line.</summary>
    private async Task<Money> AlreadyRefundedAsync(Order order, CancellationToken cancellationToken)
    {
        var sent = await _db.Refunds
            .Where(refund => refund.OrderId == order.Id && refund.Method == RefundMethod.Gateway)
            .Select(refund => refund.AmountMinor)
            .ToListAsync(cancellationToken);

        return new Money(sent.Sum(amount => amount.AmountMinor));
    }

    /// <summary>Tells a person. Never throws: the refund itself has already been decided.</summary>
    private async Task AlertAsync(
        AlertSeverity severity,
        string title,
        string body,
        Guid agencyId,
        CancellationToken cancellationToken)
    {
        try
        {
            await _alerter.RaiseAsync(new PlatformAlert(severity, title, body, AlertSource, agencyId), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogAlertFailed(_logger, ex, title);
        }
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Booking {Reference}: {AmountMinor} kobo sent back to the card (gateway refund {GatewayRefundReference}).")]
    private static partial void LogRefunded(ILogger logger, string reference, long amountMinor, string? gatewayRefundReference);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Booking {Reference}: nothing left to send back to the card; its payments were already refunded.")]
    private static partial void LogNothingToSend(ILogger logger, string reference);

    [LoggerMessage(Level = LogLevel.Critical, Message = "Could not raise the alert '{Title}'.")]
    private static partial void LogAlertFailed(ILogger logger, Exception exception, string title);
}
