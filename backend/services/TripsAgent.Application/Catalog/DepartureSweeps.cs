using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TripsAgent.Application.Persistence;
using TripsAgent.Application.Tenancy;
using TripsAgent.Domain.Catalog;

namespace TripsAgent.Application.Catalog;

/// <summary>What one run of the hold expiry job did.</summary>
/// <param name="Released">Holds whose time ran out, and whose seats went back on sale.</param>
/// <param name="SeatsReturned">Seats those holds were counting against capacity.</param>
public sealed record DepartureHoldExpiryRun(int Released, int SeatsReturned);

/// <summary>
/// Plan §3 job 6, the departures half: releases seat holds whose time ran out and restores the
/// capacity they were counting against.
/// </summary>
/// <remarks>
/// <para>
/// A hold is the only thing standing between "somebody is buying this" and "nobody can have it".
/// Without this job an abandoned checkout would keep its seats for ever, and a departure would show
/// sold out with nobody on it.
/// </para>
/// <para>
/// Each hold is released through <see cref="DepartureSeats.ReleaseAsync"/>, which is idempotent and
/// moves the capacity in one atomic statement — so two Workers running this at once give the seats
/// back once, not twice. Releasing also offers the freed seats to the waitlist.
/// </para>
/// </remarks>
public sealed partial class DepartureHoldExpiry
{
    /// <summary>A ceiling on holds handled per run, so one run cannot hold a Worker for ever.</summary>
    public const int MaxPerRun = 500;

    private readonly IAppDbContext _db;
    private readonly DepartureSeats _seats;
    private readonly IPlatformScope _platformScope;
    private readonly TimeProvider _clock;
    private readonly ILogger<DepartureHoldExpiry> _logger;

    public DepartureHoldExpiry(
        IAppDbContext db,
        DepartureSeats seats,
        IPlatformScope platformScope,
        TimeProvider clock,
        ILogger<DepartureHoldExpiry> logger)
    {
        _db = db;
        _seats = seats;
        _platformScope = platformScope;
        _clock = clock;
        _logger = logger;
    }

    public async Task<DepartureHoldExpiryRun> RunAsync(CancellationToken cancellationToken = default)
    {
        using var scope = _platformScope.Enter(
            "departure hold expiry — gives back every agency's seats whose checkout timed out");

        var now = _clock.GetUtcNow();

        var lapsed = await _db.DepartureHolds.AsNoTracking()
            .Where(hold => hold.Status == DepartureHoldStatus.Held && hold.ExpiresAt <= now)
            .OrderBy(hold => hold.ExpiresAt)
            .Take(MaxPerRun)
            .Select(hold => new { hold.Id, hold.PaxCount })
            .ToListAsync(cancellationToken);

        var released = 0;
        var seats = 0;

        foreach (var hold in lapsed)
        {
            if (await _seats.ReleaseAsync(hold.Id, cancellationToken))
            {
                released++;
                seats += hold.PaxCount;
            }
        }

        if (released > 0)
        {
            LogReleased(_logger, released, seats);
        }

        return new DepartureHoldExpiryRun(released, seats);
    }

    [LoggerMessage(EventId = 6111, Level = LogLevel.Information, Message = "Released {Released} lapsed seat holds, returning {Seats} seats.")]
    private static partial void LogReleased(ILogger logger, int released, int seats);
}

/// <summary>What one run of the status sweep did.</summary>
/// <param name="Checked">Departures looked at.</param>
/// <param name="Changed">Departures whose status was out of step with their seats.</param>
public sealed record DepartureStatusSweepRun(int Checked, int Changed);

/// <summary>
/// Plan §3 job 9's nightly half: puts every live departure's status back in line with its seats.
/// </summary>
/// <remarks>
/// <para>
/// The status is normally moved by the seat move that earned it — <see cref="DepartureSeats"/> does
/// it in the same transaction. This is the backstop for the move that was interrupted between the
/// capacity update and the status write, and for a departure whose status was never recomputed
/// because nothing has happened to it in months.
/// </para>
/// <para>
/// It never touches a closed or cancelled departure: those are the agent's own decision, and
/// <see cref="Departure.RefreshStatus"/> leaves them alone.
/// </para>
/// </remarks>
public sealed partial class DepartureStatusSweep
{
    /// <summary>A ceiling on departures handled per run.</summary>
    public const int MaxPerRun = 2_000;

    private readonly IAppDbContext _db;
    private readonly IPlatformScope _platformScope;
    private readonly TimeProvider _clock;
    private readonly ILogger<DepartureStatusSweep> _logger;

    public DepartureStatusSweep(
        IAppDbContext db,
        IPlatformScope platformScope,
        TimeProvider clock,
        ILogger<DepartureStatusSweep> logger)
    {
        _db = db;
        _platformScope = platformScope;
        _clock = clock;
        _logger = logger;
    }

    public async Task<DepartureStatusSweepRun> RunAsync(CancellationToken cancellationToken = default)
    {
        using var scope = _platformScope.Enter(
            "departure status sweep — keeps every agency's departure statuses in step with their seats");

        // Only the ones that have not left yet: a departure in the past cannot change status, and
        // walking every departure an agency has ever run would grow without bound.
        var today = DateOnly.FromDateTime(_clock.GetUtcNow().UtcDateTime);

        var departures = await _db.Departures
            .Where(departure => departure.DepartureDate >= today)
            .Where(departure => departure.Status != DepartureStatus.Closed
                             && departure.Status != DepartureStatus.Cancelled)
            .OrderBy(departure => departure.DepartureDate)
            .Take(MaxPerRun)
            .ToListAsync(cancellationToken);

        var changed = departures.Count(departure => departure.RefreshStatus());

        if (changed > 0)
        {
            await _db.SaveChangesAsync(cancellationToken);
            LogSwept(_logger, departures.Count, changed);
        }

        return new DepartureStatusSweepRun(departures.Count, changed);
    }

    [LoggerMessage(EventId = 6121, Level = LogLevel.Information, Message = "Swept {Checked} departures; {Changed} statuses were out of step with their seats.")]
    private static partial void LogSwept(ILogger logger, int @checked, int changed);
}
