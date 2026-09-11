using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TripsAgent.Application.Checkout;
using TripsAgent.Application.Messaging;
using TripsAgent.Application.Notifications;
using TripsAgent.Application.Persistence;
using TripsAgent.Application.Tenancy;
using TripsAgent.Domain.Orders;
using TripsAgent.Domain.Payments;
using TripsAgent.Domain.Suppliers;

namespace TripsAgent.Application.Suppliers;

/// <summary>What one run of the monitor did.</summary>
public sealed record TicketTimeLimitRun(int Expired, int Warned);

/// <summary>
/// Watches the ticket time limit on held bookings (#38, plan §3 job 4): warns the agent an hour and a
/// quarter of an hour before it, and lapses the booking once it passes.
/// </summary>
/// <remarks>
/// <para>
/// <b>Held</b> means price confirmed and never issued. The limit is the supplier's: issue after it and
/// the seats are gone. Once issuing has begun the confirmation is consumed and this monitor has no say —
/// a booking issuing, pending or ticketed may already hold a real ticket, and only the supplier, through
/// the status poller, can say otherwise. So a booking that is not <c>PriceConfirmed</c> is left alone
/// (<see cref="SupplierBooking.Expire"/>).
/// </para>
/// <para>
/// <b>Exactly once per booking.</b> Each booking is handled in its own transaction under a row lock
/// taken with SKIP LOCKED, and what it changes — the status, or the warning's timestamp — is what takes
/// it out of the next query. Two Workers, or three runs back to back, find it once between them. The
/// notification's dedupe key backs that up.
/// </para>
/// <para>
/// <b>An expiry is one transaction:</b> the booking lapses, its order line goes to the agent's
/// resolution queue, the wallet hold is released, and the agent is told — all of it, or none.
/// </para>
/// </remarks>
public sealed partial class TicketTimeLimitMonitor
{
    /// <summary>A ceiling on bookings handled per run, so one run cannot hold a Worker for ever.</summary>
    public const int MaxPerRun = 200;

    private const string ExpiredLineReason =
        "The ticket time limit passed before the ticket was issued, so the supplier released the booking. "
        + "No ticket exists.";

    private readonly IAppDbContext _db;
    private readonly ITransactionRunner _transactions;
    private readonly ISupplierBookingLocks _bookingLocks;
    private readonly IPlatformScope _platformScope;
    private readonly INotifier _notifier;
    private readonly IOutbox _outbox;
    private readonly TimeProvider _clock;
    private readonly ILogger<TicketTimeLimitMonitor> _logger;

