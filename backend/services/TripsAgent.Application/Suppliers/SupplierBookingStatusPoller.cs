using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TripsAgent.Application.Notifications;
using TripsAgent.Application.Persistence;
using TripsAgent.Application.Tenancy;
using TripsAgent.Domain.Suppliers;

namespace TripsAgent.Application.Suppliers;

/// <summary>
/// Asks the supplier about every booking still awaiting its outcome. Plan §3, job 1: the single most
/// important job in the system (#37).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why it exists.</b> Trips Africa has no webhooks. The issue call usually answers "TicketPending",
/// and a timed-out one answers nothing at all. This job is the only way a booking's real outcome is
/// ever learned, and the only thing that ever asks for a payment to be reversed.
/// </para>
/// <para>
/// <b>How it runs.</b> Every thirty seconds. Each run claims the due bookings off the
/// <c>(status, next_poll_at)</c> index with <c>FOR UPDATE SKIP LOCKED</c>, and pushes their next poll a
/// lease into the future in the same short transaction — so any number of Workers can run it at once,
/// each taking different bookings, and no database lock is held while the supplier is asked.
/// </para>
/// <para>
/// <b>What it does with an answer</b> is the booking's decision, not this class's — see
/// <see cref="SupplierBooking.RecordStatusPoll"/>. Every poll, answered or not, becomes a
/// <c>supplier_status_polls</c> row saved in the same transaction as the booking and its events: the
/// evidence any reversal must rest on.
/// </para>
/// </remarks>
public sealed partial class SupplierBookingStatusPoller
{
    /// <summary>Bookings per run. At most one supplier call each.</summary>
    public const int BatchSize = 50;

    /// <summary>The alert source for a supplier error — mapped to its own back-office alert type.</summary>
    public const string AlertSource = nameof(SupplierBookingStatusPoller);

    /// <summary>The alert source for a booking unresolved past its time limit.</summary>
    public const string TimeLimitAlertSource = AlertSource + ".TimeLimit";

    /// <summary>How long a claim keeps other Workers off a booking while it is polled.</summary>
    public static readonly TimeSpan ClaimLease = TimeSpan.FromMinutes(2);

    private const int MaxRecordAttempts = 3;

    private readonly IAppDbContext _db;
    private readonly ISupplierBookingLocks _bookingLocks;
    private readonly IPlatformScope _platformScope;
    private readonly ISupplierAdapterRegistry _adapters;
    private readonly IPlatformAlerter _alerter;
    private readonly TimeProvider _clock;
    private readonly ILogger<SupplierBookingStatusPoller> _logger;

