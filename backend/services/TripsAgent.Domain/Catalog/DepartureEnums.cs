namespace TripsAgent.Domain.Catalog;

/// <summary>
/// Where a dated departure stands. Stored by name, so reordering this enum can never change what a
/// stored departure is.
/// </summary>
/// <remarks>
/// Only <see cref="Closed"/> and <see cref="Cancelled"/> are the agent's own decision. Everything
/// else is worked out from the seats by <see cref="DepartureStatusRules"/> and rewritten whenever
/// they move — so nobody can put a full departure back on sale by editing a column.
/// </remarks>
public enum DepartureStatus
{
    /// <summary>Selling, and not yet certain to run.</summary>
    Open = 1,

    /// <summary>It will run: enough travellers have paid, or it never needed a minimum.</summary>
    Guaranteed = 2,

    /// <summary>85% or more of the seats are taken.</summary>
    NearlyFull = 3,

    /// <summary>Every seat is taken. New interest goes to the waitlist.</summary>
    SoldOut = 4,

    /// <summary>The agent stopped new bookings. The ones already made stand.</summary>
    Closed = 5,

    /// <summary>The agent called it off. Everyone who paid is refunded (build plan decision 12).</summary>
    Cancelled = 6,
}

/// <summary>What a traveller pays up front to hold a seat.</summary>
public enum DepositType
{
    /// <summary>Nothing up front: the whole price is due on the installment schedule.</summary>
    None = 1,

    /// <summary>A share of the seat price, in basis points.</summary>
    Percent = 2,

    /// <summary>A flat amount in minor units, whatever the seat costs.</summary>
    Fixed = 3,
}

/// <summary>Which date an installment is counted from.</summary>
public enum InstallmentDueBasis
{
    /// <summary><c>dueOffsetDays</c> after the day they booked.</summary>
    FromBooking = 1,

    /// <summary><c>dueOffsetDays</c> before the day it leaves.</summary>
    BeforeDeparture = 2,
}

/// <summary>Where a seat hold taken during checkout stands.</summary>
public enum DepartureHoldStatus
{
    /// <summary>Seats are reserved and counted against capacity until <c>expires_at</c>.</summary>
    Held = 1,

    /// <summary>Given back: the cart timed out, or checkout was abandoned.</summary>
    Released = 2,

    /// <summary>Paid for. The seats moved from reserved to confirmed.</summary>
    Converted = 3,
}

/// <summary>Where someone waiting for a seat stands.</summary>
public enum WaitlistStatus
{
    /// <summary>In the queue, not yet offered anything.</summary>
    Waiting = 1,

    /// <summary>Offered a seat, with a deadline. Job 10 rolls the offer on when it passes.</summary>
    Offered = 2,

    /// <summary>Took the seat.</summary>
    Converted = 3,

    /// <summary>The offer ran out, or they withdrew.</summary>
    Expired = 4,
}
