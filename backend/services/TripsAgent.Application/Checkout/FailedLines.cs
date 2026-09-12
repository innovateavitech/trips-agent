using TripsAgent.Application.Messaging;
using TripsAgent.Domain.Orders;

namespace TripsAgent.Application.Checkout;

/// <summary>
/// Sends a paid order line to the agent's resolution queue (#44) — the one way every path does it.
/// </summary>
/// <remarks>
/// A line that failed after payment is a state, not an exception: it never auto-refunds its customer
/// and never quietly rolls back. It waits, with its reason, for the agent to decide — and the traveller
/// can be told, through <see cref="BookingNeedsResolution"/>.
/// </remarks>
public static class FailedLines
{
    /// <returns>True when the line was flagged now; false when it had already moved on.</returns>
    public static bool FlagForResolution(Order order, OrderLine line, string reason, DateTimeOffset now, IOutbox outbox)
    {
        ArgumentNullException.ThrowIfNull(order);
        ArgumentNullException.ThrowIfNull(line);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        ArgumentNullException.ThrowIfNull(outbox);

        if (line.FulfilmentStatus is not (FulfilmentStatus.Pending or FulfilmentStatus.Reserved or FulfilmentStatus.Confirming))
        {
            return false;
        }

        var clipped = reason.Length <= 500 ? reason : reason[..500];

        line.RecordFulfilment(FulfilmentStatus.FailedNeedsResolution, now, clipped);
        order.ChangeStatus(OrderStatus.PartiallyFailed, now, clipped);

        outbox.Enqueue(
            new BookingNeedsResolution(order.AgencyId, order.Id, order.OrderNumber, line.Id, clipped, now),
            order.AgencyId);

        return true;
    }
}
