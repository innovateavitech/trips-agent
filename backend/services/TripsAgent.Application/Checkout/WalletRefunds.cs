using Microsoft.EntityFrameworkCore;
using TripsAgent.Application.Payments;
using TripsAgent.Application.Persistence;
using TripsAgent.Application.Tenancy;
using TripsAgent.Domain.Common;
using TripsAgent.Domain.Orders;
using TripsAgent.Domain.Payments;

namespace TripsAgent.Application.Checkout;

/// <summary>
/// Gives an order line's money back to the agency's wallet — whichever state it is in.
/// </summary>
/// <remarks>
/// <para>
/// <b>Held</b> — never taken: the hold is released. Nothing had moved, so there is nothing for the ledger.
/// </para>
/// <para>
/// <b>Captured</b> — taken when the ticket was issued: the capture is reversed with balanced ledger
/// entries (the supplier payable and platform fee debited back, the wallet credited), and a statement
/// line says so.
/// </para>
/// <para>
/// Adds a <see cref="Refund"/> row either way, and saves nothing: the caller owns the unit of work, and
/// the unique index on the line is what makes a second refund impossible.
/// </para>
/// <para>
/// <b>Call it inside an open <c>IPlatformScope</c>, and keep it open until the save.</b> Reversing a capture
/// writes to the platform's own ledger accounts, which row-level security lets only a platform scope write.
/// </para>
/// </remarks>
public sealed class WalletRefunds
{
    private readonly IAppDbContext _db;
    private readonly IPlatformScope _platformScope;
    private readonly LedgerAccounts _accounts;

    public WalletRefunds(IAppDbContext db, IPlatformScope platformScope, LedgerAccounts accounts)
    {
        _db = db;
        _platformScope = platformScope;
        _accounts = accounts;
    }

    /// <summary>Returns the line's money, or null when no wallet money was ever held or taken for it.</summary>
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

        var hold = await _db.WalletHolds
            .Where(candidate => candidate.OrderId == order.Id)
            .OrderByDescending(candidate => candidate.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        if (hold is null || hold.Status == WalletHoldStatus.Released)
        {
            return null;
        }

        var wallet = await _db.Wallets.SingleAsync(candidate => candidate.Id == hold.WalletId, cancellationToken);
        var amount = hold.AmountMinor;

        Refund refund;

        if (hold.Status == WalletHoldStatus.Held)
        {
            wallet.ReleaseHold(hold, now);

            refund = Refund.Record(
                order.AgencyId, order.Id, line.Id, reason, RefundMethod.WalletHoldReleased, amount, order.Currency, now,
                line.SupplierBookingId, supplierStatusPollId, refundedByUserId: refundedByUserId,
                note: "Released the wallet hold: the money was never taken.");
        }
        else
        {
            var groupId = await ReverseCaptureAsync(order, line, wallet, amount, now, cancellationToken);

            refund = Refund.Record(
                order.AgencyId, order.Id, line.Id, reason, RefundMethod.WalletCredited, amount, order.Currency, now,
                line.SupplierBookingId, supplierStatusPollId, groupId, refundedByUserId,
                "Credited back to the wallet, with reversing ledger entries.");
        }

        _db.Refunds.Add(refund);
        return refund;
    }

    /// <summary>The capture, run backwards — every entry of it, so the books balance to the kobo.</summary>
    private async Task<Guid> ReverseCaptureAsync(
        Order order,
        OrderLine line,
        Wallet wallet,
        Money amount,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        using var scope = _platformScope.Enter("checkout refund — reverses a captured booking out of the platform's supplier-payable and revenue accounts");

        var walletAccount = await _accounts.AgencyWalletAsync(order.AgencyId, order.Currency, cancellationToken);
        var payable = await _accounts.PlatformAsync(LedgerAccountType.SupplierPayable, order.Currency, cancellationToken);
        var revenue = await _accounts.PlatformAsync(LedgerAccountType.PlatformRevenue, order.Currency, cancellationToken);

        var fee = amount - line.NetAmountMinor;

        var transaction = LedgerTransaction
            .Begin(now, nameof(Refund), line.Id)
            .Debit(payable, line.NetAmountMinor, $"Booking {order.OrderNumber} — refund, no longer owed to the supplier")
            .Credit(walletAccount, amount, $"Booking {order.OrderNumber} — refunded to the wallet");

        if (fee.AmountMinor > 0)
        {
            transaction.Debit(revenue, fee, $"Booking {order.OrderNumber} — platform fee returned");
        }

        _db.LedgerEntries.AddRange(transaction.Build());

        var before = wallet.BalanceMinor;
        wallet.Credit(amount);

        _db.WalletTransactions.Add(WalletTransaction.Record(
            wallet, WalletTransactionType.Refund, amount, before, $"Refund — booking {order.OrderNumber}", transaction.TransactionGroupId, now));

        return transaction.TransactionGroupId;
    }
}
