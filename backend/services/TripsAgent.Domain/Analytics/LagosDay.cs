namespace TripsAgent.Domain.Analytics;

/// <summary>
/// The day an agent means when they say "yesterday".
/// </summary>
/// <remarks>
/// <para>
/// Every instant in the system is stored in UTC — Npgsql refuses anything else, deliberately, and
/// that rule is not being bent here. But a Nigerian travel agent looking at "Monday's sales" means
/// Monday in Lagos, and a booking taken at 00:30 on Monday morning in Lagos is 23:30 on Sunday in
/// UTC. Rolling up by UTC days would put it on the wrong day of the agent's week, and the number
/// they reconcile against their own books would never match.
/// </para>
/// <para>
/// So the aggregates are keyed by a Lagos calendar day, and the conversion happens exactly here,
/// at the boundary between an instant and a day. Nothing else in the codebase converts a time
/// zone, and no global converter is registered.
/// </para>
/// <para>
/// A fixed +01:00 rather than <c>TimeZoneInfo.FindSystemTimeZoneById("Africa/Lagos")</c>: West
/// Africa Time has been UTC+1 without daylight saving since 1919, so the offset is not an
/// approximation, and a fixed offset cannot be broken by a missing or stale tz database on a
/// container image. Revisit this — and only this file — if Nigeria ever adopts DST.
/// </para>
/// </remarks>
public static class LagosDay
{
    /// <summary>West Africa Time. UTC+1 all year, every year.</summary>
    public static readonly TimeSpan Offset = TimeSpan.FromHours(1);

    /// <summary>The Lagos calendar day an instant falls on.</summary>
    public static DateOnly Of(DateTimeOffset instant) =>
        DateOnly.FromDateTime(instant.ToOffset(Offset).DateTime);

    /// <summary>
    /// Midnight at the start of a Lagos day, <b>as a UTC instant</b> — so it can go straight into
    /// a query without Npgsql rejecting a non-UTC offset.
    /// </summary>
    public static DateTimeOffset StartOfUtc(DateOnly day) =>
        new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), Offset).ToUniversalTime();

    /// <summary>
    /// Midnight at the start of the <i>next</i> Lagos day, as a UTC instant. Ranges are
    /// half-open: <c>StartOfUtc(day) &lt;= t &lt; EndOfUtc(day)</c>, so no instant is counted
    /// twice and none is missed.
    /// </summary>
    public static DateTimeOffset EndOfUtc(DateOnly day) => StartOfUtc(day.AddDays(1));

    /// <summary>Every day from <paramref name="from"/> to <paramref name="to"/>, inclusive.</summary>
    public static IEnumerable<DateOnly> Range(DateOnly from, DateOnly to)
    {
        for (var day = from; day <= to; day = day.AddDays(1))
        {
            yield return day;
        }
    }
}