    public TicketTimeLimitMonitor(
        IAppDbContext db,
        ITransactionRunner transactions,
        ISupplierBookingLocks bookingLocks,
        IPlatformScope platformScope,
        INotifier notifier,
        IOutbox outbox,
        TimeProvider clock,
        ILogger<TicketTimeLimitMonitor> logger)
    {
        _db = db;
        _transactions = transactions;
        _bookingLocks = bookingLocks;
        _platformScope = platformScope;
        _notifier = notifier;
        _outbox = outbox;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>Expires what has lapsed, then sends the warnings that are due.</summary>
    public async Task<TicketTimeLimitRun> RunAsync(CancellationToken cancellationToken = default)
    {
        using var scope = _platformScope.Enter(
            "ticket time limit monitor — watches every agency's held bookings for their deadline");

        var expired = 0;

        while (expired < MaxPerRun && await ExpireNextAsync(cancellationToken))
        {
            expired++;
        }

        var warned = 0;

        foreach (var warning in (TicketTimeLimitWarning[])[TicketTimeLimitWarning.FifteenMinutes, TicketTimeLimitWarning.SixtyMinutes])
        {
            var sent = 0;

            while (sent < MaxPerRun && await WarnNextAsync(warning, cancellationToken))
            {
                sent++;
            }

            warned += sent;
        }

        return new TicketTimeLimitRun(expired, warned);
    }

    private async Task<bool> ExpireNextAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _transactions.RunAsync(
                async token =>
                {
                    var now = _clock.GetUtcNow();
                    var bookingId = await _bookingLocks.LockNextExpiredAsync(now, token);

                    if (bookingId is null)
                    {
                        return false;
                    }

                    var booking = await _db.SupplierBookings.SingleAsync(candidate => candidate.Id == bookingId, token);

                    if (!booking.Expire(now))
                    {
                        // Found by the query but refused by the rule. Stop rather than find it again forever.
                        return false;
                    }

                    var line = await _db.OrderLines.SingleOrDefaultAsync(candidate => candidate.Id == booking.OrderLineId, token);

                    if (line is not null)
                    {
                        var order = await _db.Orders.Include(candidate => candidate.Lines)
                            .SingleAsync(candidate => candidate.Id == line.OrderId, token);

                        if (order.PaidAt is null)
                        {
                            // A price confirmed and never paid for: the money held for it goes back, and there is
                            // nothing to resolve. The order is simply closed.
                            line.RecordFulfilment(FulfilmentStatus.Cancelled, now);
                            order.ChangeStatus(OrderStatus.Cancelled, now, "The fare's ticket time limit passed before it was paid for.");
                            await ReleaseHoldsAsync(line, now, token);
                        }
                        else
                        {
                            FailedLines.FlagForResolution(order, line, ExpiredLineReason, now, _outbox);
                            await ReleaseHoldsAsync(line, now, token);
                            await NotifyAsync(
                                booking,
                                line,
                                NotificationTemplateCatalog.BookingExpired,
                                $"{NotificationTemplateCatalog.BookingExpired}:{booking.Id}",
                                [],
                                token);
                        }
                    }

                    await _db.SaveChangesAsync(token);
                    LogExpired(_logger, booking.Id, booking.OrderLineId);
                    return true;
                },
                cancellationToken);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            // A wallet moved at the same moment — a top-up, say. Nothing was saved, so nothing is half
            // done; the next run, a minute away, does it again.
            _db.ChangeTracker.Clear();
            LogConflict(_logger, ex);
            return false;
        }
    }

    private async Task<bool> WarnNextAsync(TicketTimeLimitWarning warning, CancellationToken cancellationToken)
    {
        try
        {
            return await _transactions.RunAsync(
                async token =>
                {
                    var now = _clock.GetUtcNow();
                    var bookingId = await _bookingLocks.LockNextDueWarningAsync(warning, now, token);

                    if (bookingId is null)
                    {
                        return false;
                    }

                    var booking = await _db.SupplierBookings.SingleAsync(candidate => candidate.Id == bookingId, token);

                    if (!booking.RecordTimeLimitWarning(warning, now))
                    {
                        return false;
                    }

                    var line = await _db.OrderLines.SingleOrDefaultAsync(candidate => candidate.Id == booking.OrderLineId, token);

                    if (line is not null)
                    {
                        var minutesLeft = Math.Max(1, (int)Math.Ceiling((booking.TicketTimeLimit!.Value - now).TotalMinutes));

                        await NotifyAsync(
                            booking,
                            line,
                            NotificationTemplateCatalog.BookingTimeLimitWarning,
                            string.Create(CultureInfo.InvariantCulture, $"{NotificationTemplateCatalog.BookingTimeLimitWarning}:{(int)warning}:{booking.Id}"),
                            new Dictionary<string, string>(StringComparer.Ordinal)
                            {
                                ["minutesLeft"] = minutesLeft.ToString(CultureInfo.InvariantCulture),
                            },
                            token);
                    }

                    await _db.SaveChangesAsync(token);
                    LogWarned(_logger, booking.Id, (int)warning);
                    return true;
                },
                cancellationToken);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            _db.ChangeTracker.Clear();
            LogConflict(_logger, ex);
            return false;
        }
    }

    /// <summary>
    /// Gives back the order's wallet hold — once nothing else on the order still needs it.
    /// </summary>
    /// <remarks>
    /// A hold covers its whole order. Releasing it while another line is still being ticketed would let
    /// the agent spend money a ticket is about to take, so it waits until every line has failed.
    /// </remarks>
    private async Task ReleaseHoldsAsync(OrderLine line, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var anotherLineStillNeedsIt = await _db.OrderLines.AnyAsync(
            other => other.OrderId == line.OrderId
                     && other.Id != line.Id
                     && (other.FulfilmentStatus == FulfilmentStatus.Pending
                         || other.FulfilmentStatus == FulfilmentStatus.Reserved
                         || other.FulfilmentStatus == FulfilmentStatus.Confirming
                         || other.FulfilmentStatus == FulfilmentStatus.Confirmed),
            cancellationToken);

        if (anotherLineStillNeedsIt)
        {
            LogHoldKept(_logger, line.OrderId);
            return;
        }

        var holds = await _db.WalletHolds
            .Where(hold => hold.OrderId == line.OrderId && hold.Status == WalletHoldStatus.Held)
            .ToListAsync(cancellationToken);

        foreach (var hold in holds)
        {
            var wallet = await _db.Wallets.SingleAsync(candidate => candidate.Id == hold.WalletId, cancellationToken);
            wallet.ReleaseHold(hold, now);
        }
    }

    /// <summary>Queues an email to the agency's owner, in this transaction. Agency-facing: it may say Trips.</summary>
    private async Task NotifyAsync(
        SupplierBooking booking,
        OrderLine line,
        string templateKey,
        string dedupeKey,
        Dictionary<string, string> values,
        CancellationToken cancellationToken)
    {
        var recipient = await _db.Users
            .Where(user => user.AgencyId == booking.AgencyId)
            .OrderBy(user => user.CreatedAt)
            .Select(user => new { user.Id, user.Email, user.FirstName })
            .FirstOrDefaultAsync(cancellationToken);

        if (recipient is null)
        {
            LogNoRecipient(_logger, booking.AgencyId, booking.Id);
            return;
        }

        var orderNumber = await _db.Orders
            .Where(order => order.Id == line.OrderId)
            .Select(order => order.OrderNumber)
            .SingleOrDefaultAsync(cancellationToken);

        var timezone = await _db.Agencies
            .Where(agency => agency.Id == booking.AgencyId)
            .Select(agency => agency.Timezone)
            .SingleOrDefaultAsync(cancellationToken);

        values["bookingReference"] = orderNumber ?? line.Id.ToString();
        values["itinerarySummary"] = line.TitleSnapshot;
        values["deadline"] = FormatDeadline(booking.TicketTimeLimit!.Value, timezone);

        await _notifier.QueueEmailAsync(
            new EmailNotificationRequest(
                booking.AgencyId,
                templateKey,
                recipient.Email,
                recipient.FirstName,
                values,
                dedupeKey,
                recipient.Id),
            cancellationToken);
    }

    /// <summary>The deadline on the agency's own clock, with the zone named — or UTC when the zone is unknown.</summary>
    public static string FormatDeadline(DateTimeOffset deadline, string? timezone)
    {
        if (!string.IsNullOrWhiteSpace(timezone) && TimeZoneInfo.TryFindSystemTimeZoneById(timezone, out var zone))
        {
            var local = TimeZoneInfo.ConvertTime(deadline, zone);
            return string.Create(CultureInfo.InvariantCulture, $"{local:d MMM yyyy, HH:mm} ({timezone})");
        }

        return string.Create(CultureInfo.InvariantCulture, $"{deadline.ToUniversalTime():d MMM yyyy, HH:mm} (UTC)");
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Supplier booking {BookingId} (order line {OrderLineId}) passed its ticket time limit and has expired.")]
    private static partial void LogExpired(ILogger logger, Guid bookingId, Guid orderLineId);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Warned the agent that supplier booking {BookingId} has {Minutes} minutes left to issue.")]
    private static partial void LogWarned(ILogger logger, Guid bookingId, int minutes);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Kept the wallet hold on order {OrderId}: another line on it is still being fulfilled.")]
    private static partial void LogHoldKept(ILogger logger, Guid orderId);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Agency {AgencyId} has no user to tell about supplier booking {BookingId}.")]
    private static partial void LogNoRecipient(ILogger logger, Guid agencyId, Guid bookingId);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "The ticket time limit monitor met a concurrent change and stopped this run; the next run picks it up.")]
    private static partial void LogConflict(ILogger logger, Exception exception);
}
