using Microsoft.EntityFrameworkCore;
using TripsAgent.Application.Persistence;
using TripsAgent.Domain.Common;
using TripsAgent.Domain.Payments;

namespace TripsAgent.Application.Payments;

/// <summary>
/// Turns a confirmed payment into money in a wallet: balanced ledger entries, the wallet
/// projection, and a statement line — all in one transaction, exactly once.
/// </summary>
/// <remarks>
/// <para>
/// The only place a top-up is credited. Both the webhook and the browser-redirect verification
/// route through here, because they routinely both arrive for the same payment and either could
/// be first.
/// </para>
/// <para>
/// Idempotency rests on <c>PaymentTransaction.LedgerTransactionGroupId</c>: a payment that has
/// already posted is left alone. The check and the write are in one database transaction, so two
/// concurrent deliveries cannot both pass it.
/// </para>
/// </remarks>
public sealed class WalletTopUpService
{
    private readonly IAppDbContext _db;
    private readonly TimeProvider _clock;

    public WalletTopUpService(IAppDbContext db, TimeProvider clock)
    {
        _db = db;
        _clock = clock;
    }

    /// <summary>
    /// Credits the wallet for a succeeded payment, if it has not already been credited.
    /// </summary>
    /// <returns>True when this call did the crediting; false when it had already been done.</returns>
    public async Task<bool> PostAsync(PaymentTransaction payment, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payment);

        if (!payment.AwaitsPosting)
        {
            // Already posted, or not confirmed. Either way there is nothing to do, and doing it
            // anyway would credit the wallet twice.
            return false;
        }

        // The amount the gateway confirmed, never the amount we asked for. A payer can be charged
        // something different, and crediting the request is how a wallet gains money nobody paid.
        var amount = payment.VerifiedAmountMinor
            ?? throw new InvalidOperationException(
                $"Payment {payment.Reference} is marked succeeded with no verified amount.");

        var now = _clock.GetUtcNow();

        var wallet = await _db.Wallets
            .FirstOrDefaultAsync(w => w.AgencyId == payment.AgencyId && w.Currency == payment.Currency, cancellationToken)
            ?? throw new InvalidOperationException(
                $"Agency {payment.AgencyId} has no {payment.Currency} wallet to credit.");

        var walletAccount = await AccountAsync(payment.AgencyId, LedgerAccountType.AgencyWallet, payment.Currency, cancellationToken);
        var clearingAccount = await AccountAsync(null, LedgerAccountType.GatewayClearing, payment.Currency, cancellationToken);

        // Money arrived into gateway clearing (an asset, so a debit) and increased what we owe
        // the agency (a liability, so a credit). See LedgerDirection's remarks.
        var transaction = LedgerTransaction
            .Begin(now, nameof(PaymentTransaction), payment.Id)
            .Debit(clearingAccount, amount, $"Top-up received — {payment.Reference}")
            .Credit(walletAccount, amount, $"Wallet top-up — {payment.Reference}");

        _db.LedgerEntries.AddRange(transaction.Build());

        var balanceBefore = wallet.BalanceMinor;
        wallet.Credit(amount);

        _db.WalletTransactions.Add(WalletTransaction.Record(
            wallet,
            WalletTransactionType.TopUp,
            amount,
            balanceBefore,
            $"Wallet top-up — {payment.Reference}",
            transaction.TransactionGroupId,
            now));

        payment.MarkPosted(transaction.TransactionGroupId);

        // One SaveChanges: the ledger entries, the wallet, the statement line and the payment's
        // posted marker commit together, or none of them do.
        await _db.SaveChangesAsync(cancellationToken);

        return true;
    }

    /// <summary>Finds a ledger account, creating it the first time it is needed.</summary>
    private async Task<LedgerAccount> AccountAsync(
        Guid? agencyId,
        LedgerAccountType accountType,
        string currency,
        CancellationToken cancellationToken)
    {
        var existing = await _db.LedgerAccounts.FirstOrDefaultAsync(
            a => a.AgencyId == agencyId && a.AccountType == accountType && a.Currency == currency,
            cancellationToken);

        if (existing is not null)
        {
            return existing;
        }

        var created = agencyId is { } id
            ? LedgerAccount.ForAgency(id, accountType, currency, $"{accountType} ({currency})")
            : LedgerAccount.ForPlatform(accountType, currency, $"{accountType} ({currency})");

        _db.LedgerAccounts.Add(created);

        return created;
    }
}
