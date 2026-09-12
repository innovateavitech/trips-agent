using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TripsAgent.Application.Messaging;
using TripsAgent.Application.Notifications;
using TripsAgent.Application.Persistence;
using TripsAgent.Application.Tenancy;
using TripsAgent.Domain.Orders;
using TripsAgent.Domain.Payments;
using TripsAgent.Domain.Suppliers;

namespace TripsAgent.Application.Checkout;

/// <summary>What one reversal request came to.</summary>
public enum ReversalOutcome
{
    /// <summary>The money went back, and the line waits for the agent.</summary>
    Reversed = 1,

    /// <summary>It had already gone back. Nothing moved again.</summary>
    AlreadyReversed = 2,

    /// <summary>The poll it named does not justify a reversal. Nothing moved, and a person was told.</summary>
    NoEvidence = 3,

    /// <summary>There was no wallet money held or taken for the line. Nothing moved, and a person was told.</summary>
    NothingToReverse = 4,
}

/// <summary>
/// Gives the money back when the supplier's documented reversal conditions are met (#43). Driven by
/// <see cref="PaymentReversalRequired"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Trips Africa's rules, exactly.</b> Reverse when the issue call answered 200 with status 0, 1 or 11;
/// reverse when it answered 400 and a status query then says 0, 1 or 11. Both arrive here the same way:
/// the status poller recorded a query answering 0, 1 or 11, and asked for this — naming that poll.
/// </para>
/// <para>
/// <b>Evidence-based.</b> The poll must exist, be this booking's, and say what the rules need. If it does
/// not, nothing moves and a person is alerted: an automated refund with nothing behind it is exactly the
/// kind of money movement nobody can explain later.
/// </para>
/// <para>
/// <b>Once.</b> One refund per line, by unique index: a message delivered twice, or racing the agent's own
/// refund, refunds nothing more. The line goes to the agent's resolution queue — the traveller usually
/// still wants to travel, and the agent decides what to offer them (#44).
/// </para>
/// </remarks>
public sealed partial class PaymentReversalService
{
    /// <summary>Where this service's alerts say they came from.</summary>
    public const string AlertSource = nameof(PaymentReversalService);

    private readonly IAppDbContext _db;
    private readonly ITransactionRunner _transactions;
    private readonly OrderRefunds _refunds;
    private readonly IPlatformScope _platformScope;
    private readonly IOutbox _outbox;
    private readonly IPlatformAlerter _alerter;
    private readonly IUniqueViolationDetector _uniqueViolations;
    private readonly TimeProvider _clock;
    private readonly ILogger<PaymentReversalService> _logger;

    public PaymentReversalService(
        IAppDbContext db,
        ITransactionRunner transactions,
        OrderRefunds refunds,
        IPlatformScope platformScope,
        IOutbox outbox,
        IPlatformAlerter alerter,
        IUniqueViolationDetector uniqueViolations,
        TimeProvider clock,
        ILogger<PaymentReversalService> logger)
    {
        _db = db;
        _transactions = transactions;
        _refunds = refunds;
        _platformScope = platformScope;
        _outbox = outbox;
        _alerter = alerter;
        _uniqueViolations = uniqueViolations;
        _clock = clock;
        _logger = logger;
    }

