using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TripsAgent.Application.Messaging;
using TripsAgent.Application.Persistence;
using TripsAgent.Domain.Orders;
using TripsAgent.Domain.Payments;

namespace TripsAgent.Application.Checkout;

/// <summary>What an agent chose for a failed booking. Substituting is a new search, not a call here.</summary>
public enum ResolutionChoice
{
    Retry = 1,
    Refund = 2,
}

/// <summary>
/// Carries out an agent's decision about an order line waiting in the resolution queue (#44).
/// </summary>
/// <remarks>
/// <para>
/// <b>Refund</b> gives back whatever wallet money is still held or was taken for the line — unless a
/// supplier reversal already did — and closes the line as resolved-refunded. The order's status trail
/// records who decided and what happened, and the order line's change is written to the audit trail.
/// </para>
/// <para>
/// <b>Retry is refused, honestly.</b> A line lands here because the supplier released its fare or said no
/// ticket will come. Asking the supplier to issue again is never done (ADR-0003); asking it for the fare
/// again needs a fresh price confirmation — which is what Substitute starts, from a new search.
/// </para>
/// </remarks>
public sealed partial class ResolutionService
{
    private readonly IAppDbContext _db;
    private readonly ITransactionRunner _transactions;
    private readonly WalletRefunds _refunds;
    private readonly IOutbox _outbox;
    private readonly IUniqueViolationDetector _uniqueViolations;
    private readonly TimeProvider _clock;
    private readonly ILogger<ResolutionService> _logger;

    public ResolutionService(
        IAppDbContext db,
        ITransactionRunner transactions,
        WalletRefunds refunds,
        IOutbox outbox,
        IUniqueViolationDetector uniqueViolations,
        TimeProvider clock,
        ILogger<ResolutionService> logger)
    {
        _db = db;
        _transactions = transactions;
        _refunds = refunds;
        _outbox = outbox;
        _uniqueViolations = uniqueViolations;
        _clock = clock;
        _logger = logger;
    }

    /// <exception cref="CheckoutRefusedException">No such booking, it no longer needs a decision, or the choice cannot be carried out.</exception>
    public async Task ResolveAsync(string reference, ResolutionChoice choice, Guid decidedByUserId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);
        ArgumentOutOfRangeException.ThrowIfEqual(decidedByUserId, Guid.Empty);

        if (choice == ResolutionChoice.Retry)
        {
            throw new CheckoutRefusedException(
                CheckoutRefusal.Conflict,
                "This booking cannot be tried again as it is.",
                "The supplier has released this fare, so asking again needs a fresh price. Choose Substitute to find a new "
                + "fare for the same trip, or Refund.");
        }

        try
        {
            await _transactions.RunAsync(token => RefundAsync(reference.Trim(), decidedByUserId, token), cancellationToken);
        }
        catch (DbUpdateException ex) when (_uniqueViolations.IsUniqueViolation(ex))
        {
            // A supplier reversal refunded the line at this very moment. Nothing moved twice; decide again.
            _db.ChangeTracker.Clear();
            throw new CheckoutRefusedException(
                CheckoutRefusal.Conflict,
                "That booking changed while you were deciding.",
                "Nothing was refunded twice. Open it again to see where it stands.");
        }
    }

    private async Task<bool> RefundAsync(string reference, Guid decidedByUserId, CancellationToken cancellationToken)
    {
        var order = await _db.Orders.Include(candidate => candidate.Lines)
            .SingleOrDefaultAsync(candidate => candidate.OrderNumber == reference, cancellationToken)
            ?? throw new CheckoutRefusedException(
                CheckoutRefusal.NotFound,
                "We could not find that booking.",
                "It may belong to another agency, or the reference may be mistyped.");

        var line = order.Lines.FirstOrDefault(candidate =>
                candidate.FulfilmentStatus == FulfilmentStatus.FailedNeedsResolution
                && candidate.ResolutionStatus is ResolutionStatus.Open or ResolutionStatus.InProgress)
            ?? throw new CheckoutRefusedException(
                CheckoutRefusal.Conflict,
                "That booking no longer needs a decision.",
                "Someone may have resolved it already.");

        var now = _clock.GetUtcNow();

        // A supplier reversal may already have given the money back; then there is nothing left to move.
        var refund = await _db.Refunds.AnyAsync(candidate => candidate.OrderLineId == line.Id, cancellationToken)
            ? null
            : await _refunds.ReturnAsync(order, line, RefundReason.AgentResolution, now, refundedByUserId: decidedByUserId, cancellationToken: cancellationToken);

        var note = refund is null
            ? "Closed and refunded: the money for it had already gone back to the wallet."
            : $"Refunded {order.Currency} {refund.AmountMinor.AmountMinor} (in kobo) to the wallet.";

        line.Resolve(ResolutionStatus.ResolvedRefunded, decidedByUserId, now);
        line.RecordFulfilment(FulfilmentStatus.Refunded, now);
        order.ChangeStatus(OrderStatus.Refunded, now, note, decidedByUserId);

        _outbox.Enqueue(
            new BookingResolved(order.AgencyId, order.Id, order.OrderNumber, line.Id, nameof(ResolutionStatus.ResolvedRefunded), decidedByUserId, now),
            order.AgencyId);

        if (refund is not null)
        {
            _outbox.Enqueue(
                new PaymentReversed(order.AgencyId, order.Id, line.Id, refund.Id, refund.Method.ToString(), refund.AmountMinor.AmountMinor, refund.Currency, now),
                order.AgencyId);
        }

        await _db.SaveChangesAsync(cancellationToken);

        LogResolved(_logger, order.OrderNumber, decidedByUserId);
        return true;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Booking {Reference} resolved as refunded by user {UserId}.")]
    private static partial void LogResolved(ILogger logger, string reference, Guid userId);
}
