using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TripsAgent.Application.Messaging;
using TripsAgent.Application.Payments;
using TripsAgent.Application.Persistence;
using TripsAgent.Application.Tenancy;
using TripsAgent.Domain.Common;
using TripsAgent.Domain.Orders;
using TripsAgent.Domain.Payments;
using TripsAgent.Domain.Suppliers;

namespace TripsAgent.Application.Checkout;

/// <summary>
/// The checkout's last step (#42): a ticket was issued, so the money held for it is taken and the booking
/// is confirmed. Driven by <see cref="BookingTicketed"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Captured only on Ticketed.</b> The wallet hold placed at payment becomes a real debit here and
/// nowhere else, with balanced ledger entries: the wallet debited, the supplier's net rate owed to the
/// supplier, and the platform fee earned.
/// </para>
/// <para>
/// <b>Once.</b> A redelivered event finds the line already confirmed and the hold already captured, and
/// does nothing. The capture, the line, the order and the <see cref="BookingConfirmed"/> announcement
/// commit together.
/// </para>
/// </remarks>
public sealed partial class CheckoutCompletion
{
    private const int MaxAttempts = 3;

    private readonly IAppDbContext _db;
    private readonly ITransactionRunner _transactions;
    private readonly IPlatformScope _platformScope;
    private readonly LedgerAccounts _accounts;
    private readonly IOutbox _outbox;
    private readonly TimeProvider _clock;
    private readonly ILogger<CheckoutCompletion> _logger;

    public CheckoutCompletion(
        IAppDbContext db,
        ITransactionRunner transactions,
        IPlatformScope platformScope,
        LedgerAccounts accounts,
        IOutbox outbox,
        TimeProvider clock,
        ILogger<CheckoutCompletion> logger)
    {
        _db = db;
        _transactions = transactions;
        _platformScope = platformScope;
        _accounts = accounts;
        _outbox = outbox;
        _clock = clock;
        _logger = logger;
    }

    /// <returns>True when this call confirmed the booking; false when there was nothing left to do.</returns>
    public async Task<bool> CompleteAsync(BookingTicketed ticketed, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ticketed);

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await _transactions.RunAsync(token => CompleteOnceAsync(ticketed, token), cancellationToken);
            }
            catch (DbUpdateConcurrencyException) when (attempt < MaxAttempts)
            {
                // The wallet moved at the same moment. Read it again.
                _db.ChangeTracker.Clear();
            }
        }
    }

    private async Task<bool> CompleteOnceAsync(BookingTicketed ticketed, CancellationToken cancellationToken)
    {
        var line = await _db.OrderLines.SingleOrDefaultAsync(candidate => candidate.Id == ticketed.OrderLineId, cancellationToken);

        if (line is null || line.FulfilmentStatus == FulfilmentStatus.Confirmed)
        {
            return false;
        }

        var order = await _db.Orders.Include(candidate => candidate.Lines).SingleAsync(candidate => candidate.Id == line.OrderId, cancellationToken);
        var now = _clock.GetUtcNow();

        var hold = await _db.WalletHolds.SingleOrDefaultAsync(
            candidate => candidate.OrderId == order.Id && candidate.Status == WalletHoldStatus.Held,
            cancellationToken);

        if (hold is not null)
        {
            await CaptureAsync(order, line, hold, now, cancellationToken);
        }

        line.RecordFulfilment(FulfilmentStatus.Confirmed, now);

        if (order.Lines.All(candidate => candidate.FulfilmentStatus == FulfilmentStatus.Confirmed))
        {
            order.ChangeStatus(
                OrderStatus.Confirmed,
                now,
                ticketed.Pnr is null ? "Ticketed." : $"Ticketed — PNR {ticketed.Pnr}.");
        }

        _outbox.Enqueue(
            new BookingConfirmed(order.AgencyId, order.Id, order.OrderNumber, line.Id, ticketed.SupplierBookingId, ticketed.Pnr, now),
            order.AgencyId);

        await _db.SaveChangesAsync(cancellationToken);

        LogConfirmed(_logger, order.OrderNumber, ticketed.Pnr);
        return true;
    }

    /// <summary>The hold becomes a payment: money leaves the wallet only now, when there is a ticket for it.</summary>
    private async Task CaptureAsync(Order order, OrderLine line, WalletHold hold, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var wallet = await _db.Wallets.SingleAsync(candidate => candidate.Id == hold.WalletId, cancellationToken);
        var before = wallet.BalanceMinor;

        wallet.CaptureHold(hold, now);

        using var scope = _platformScope.Enter("checkout — posts a ticketed booking to the platform's supplier-payable and revenue accounts");

        var walletAccount = await _accounts.AgencyWalletAsync(order.AgencyId, order.Currency, cancellationToken);
        var payable = await _accounts.PlatformAsync(LedgerAccountType.SupplierPayable, order.Currency, cancellationToken);
        var revenue = await _accounts.PlatformAsync(LedgerAccountType.PlatformRevenue, order.Currency, cancellationToken);

        // The hold was the net rate plus the platform fee; the fee is what is left once the supplier's share is owed.
        var fee = hold.AmountMinor - line.NetAmountMinor;

        var transaction = LedgerTransaction
            .Begin(now, nameof(OrderLine), line.Id)
            .Debit(walletAccount, hold.AmountMinor, $"Booking {order.OrderNumber} — ticketed")
            .Credit(payable, line.NetAmountMinor, $"Booking {order.OrderNumber} — owed to the supplier");

        if (fee.AmountMinor > 0)
        {
            transaction.Credit(revenue, fee, $"Booking {order.OrderNumber} — platform fee");
        }

        _db.LedgerEntries.AddRange(transaction.Build());

        _db.WalletTransactions.Add(WalletTransaction.Record(
            wallet,
            WalletTransactionType.BookingPayment,
            new Money(-hold.AmountMinor.AmountMinor),
            before,
            $"Booking {order.OrderNumber}",
            transaction.TransactionGroupId,
            now));
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Booking {Reference} confirmed: ticketed with PNR {Pnr}, and the wallet hold captured.")]
    private static partial void LogConfirmed(ILogger logger, string reference, string? pnr);
}