    public SupplierBookingStatusPoller(
        IAppDbContext db,
        ISupplierBookingLocks bookingLocks,
        IPlatformScope platformScope,
        ISupplierAdapterRegistry adapters,
        IPlatformAlerter alerter,
        TimeProvider clock,
        ILogger<SupplierBookingStatusPoller> logger)
    {
        _db = db;
        _bookingLocks = bookingLocks;
        _platformScope = platformScope;
        _adapters = adapters;
        _alerter = alerter;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>Polls every booking that is due, up to <see cref="BatchSize"/>.</summary>
    /// <returns>How many polls were recorded.</returns>
    public async Task<int> RunAsync(CancellationToken cancellationToken = default)
    {
        using var scope = _platformScope.Enter(
            "supplier status poller — asks the supplier about every agency's bookings still awaiting an outcome");

        var now = _clock.GetUtcNow();
        var due = await _bookingLocks.ClaimDueForPollingAsync(now, now + ClaimLease, BatchSize, cancellationToken);
        var recorded = 0;

        foreach (var bookingId in due)
        {
            try
            {
                if (await PollAsync(bookingId, cancellationToken))
                {
                    recorded++;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One booking's trouble must not stop the rest. Its claim lapses and the next run asks again.
                LogPollFailed(_logger, ex, bookingId);
            }
        }

        return recorded;
    }

    /// <summary>Polls one booking now, due or not, if it is still awaiting its outcome.</summary>
    /// <returns>True when a poll was recorded.</returns>
    public async Task<bool> PollAsync(Guid supplierBookingId, CancellationToken cancellationToken = default)
    {
        using var scope = _platformScope.Enter("supplier status poller — asks the supplier about one booking");

        _db.ChangeTracker.Clear();

        var target = await _db.SupplierBookings
            .AsNoTracking()
            .Where(booking => booking.Id == supplierBookingId)
            .Select(booking => new PollTarget(
                booking.Id,
                booking.AgencyId,
                booking.SupplierId,
                booking.OrderLineId,
                booking.ProductType,
                booking.Status,
                booking.ConfirmationCode,
                booking.Pnr))
            .SingleOrDefaultAsync(cancellationToken);

        if (target is null || !IsAwaitingOutcome(target.Status))
        {
            return false;
        }

        var observation = await AskAsync(target, cancellationToken);
        var recorded = await RecordAsync(supplierBookingId, observation, cancellationToken);

        if (recorded is { Alert: { } alert })
        {
            // After the save, so the alert describes something that is on record.
            await RaiseAsync(target, recorded.Poll, alert, cancellationToken);
        }

        return recorded is not null;
    }

    private async Task<SupplierStatusObservation> AskAsync(PollTarget target, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(target.ConfirmationCode))
        {
            return NoAnswer(SupplierPollOutcome.HttpError, "The booking has no confirmation code, so the supplier cannot be asked about it.");
        }

        try
        {
            var supplierCode = await _db.Suppliers
                .AsNoTracking()
                .Where(supplier => supplier.Id == target.SupplierId)
                .Select(supplier => supplier.Code)
                .SingleAsync(cancellationToken);

            var adapter = _adapters.Resolve(supplierCode, target.ProductType);
            var surname = await SurnameAsync(target, cancellationToken);

            // A read, so safe to ask as often as needed — unlike the issue call it is settling.
            var answer = await adapter.GetStatusAsync(
                new SupplierCallContext(target.AgencyId, target.Id),
                new SupplierStatusQuery(target.ProductType, target.ConfirmationCode, target.Pnr, surname),
                cancellationToken);

            return new SupplierStatusObservation(
                answer.Outcome,
                answer.Status,
                answer.SupplierStatusCode,
                answer.HttpStatusCode,
                answer.Pnr,
                answer.SupplierApiCallId,
                answer.Message);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Recorded as a poll all the same: the trail shows every time we asked, not only the answers.
            return NoAnswer(
                ex is SupplierCallOutcomeUnknownException ? SupplierPollOutcome.Timeout : SupplierPollOutcome.HttpError,
                $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// The surname the supplier identifies the booking by: the lead passenger's, from the supplier
    /// booking's own passengers, or the order line's travellers when it has none.
    /// </summary>
    private async Task<string?> SurnameAsync(PollTarget target, CancellationToken cancellationToken)
    {
        var passengers = await _db.SupplierBookingPassengers
            .AsNoTracking()
            .Where(passenger => passenger.SupplierBookingId == target.Id)
            .Select(passenger => new { passenger.PassengerType, passenger.LastName })
            .ToListAsync(cancellationToken);

        var lead = passengers.OrderBy(passenger => passenger.PassengerType).FirstOrDefault();

        if (lead is not null)
        {
            return lead.LastName;
        }

        return await _db.OrderTravellers
            .AsNoTracking()
            .Where(traveller => traveller.OrderLineId == target.OrderLineId)
            .OrderBy(traveller => traveller.CreatedAt)
            .Select(traveller => traveller.LastName)
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>Saves the poll and whatever it changed, in one transaction. Null when there was nothing left to poll.</summary>
    private async Task<SupplierPollRecorded?> RecordAsync(
        Guid supplierBookingId,
        SupplierStatusObservation observation,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            _db.ChangeTracker.Clear();

            var booking = await _db.SupplierBookings.SingleOrDefaultAsync(
                candidate => candidate.Id == supplierBookingId, cancellationToken);

            if (booking is null || !booking.IsAwaitingOutcome)
            {
                // The issue call's answer was recorded while we were asking.
                LogNothingToPoll(_logger, supplierBookingId);
                return null;
            }

            var recorded = booking.RecordStatusPoll(observation, _clock.GetUtcNow());
            _db.SupplierStatusPolls.Add(recorded.Poll);

            try
            {
                await _db.SaveChangesAsync(cancellationToken);
                LogPolled(_logger, supplierBookingId, observation.Outcome, observation.SupplierStatusCode, recorded.Poll.ActionTaken, booking.Status);
                return recorded;
            }
            catch (DbUpdateConcurrencyException) when (attempt < MaxRecordAttempts)
            {
                // Someone else wrote to the booking in between. Read again and decide afresh.
            }
        }
    }

    private Task RaiseAsync(PollTarget target, SupplierStatusPoll poll, SupplierPollAlert alert, CancellationToken cancellationToken)
    {
        var code = poll.SupplierStatusCode?.ToString(CultureInfo.InvariantCulture) ?? "none";

        var platformAlert = alert == SupplierPollAlert.SupplierReportedError
            ? new PlatformAlert(
                AlertSeverity.P1,
                "The supplier reported an error on a booking awaiting its ticket",
                $"Supplier booking {target.Id} (order line {target.OrderLineId}) answered its status query with code {code}: "
                + $"{poll.Note ?? "no message"}. Nothing has been reversed and the money is still held; the poller keeps asking "
                + "on its back-off. Check the booking with the supplier, and read its supplier_status_polls and "
                + "supplier_api_calls rows for exactly what was said.",
                AlertSource,
                target.AgencyId)
            : new PlatformAlert(
                AlertSeverity.P1,
                "A booking is still unresolved past its ticket time limit",
                $"Supplier booking {target.Id} (order line {target.OrderLineId}) has no final answer from the supplier "
                + $"{SupplierPollSchedule.TicketTimeLimitBuffer.TotalMinutes:0} minutes after its ticket time limit; the last "
                + $"status code was {code}. It is never resolved by a timeout: the money stays held, and the poller keeps "
                + "asking hourly. Ask the supplier what happened to it.",
                TimeLimitAlertSource,
                target.AgencyId);

        return _alerter.RaiseAsync(platformAlert, cancellationToken);
    }

    private static bool IsAwaitingOutcome(SupplierBookingStatus status) =>
        status is SupplierBookingStatus.Issuing
            or SupplierBookingStatus.IssueOutcomeUnknown
            or SupplierBookingStatus.TicketPending;

    private static SupplierStatusObservation NoAnswer(SupplierPollOutcome outcome, string message) =>
        new(outcome, ReportedStatus: null, SupplierStatusCode: null, HttpStatusCode: null, Pnr: null, SupplierApiCallId: null, message);

    [LoggerMessage(Level = LogLevel.Error, Message = "Polling supplier booking {BookingId} failed; it will be polled again.")]
    private static partial void LogPollFailed(ILogger logger, Exception exception, Guid bookingId);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Supplier booking {BookingId} was settled while it was being polled; nothing to record.")]
    private static partial void LogNothingToPoll(ILogger logger, Guid bookingId);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Polled supplier booking {BookingId}: {Outcome}, supplier status {StatusCode}, action {Action}, booking now {Status}.")]
    private static partial void LogPolled(
        ILogger logger, Guid bookingId, SupplierPollOutcome outcome, int? statusCode, SupplierPollAction action, SupplierBookingStatus status);

    private sealed record PollTarget(
        Guid Id,
        Guid AgencyId,
        Guid SupplierId,
        Guid OrderLineId,
        SupplierProductType ProductType,
        SupplierBookingStatus Status,
        string? ConfirmationCode,
        string? Pnr);
}
