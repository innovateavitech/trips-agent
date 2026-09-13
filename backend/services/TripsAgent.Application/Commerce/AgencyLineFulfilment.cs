using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TripsAgent.Application.Catalog;
using TripsAgent.Application.Checkout;
using TripsAgent.Application.Messaging;
using TripsAgent.Application.Payments;
using TripsAgent.Application.Persistence;
using TripsAgent.Application.Tenancy;
using TripsAgent.Domain.Orders;
using TripsAgent.Domain.Payments;

namespace TripsAgent.Application.Commerce;

/// <summary>
/// Confirms a paid line the agency fulfils itself: a tour, a visa, a package, or seats on a dated
/// departure (build plan F5 and F6).
/// </summary>
/// <remarks>
/// <para>
/// <b>There is no supplier, so there is nothing to wait for.</b> A flight waits for the airline to
/// issue a ticket, and <see cref="CheckoutCompletion"/> confirms it when that happens. An agency's
/// own product is confirmed the moment it is paid for: the agency is the supplier.
/// </para>
/// <para>
/// <b>The money moves the same way it does for a flight.</b> The hold placed at payment is captured
/// here with balanced ledger entries — but what the agency owes is the platform's fee alone, since
/// there is no supplier's net rate to owe anybody (decision 4). Capture is <see cref="LedgerAccounts"/>'
/// platform revenue account, exactly as the fee on a flight is.
/// </para>
/// <para>
/// <b>Seats become confirmed ones.</b> The hold taken at checkout is converted through
/// <see cref="DepartureSeats.ConfirmAsync"/>, which is also what moves the departure's status on —
/// Open to Guaranteed to Nearly Full — as it is earned.
/// </para>
/// <para>
/// <b>Once.</b> A line already confirmed moves nothing, and a hold already captured cannot be
/// captured twice. The same <see cref="BookingConfirmed"/> announcement a supplier booking raises is
/// raised here, so the invoice, the voucher, the traveller's email and the CRM's customer record all
/// happen for an agency's own products too.
/// </para>
/// </remarks>
public sealed partial class AgencyLineFulfilment
{
    private readonly IAppDbContext _db;
    private readonly IPlatformScope _platformScope;
    private readonly LedgerAccounts _accounts;
    private readonly DepartureSeats _seats;
    private readonly IOutbox _outbox;
    private readonly ILogger<AgencyLineFulfilment> _logger;

    public AgencyLineFulfilment(
        IAppDbContext db,
        IPlatformScope platformScope,
        LedgerAccounts accounts,
        DepartureSeats seats,
        IOutbox outbox,
        ILogger<AgencyLineFulfilment> logger)
    {
        _db = db;
        _platformScope = platformScope;
        _accounts = accounts;
        _seats = seats;
        _outbox = outbox;
        _logger = logger;
    }

    /// <summary>
    /// Confirms one line, capturing its money and turning its held seats into confirmed ones.
    /// </summary>
    /// <remarks>
    /// Stages its work on the caller's context and saves it, inside whatever transaction the caller
    /// already has open.
    /// </remarks>
    /// <returns>True when this call confirmed the line; false when there was nothing left to do.</returns>
    public async Task<bool> ConfirmAsync(
        Order order,
        OrderLine line,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(order);
        ArgumentNullException.ThrowIfNull(line);

        if (line.FulfilmentStatus is FulfilmentStatus.Confirmed or FulfilmentStatus.FailedNeedsResolution)
        {
            return false;
        }

        // The seats this line bought, if it bought any. Converted before the money is captured: a
        // departure that somehow has no room left is a line for the resolution queue, not a capture.
        var seatHoldId = await _db.DepartureHolds.AsNoTracking()
            .Where(hold => hold.OrderLineId == line.Id)
            .Select(hold => (Guid?)hold.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (seatHoldId is { } holdId)
        {
            await _seats.ConfirmAsync(holdId, cancellationToken);
        }

        var hold = await _db.WalletHolds.SingleOrDefaultAsync(
            candidate => candidate.OrderLineId == line.Id && candidate.Status == WalletHoldStatus.Held,
            cancellationToken);

        // From here to the save the platform's own revenue account is written to, and row-level
        // security lets only a platform scope do that. Everything above was read under the agency's
        // own filter.
        using var scope = _platformScope.Enter(
            "storefront checkout — posts an agency's own product to the platform's revenue account");

        if (hold is not null)
        {
            await CaptureAsync(order, line, hold, now, cancellationToken);
        }

        line.RecordFulfilment(FulfilmentStatus.Confirmed, now);

        if (order.Lines.All(candidate => candidate.FulfilmentStatus == FulfilmentStatus.Confirmed))
        {
            order.ChangeStatus(OrderStatus.Confirmed, now, "Every item on this booking is confirmed.");
        }

        _outbox.Enqueue(
            new BookingConfirmed(
                order.AgencyId, order.Id, order.OrderNumber, line.Id, SupplierBookingId: null, Pnr: null, now),
            order.AgencyId);

        await _db.SaveChangesAsync(cancellationToken);

        LogConfirmed(_logger, order.OrderNumber, line.TitleSnapshot);

        return true;
    }

    /// <summary>
    /// The hold becomes a payment: the platform's fee leaves the wallet, and nothing else does.
    /// </summary>
    /// <remarks>
    /// The whole of what the traveller paid is already in the agency's wallet. What the agency owes
    /// for a product it hosts itself is Trips' fee, taken out of its margin and never added to the
    /// traveller's price (decision 4). There is no supplier payable, because there is no supplier.
    /// </remarks>
    private async Task CaptureAsync(
        Order order,
        OrderLine line,
        WalletHold hold,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var wallet = await _db.Wallets.SingleAsync(candidate => candidate.Id == hold.WalletId, cancellationToken);
        var before = wallet.BalanceMinor;

        wallet.CaptureHold(hold, now);

        var walletAccount = await _accounts.AgencyWalletAsync(order.AgencyId, order.Currency, cancellationToken);
        var revenue = await _accounts.PlatformAsync(LedgerAccountType.PlatformRevenue, order.Currency, cancellationToken);

        var transaction = LedgerTransaction
            .Begin(now, nameof(OrderLine), line.Id)
            .Debit(walletAccount, hold.AmountMinor, $"Booking {order.OrderNumber} — {line.TitleSnapshot}")
            .Credit(revenue, hold.AmountMinor, $"Booking {order.OrderNumber} — platform fee");

        _db.LedgerEntries.AddRange(transaction.Build());

        _db.WalletTransactions.Add(WalletTransaction.Record(
            wallet,
            WalletTransactionType.BookingPayment,
            new Domain.Common.Money(-hold.AmountMinor.AmountMinor),
            before,
            $"Booking {order.OrderNumber} — platform fee",
            transaction.TransactionGroupId,
            now));
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Booking {Reference} — {Title} confirmed: the agency hosts it, so there was nothing to book with a supplier.")]
    private static partial void LogConfirmed(ILogger logger, string reference, string title);
}
