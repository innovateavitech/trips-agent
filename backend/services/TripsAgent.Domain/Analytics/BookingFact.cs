using TripsAgent.Domain.Common;
using TripsAgent.Domain.Orders;

namespace TripsAgent.Domain.Analytics;

/// <summary>
/// What a sale counts as. The one place the words "sale", "refund" and "failure" are defined.
/// </summary>
/// <remarks>
/// These are written onto the fact row rather than worked out when a dashboard is drawn, for two
/// reasons. The aggregate queries stay a plain <c>SUM … WHERE is_sale</c>, which PostgreSQL can
/// index; and the definition lives in one testable place instead of being restated — slightly
/// differently each time — in every query that counts money.
/// </remarks>
public static class BookingFactRules
{
    /// <summary>
    /// True when this line is revenue: the order's money landed and the line was not given back.
    /// </summary>
    /// <remarks>
    /// An order at <see cref="OrderStatus.PendingPayment"/> is a hope, not a sale. A cancelled or
    /// refunded order has been handed back. Counting either would show an agency money it does not
    /// have — the same rule the operations dashboard already applies to platform sales.
    /// </remarks>
    public static bool CountsAsSale(OrderStatus orderStatus, FulfilmentStatus fulfilmentStatus) =>
        orderStatus is not (OrderStatus.PendingPayment or OrderStatus.Cancelled or OrderStatus.Refunded)
        && fulfilmentStatus is not (FulfilmentStatus.Cancelled or FulfilmentStatus.Refunded);

    /// <summary>True when the money went back to where it came from.</summary>
    public static bool CountsAsRefund(OrderStatus orderStatus, FulfilmentStatus fulfilmentStatus) =>
        orderStatus == OrderStatus.Refunded || fulfilmentStatus == FulfilmentStatus.Refunded;

    /// <summary>True when the sale was called off before it was fulfilled.</summary>
    public static bool CountsAsCancellation(OrderStatus orderStatus, FulfilmentStatus fulfilmentStatus) =>
        orderStatus == OrderStatus.Cancelled || fulfilmentStatus == FulfilmentStatus.Cancelled;

    /// <summary>True when the traveller paid and the supplier did not deliver.</summary>
    public static bool CountsAsFailure(FulfilmentStatus fulfilmentStatus) =>
        fulfilmentStatus == FulfilmentStatus.FailedNeedsResolution;
}

/// <summary>
/// <c>analytics.fact_bookings</c> — one row per order line, flattened for counting.
/// </summary>
/// <remarks>
/// <para>
/// <b>Derived, never authoritative.</b> Every figure here is a copy of something in
/// <c>orders</c>, and the copy is thrown away and made again whenever the rollup runs. Nothing
/// reads this table to decide anything: it exists so a dashboard can ask "what did March look
/// like" without dragging six OLTP tables through a join while an agent is trying to book a seat.
/// If this table and <c>orders</c> ever disagree, <c>orders</c> is right and this is a bug in the
/// rollup.
/// </para>
/// <para>
/// <b>Why <c>root_agency_id</c>.</b> A principal wants its whole network in one number. Carrying
/// the principal's id on every row means that is one indexed scan rather than a recursive walk up
/// the agency tree per row. Until the sub-agent network lands it equals <c>agency_id</c> for
/// everybody, which is exactly right for an agency with nobody beneath it.
/// </para>
/// <para>
/// <b>Why the day is stored.</b> <see cref="BookingDay"/> is the Lagos calendar day of
/// <see cref="OccurredAt"/> — see <see cref="LagosDay"/>. Storing it means the aggregates group by
/// a column rather than by an expression over a timestamp, so the grouping is indexable and the
/// time-zone conversion happens once, in C#, at the only boundary that does it.
/// </para>
/// </remarks>
public sealed class BookingFact : Entity, ITenantScoped
{
    private BookingFact()
    {
        Currency = string.Empty;
    }

