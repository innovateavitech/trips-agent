namespace TripsAgent.Domain.Catalog;

/// <summary>How many seats stand where, on one departure.</summary>
/// <param name="CapacityTotal">Every seat there is.</param>
/// <param name="CapacityReserved">Held during checkout, not yet paid.</param>
/// <param name="CapacityConfirmed">Paid for.</param>
public readonly record struct SeatCount(int CapacityTotal, int CapacityReserved, int CapacityConfirmed)
{
    /// <summary>Reserved plus confirmed: what somebody else cannot have.</summary>
    public int Taken => CapacityReserved + CapacityConfirmed;

    /// <summary>What is still on sale. Never below zero.</summary>
    public int SeatsLeft => Math.Max(0, CapacityTotal - Taken);
}

/// <summary>
/// The status a departure's seats give it (plan §3, job 9).
/// </summary>
/// <remarks>
/// <para>
/// Open → Guaranteed at min pax → Nearly full at 85% → Sold out, in that order of precedence: a
/// departure that is both guaranteed and full is <b>sold out</b>, because that is the one an agent
/// has to act on. Closed and Cancelled are the agent's own and are never overwritten.
/// </para>
/// <para>
/// Integer arithmetic on purpose: <c>taken * 100 &gt;= total * 85</c> rather than
/// <c>taken &gt;= total * 0.85</c>, so 17 of 20 is nearly full on every machine, in every culture,
/// for ever. Floating point has no business deciding whether something is for sale.
/// </para>
/// </remarks>
public static class DepartureStatusRules
{
    /// <summary>The share of the seats that makes a departure nearly full, as a percentage.</summary>
    public const int NearlyFullPercent = 85;

    /// <summary>Whether <paramref name="status"/> is the agent's own decision rather than the seats'.</summary>
    public static bool IsManual(DepartureStatus status) =>
        status is DepartureStatus.Closed or DepartureStatus.Cancelled;

    /// <summary>
    /// The status <paramref name="seats"/> give a departure, ignoring anything the agent has decided.
    /// </summary>
    /// <param name="isGroupDeparture">
    /// False means it runs whatever happens, so it is guaranteed from the moment it opens. True
    /// means it waits for <paramref name="minPax"/> travellers to pay.
    /// </param>
    public static DepartureStatus FromSeats(SeatCount seats, bool isGroupDeparture, int minPax)
    {
        if (seats.CapacityTotal > 0 && seats.Taken >= seats.CapacityTotal)
        {
            return DepartureStatus.SoldOut;
        }

        if (seats.CapacityTotal > 0 && seats.Taken * 100 >= seats.CapacityTotal * NearlyFullPercent)
        {
            return DepartureStatus.NearlyFull;
        }

        return !isGroupDeparture || seats.CapacityConfirmed >= minPax
            ? DepartureStatus.Guaranteed
            : DepartureStatus.Open;
    }
}
