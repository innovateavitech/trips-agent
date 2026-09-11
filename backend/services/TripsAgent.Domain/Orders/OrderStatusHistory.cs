using TripsAgent.Domain.Common;

namespace TripsAgent.Domain.Orders;

/// <summary>
/// Every status an order has ever had, and when it changed.
/// </summary>
/// <remarks>
/// <para>
/// Append-only, and deliberately so: the application role is granted SELECT and INSERT on this
/// table and nothing else. A trail somebody can edit is not a trail. When a traveller disputes a
/// refund or an agent asks why an order cancelled itself at 3am, this is the answer.
/// </para>
/// <para>
/// Rows are written by <see cref="Order.Place"/> and <see cref="Order.ChangeStatus"/> rather than
/// by callers, so the trail cannot be forgotten by whoever moves the order next.
/// </para>
/// </remarks>
public sealed class OrderStatusHistory : Entity, ITenantScoped
{
    private OrderStatusHistory()
    {
    }

    internal static OrderStatusHistory Record(
        Guid agencyId,
        Guid orderId,
        OrderStatus? from,
        OrderStatus to,
        DateTimeOffset changedAt,
        string? reason = null,
        Guid? changedByUserId = null) =>
        new()
        {
            AgencyId = agencyId,
            OrderId = orderId,
            FromStatus = from,
            ToStatus = to,
            ChangedAt = changedAt,
            Reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim(),
            ChangedByUserId = changedByUserId,
        };

    /// <inheritdoc />
    public Guid AgencyId { get; private set; }

    public Guid OrderId { get; private set; }

    /// <summary>Null on the first entry — the order did not come from anywhere.</summary>
    public OrderStatus? FromStatus { get; private set; }

    public OrderStatus ToStatus { get; private set; }

    /// <summary>Why, when a person decided it. Null when a saga moved the order on its own.</summary>
    public string? Reason { get; private set; }

    /// <summary>Who, when a person did it. Null for anything the system did by itself.</summary>
    public Guid? ChangedByUserId { get; private set; }

    public DateTimeOffset ChangedAt { get; private set; }
}
