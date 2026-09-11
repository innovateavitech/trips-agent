using TripsAgent.Domain.Auditing;
using TripsAgent.Domain.Common;

namespace TripsAgent.Domain.Payments;

public enum WalletStatus
{
    Active = 1,

    /// <summary>Suspended by Trips. Reads fine; nothing moves in or out.</summary>
    Frozen = 2,
}

/// <summary>Where a hold is in its life.</summary>
public enum WalletHoldStatus
{
    /// <summary>Funds reserved, not yet spent.</summary>
    Held = 1,

    /// <summary>Spent — the booking went through.</summary>
    Captured = 2,

    /// <summary>Given back — the booking failed, or the hold expired.</summary>
    Released = 3,
}

/// <summary>What moved, from the agent's point of view.</summary>
public enum WalletTransactionType
{
    TopUp = 1,
    BookingPayment = 2,
    Refund = 3,
    Reversal = 4,
    Adjustment = 5,
}

/// <summary>
/// An agency's prepaid balance.
/// </summary>
/// <remarks>
/// <para>
/// <b>The balance here is a projection, not the truth.</b> The ledger is authoritative; this is a
/// cached total so the console does not sum thousands of entries to render a header. A nightly
/// job asserts the two agree, and a divergence is a P1 — see the delivery plan §2.9.
/// </para>
/// <para>
/// Every mutation goes through this class so the invariants hold in one place: the balance never
/// goes negative, reserved never exceeds the balance, and available is always balance minus
/// reserved rather than a third number that can drift from the other two.
/// </para>
/// </remarks>
public sealed class Wallet : Entity, IAuditableEntity, ITenantScoped, IAuditLogged
{
    private Wallet() => Currency = string.Empty;

    public static Wallet OpenFor(Guid agencyId, string currency)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(agencyId, Guid.Empty);

        var normalised = (currency ?? string.Empty).Trim().ToUpperInvariant();

        if (normalised.Length != 3)
        {
            throw new ArgumentException($"'{currency}' is not an ISO 4217 currency code.", nameof(currency));
        }

        return new Wallet
        {
            AgencyId = agencyId,
            Currency = normalised,
            Status = WalletStatus.Active,
        };
    }

    public Guid AgencyId { get; private set; }

    /// <summary>One wallet per agency per currency.</summary>
    public string Currency { get; private set; }

    /// <summary>Everything in the wallet, including funds reserved by holds.</summary>
    public Money BalanceMinor { get; private set; }

    /// <summary>Reserved by outstanding holds. Part of the balance, but not spendable.</summary>
    public Money ReservedMinor { get; private set; }

    public WalletStatus Status { get; private set; }

    /// <summary>
    /// Bumped on every change, and compared on write.
    /// </summary>
    /// <remarks>
    /// Optimistic concurrency rather than a lock: two debits that read the same balance and both
    /// write would lose one of them, and the loser is somebody's money. The second write fails
    /// and the caller retries against the new balance.
    /// </remarks>
    public int Version { get; private set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>What can actually be spent: the balance less anything reserved.</summary>
    /// <remarks>
    /// Derived, never stored as its own column. A third number is a third thing to keep in step,
    /// and the one that silently disagrees is the one nobody notices.
    /// </remarks>
    public Money AvailableMinor => BalanceMinor - ReservedMinor;

    /// <summary>Adds funds — a top-up, a refund, a correction.</summary>
    public void Credit(Money amount)
    {
        RequirePositive(amount);
        RequireActive();

        BalanceMinor += amount;
        Version++;
    }

    /// <summary>
    /// Removes funds. Refuses to overdraw.
    /// </summary>
    /// <remarks>
    /// Checked against <see cref="AvailableMinor"/>, not the balance: money reserved by a hold is
    /// already spoken for, and spending it twice is how a wallet ends up funding two bookings
    /// with one amount.
    /// </remarks>
    public void Debit(Money amount)
    {
        RequirePositive(amount);
        RequireActive();

        if (amount > AvailableMinor)
        {
            throw new InvalidOperationException(
                $"Cannot debit {amount} — only {AvailableMinor} is available "
                + $"(balance {BalanceMinor}, reserved {ReservedMinor}).");
        }

        BalanceMinor -= amount;
        Version++;
    }

    /// <summary>Reserves funds so a concurrent booking cannot spend them.</summary>
    public WalletHold PlaceHold(Money amount, DateTimeOffset now, TimeSpan ttl, Guid? orderId = null)
    {
        RequirePositive(amount);
        RequireActive();

        if (amount > AvailableMinor)
        {
            throw new InvalidOperationException(
                $"Cannot hold {amount} — only {AvailableMinor} is available.");
        }

        ReservedMinor += amount;
        Version++;

        return WalletHold.Create(Id, AgencyId, amount, now.Add(ttl), orderId);
    }

    /// <summary>Spends a hold: the reservation becomes an actual debit.</summary>
    public void CaptureHold(WalletHold hold, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(hold);
        RequireOwnHold(hold);

        if (hold.Status != WalletHoldStatus.Held)
        {
            throw new InvalidOperationException($"That hold is already {hold.Status}.");
        }

        // Both move together: the money leaves and stops being reserved in one step, so there is
        // no moment where it is counted twice or not at all.
        ReservedMinor -= hold.AmountMinor;
        BalanceMinor -= hold.AmountMinor;
        Version++;

        hold.Capture(now);
    }

    /// <summary>Gives a hold back — the booking failed, or it expired.</summary>
    public void ReleaseHold(WalletHold hold, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(hold);
        RequireOwnHold(hold);

        if (hold.Status != WalletHoldStatus.Held)
        {
            return;   // releasing twice is harmless and happens when a timeout races a failure
        }

        ReservedMinor -= hold.AmountMinor;
        Version++;

        hold.Release(now);
    }

    /// <summary>Stops anything moving in or out.</summary>
    public void Freeze() => Status = WalletStatus.Frozen;

    public void Unfreeze() => Status = WalletStatus.Active;

    /// <summary>
    /// Corrects the cached balance to what the ledger says.
    /// </summary>
    /// <remarks>
    /// For the nightly integrity job only. A divergence is a P1 and this is the repair, not a
    /// routine operation — which is why it is named for what it is rather than being a setter.
    /// </remarks>
    public void ReconcileTo(Money ledgerBalance)
    {
        BalanceMinor = ledgerBalance;
        Version++;
    }

    private void RequireActive()
    {
        if (Status != WalletStatus.Active)
        {
            throw new InvalidOperationException($"This wallet is {Status}; nothing can move in or out.");
        }
    }

    private void RequireOwnHold(WalletHold hold)
    {
        if (hold.WalletId != Id)
        {
            throw new InvalidOperationException("That hold belongs to a different wallet.");
        }
    }

    private static void RequirePositive(Money amount)
    {
        if (amount.AmountMinor <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(amount), amount.AmountMinor, "A wallet movement must be a positive amount.");
        }
    }
}

