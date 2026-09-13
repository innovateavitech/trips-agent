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
        ArgumentNullException.ThrowIfNull(line);

        // A ticketed line is never sent here: only the supplier can say a real ticket is not real,
        // and it says so through the status poller.
        return line.FulfilmentStatus is FulfilmentStatus.Pending or FulfilmentStatus.Reserved or FulfilmentStatus.Confirming
            && Flag(order, line, reason, now, outbox);
    }

    /// <summary>
    /// Sends a line the agency itself called off to the resolution queue, paid or not — a cancelled
    /// group departure, which decision 12 refunds in full.
    /// </summary>
    /// <remarks>
    /// The one case where a <see cref="FulfilmentStatus.Confirmed"/> line may be flagged, and it is
    /// safe for exactly one reason: the agency hosts the product itself, so there is no supplier
    /// ticket to contradict and no ADR-0003 question to ask. A flight or a bus is never flagged this
    /// way, which is why the item type is checked here rather than trusted from the caller.
    /// </remarks>
    /// <returns>True when the line was flagged now; false when it had already moved on.</returns>
    public static bool FlagAgencyCancellation(Order order, OrderLine line, string reason, DateTimeOffset now, IOutbox outbox)
    {
        ArgumentNullException.ThrowIfNull(line);

        return line.ItemType is OrderLineItemType.Tour or OrderLineItemType.GroupDeparture
            && line.FulfilmentStatus is FulfilmentStatus.Pending
                or FulfilmentStatus.Reserved
                or FulfilmentStatus.Confirming
                or FulfilmentStatus.Confirmed
            && Flag(order, line, reason, now, outbox);
    }

    private static bool Flag(Order order, OrderLine line, string reason, DateTimeOffset now, IOutbox outbox)
    {
        ArgumentNullException.ThrowIfNull(order);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        ArgumentNullException.ThrowIfNull(outbox);

        var clipped = reason.Length <= 500 ? reason : reason[..500];

        line.RecordFulfilment(FulfilmentStatus.FailedNeedsResolution, now, clipped);
        order.ChangeStatus(OrderStatus.PartiallyFailed, now, clipped);

        outbox.Enqueue(
            new BookingNeedsResolution(order.AgencyId, order.Id, order.OrderNumber, line.Id, clipped, now),
            order.AgencyId);

        return true;
    }
}
