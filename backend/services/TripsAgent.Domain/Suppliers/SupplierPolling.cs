namespace TripsAgent.Domain.Suppliers;

/// <summary>
/// When the status poller asks the supplier about a booking again (#37).
/// </summary>
/// <remarks>
/// <para>
/// Trips Africa has no webhooks, so polling is the only way a booking's outcome is ever learned. The
/// schedule backs off — 30 seconds, then 1, 2, 5, 15 and 30 minutes, then hourly — because most
/// bookings settle within a minute and the few that do not should not cost a supplier call every
/// thirty seconds for a day.
/// </para>
/// <para>
/// <b>It never stops.</b> Near the ticket time limit the next poll is pulled in so the booking is
/// asked about at the limit plus <see cref="TicketTimeLimitBuffer"/>; after that it carries on hourly.
/// A booking still <c>TicketPending</c> is never resolved by a timeout — the ticket may yet be issued,
/// so the money stays held until the supplier says otherwise.
/// </para>
/// </remarks>
public static class SupplierPollSchedule
{
    /// <summary>The gap after each poll: the first entry follows the issue call, the last repeats forever.</summary>
    public static readonly IReadOnlyList<TimeSpan> Backoff =
    [
        TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(2),
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(15),
        TimeSpan.FromMinutes(30),
        TimeSpan.FromHours(1),
    ];

    /// <summary>
    /// How long after the ticket time limit a supplier is given to finish. The poll is scheduled for
    /// then, and a booking still unresolved at that point is put in front of a person.
    /// </summary>
    public static readonly TimeSpan TicketTimeLimitBuffer = TimeSpan.FromMinutes(30);

    /// <summary>
    /// How long after an issue call starts the poller takes the booking over if no answer was recorded.
    /// </summary>
    /// <remarks>
    /// This is how a worker killed mid-call is recovered: the booking was saved as <c>Issuing</c> before
    /// the call left, and nothing is left to record the answer. It has to be longer than the issue call's
    /// own timeout — the Trips Africa options refuse to start otherwise — so the poller never takes over
    /// a call that is still legitimately waiting.
    /// </remarks>
    public static readonly TimeSpan IssueRecoveryDelay = TimeSpan.FromMinutes(2);

    /// <summary>When to poll next, after <paramref name="pollsSoFar"/> polls, at <paramref name="at"/>.</summary>
    public static DateTimeOffset Next(int pollsSoFar, DateTimeOffset at, DateTimeOffset? ticketTimeLimit)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(pollsSoFar);

        var next = at + Backoff[Math.Min(pollsSoFar, Backoff.Count - 1)];

        if (ticketTimeLimit is { } limit)
        {
            // Pulled in so the last word before the deadline is not an hour late.
            var deadline = limit + TicketTimeLimitBuffer;

            if (at < deadline && next > deadline)
            {
                next = deadline;
            }
        }

        return next;
    }

    /// <summary>True once the ticket time limit and its buffer have both passed.</summary>
    public static bool IsPastDeadline(DateTimeOffset? ticketTimeLimit, DateTimeOffset at) =>
        ticketTimeLimit is { } limit && at >= limit + TicketTimeLimitBuffer;
}

/// <summary>
/// What one status poll learned, in our words. The adapter's answer, before the booking acts on it.
/// </summary>
/// <param name="Outcome">Whether the supplier answered at all.</param>
/// <param name="ReportedStatus">The adapter's reading of the supplier's code, when it could make one.</param>
/// <param name="SupplierStatusCode">The supplier's own code, verbatim — Trips Africa's 0, 1, 2, 3, 11 or 100.</param>
/// <param name="HttpStatusCode">The status query's HTTP status, when a response arrived.</param>
/// <param name="Pnr">The booking reference, when the answer carried one.</param>
/// <param name="SupplierApiCallId">The audited call behind the poll, kept on the poll row as evidence.</param>
/// <param name="Message">Anything the supplier said in words, or why no answer came.</param>
public sealed record SupplierStatusObservation(
    SupplierPollOutcome Outcome,
    SupplierBookingStatus? ReportedStatus,
    int? SupplierStatusCode,
    int? HttpStatusCode,
    string? Pnr,
    Guid? SupplierApiCallId,
    string? Message);

/// <summary>Why a poll put a booking in front of a person.</summary>
public enum SupplierPollAlert
{
    /// <summary>The supplier answered with something other than a status we can act on — Trips Africa's 100.</summary>
    SupplierReportedError = 1,

    /// <summary>Still unresolved at the ticket time limit plus its buffer. Polling carries on.</summary>
    UnresolvedPastTimeLimit = 2,
}

/// <summary>A recorded poll, and the alert it calls for, if any.</summary>
/// <param name="Poll">The evidence row. The caller saves it in the same unit of work as the booking.</param>
/// <param name="Alert">Null when nobody needs to be told.</param>
public sealed record SupplierPollRecorded(SupplierStatusPoll Poll, SupplierPollAlert? Alert);

/// <summary>The two warnings an agent gets before a held booking's ticket time limit (#38).</summary>
public enum TicketTimeLimitWarning
{
    FifteenMinutes = 15,
    SixtyMinutes = 60,
}
