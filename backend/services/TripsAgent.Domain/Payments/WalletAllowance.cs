using TripsAgent.Domain.Auditing;
using TripsAgent.Domain.Common;

namespace TripsAgent.Domain.Payments;

/// <summary>How often a sub-agent's allowance starts again.</summary>
public enum AllowancePeriod
{
    /// <summary>One pot, never refilled. Raising the limit is the only way to give more.</summary>
    Lifetime = 1,

    Daily = 2,
    Weekly = 3,
    Monthly = 4,
}

/// <summary>Whether an allowance can be drawn on at all.</summary>
public enum AllowanceStatus
{
    Active = 1,

    /// <summary>Frozen by the principal. Reads fine; nothing may be reserved against it.</summary>
    Frozen = 2,
}

/// <summary>
/// A hard cap on what one sub-agent may spend against its principal's money, per period.
/// </summary>
/// <remarks>
/// <para>
/// Build-plan decision 8. It is a <b>cap</b>, not a second wallet and not credit: there is no
/// balance here to top up and nothing to withdraw. Every booking a sub-agent makes reserves
/// against <see cref="SpentMinor"/> first, and the booking is refused the moment the reservation
/// would take it past <see cref="LimitMinor"/>.
/// </para>
/// <para>
/// <b>The arithmetic here is not what makes it race-free.</b> Two bookings in flight at once would
/// both read the same <see cref="SpentMinor"/> and both pass this check. What actually stops them
/// is a single conditional <c>UPDATE</c> in PostgreSQL, which re-evaluates its own <c>WHERE</c>
/// against the committed row — see <c>SubAgentAllowanceService</c>. This class holds the same rule
/// in one readable place so it can be unit-tested, and so the two can be compared.
/// </para>
/// <para>
/// <see cref="AgencyId"/> is the principal that owns the money, and <see cref="SubAgencyId"/> the
/// agency spending it. Owning the row by the principal keeps every write inside the plain tenant
/// rule; the sub-agent only ever reads its own row.
/// </para>
/// </remarks>
public sealed class WalletAllowance : Entity, IAuditableEntity, ITenantScoped, IAuditLogged
{
    private WalletAllowance() => Currency = string.Empty;

    /// <summary>Opens an allowance for <paramref name="subAgencyId"/> against its principal.</summary>
    public static WalletAllowance Open(
        Guid agencyId,
        Guid subAgencyId,
        string currency,
        Money limitMinor,
        AllowancePeriod period,
        DateTimeOffset now)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(agencyId, Guid.Empty);
        ArgumentOutOfRangeException.ThrowIfEqual(subAgencyId, Guid.Empty);

        if (agencyId == subAgencyId)
        {
            throw new InvalidOperationException(
                "An agency does not hold an allowance against itself — that is what its wallet is.");
        }

        var normalised = (currency ?? string.Empty).Trim().ToUpperInvariant();

        if (normalised.Length != 3)
        {
            throw new ArgumentException($"'{currency}' is not an ISO 4217 currency code.", nameof(currency));
        }

        RequireNotNegative(limitMinor);

