using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TripsAgent.Application.Concurrency;
using TripsAgent.Application.Persistence;
using TripsAgent.Domain.Suppliers;

namespace TripsAgent.Application.Suppliers;

/// <summary>
/// Asks the Worker to issue the ticket for one order line's supplier booking. Sent to
/// <c>booking.saga</c> by the checkout saga (#42).
/// </summary>
/// <remarks>
/// Delivering it twice, or twenty times, is harmless: see <see cref="TicketIssuanceService"/>.
/// </remarks>
/// <param name="IdempotencyKey">The booking's <c>idempotency_key</c>. A message whose key does not match is refused.</param>
public sealed record IssueSupplierTicket(
    Guid OrderLineId,
    Guid AgencyId,
    string IdempotencyKey,
    string? CorrelationId = null);

/// <summary>What one attempt to issue came to.</summary>
public enum TicketIssueOutcome
{
    /// <summary>This attempt sent the one issue call. What came back is on the booking.</summary>
    Sent = 1,

    /// <summary>Another worker holds this order line's issue lock. Nothing was sent.</summary>
    AlreadyInProgress = 2,

    /// <summary>Issuing already began for this booking, here or elsewhere. Nothing was sent.</summary>
    AlreadyStarted = 3,

    /// <summary>
    /// The booking cannot be issued: there is none for the line, its price was not verified, its time
    /// limit passed, or the idempotency key does not match. Nothing was sent.
    /// </summary>
    NotIssuable = 4,
}

/// <summary>
/// Issues a ticket with the supplier: once, and never again (#36).
/// </summary>
/// <remarks>
/// <para>
/// <b>Read docs/adr/0003-never-retry-ticket-issuance.md first.</b> The supplier's issue call is not
/// idempotent and has no idempotency key, so a request sent twice is two real tickets for one person.
/// This class sends it at most once per booking, whatever retries, redelivers or crashes around it.
/// </para>
/// <para>
/// <b>Four guards against a second ticket</b>, outermost first. Any one of them turns a duplicate away;
/// the last is the one that survives everything:
/// </para>
/// <list type="number">
/// <item><b>The idempotency key.</b> The message names the booking's own key, which is unique across the
/// platform; a message for a line with a different key is refused.</item>
/// <item><b>A distributed lock on the order line</b> (<see cref="IDistributedLock"/>). A duplicate
/// arriving while the first is in flight is turned away before it touches the database.</item>
/// <item><b>The booking's state, under a row lock.</b> The move from <c>PriceConfirmed</c> to
/// <c>Issuing</c> is committed <i>before</i> the call leaves, while the row is locked. A second worker
/// waits for the lock, then finds the booking already issuing and stops. The domain refuses the move
/// from any other state (<see cref="SupplierBooking.BeginIssue"/>).</item>
/// <item><b><c>UNIQUE (order_line_id)</c></b> on <c>supplier_bookings</c>. One line can only ever have one
/// booking to issue: a second one cannot even be created. The constraint is the one that actually holds —
/// it survives a process dying, a lock expiring and a bug in everything above.</item>
/// </list>
/// <para>
/// <b>Every uncertain answer goes to the poller.</b> A timeout, a dropped connection, a refusal, or a code
/// the reversal rules want confirmed all move the booking to <c>IssueOutcomeUnknown</c>, and
/// <see cref="SupplierBookingStatusPoller"/> settles it with a status query. A worker killed mid-call
/// leaves the booking <c>Issuing</c>, and the poller takes it over after
/// <see cref="SupplierPollSchedule.IssueRecoveryDelay"/>. Nothing ever goes back to <c>PriceConfirmed</c>.
/// </para>
/// </remarks>
public sealed partial class TicketIssuanceService
{
    /// <summary>
    /// How long the issue lock is held at most. Longer than the issue call's own timeout plus the saves
    /// either side of it, so the lock cannot lapse while the call is still in flight.
    /// </summary>
    public static readonly TimeSpan LockLease = TimeSpan.FromMinutes(2);

