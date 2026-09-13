using Microsoft.EntityFrameworkCore;
using TripsAgent.Application.Persistence;
using TripsAgent.Domain.Common;
using TripsAgent.Domain.Payments;

namespace TripsAgent.Application.Payments;

/// <summary>
/// The rules about how much an agency may withdraw, and when.
/// </summary>
/// <remarks>
/// Constants rather than configuration for the MVP. Each one is a number somebody will want to
/// change, and the moment they do it belongs in the agency's settings — but a per-agency payout
/// limit nobody has asked for is a table, a screen and a migration in exchange for nothing.
/// </remarks>
public static class PayoutLimits
{
    /// <summary>
    /// The smallest withdrawal.
    /// </summary>
    /// <remarks>
    /// A floor because every transfer costs the platform a fee, and because a queue full of ₦200
    /// withdrawals is an approval step nobody reads properly.
    /// </remarks>
    public static readonly Money Minimum = Money.FromMajor(5_000);

    /// <summary>
    /// The most one agency may ask for in a day, across every request.
    /// </summary>
    /// <remarks>
    /// Not a limit on how much money an agency may have. It is a blast radius: if a login is
    /// taken over and the attacker changes the bank account, this is the most that can leave
    /// before somebody notices — and it buys the approval step a day to work in.
    /// </remarks>
    public static readonly Money DailyCap = Money.FromMajor(5_000_000);

    /// <summary>
    /// How long money must have been in the wallet before it can be withdrawn.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Card money is credited to a wallet the moment the gateway confirms the charge, which is
    /// hours or days before the gateway actually settles it into the platform's bank. Paying it
    /// out before then means paying it out of somebody else's money.
    /// </para>
    /// <para>
    /// Paystack settles Nigerian card payments on T+1. Two days rather than one because T+1 means
    /// the next <i>working</i> day, and a Friday evening payment settles on Monday.
    /// </para>
    /// </remarks>
    public static readonly TimeSpan SettlementWindow = TimeSpan.FromDays(2);

    /// <summary>
    /// How long a newly added bank account must wait before it can receive money.
    /// </summary>
    /// <remarks>
    /// The control against a quiet account takeover: someone who gets into an agent's login and
    /// changes the payout destination has to wait a day, during which the agency's owner has been
    /// emailed about the change. Without it, a stolen password is a same-day cash-out.
    /// </remarks>
    public static readonly TimeSpan NewAccountCoolingOff = TimeSpan.FromHours(24);

    /// <summary>The currencies there is a payout rail for. NGN only, over NUBAN.</summary>
    /// <remarks>
    /// An agency may hold a wallet in any currency (open question 17 keeps the MVP to one), and a
    /// balance in a currency with no way out is worse than a refusal that says so.
    /// </remarks>
    public static readonly IReadOnlySet<string> PayableCurrencies = new HashSet<string>(StringComparer.Ordinal) { "NGN" };
}

/// <summary>
/// What an agency could withdraw right now, and why it is not simply their balance.
/// </summary>
/// <param name="BalanceMinor">Everything in the wallet.</param>
/// <param name="ReservedMinor">Held against bookings in flight. Spoken for, not spent.</param>
/// <param name="AvailableMinor">Balance less reserved — what may be <i>spent</i>.</param>
/// <param name="PendingSettlementMinor">
/// Card money credited but not yet settled to the platform's own bank.
/// </param>
/// <param name="WithdrawableMinor">
/// What may actually leave: available, less what has not settled. Never below zero.
/// </param>
/// <param name="Currency">ISO 4217.</param>
public sealed record WithdrawableBalance(
    long BalanceMinor,
    long ReservedMinor,
    long AvailableMinor,
    long PendingSettlementMinor,
    long WithdrawableMinor,
    string Currency)
{
    /// <summary>True when there is enough to make the smallest allowed request.</summary>
    public bool MeetsMinimum => WithdrawableMinor >= PayoutLimits.Minimum.AmountMinor;
}