        return new WalletAllowance
        {
            AgencyId = agencyId,
            SubAgencyId = subAgencyId,
            Currency = normalised,
            LimitMinor = limitMinor,
            SpentMinor = Money.Zero,
            Period = period,
            Status = AllowanceStatus.Active,
            ResetsAt = NextResetAfter(now, period),
        };
    }

    /// <summary>The principal whose money this caps. Owns the row.</summary>
    public Guid AgencyId { get; private set; }

    /// <summary>The sub-agent spending it.</summary>
    public Guid SubAgencyId { get; private set; }

    /// <summary>ISO 4217. One allowance per sub-agent per currency.</summary>
    public string Currency { get; private set; }

    /// <summary>The cap for this period. Zero means the sub-agent may not spend at all.</summary>
    public Money LimitMinor { get; private set; }

    /// <summary>Reserved so far this period. Never negative, never above the limit.</summary>
    public Money SpentMinor { get; private set; }

    public AllowancePeriod Period { get; private set; }

    public AllowanceStatus Status { get; private set; }

    /// <summary>When <see cref="SpentMinor"/> next goes back to zero. Null for a lifetime allowance.</summary>
    public DateTimeOffset? ResetsAt { get; private set; }

    /// <summary>Bumped on every change, and compared on write — the same rule as the wallet's.</summary>
    public int Version { get; private set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>What is left to spend this period. Never negative, even after the limit is lowered.</summary>
    public Money RemainingMinor =>
        LimitMinor > SpentMinor ? LimitMinor - SpentMinor : Money.Zero;

    /// <summary>
    /// Reserves <paramref name="amount"/>, or refuses. See the class remarks: the database, not
    /// this method, is what makes concurrent reservations safe.
    /// </summary>
    public bool TryReserve(Money amount)
    {
        RequirePositive(amount);

        if (Status != AllowanceStatus.Active || SpentMinor + amount > LimitMinor)
        {
            return false;
        }

        SpentMinor += amount;
        Version++;
        return true;
    }

    /// <summary>
    /// Gives a reservation back — the booking failed, or its money was refunded.
    /// </summary>
    /// <remarks>
    /// Clamped at zero rather than throwing. Releasing more than was reserved means something
    /// released twice, and the safe answer to that is an allowance at zero spent, not an
    /// exception on a path that is already cleaning up after a failure.
    /// </remarks>
    public void Release(Money amount)
    {
        RequirePositive(amount);

        SpentMinor = SpentMinor > amount ? SpentMinor - amount : Money.Zero;
        Version++;
    }

    /// <summary>
    /// Changes the cap.
    /// </summary>
    /// <remarks>
    /// Lowering it below what is already spent is allowed and does not claw anything back: the
    /// money is spent. It simply means nothing more can be reserved until the period turns over.
    /// </remarks>
    public void ChangeLimit(Money limitMinor)
    {
        RequireNotNegative(limitMinor);

        LimitMinor = limitMinor;
        Version++;
    }

    /// <summary>Changes how often the allowance starts again, and when that next happens.</summary>
    public void ChangePeriod(AllowancePeriod period, DateTimeOffset now)
    {
        Period = period;
        ResetsAt = NextResetAfter(now, period);
        Version++;
    }

    /// <summary>Stops anything being reserved against it.</summary>
    public void Freeze()
    {
        Status = AllowanceStatus.Frozen;
        Version++;
    }

    /// <summary>Lets it be drawn on again.</summary>
    public void Unfreeze()
    {
        Status = AllowanceStatus.Active;
        Version++;
    }

    /// <summary>
    /// Starts the period again if it is due. Idempotent: running it twice in one period resets once.
    /// </summary>
    /// <returns>True if this call reset it.</returns>
    public bool ResetIfDue(DateTimeOffset now)
    {
        if (Period == AllowancePeriod.Lifetime || ResetsAt is not { } due || now < due)
        {
            return false;
        }

        SpentMinor = Money.Zero;

        // Advanced from now rather than from the old boundary, so a job that did not run for a
        // week resets once and lands on the next real boundary instead of catching up in a loop.
        ResetsAt = NextResetAfter(now, Period);
        Version++;

        return true;
    }

    /// <summary>The next boundary after <paramref name="now"/>, in UTC. Null for a lifetime allowance.</summary>
    /// <remarks>
    /// UTC throughout, and deliberately not the agency's own timezone: the reset job is one query
    /// over every allowance, and a per-agency boundary would make it a query per timezone. The
    /// cost is that a Lagos month turns over at 01:00 local, which no agent will notice.
    /// </remarks>
    public static DateTimeOffset? NextResetAfter(DateTimeOffset now, AllowancePeriod period)
    {
        var utc = now.ToUniversalTime();

        return period switch
        {
            AllowancePeriod.Lifetime => null,
            AllowancePeriod.Daily => new DateTimeOffset(utc.Date, TimeSpan.Zero).AddDays(1),
            AllowancePeriod.Weekly => new DateTimeOffset(utc.Date, TimeSpan.Zero)
                .AddDays(7 - (int)utc.DayOfWeek),
            AllowancePeriod.Monthly => new DateTimeOffset(
                new DateTime(utc.Year, utc.Month, 1, 0, 0, 0, DateTimeKind.Utc), TimeSpan.Zero).AddMonths(1),
            _ => null,
        };
    }

    private static void RequirePositive(Money amount)
    {
        if (amount <= Money.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(amount), amount, "An allowance moves by a positive amount or not at all.");
        }
    }

    private static void RequireNotNegative(Money limitMinor)
    {
        if (limitMinor < Money.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(limitMinor), limitMinor, "An allowance limit cannot be negative. Zero means 'may not spend'.");
        }
    }
}