    private const int MaxRecordAttempts = 3;

    private readonly IAppDbContext _db;
    private readonly ITransactionRunner _transactions;
    private readonly ISupplierBookingLocks _bookingLocks;
    private readonly IDistributedLock _locks;
    private readonly ISupplierAdapterRegistry _adapters;
    private readonly TimeProvider _clock;
    private readonly ILogger<TicketIssuanceService> _logger;

    public TicketIssuanceService(
        IAppDbContext db,
        ITransactionRunner transactions,
        ISupplierBookingLocks bookingLocks,
        IDistributedLock locks,
        ISupplierAdapterRegistry adapters,
        TimeProvider clock,
        ILogger<TicketIssuanceService> logger)
    {
        _db = db;
        _transactions = transactions;
        _bookingLocks = bookingLocks;
        _locks = locks;
        _adapters = adapters;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>The distributed lock's name for an order line.</summary>
    public static string LockKey(Guid orderLineId) => $"supplier:issue:{orderLineId:N}";

    /// <summary>
    /// Issues the ticket for <paramref name="orderLineId"/>'s supplier booking, if nobody has yet.
    /// </summary>
    /// <param name="orderLineId">The line whose booking to issue. One line has at most one booking.</param>
    /// <param name="idempotencyKey">The booking's idempotency key, as the caller knows it. Null skips the check.</param>
    /// <param name="correlationId">Carried to the audited supplier call.</param>
    /// <param name="cancellationToken">
    /// Cancelling it mid-call — a host shutting down — leaves the booking <c>Issuing</c>, exactly as a
    /// killed process would, and the poller recovers it the same way.
    /// </param>
    public async Task<TicketIssueOutcome> IssueAsync(
        Guid orderLineId,
        string? idempotencyKey = null,
        string? correlationId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(orderLineId, Guid.Empty);

        // Guard 2: the distributed lock. Null means someone else is issuing this line right now.
        await using var held = await _locks.TryAcquireAsync(LockKey(orderLineId), LockLease, cancellationToken);

        if (held is null)
        {
            LogLockHeld(_logger, orderLineId);
            return TicketIssueOutcome.AlreadyInProgress;
        }

        var claim = await ClaimAsync(orderLineId, idempotencyKey, correlationId, cancellationToken);

        if (claim.Stopped is { } stopped)
        {
            return stopped;
        }

        SupplierIssueResult result;

        try
        {
            // THE call — once. Never retried: not here, not underneath (AddSupplierHttpClient refuses any
            // retry handler), and not by a redelivered message, which stops at the claim because the
            // booking is no longer PriceConfirmed. See docs/adr/0003-never-retry-ticket-issuance.md.
            result = await claim.Adapter!.IssueAsync(claim.Context!, claim.Request!, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutting down mid-call. The booking stays Issuing, as if the process had been killed, and
            // the poller takes it over: one recovery path for both.
            LogAbandoned(_logger, claim.BookingId, orderLineId);
            throw;
        }
        catch (Exception ex)
        {
            // The request may have left. An adapter reports this as Unknown rather than throwing; this is
            // the backstop for one that does not, and it is treated exactly the same way.
            result = new SupplierIssueResult(
                SupplierIssueOutcome.Unknown, HttpStatusCode: null, SupplierStatusCode: null, Status: null, Pnr: null,
                Message: $"{ex.GetType().Name}: {ex.Message}");
        }

        await RecordAsync(claim.BookingId, result);
        return TicketIssueOutcome.Sent;
    }

    /// <summary>Guard 3: the row lock and the move to Issuing, committed before anything is sent.</summary>
    private Task<Claim> ClaimAsync(
        Guid orderLineId,
        string? idempotencyKey,
        string? correlationId,
        CancellationToken cancellationToken) =>
        _transactions.RunAsync(
            async token =>
            {
                var bookingId = await _db.SupplierBookings
                    .Where(booking => booking.OrderLineId == orderLineId)
                    .Select(booking => (Guid?)booking.Id)
                    .SingleOrDefaultAsync(token);

                if (bookingId is not { } id)
                {
                    LogNoBooking(_logger, orderLineId);
                    return Claim.Stop(TicketIssueOutcome.NotIssuable);
                }

                // Waits for any other worker holding this row. When it gets the lock, that worker has
                // committed — and the booking below reads as Issuing, not PriceConfirmed.
                await _bookingLocks.LockAsync(id, token);

                var booking = await _db.SupplierBookings.SingleAsync(candidate => candidate.Id == id, token);

                // Guard 1: the idempotency key.
                if (idempotencyKey is not null && !string.Equals(booking.IdempotencyKey, idempotencyKey, StringComparison.Ordinal))
                {
                    LogKeyMismatch(_logger, booking.Id, orderLineId);
                    return Claim.Stop(TicketIssueOutcome.NotIssuable);
                }

                if (booking.Status != SupplierBookingStatus.PriceConfirmed)
                {
                    LogNotClaimable(_logger, booking.Id, booking.Status);
                    return Claim.Stop(booking.IssueStartedAt is null
                        ? TicketIssueOutcome.NotIssuable
                        : TicketIssueOutcome.AlreadyStarted);
                }

                // Everything that can fail is done before the booking moves, so a failure here leaves it
                // PriceConfirmed with nothing sent.
                var supplierCode = await _db.Suppliers
                    .Where(supplier => supplier.Id == booking.SupplierId)
                    .Select(supplier => supplier.Code)
                    .SingleAsync(token);

                var adapter = _adapters.Resolve(supplierCode, booking.ProductType);

                var confirmationCodes = await _db.SupplierBookings
                    .Where(candidate => candidate.Id == id)
                    .SelectMany(candidate => candidate.Confirmations)
                    .OrderBy(confirmation => confirmation.Sequence)
                    .Select(confirmation => confirmation.ConfirmationCode)
                    .ToListAsync(token);

                try
                {
                    booking.BeginIssue(_clock.GetUtcNow());
                }
                catch (InvalidOperationException ex)
                {
                    // An unverified price, or a passed time limit. Nothing moves and nothing is sent.
                    LogRefused(_logger, booking.Id, ex.Message);
                    return Claim.Stop(TicketIssueOutcome.NotIssuable);
                }

                await _db.SaveChangesAsync(token);

                return Claim.Go(
                    booking.Id,
                    adapter,
                    new SupplierCallContext(booking.AgencyId, booking.Id, correlationId),
                    new SupplierIssueRequest(
                        booking.ProductType,
                        booking.SupplierSessionId,
                        booking.TripType,
                        booking.TripMode,
                        confirmationCodes,
                        booking.IdempotencyKey));
            },
            cancellationToken);

    /// <summary>
    /// Writes the answer onto the booking. Retried only against a concurrent write — the poller — and
    /// never by sending anything again.
    /// </summary>
    /// <remarks>
    /// <see cref="CancellationToken.None"/> on purpose: the call has been made, and its answer is worth
    /// keeping even while the host shuts down. Lost, it costs a two-minute wait and a status query.
    /// </remarks>
    private async Task RecordAsync(Guid bookingId, SupplierIssueResult result)
    {
        for (var attempt = 1; ; attempt++)
        {
            _db.ChangeTracker.Clear();

            var booking = await _db.SupplierBookings.SingleAsync(candidate => candidate.Id == bookingId, CancellationToken.None);

            if (!Apply(booking, result, _clock.GetUtcNow()))
            {
                // Already settled by the poller while the call was out.
                LogAlreadySettled(_logger, bookingId, booking.Status);
                return;
            }

            try
            {
                await _db.SaveChangesAsync(CancellationToken.None);
                LogRecorded(_logger, bookingId, result.Outcome, booking.Status);
                return;
            }
            catch (DbUpdateConcurrencyException) when (attempt < MaxRecordAttempts)
            {
                // The poller wrote to the booking at the same moment. Read again and see what it did.
            }
        }
    }

    /// <summary>What an answer means for the booking. False when the booking is no longer issuing.</summary>
    public static bool Apply(SupplierBooking booking, SupplierIssueResult result, DateTimeOffset now) =>
        result switch
        {
            { Outcome: SupplierIssueOutcome.Accepted, Status: SupplierBookingStatus.Ticketed } =>
                booking.RecordIssueTicketed(result.Pnr, result.SupplierStatusCode, now),

            { Outcome: SupplierIssueOutcome.Accepted, Status: SupplierBookingStatus.TicketPending or null } =>
                booking.RecordIssuePending(result.Pnr, result.SupplierStatusCode, now),

            // Trips Africa: HTTP 200 with 0, 1 or 11 means reverse — but only on the evidence of a status
            // query, which is what the poller records. So it runs at once rather than being assumed here.
            { Outcome: SupplierIssueOutcome.Accepted } =>
                booking.RecordIssueUnresolved(
                    $"The supplier accepted the issue call but reported status {result.SupplierStatusCode} "
                    + $"({result.Status}). The status query confirms it before anything is reversed.",
                    result.Pnr, result.SupplierStatusCode, now, pollNow: true),

            // Trips Africa: HTTP 400 means reverse only if a status query then says 0, 1 or 11.
            { Outcome: SupplierIssueOutcome.Rejected } =>
                booking.RecordIssueUnresolved(
                    $"The supplier refused the issue call (HTTP {result.HttpStatusCode}): {result.Message}. "
                    + "Nothing is assumed from a refusal; the status query settles it.",
                    result.Pnr, result.SupplierStatusCode, now, pollNow: true),

            _ =>
                booking.RecordIssueUnresolved(
                    $"No usable answer to the issue call: {result.Message}. The outcome is unknown and is resolved "
                    + "by polling the booking's status, never by issuing again (ADR-0003).",
                    result.Pnr, result.SupplierStatusCode, now, pollNow: false),
        };

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Order line {OrderLineId} is already being issued by another worker; this attempt sent nothing.")]
    private static partial void LogLockHeld(ILogger logger, Guid orderLineId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Order line {OrderLineId} has no supplier booking to issue.")]
    private static partial void LogNoBooking(ILogger logger, Guid orderLineId);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "An issue request for supplier booking {BookingId} (order line {OrderLineId}) carried the wrong idempotency key and was refused.")]
    private static partial void LogKeyMismatch(ILogger logger, Guid bookingId, Guid orderLineId);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Supplier booking {BookingId} is {Status}, not PriceConfirmed; nothing was sent.")]
    private static partial void LogNotClaimable(ILogger logger, Guid bookingId, SupplierBookingStatus status);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Supplier booking {BookingId} cannot be issued: {Reason}")]
    private static partial void LogRefused(ILogger logger, Guid bookingId, string reason);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "The issue call for supplier booking {BookingId} (order line {OrderLineId}) was abandoned mid-flight by shutdown. "
                  + "The booking stays Issuing and the status poller will recover it.")]
    private static partial void LogAbandoned(ILogger logger, Guid bookingId, Guid orderLineId);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Supplier booking {BookingId} was settled by the poller before the issue answer was recorded; it is {Status}.")]
    private static partial void LogAlreadySettled(ILogger logger, Guid bookingId, SupplierBookingStatus status);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Supplier booking {BookingId}: issue call {Outcome}, booking now {Status}.")]
    private static partial void LogRecorded(ILogger logger, Guid bookingId, SupplierIssueOutcome outcome, SupplierBookingStatus status);

    private sealed record Claim(
        Guid BookingId,
        TicketIssueOutcome? Stopped,
        ISupplierAdapter? Adapter,
        SupplierCallContext? Context,
        SupplierIssueRequest? Request)
    {
        public static Claim Stop(TicketIssueOutcome outcome) => new(Guid.Empty, outcome, null, null, null);

        public static Claim Go(Guid bookingId, ISupplierAdapter adapter, SupplierCallContext context, SupplierIssueRequest request) =>
            new(bookingId, null, adapter, context, request);
    }
}
