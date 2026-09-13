using System.Globalization;
using Microsoft.EntityFrameworkCore;
using TripsAgent.Application.Persistence;
using TripsAgent.Application.Tenancy;
using TripsAgent.Domain.Common;
using TripsAgent.Domain.Payments;

namespace TripsAgent.Application.Payments;

/// <summary>What happened when an agent asked to withdraw.</summary>
public abstract record RequestPayoutOutcome
{
    private RequestPayoutOutcome()
    {
    }

    /// <summary>Recorded, and waiting on a Finance approval.</summary>
    public sealed record Requested(Guid PayoutId, string Reference, long AmountMinor) : RequestPayoutOutcome;

    /// <summary>The amount is outside what may be withdrawn. Carries the explanation to show.</summary>
    public sealed record NotAllowed(string Reason) : RequestPayoutOutcome;

    /// <summary>The destination is missing, unverified, or still cooling off.</summary>
    public sealed record DestinationNotReady(string Reason) : RequestPayoutOutcome;

    /// <summary>Somebody else changed the wallet at the same moment. Ask again.</summary>
    public sealed record Busy : RequestPayoutOutcome;
}

/// <summary>
/// Asking for money, and authorising it. Not sending it — that is <see cref="PayoutTransferService"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>The ledger moves here, and the bank moves later.</b> A request debits the wallet in the
/// same save that records the payout, and parks the money in the platform's payout-payable
/// account. Three things follow from that, and all three are the reason for it:
/// </para>
/// <list type="bullet">
///   <item>
///     Two requests cannot spend the same naira. The wallet's version token means the second save
///     matches no row, so of two simultaneous requests for the whole balance exactly one survives.
///   </item>
///   <item>
///     The balance an agent sees is immediately true. A pending withdrawal that has not left the
///     balance is a number that invites them to spend it twice.
///   </item>
///   <item>
///     A refusal has something to reverse. Rejecting, failing or reversing a payout each write
///     their own balanced entries putting the money back — never an edit to the entries that took
///     it out.
///   </item>
/// </list>
/// <para>
/// Approval is a separate step by a separate person, holding a permission no agency has. That is
/// the only thing standing between one compromised login and a bank transfer, so it is not
/// something the requester can do for themselves even if they are the agency's owner.
/// </para>
/// </remarks>
public sealed class PayoutService
{
    private readonly IAppDbContext _db;
    private readonly PayoutBalances _balances;
    private readonly LedgerAccounts _accounts;
    private readonly IPlatformScope _platformScope;
    private readonly ITenantContext _tenant;
    private readonly TimeProvider _clock;

    public PayoutService(
        IAppDbContext db,
        PayoutBalances balances,
        LedgerAccounts accounts,
        IPlatformScope platformScope,
        ITenantContext tenant,
        TimeProvider clock)
    {
        _db = db;
        _balances = balances;
        _accounts = accounts;
        _platformScope = platformScope;
        _tenant = tenant;
        _clock = clock;
    }

    /// <summary>What the agent may withdraw right now, and why it is not their balance.</summary>
    public async Task<WithdrawableBalance?> WithdrawableAsync(CancellationToken cancellationToken = default)
    {
        var wallet = await _db.Wallets.AsNoTracking().FirstOrDefaultAsync(cancellationToken);

        return wallet is null ? null : await _balances.ForAsync(wallet, cancellationToken);
    }