    public async Task<ReversalOutcome> ReverseAsync(PaymentReversalRequired request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        ReversalOutcome outcome;

        try
        {
            outcome = await _transactions.RunAsync(token => ReverseOnceAsync(request, token), cancellationToken);
        }
        catch (DbUpdateException ex) when (_uniqueViolations.IsUniqueViolation(ex))
        {
            // Another delivery, or the agent's own refund, got there first.
            _db.ChangeTracker.Clear();
            outcome = ReversalOutcome.AlreadyReversed;
        }

        switch (outcome)
        {
            case ReversalOutcome.NoEvidence:
                await _alerter.RaiseAsync(
                    new PlatformAlert(
                        AlertSeverity.P1,
                        "A payment reversal was asked for without evidence",
                        $"A reversal for supplier booking {request.SupplierBookingId} (order line {request.OrderLineId}) named status "
                        + $"poll {request.SupplierStatusPollId}, which does not record the supplier reporting 0, 1 or 11 for it. "
                        + "Nothing was refunded. Read the booking's supplier_status_polls rows before doing anything by hand.",
                        AlertSource,
                        request.AgencyId),
                    cancellationToken);
                break;

            case ReversalOutcome.NothingToReverse:
                await _alerter.RaiseAsync(
                    new PlatformAlert(
                        AlertSeverity.P2,
                        "A booking to be reversed had no wallet payment to give back",
                        $"The supplier reported status {request.SupplierStatusCode} for supplier booking {request.SupplierBookingId} "
                        + $"(order line {request.OrderLineId}), but no wallet hold or payment exists for its order. The line is in the "
                        + "agency's resolution queue; check how the order was paid.",
                        AlertSource,
                        request.AgencyId),
                    cancellationToken);
                break;

            default:
                break;
        }

        LogOutcome(_logger, request.SupplierBookingId, outcome);
        return outcome;
    }

    private async Task<ReversalOutcome> ReverseOnceAsync(PaymentReversalRequired request, CancellationToken cancellationToken)
    {
        var evidence = await _db.SupplierStatusPolls.AsNoTracking().SingleOrDefaultAsync(
            poll => poll.Id == request.SupplierStatusPollId && poll.SupplierBookingId == request.SupplierBookingId,
            cancellationToken);

        if (!IsEvidence(evidence))
        {
            return ReversalOutcome.NoEvidence;
        }

        if (await _db.Refunds.AnyAsync(refund => refund.OrderLineId == request.OrderLineId, cancellationToken))
        {
            return ReversalOutcome.AlreadyReversed;
        }

        var line = await _db.OrderLines.SingleAsync(candidate => candidate.Id == request.OrderLineId, cancellationToken);
        var order = await _db.Orders.Include(candidate => candidate.Lines).SingleAsync(candidate => candidate.Id == line.OrderId, cancellationToken);
        var now = _clock.GetUtcNow();

        // Open until the save: giving back money that had been taken posts to the platform's own ledger
        // accounts, which row-level security lets only a platform scope write. Everything above was read
        // by id, under the agency's own filter.
        using var scope = _platformScope.Enter(
            "payment reversal — gives a booking's money back to wherever it was paid from, through the ledger");

        var refund = await _refunds.ReturnAsync(
            order, line, RefundReason.SupplierReversal, now, supplierStatusPollId: evidence!.Id, cancellationToken: cancellationToken);

        FailedLines.FlagForResolution(
            order,
            line,
            $"The supplier reported status {request.SupplierStatusCode}: no ticket was issued. "
            + (refund is null
                ? "No payment was found to give back."
                : refund.Method == RefundMethod.Gateway
                    ? "The money for it has gone back to the card it was paid from."
                    : "The money for it has gone back to the wallet."),
            now,
            _outbox);

        if (refund is not null)
        {
            _outbox.Enqueue(
                new PaymentReversed(
                    order.AgencyId, order.Id, line.Id, refund.Id, refund.Method.ToString(), refund.AmountMinor.AmountMinor, refund.Currency, now),
                order.AgencyId);
        }

        await _db.SaveChangesAsync(cancellationToken);

        return refund is null ? ReversalOutcome.NothingToReverse : ReversalOutcome.Reversed;
    }

    /// <summary>A recorded answer of 0, 1 or 11 that the poller acted on by asking for a reversal.</summary>
    internal static bool IsEvidence(SupplierStatusPoll? poll) =>
        poll is
        {
            Outcome: SupplierPollOutcome.Answered,
            ActionTaken: SupplierPollAction.ReversalRequested,
            SupplierStatusCode: 0 or 1 or 11,
        };

    [LoggerMessage(Level = LogLevel.Information, Message = "Payment reversal for supplier booking {BookingId}: {Outcome}.")]
    private static partial void LogOutcome(ILogger logger, Guid bookingId, ReversalOutcome outcome);
}
