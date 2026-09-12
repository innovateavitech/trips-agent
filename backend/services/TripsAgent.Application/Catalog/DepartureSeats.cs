using Microsoft.EntityFrameworkCore;
using TripsAgent.Application.Persistence;
using TripsAgent.Domain.Catalog;

namespace TripsAgent.Application.Catalog;

/// <summary>What came of asking for seats on a departure.</summary>
public abstract record SeatHoldOutcome
{
    private SeatHoldOutcome()
    {
    }

    /// <summary>The seats are held until <see cref="DepartureHold.ExpiresAt"/>.</summary>
    public sealed record Held(DepartureHold Hold, DepartureStatus Status) : SeatHoldOutcome;

    /// <summary>No departure with that id belongs to this agency.</summary>
    public sealed record NotFound : SeatHoldOutcome;

    /// <summary>Closed, cancelled, or past its cutoff.</summary>
    public sealed record NotSellable(string Reason) : SeatHoldOutcome;

    /// <summary>
    /// Somebody else took them first. <paramref name="SeatsLeft"/> is what was left when this was
    /// refused — the number to offer the waitlist against, not a promise it is still free.
    /// </summary>
    public sealed record NoSeats(int SeatsLeft) : SeatHoldOutcome;
}

/// <summary>
/// Moves seats on a departure: held during checkout, confirmed when paid, given back when a hold
/// lapses (build plan F6, plan §3 jobs 6 and 9).
/// </summary>
/// <remarks>
/// <para>
/// <b>Every move is one UPDATE statement, and the database decides.</b> A seat is taken with
/// <c>SET capacity_reserved = capacity_reserved + n WHERE reserved + confirmed + n &lt;= total</c>.
/// PostgreSQL locks the row for the statement and, under READ COMMITTED, re-evaluates that WHERE
/// against the row as the previous transaction left it — so of two checkouts racing for the last
/// seat exactly one updates a row and the other updates none. Nothing here reads the count and then
/// writes it back, because that is the shape that oversells.
/// </para>
/// <para>
/// Behind that sits <c>ck_departures_no_oversell</c>. The guard above means it should never fire;
/// it is what makes the guarantee true of code nobody has written yet, including a hand-typed
/// <c>UPDATE</c> at a psql prompt.
/// </para>
/// <para>
/// <b>The status follows the seats (job 9).</b> Every move ends by putting the status back in line
/// with the counts, so Open → Guaranteed → NearlyFull → SoldOut happen as they are earned rather
/// than overnight. The nightly sweep exists for the moves that were interrupted, not for the rest.
/// </para>
/// </remarks>
public sealed class DepartureSeats
{
    private readonly IAppDbContext _db;
    private readonly ITransactionRunner _transactions;
    private readonly DepartureWaitlistService _waitlist;
    private readonly TimeProvider _clock;

    public DepartureSeats(
        IAppDbContext db,
        ITransactionRunner transactions,
        DepartureWaitlistService waitlist,
        TimeProvider clock)
    {
        _db = db;
        _transactions = transactions;
        _waitlist = waitlist;
        _clock = clock;
    }

    /// <summary>How long a checkout has to pay for the seats it is holding.</summary>
    public static readonly TimeSpan DefaultHoldTimeToLive = TimeSpan.FromMinutes(20);

    /// <summary>
    /// Holds <paramref name="paxCount"/> seats for a cart. The seats count against capacity from
    /// this moment, and go back when the hold is released or lapses.
    /// </summary>
    public Task<SeatHoldOutcome> HoldAsync(
        Guid departureId,
        Guid cartId,
        int paxCount,
        TimeSpan? timeToLive = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(paxCount, 1);

        return _transactions.RunAsync<SeatHoldOutcome>(
            async token =>
            {
                var now = _clock.GetUtcNow();

                var departure = await _db.Departures.AsNoTracking()
                    .FirstOrDefaultAsync(candidate => candidate.Id == departureId, token);

                if (departure is null)
                {
                    return new SeatHoldOutcome.NotFound();
                }

                if (departure.Status is DepartureStatus.Closed or DepartureStatus.Cancelled)
                {
                    return new SeatHoldOutcome.NotSellable(
                        departure.Status == DepartureStatus.Cancelled
                            ? "This departure has been cancelled."
                            : "This departure is closed to new bookings.");
                }

                if (now >= departure.CutoffAt)
                {
                    return new SeatHoldOutcome.NotSellable("Bookings for this departure have closed.");
                }

                // The one statement that decides. A read before it would only be a guess.
                var taken = await _db.Departures
                    .Where(candidate => candidate.Id == departureId)
                    .Where(candidate =>
                        candidate.CapacityReserved + candidate.CapacityConfirmed + paxCount <= candidate.CapacityTotal)
                    .ExecuteUpdateAsync(
                        setters => setters
                            .SetProperty(candidate => candidate.CapacityReserved, candidate => candidate.CapacityReserved + paxCount)
                            .SetProperty(candidate => candidate.UpdatedAt, now),
                        token);

                if (taken == 0)
                {
                    return new SeatHoldOutcome.NoSeats(await SeatsLeftAsync(departureId, token));
                }

                var hold = DepartureHold.Place(
                    departure.AgencyId, departureId, cartId, paxCount, now, timeToLive ?? DefaultHoldTimeToLive);

                _db.DepartureHolds.Add(hold);
                await _db.SaveChangesAsync(token);

                return new SeatHoldOutcome.Held(hold, await RefreshStatusAsync(departureId, token));
            },
            cancellationToken);
    }