    /// <summary>Records a withdrawal request and takes the money out of the wallet.</summary>
    public async Task<RequestPayoutOutcome> RequestAsync(
        Money amount,
        Guid? bankAccountId,
        CancellationToken cancellationToken = default)
    {
        if (_tenant.AgencyId is not { } agencyId || _tenant.UserId is not { } userId)
        {
            return new RequestPayoutOutcome.NotAllowed("Only a signed-in agency user can withdraw.");
        }

        if (amount.AmountMinor <= 0)
        {
            return new RequestPayoutOutcome.NotAllowed("Say how much to withdraw.");
        }

        var agency = await _db.Agencies.AsNoTracking().FirstOrDefaultAsync(a => a.Id == agencyId, cancellationToken);

        if (agency is null || !agency.CanTransact)
        {
            return new RequestPayoutOutcome.NotAllowed(
                "This agency cannot withdraw until its business verification is approved.");
        }

        var wallet = await _db.Wallets.FirstOrDefaultAsync(cancellationToken);

        if (wallet is null)
        {
            return new RequestPayoutOutcome.NotAllowed("This agency has no wallet yet.");
        }

        if (wallet.Status != WalletStatus.Active)
        {
            return new RequestPayoutOutcome.NotAllowed(
                $"This wallet is {wallet.Status.ToString().ToLowerInvariant()}. Nothing can move in or out of it. "
                + "Contact support.");
        }

        if (!PayoutLimits.PayableCurrencies.Contains(wallet.Currency))
        {
            return new RequestPayoutOutcome.NotAllowed(
                $"There is no payout rail for {wallet.Currency} yet.");
        }

        var account = await DestinationAsync(bankAccountId, wallet.Currency, cancellationToken);

        if (account is null)
        {
            return new RequestPayoutOutcome.DestinationNotReady(
                "Add a bank account and let us verify it with your bank before withdrawing.");
        }

        var now = _clock.GetUtcNow();
        var usableFrom = (account.VerifiedAt ?? account.CreatedAt) + PayoutLimits.NewAccountCoolingOff;

        if (now < usableFrom)
        {
            // The cooling-off period. Someone who has taken over a login and changed the
            // destination has to wait a day, during which the owner has been emailed about it.
            return new RequestPayoutOutcome.DestinationNotReady(
                $"This account was added recently and can receive its first withdrawal from "
                + $"{usableFrom.ToString("d MMMM yyyy, HH:mm 'UTC'", CultureInfo.InvariantCulture)}. "
                + "The wait gives you time to spot a change you did not make.");
        }

        if (amount < PayoutLimits.Minimum)
        {
            return new RequestPayoutOutcome.NotAllowed(
                $"The smallest withdrawal is {Naira(PayoutLimits.Minimum)}.");
        }

        var withdrawable = await _balances.ForAsync(wallet, cancellationToken);

        if (amount.AmountMinor > withdrawable.WithdrawableMinor)
        {
            return new RequestPayoutOutcome.NotAllowed(Explain(withdrawable, amount));
        }

        var timezone = ZoneOf(agency.Timezone);
        var today = await _balances.RequestedTodayAsync(agencyId, timezone, cancellationToken);

        if (today + amount > PayoutLimits.DailyCap)
        {
            return new RequestPayoutOutcome.NotAllowed(
                $"That would take today's withdrawals past the {Naira(PayoutLimits.DailyCap)} daily limit — "
                + $"{Naira(today)} has already been requested today. Try again tomorrow, or contact support.");
        }

        // The random tail of the id, not its head: a v7 id starts with the millisecond, and two
        // requests in the same millisecond would otherwise share a reference.
        var reference = $"PO-{now.UtcDateTime:yyyyMMdd}-{Guid.NewGuid().ToString("N")[^12..].ToUpperInvariant()}";
        var groupId = await MoveOutOfWalletAsync(wallet, amount, reference, now, cancellationToken);

        var payout = Payout.Request(
            agencyId, account.Id, amount, wallet.Currency, reference, userId, now, groupId);

        _db.Payouts.Add(payout);

        try
        {
            // One save: the ledger entries, the wallet's new balance, the statement line and the
            // payout row commit together or not at all.
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Another write moved this wallet between our read and our save — a booking capturing
            // a hold, or a second payout request. The concurrency token refused ours, which is
            // exactly the protection it is there for.
            _db.ChangeTracker.Clear();
            return new RequestPayoutOutcome.Busy();
        }

        return new RequestPayoutOutcome.Requested(payout.Id, reference, amount.AmountMinor);
    }

