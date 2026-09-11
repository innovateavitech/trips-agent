using Microsoft.EntityFrameworkCore;
using TripsAgent.Application.Persistence;
using TripsAgent.Domain.Payments;

namespace TripsAgent.Application.Payments;

/// <summary>
/// Turns a confirmed payment into money in a wallet: balanced ledger entries, the wallet
/// projection, and a statement line — all in one save, at most once.
/// </summary>
/// <remarks>
/// <para>
/// The only place a top-up is credited. Both the webhook and the browser-redirect verification
/// route through here, because they routinely both arrive for the same payment and either could
/// be first.
/// </para>
/// <para>
/// Idempotency rests on <c>PaymentTransaction.LedgerTransactionGroupId</c>, and the in-memory
/// check below is only the fast path. Two callers can each load the payment before either posts
/// it, and both would pass that check. What actually stops the second one is in the database:
/// the column is an EF Core concurrency token, so the second save's UPDATE matches no row and
/// the whole save — ledger entries, wallet, statement line — rolls back with a
/// <see cref="DbUpdateConcurrencyException"/>. A unique index on the ledger entries for a
/// payment backs that up. Callers must expect either exception; see
/// <see cref="VerifyTopUpHandler"/>.
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
    /// <returns>True when this call did the crediting; false when there was nothing to do.</returns>
    /// <exception cref="DbUpdateConcurrencyException">Another caller posted this payment first.</exception>
    /// <exception cref="DbUpdateException">A unique index refused a duplicate posting or account.</exception>
    public async Task<bool> PostAsync(PaymentTransaction payment, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payment);

        if (!payment.AwaitsPosting)
        {
            // Already posted, or not creditable. Either way there is nothing to do.
            return false;
        }

        // What we asked for, not what the gateway says was charged. PaymentTransaction only
        // reaches Succeeded when the charge covers the request in the same currency; when the
        // payer bears the fee the charge includes it, and crediting that would hand the agency
        // money the platform never receives. See PaymentTransaction.MarkSucceeded.
        var amount = payment.AmountMinor;

        var now = _clock.GetUtcNow();

        // KYB approval opens the wallet. Missing here means something bypassed that, and it is
        // thrown rather than papered over: the webhook path retries and then raises an alert.
        var wallet = await _db.Wallets
            .FirstOrDefaultAsync(w => w.AgencyId == payment.AgencyId && w.Currency == payment.Currency, cancellationToken)
            ?? throw new InvalidOperationException(
                $"Agency {payment.AgencyId} has no {payment.Currency} wallet to credit. Wallets are opened when KYB "
                + "is approved.");

        var walletAccount = await AgencyAccountAsync(payment.AgencyId, payment.Currency, cancellationToken);
        var clearingAccount = await PlatformAccountAsync(LedgerAccountType.GatewayClearing, payment.Currency, cancellationToken);

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

        // One SaveChanges, so one database transaction: the ledger entries, the wallet, the
        // statement line and the payment's posted marker commit together, or none of them do.
        await _db.SaveChangesAsync(cancellationToken);

        return true;
    }

    /// <summary>The agency's wallet account, opened the first time it is needed.</summary>
    /// <remarks>
    /// KYB approval opens this too; creating it here covers agencies approved before that. The
    /// unique index on <c>(agency_id, account_type, currency)</c> makes a race harmless: the
    /// loser's save fails and the caller re-reads.
    /// </remarks>
    private async Task<LedgerAccount> AgencyAccountAsync(
        Guid agencyId,
        string currency,
        CancellationToken cancellationToken)
    {
        var existing = await _db.LedgerAccounts.FirstOrDefaultAsync(
            a => a.AgencyId == agencyId && a.AccountType == LedgerAccountType.AgencyWallet && a.Currency == currency,
            cancellationToken);

        if (existing is not null)
        {
            return existing;
        }

        var created = LedgerAccount.ForAgency(
            agencyId, LedgerAccountType.AgencyWallet, currency, $"{LedgerAccountType.AgencyWallet} ({currency})");

        _db.LedgerAccounts.Add(created);

        return created;
    }

    /// <summary>One of the platform's own accounts. Never created here.</summary>
    /// <remarks>
    /// Seeded by the <c>HardenMoneyPath</c> migration. Creating these on first use, as this
    /// method once did, let two first-ever top-ups each create a clearing account, splitting the
    /// platform's balance between them.
    /// </remarks>
    private async Task<LedgerAccount> PlatformAccountAsync(
        LedgerAccountType accountType,
        string currency,
        CancellationToken cancellationToken) =>
        await _db.LedgerAccounts.FirstOrDefaultAsync(
            a => a.AgencyId == null && a.AccountType == accountType && a.Currency == currency,
            cancellationToken)
        ?? throw new InvalidOperationException(
            $"The platform has no {accountType} account in {currency}. Platform accounts are seeded by migration; "
            + "add one for this currency before accepting payments in it.");
}