    /// <summary>
    /// Turns a hold's seats into paid ones. Idempotent: a hold that is no longer held moves nothing.
    /// </summary>
    /// <returns>True when this call is the one that converted it.</returns>
    public Task<bool> ConfirmAsync(Guid holdId, CancellationToken cancellationToken = default) =>
        _transactions.RunAsync(
            async token =>
            {
                var now = _clock.GetUtcNow();
                var hold = await _db.DepartureHolds.FirstOrDefaultAsync(candidate => candidate.Id == holdId, token);

                if (hold is null || !hold.Convert(now))
                {
                    return false;
                }

                var moved = hold.PaxCount;

                await _db.Departures
                    .Where(candidate => candidate.Id == hold.DepartureId)
                    .ExecuteUpdateAsync(
                        setters => setters
                            .SetProperty(candidate => candidate.CapacityReserved, candidate => candidate.CapacityReserved - moved)
                            .SetProperty(candidate => candidate.CapacityConfirmed, candidate => candidate.CapacityConfirmed + moved)
                            .SetProperty(candidate => candidate.UpdatedAt, now),
                        token);

                await _db.SaveChangesAsync(token);
                await RefreshStatusAsync(hold.DepartureId, token);

                return true;
            },
            cancellationToken);

    /// <summary>
    /// Gives a hold's seats back. Idempotent, for the same reason as <see cref="ConfirmAsync"/>.
    /// </summary>
    /// <returns>True when this call is the one that released it.</returns>
    public Task<bool> ReleaseAsync(Guid holdId, CancellationToken cancellationToken = default) =>
        _transactions.RunAsync(
            async token =>
            {
                var now = _clock.GetUtcNow();
                var hold = await _db.DepartureHolds.FirstOrDefaultAsync(candidate => candidate.Id == holdId, token);

                if (hold is null || !hold.Release(now))
                {
                    return false;
                }

                var given = hold.PaxCount;

                await _db.Departures
                    .Where(candidate => candidate.Id == hold.DepartureId)
                    .ExecuteUpdateAsync(
                        setters => setters
                            .SetProperty(candidate => candidate.CapacityReserved, candidate => candidate.CapacityReserved - given)
                            .SetProperty(candidate => candidate.UpdatedAt, now),
                        token);

                await _db.SaveChangesAsync(token);
                await RefreshStatusAsync(hold.DepartureId, token);

                // Seats came back, so whoever is waiting for them hears about it now rather than at
                // the next sweep — plan §3 job 9, "on freed capacity offers the next entry".
                await _waitlist.OfferFreedSeatsAsync(hold.DepartureId, cancellationToken: token);
                await _db.SaveChangesAsync(token);

                return true;
            },
            cancellationToken);

    /// <summary>
    /// Puts the departure's status back in line with its seats (job 9). Does nothing while the agent
    /// has closed or cancelled it.
    /// </summary>
    /// <remarks>
    /// Reads the counts straight from the database rather than from anything the caller's change
    /// tracker is holding: the seats were just moved by an <c>ExecuteUpdate</c>, which the tracker
    /// knows nothing about, so a departure loaded earlier in the same unit of work would carry the
    /// counts as they were before the move. The write is a compare-and-set on the status, so a
    /// concurrent move that got there first simply wins.
    /// </remarks>
    /// <returns>The status it now has.</returns>
    public async Task<DepartureStatus> RefreshStatusAsync(Guid departureId, CancellationToken cancellationToken = default)
    {
        var row = await _db.Departures.AsNoTracking()
            .Where(candidate => candidate.Id == departureId)
            .Select(candidate => new
            {
                candidate.Status,
                candidate.IsGroupDeparture,
                candidate.MinPax,
                candidate.CapacityTotal,
                candidate.CapacityReserved,
                candidate.CapacityConfirmed,
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (row is null)
        {
            return DepartureStatus.Cancelled;
        }

        if (DepartureStatusRules.IsManual(row.Status))
        {
            return row.Status;
        }

        var next = DepartureStatusRules.FromSeats(
            new SeatCount(row.CapacityTotal, row.CapacityReserved, row.CapacityConfirmed),
            row.IsGroupDeparture,
            row.MinPax);

        if (next == row.Status)
        {
            return row.Status;
        }

        var now = _clock.GetUtcNow();

        await _db.Departures
            .Where(candidate => candidate.Id == departureId && candidate.Status == row.Status)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(candidate => candidate.Status, next)
                    .SetProperty(candidate => candidate.UpdatedAt, now),
                cancellationToken);

        return next;
    }

    private async Task<int> SeatsLeftAsync(Guid departureId, CancellationToken cancellationToken)
    {
        var seats = await _db.Departures.AsNoTracking()
            .Where(candidate => candidate.Id == departureId)
            .Select(candidate => new
            {
                candidate.CapacityTotal,
                candidate.CapacityReserved,
                candidate.CapacityConfirmed,
            })
            .FirstOrDefaultAsync(cancellationToken);

        return seats is null
            ? 0
            : new SeatCount(seats.CapacityTotal, seats.CapacityReserved, seats.CapacityConfirmed).SeatsLeft;
    }
}