/// <summary>Funds reserved against a wallet while a booking is in flight.</summary>
/// <remarks>
/// Holds expire. A booking that dies without releasing its hold would otherwise strand the money
/// forever, and the agent would have to ring support to spend their own balance.
/// </remarks>
public sealed class WalletHold : Entity, IAuditableEntity, ITenantScoped
{
    private WalletHold()
    {
    }

    internal static WalletHold Create(Guid walletId, Guid agencyId, Money amount, DateTimeOffset expiresAt, Guid? orderId) =>
        new()
        {
            WalletId = walletId,
            AgencyId = agencyId,
            AmountMinor = amount,
            Status = WalletHoldStatus.Held,
            ExpiresAt = expiresAt,
            OrderId = orderId,
        };

    public Guid WalletId { get; private set; }

    public Guid AgencyId { get; private set; }

    /// <summary>The order this reserves funds for. Null for a hold placed before one exists.</summary>
    public Guid? OrderId { get; private set; }

    public Money AmountMinor { get; private set; }

    public WalletHoldStatus Status { get; private set; }

    public DateTimeOffset ExpiresAt { get; private set; }

    public DateTimeOffset? SettledAt { get; private set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>True when this hold is still reserving funds at <paramref name="now"/>.</summary>
    public bool IsActive(DateTimeOffset now) => Status == WalletHoldStatus.Held && ExpiresAt > now;

    /// <summary>True when it is still held but past its deadline — the sweeper's business.</summary>
    public bool HasExpired(DateTimeOffset now) => Status == WalletHoldStatus.Held && ExpiresAt <= now;

    internal void Capture(DateTimeOffset at)
    {
        Status = WalletHoldStatus.Captured;
        SettledAt = at;
    }

    internal void Release(DateTimeOffset at)
    {
        Status = WalletHoldStatus.Released;
        SettledAt = at;
    }
}

/// <summary>
/// One line of the agent's statement.
/// </summary>
/// <remarks>
/// A projection for people to read, written alongside the ledger entries that are the truth. It
/// carries the running balance before and after, because a statement that makes you add up the
/// column yourself to check a figure is not a statement.
/// </remarks>
public sealed class WalletTransaction : Entity, IAuditableEntity, ITenantScoped
{
    private WalletTransaction() => Description = string.Empty;

    public static WalletTransaction Record(
        Wallet wallet,
        WalletTransactionType type,
        Money amount,
        Money balanceBefore,
        string description,
        Guid transactionGroupId,
        DateTimeOffset occurredAt)
    {
        ArgumentNullException.ThrowIfNull(wallet);

        return new WalletTransaction
        {
            WalletId = wallet.Id,
            AgencyId = wallet.AgencyId,
            Type = type,
            AmountMinor = amount,
            BalanceBeforeMinor = balanceBefore,
            BalanceAfterMinor = wallet.BalanceMinor,
            Description = description,
            TransactionGroupId = transactionGroupId,
            OccurredAt = occurredAt,
        };
    }

    public Guid WalletId { get; private set; }

    public Guid AgencyId { get; private set; }

    public WalletTransactionType Type { get; private set; }

    /// <summary>Signed: positive for money in, negative for money out, as a statement reads.</summary>
    public Money AmountMinor { get; private set; }

    public Money BalanceBeforeMinor { get; private set; }

    public Money BalanceAfterMinor { get; private set; }

    public string Description { get; private set; }

    /// <summary>Links the statement line to the ledger entries behind it.</summary>
    public Guid TransactionGroupId { get; private set; }

    public DateTimeOffset OccurredAt { get; private set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
