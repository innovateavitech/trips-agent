using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TripsAgent.Application.Messaging;
using TripsAgent.Application.Persistence;
using TripsAgent.Application.Suppliers;
using TripsAgent.Application.Tenancy;
using TripsAgent.Domain.Orders;
using TripsAgent.Domain.Payments;
using TripsAgent.Domain.Suppliers;

namespace TripsAgent.Application.Checkout;

/// <summary>
/// The checkout's timeout for the one waiting state nothing else watches (#42): paid for, but the issue
/// message never ran.
/// </summary>
/// <remarks>
/// <para>
/// Every other wait has its own clock. A confirmed price lapses at its ticket time limit (the time limit
/// monitor); an issue call with no answer is taken over by the poller; a pending ticket is polled until
/// the supplier settles it. What is left is a booking paid for whose <see cref="IssueSupplierTicket"/> was
/// lost — a broker outage, a message dead-lettered. This finds those and sends the message again.
/// </para>
/// <para>
/// <b>Sending it again is safe</b>, and is the only thing here that could look like a retry. It is not
/// one: the issuer takes the booking from PriceConfirmed to Issuing under a row lock before it calls the
/// supplier, so a second message finds it already issuing and sends nothing (ADR-0003).
/// </para>
/// <para>
/// It also gives back a <b>provisional hold</b> — the one a price confirmation places before it asks the
/// supplier — that nothing replaced or released: a checkout that died halfway. Once the hold's own
/// lifetime has passed, the money goes back to the wallet.
/// </para>
/// </remarks>
public sealed partial class CheckoutSweeper
{
    /// <summary>How long a paid booking may wait for its issue message before it is sent again.</summary>
    public static readonly TimeSpan IssueNudgeAfter = TimeSpan.FromMinutes(2);

    public const int MaxPerRun = 100;

    private readonly IAppDbContext _db;
    private readonly IPlatformScope _platformScope;
    private readonly IOutbox _outbox;
    private readonly TimeProvider _clock;
    private readonly ILogger<CheckoutSweeper> _logger;

    public CheckoutSweeper(IAppDbContext db, IPlatformScope platformScope, IOutbox outbox, TimeProvider clock, ILogger<CheckoutSweeper> logger)
    {
        _db = db;
        _platformScope = platformScope;
        _outbox = outbox;
        _clock = clock;
        _logger = logger;
    }

    /// <returns>How many things it put right: issue messages sent again, and orphaned holds released.</returns>
    public async Task<int> RunAsync(CancellationToken cancellationToken = default)
    {
        using var scope = _platformScope.Enter("checkout sweeper — finds every agency's stalled bookings and orphaned wallet holds");

        var now = _clock.GetUtcNow();
        var released = await ReleaseOrphanedHoldsAsync(now, cancellationToken);
        var paidBefore = now - IssueNudgeAfter;

        var stalled = await (
                from line in _db.OrderLines
                join order in _db.Orders on line.OrderId equals order.Id
                join booking in _db.SupplierBookings on line.SupplierBookingId equals booking.Id
                where line.FulfilmentStatus == FulfilmentStatus.Confirming
                      && order.PaidAt != null && order.PaidAt <= paidBefore
                      && booking.Status == SupplierBookingStatus.PriceConfirmed
                      && booking.TicketTimeLimit > now
                orderby order.PaidAt
                select new { LineId = line.Id, order.AgencyId, booking.IdempotencyKey })
            .Take(MaxPerRun)
            .ToListAsync(cancellationToken);

        foreach (var line in stalled)
        {
            _outbox.Enqueue(new IssueSupplierTicket(line.LineId, line.AgencyId, line.IdempotencyKey), line.AgencyId);
        }

        if (stalled.Count > 0)
        {
            await _db.SaveChangesAsync(cancellationToken);
            LogResent(_logger, stalled.Count);
        }

        return stalled.Count + released;
    }

    private async Task<int> ReleaseOrphanedHoldsAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var orphaned = await _db.WalletHolds
            .Where(hold => hold.OrderId == null && hold.Status == WalletHoldStatus.Held && hold.ExpiresAt <= now)
            .OrderBy(hold => hold.ExpiresAt)
            .Take(MaxPerRun)
            .ToListAsync(cancellationToken);

        foreach (var hold in orphaned)
        {
            var wallet = await _db.Wallets.SingleAsync(candidate => candidate.Id == hold.WalletId, cancellationToken);
            wallet.ReleaseHold(hold, now);
        }

        if (orphaned.Count > 0)
        {
            await _db.SaveChangesAsync(cancellationToken);
            LogReleased(_logger, orphaned.Count);
        }

        return orphaned.Count;
    }

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Sent {Count} issue messages again for bookings paid for but never issued. Look for a broker outage or dead letters.")]
    private static partial void LogResent(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Released {Count} provisional wallet holds left behind by checkouts that stopped halfway.")]
    private static partial void LogReleased(ILogger logger, int count);
}