    /// <summary>
    /// Authorises a payout. Platform Finance only, and never the person who asked.
    /// </summary>
    /// <remarks>
    /// Nothing is sent here. Approving marks the payout ready and the sender picks it up, so the
    /// authorisation and the money-moving call are separate units of work with a commit between
    /// them — which is what lets the sender be a dumb executor of instructions somebody else
    /// already checked.
    /// </remarks>
    /// <returns>False when there is no such payout, or it is no longer waiting.</returns>
    public async Task<bool> ApproveAsync(Guid payoutId, Guid approvedByUserId, CancellationToken cancellationToken = default)
    {
        using var scope = _platformScope.Enter("payout approval — Finance authorises withdrawals for every agency");

        var payout = await _db.Payouts.FirstOrDefaultAsync(p => p.Id == payoutId, cancellationToken);

        if (payout is null || payout.Status != PayoutStatus.Requested)
        {
            return false;
        }

        payout.Approve(approvedByUserId, _clock.GetUtcNow());
        await _db.SaveChangesAsync(cancellationToken);

        return true;
    }

    /// <summary>Refuses a payout and puts the money back in the wallet.</summary>
    /// <returns>False when there is no such payout, or it is no longer waiting.</returns>
    public async Task<bool> RejectAsync(
        Guid payoutId,
        Guid decidedByUserId,
        string reason,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        using var scope = _platformScope.Enter(
            "payout rejection — Finance refuses a withdrawal and returns it to the agency's wallet");

        var payout = await _db.Payouts.FirstOrDefaultAsync(p => p.Id == payoutId, cancellationToken);

        if (payout is null || payout.Status != PayoutStatus.Requested)
        {
            return false;
        }

        var now = _clock.GetUtcNow();
        var wallet = await _db.Wallets.FirstAsync(w => w.AgencyId == payout.AgencyId, cancellationToken);

        var groupId = await ReturnToWalletAsync(
            payout, wallet, WalletTransactionType.PayoutReturned, $"Withdrawal declined — {payout.Reference}", now,
            cancellationToken);

        payout.Reject(decidedByUserId, reason, now, groupId);
        await _db.SaveChangesAsync(cancellationToken);

        return true;
    }

    /// <summary>
    /// Takes the money out of the wallet and parks it in the platform's payout-payable account.
    /// </summary>
    /// <remarks>
    /// Debit the wallet (a liability, so a debit reduces what we owe the agency) and credit
    /// payout-payable (also a liability, so a credit increases what we owe their bank). The total
    /// we owe them has not changed — only which promise it is.
    /// </remarks>
    private async Task<Guid> MoveOutOfWalletAsync(
        Wallet wallet,
        Money amount,
        string reference,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        using var scope = _platformScope.Enter(
            "payout request — parks the money in the platform's payout-payable account");

        var walletAccount = await _accounts.AgencyWalletAsync(wallet.AgencyId, wallet.Currency, cancellationToken);
        var payable = await _accounts.PlatformAsync(LedgerAccountType.PayoutPayable, wallet.Currency, cancellationToken);

        var description = $"Withdrawal requested — {reference}";

        var transaction = LedgerTransaction
            .Begin(now, nameof(Payout), null)
            .Debit(walletAccount, amount, description)
            .Credit(payable, amount, description);

        _db.LedgerEntries.AddRange(transaction.Build());

        var balanceBefore = wallet.BalanceMinor;
        wallet.Debit(amount);

        _db.WalletTransactions.Add(WalletTransaction.Record(
            wallet, WalletTransactionType.Payout, -amount, balanceBefore, description,
            transaction.TransactionGroupId, now));

        return transaction.TransactionGroupId;
    }