/// <summary>
/// Works out how much of a wallet an agency may actually take out.
/// </summary>
/// <remarks>
/// <para>
/// Three numbers get subtracted from the balance, and each of them is somebody else's money until
/// it is not:
/// </para>
/// <list type="number">
///   <item>
///     <b>Reserved.</b> A booking in flight has a wallet hold against it. Pay that out and the
///     checkout fails at issue time, inside the ticket time limit, on a booking a traveller has
///     already paid for — and the money that should have covered it is in a bank account we
///     cannot reach into. This is the single most important subtraction here.
///   </item>
///   <item>
///     <b>Unsettled.</b> A card payment credits the wallet when the gateway confirms the charge,
///     which is a day or more before the gateway actually pays it to us. Paying it out early is
///     paying it out of the float.
///   </item>
///   <item>
///     <b>Payouts already in flight.</b> These need no subtraction at all, and that is deliberate:
///     requesting a payout debits the wallet immediately, so the balance already reflects it.
///     Subtracting them here as well would count the same naira twice.
///   </item>
/// </list>
/// </remarks>
public sealed class PayoutBalances
{
    private readonly IAppDbContext _db;
    private readonly TimeProvider _clock;

    public PayoutBalances(IAppDbContext db, TimeProvider clock)
    {
        _db = db;
        _clock = clock;
    }

    /// <summary>
    /// Statement lines that represent money arriving from the gateway rather than moving inside
    /// the platform. Only these wait for settlement.
    /// </summary>
    private static readonly WalletTransactionType[] GatewayCredits =
    [
        WalletTransactionType.TopUp,
        WalletTransactionType.CustomerPayment,
    ];

    /// <summary>Works out what the wallet in front of us may pay out.</summary>
    /// <remarks>
    /// Takes the wallet rather than looking it up, so a caller that has already loaded and locked
    /// one does not read a second copy and reason about a balance that is not the one it is about
    /// to write.
    /// </remarks>
    public async Task<WithdrawableBalance> ForAsync(Wallet wallet, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(wallet);

        var settledBefore = _clock.GetUtcNow() - PayoutLimits.SettlementWindow;

        // Summed from the statement rather than from payment_transactions, because the statement
        // is per wallet and already says which credits came from outside. Summed in memory: Money
        // is a value converter, which EF cannot aggregate, and a two-day window is a handful of rows.
        var recentCredits = await _db.WalletTransactions
            .AsNoTracking()
            .Where(line => line.WalletId == wallet.Id
                           && line.OccurredAt > settledBefore
                           && GatewayCredits.Contains(line.Type))
            .Select(line => line.AmountMinor)
            .ToListAsync(cancellationToken);

        var pendingSettlement = recentCredits.Sum(amount => amount.AmountMinor);

        var available = wallet.AvailableMinor.AmountMinor;

        // Clamped: an agency that topped up ₦100k this morning and spent ₦90k of it on tickets has
        // less available than is unsettled, and the answer to "how much can I withdraw" is none,
        // not a negative number nobody can act on.
        var withdrawable = Math.Max(0, available - pendingSettlement);

        return new WithdrawableBalance(
            wallet.BalanceMinor.AmountMinor,
            wallet.ReservedMinor.AmountMinor,
            available,
            pendingSettlement,
            withdrawable,
            wallet.Currency);
    }

    /// <summary>
    /// What an agency has already asked for today, in its own day rather than in UTC.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Counts every request made today whatever became of it — including the ones that were
    /// rejected. A cap that resets when a request is refused is a cap an attacker clears by making
    /// requests until one is refused.
    /// </para>
    /// <param name="agencyId">Whose day to count.</param>
    /// <param name="timezone">The agency's own zone. A cap that resets at 01:00 local is a surprise.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// </remarks>
    public async Task<Money> RequestedTodayAsync(
        Guid agencyId,
        TimeZoneInfo timezone,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(timezone);

        var now = _clock.GetUtcNow();

        // Converted at the boundary and turned straight back into a UTC instant. Npgsql refuses a
        // non-UTC DateTimeOffset, so the local time exists only long enough to find midnight.
        var localNow = TimeZoneInfo.ConvertTime(now, timezone);
        var localMidnight = new DateTimeOffset(localNow.Date, localNow.Offset);
        var since = localMidnight.ToUniversalTime();

        var amounts = await _db.Payouts
            .AsNoTracking()
            .Where(payout => payout.AgencyId == agencyId && payout.RequestedAt >= since)
            .Select(payout => payout.AmountMinor)
            .ToListAsync(cancellationToken);

        return new Money(amounts.Sum(amount => amount.AmountMinor));
    }
}
