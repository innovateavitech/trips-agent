using TripsAgent.Domain.Common;

namespace TripsAgent.Domain.Orders;

/// <summary>
/// What an agency sold, once: a human-readable number, the lines, and the totals as they were.
/// </summary>
/// <remarks>
/// <para>
/// The totals are the sum of the lines at the moment of placing, stored rather than computed on
/// read — the same reason the lines snapshot their own money (CLAUDE.md rule 5). Nothing here
/// recalculates a price.
/// </para>
/// <para>
/// <see cref="OrderNumber"/> is gapless per agency, allocated by the document numbering from #47 in
/// the same transaction as the insert, so a rolled-back order leaves no hole in the sequence — an
/// auditor reads a missing number as a missing order.
/// </para>
/// </remarks>
public sealed class Order : Entity, IAuditableEntity, ITenantScoped
{
    private readonly List<OrderLine> _lines = [];
    private readonly List<OrderStatusHistory> _statusHistory = [];

    private Order()
    {
        OrderNumber = string.Empty;
        Currency = string.Empty;
    }

    /// <summary>Places an order for <paramref name="lines"/>, which are frozen from this moment.</summary>
    public static Order Place(
        Guid agencyId,
        string orderNumber,
        string currency,
        BuyerType buyerType,
        OrderChannel channel,
        Guid? customerId,
        IReadOnlyCollection<OrderLine> lines,
        DateTimeOffset now)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(agencyId, Guid.Empty);
        ArgumentException.ThrowIfNullOrWhiteSpace(orderNumber);
        ArgumentException.ThrowIfNullOrWhiteSpace(currency);
        ArgumentNullException.ThrowIfNull(lines);

        if (lines.Count == 0)
        {
            throw new ArgumentException("An order needs at least one line.", nameof(lines));
        }

        var money = currency.Trim().ToUpperInvariant();

        foreach (var line in lines)
        {
            if (line.AgencyId != agencyId)
            {
                throw new ArgumentException("Every line must belong to the agency placing the order.", nameof(lines));
            }

            // One order, one currency. Mixing them would make the totals meaningless, and nothing
            // in the product sells across currencies yet (open question 17).
            if (!string.Equals(line.Currency, money, StringComparison.Ordinal))
            {
                throw new ArgumentException($"Line currency {line.Currency} does not match the order's {money}.", nameof(lines));
            }
        }

        var order = new Order
        {
            AgencyId = agencyId,
            OrderNumber = orderNumber.Trim(),
            Currency = money,
            BuyerType = buyerType,
            Channel = channel,
            CustomerId = customerId,
            Status = OrderStatus.PendingPayment,
            PlacedAt = now,
        };

        foreach (var line in lines)
        {
            line.AttachTo(order.Id, now);
            order._lines.Add(line);
        }

        order.TotalNetMinor = Sum(lines, line => line.NetAmountMinor);
        order.TotalMarkupMinor = Sum(lines, line => line.MarkupAmountMinor);
        order.TotalTaxMinor = Sum(lines, line => line.TaxAmountMinor);
        order.TotalPlatformFeeMinor = Sum(lines, line => line.PlatformFeeMinor);
        order.TotalGrossMinor = Sum(lines, line => line.GrossAmountMinor);

        order._statusHistory.Add(
            OrderStatusHistory.Record(agencyId, order.Id, from: null, to: order.Status, now));

        return order;
    }

    /// <inheritdoc />
    public Guid AgencyId { get; private set; }

    /// <summary>Human-readable and gapless per agency, e.g. <c>ORD-2026-000142</c>.</summary>
    public string OrderNumber { get; private set; }

    /// <summary>Null for guest checkout: somebody who bought without an account (FRD §2.4 RS-2).</summary>
    public Guid? CustomerId { get; private set; }

    public BuyerType BuyerType { get; private set; }

    public OrderChannel Channel { get; private set; }

    public OrderStatus Status { get; private set; }

    public string Currency { get; private set; }

    public Money TotalNetMinor { get; private set; }

    public Money TotalMarkupMinor { get; private set; }

    public Money TotalTaxMinor { get; private set; }

    public Money TotalPlatformFeeMinor { get; private set; }

    /// <summary>What the buyer pays: net + markup + tax, across every line.</summary>
    public Money TotalGrossMinor { get; private set; }

    public DateTimeOffset? PlacedAt { get; private set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public IReadOnlyList<OrderLine> Lines => _lines;

    /// <summary>Every status this order has had, oldest first. Append-only.</summary>
    public IReadOnlyList<OrderStatusHistory> StatusHistory => _statusHistory;

    /// <summary>What the agency keeps once Trips' fee comes out of the markup.</summary>
    public Money AgentMarginMinor => TotalMarkupMinor - TotalPlatformFeeMinor;

    /// <summary>
    /// Moves the order on. The checkout saga (#42) owns which transitions make sense; this only
    /// refuses to reopen an order that is already finished, which no correct saga would ask for.
    /// </summary>
    public void ChangeStatus(OrderStatus next, DateTimeOffset now, string? reason = null, Guid? changedByUserId = null)
    {
        if (Status is OrderStatus.Cancelled or OrderStatus.Refunded)
        {
            throw new InvalidOperationException($"Order {OrderNumber} is {Status} and cannot become {next}.");
        }

        // Recorded here rather than by the caller: a trail that a saga can forget to write is a
        // trail that is missing exactly when somebody needs it.
        _statusHistory.Add(OrderStatusHistory.Record(AgencyId, Id, Status, next, now, reason, changedByUserId));

        Status = next;
        UpdatedAt = now;
    }

    /// <summary>
    /// Records which of the agency's customers made this booking. The CRM does it when the booking is
    /// recorded against the customer (FRD §2.8 RS-1). Once set it stays: a booking never moves from
    /// one customer to another.
    /// </summary>
    /// <exception cref="InvalidOperationException">The order already belongs to a different customer.</exception>
    public void LinkCustomer(Guid customerId)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(customerId, Guid.Empty);

        if (CustomerId == customerId)
        {
            return;
        }

        if (CustomerId is not null)
        {
            throw new InvalidOperationException($"Order {OrderNumber} already belongs to customer {CustomerId}.");
        }

        CustomerId = customerId;
    }

    private static Money Sum(IReadOnlyCollection<OrderLine> lines, Func<OrderLine, Money> pick)
    {
        var total = default(Money);

        foreach (var line in lines)
        {
            total += pick(line);
        }

        return total;
    }
}