    /// <summary>
    /// Flattens one order line. Pure: the same inputs always produce the same row.
    /// </summary>
    /// <param name="orderId">The order the line belongs to.</param>
    /// <param name="orderLineId">The line itself. The fact's natural key.</param>
    /// <param name="agencyId">Who sold it.</param>
    /// <param name="rootAgencyId">The principal at the top of that agency's tree.</param>
    /// <param name="occurredAt">
    /// When the sale happened: the order's <c>placed_at</c>, falling back to when the line was
    /// created for an order that has not been placed yet.
    /// </param>
    /// <param name="supplierId">The supplier behind the line, where there is one.</param>
    /// <param name="builtAt">When this row was made. Observability only; never grouped on.</param>
    public static BookingFact From(
        Guid orderId,
        Guid orderLineId,
        Guid agencyId,
        Guid rootAgencyId,
        DateTimeOffset occurredAt,
        OrderStatus orderStatus,
        FulfilmentStatus fulfilmentStatus,
        OrderLineItemType itemType,
        OrderChannel channel,
        BuyerType buyerType,
        Guid? supplierId,
        string currency,
        Money netAmountMinor,
        Money markupAmountMinor,
        Money taxAmountMinor,
        Money platformFeeMinor,
        Money grossAmountMinor,
        DateTimeOffset sourceUpdatedAt,
        DateTimeOffset builtAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(currency);

        return new BookingFact
        {
            // The line's own id, not a fresh one. A fact row is the line, seen from another angle,
            // and keying it this way makes "rebuild this day" an idempotent delete-and-insert
            // rather than a duplicate waiting to happen.
            Id = orderLineId,
            OrderId = orderId,
            OrderLineId = orderLineId,
            AgencyId = agencyId,
            RootAgencyId = rootAgencyId,
            OccurredAt = occurredAt,
            BookingDay = LagosDay.Of(occurredAt),
            OrderStatus = orderStatus,
            FulfilmentStatus = fulfilmentStatus,
            ItemType = itemType,
            Channel = channel,
            BuyerType = buyerType,
            SupplierId = supplierId,
            Currency = currency,
            NetAmountMinor = netAmountMinor,
            MarkupAmountMinor = markupAmountMinor,
            TaxAmountMinor = taxAmountMinor,
            PlatformFeeMinor = platformFeeMinor,
            GrossAmountMinor = grossAmountMinor,
            IsSale = BookingFactRules.CountsAsSale(orderStatus, fulfilmentStatus),
            IsRefunded = BookingFactRules.CountsAsRefund(orderStatus, fulfilmentStatus),
            IsCancelled = BookingFactRules.CountsAsCancellation(orderStatus, fulfilmentStatus),
            IsFailed = BookingFactRules.CountsAsFailure(fulfilmentStatus),
            SourceUpdatedAt = sourceUpdatedAt,
            BuiltAt = builtAt,
        };
    }

    /// <summary>The order this line sat on.</summary>
    public Guid OrderId { get; private set; }

    /// <summary>The line itself. Unique — one fact row per line, always.</summary>
    public Guid OrderLineId { get; private set; }

    /// <inheritdoc />
    public Guid AgencyId { get; private set; }

    /// <summary>The principal at the top of the selling agency's tree.</summary>
    public Guid RootAgencyId { get; private set; }

    /// <summary>When the sale happened, in UTC.</summary>
    public DateTimeOffset OccurredAt { get; private set; }

    /// <summary>The Lagos calendar day of <see cref="OccurredAt"/>. What every aggregate groups by.</summary>
    public DateOnly BookingDay { get; private set; }

    public OrderStatus OrderStatus { get; private set; }

    public FulfilmentStatus FulfilmentStatus { get; private set; }

    public OrderLineItemType ItemType { get; private set; }

    public OrderChannel Channel { get; private set; }

    public BuyerType BuyerType { get; private set; }

    /// <summary>The supplier behind the line. Null for an agent's own catalog product.</summary>
    public Guid? SupplierId { get; private set; }

    public string Currency { get; private set; }

    /// <summary>What Trips charged the agency. Margin — shown only to <c>margin.view</c>.</summary>
    public Money NetAmountMinor { get; private set; }

    /// <summary>What the agency added. Margin — shown only to <c>margin.view</c>.</summary>
    public Money MarkupAmountMinor { get; private set; }

    public Money TaxAmountMinor { get; private set; }

    /// <summary>The platform's cut, taken from the agency's markup. Margin.</summary>
    public Money PlatformFeeMinor { get; private set; }

    /// <summary>What was actually charged. The only figure a traveller ever sees.</summary>
    public Money GrossAmountMinor { get; private set; }

    /// <summary>Counted in sales and revenue. See <see cref="BookingFactRules.CountsAsSale"/>.</summary>
    public bool IsSale { get; private set; }

    public bool IsRefunded { get; private set; }

    public bool IsCancelled { get; private set; }

    /// <summary>Paid for, not delivered, and sitting in somebody's resolution queue.</summary>
    public bool IsFailed { get; private set; }

    /// <summary>
    /// The latest <c>updated_at</c> of the order and line this was built from.
    /// </summary>
    /// <remarks>
    /// The incremental rollup's watermark reads this: a day whose source rows changed after the
    /// last run is rebuilt from scratch. It is a property of the source, not of this row, which is
    /// why it is not <c>BuiltAt</c>.
    /// </remarks>
    public DateTimeOffset SourceUpdatedAt { get; private set; }

    /// <summary>When the rollup made this row.</summary>
    public DateTimeOffset BuiltAt { get; private set; }

    /// <summary>What the agency keeps: markup less the platform's fee.</summary>
    public Money AgentMarginMinor => MarkupAmountMinor - PlatformFeeMinor;
}