    /// <summary>
    /// Puts a payout's money back: the request's entries, run backwards.
    /// </summary>
    /// <remarks>
    /// A new balanced group, never an edit. The books should read "it went out, then it came
    /// back", because that is what happened and an agent looking at their statement deserves to
    /// see both halves.
    /// </remarks>
    internal async Task<Guid> ReturnToWalletAsync(
        Payout payout,
        Wallet wallet,
        WalletTransactionType statementType,
        string description,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        using var scope = _platformScope.Enter(
            "payout return — reverses a withdrawal out of the platform's payout-payable account");

        var walletAccount = await _accounts.AgencyWalletAsync(payout.AgencyId, payout.Currency, cancellationToken);
        var payable = await _accounts.PlatformAsync(LedgerAccountType.PayoutPayable, payout.Currency, cancellationToken);

        var transaction = LedgerTransaction
            .Begin(now, nameof(Payout), payout.Id)
            .Debit(payable, payout.AmountMinor, description)
            .Credit(walletAccount, payout.AmountMinor, description);

        _db.LedgerEntries.AddRange(transaction.Build());

        var balanceBefore = wallet.BalanceMinor;
        wallet.Credit(payout.AmountMinor);

        _db.WalletTransactions.Add(WalletTransaction.Record(
            wallet, statementType, payout.AmountMinor, balanceBefore, description,
            transaction.TransactionGroupId, now));

        return transaction.TransactionGroupId;
    }

    /// <summary>The named account, or the agency's default when none was named.</summary>
    private async Task<AgencyBankAccount?> DestinationAsync(
        Guid? bankAccountId,
        string currency,
        CancellationToken cancellationToken)
    {
        var accounts = _db.AgencyBankAccounts.Where(account => account.Currency == currency);

        var chosen = bankAccountId is { } id
            ? await accounts.FirstOrDefaultAsync(account => account.Id == id, cancellationToken)
            : await accounts.FirstOrDefaultAsync(account => account.IsDefault, cancellationToken);

        return chosen?.CanReceiveMoney == true ? chosen : null;
    }

    /// <summary>
    /// Says which of the three subtractions is the one blocking this request.
    /// </summary>
    /// <remarks>
    /// "Insufficient funds" against a balance the agent can see is £5,000 is the support ticket
    /// this method exists to prevent. The three reasons are genuinely different and the agent can
    /// act on two of them.
    /// </remarks>
    private static string Explain(WithdrawableBalance balance, Money amount)
    {
        if (balance.ReservedMinor > 0 && amount.AmountMinor <= balance.BalanceMinor)
        {
            return $"{Naira(new Money(balance.WithdrawableMinor))} of your {Naira(new Money(balance.BalanceMinor))} "
                   + $"is available: {Naira(new Money(balance.ReservedMinor))} is held against bookings in progress"
                   + (balance.PendingSettlementMinor > 0
                       ? $", and {Naira(new Money(balance.PendingSettlementMinor))} was paid in too recently to have "
                         + "cleared."
                       : ".");
        }

        if (balance.PendingSettlementMinor > 0 && amount.AmountMinor <= balance.AvailableMinor)
        {
            return $"{Naira(new Money(balance.PendingSettlementMinor))} was paid in within the last "
                   + $"{PayoutLimits.SettlementWindow.TotalDays:0} days and has not cleared into our account yet. "
                   + $"You can withdraw {Naira(new Money(balance.WithdrawableMinor))} now, and the rest once it "
                   + "clears.";
        }

        return $"You can withdraw {Naira(new Money(balance.WithdrawableMinor))} right now.";
    }

    /// <summary>The agency's own zone, falling back to Lagos rather than to UTC.</summary>
    /// <remarks>
    /// A daily cap that resets at 01:00 local time is a cap that surprises somebody. Falling back
    /// to UTC would do exactly that for every Nigerian agency, which is all of them.
    /// </remarks>
    private static TimeZoneInfo ZoneOf(string timezone)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timezone);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Africa/Lagos");
        }
    }

    /// <summary>An amount as an agent reads it. Money formats itself; this only adds the sign.</summary>
    /// <remarks>
    /// No arithmetic. Dividing by 100 to make a naira figure is how a decimal gets near money, and
    /// <see cref="Money.ToString()"/> already knows where the point goes.
    /// </remarks>
    private static string Naira(Money amount) => "₦" + amount;
}
