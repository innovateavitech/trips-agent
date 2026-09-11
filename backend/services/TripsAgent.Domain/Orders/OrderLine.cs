using TripsAgent.Domain.Common;
using TripsAgent.Domain.Pricing;

namespace TripsAgent.Domain.Orders;

/// <summary>
/// One thing bought, with the price it was bought at.
/// </summary>
/// <remarks>
/// <para>
/// Every money figure is COPIED from the <see cref="PriceQuote"/> the line was built from, never
/// recalculated (CLAUDE.md rule 5). A markup rule edited next month must not rewrite last month's
/// margin, so the quote id and the winning rule id travel with the line and a database trigger
/// refuses to change any money column once <see cref="PlacedAt"/> is set.
/// </para>
/// <para>
/// <see cref="SupplierBookingId"/> is filled in later, when the supplier actually books — the
/// booking row points back here through a unique <c>order_line_id</c>, so one line has at most one
/// supplier booking.
/// </para>
/// </remarks>
public sealed class OrderLine : Entity, IAuditableEntity, ITenantScoped
{
    private OrderLine()
    {
        Currency = string.Empty;
        TitleSnapshot = string.Empty;
        PaxBreakdown = "{}";
    }

    /// <summary>
    /// Builds a line from a quote, at <paramref name="now"/>.
    /// </summary>
    /// <exception cref="PriceQuoteExpiredException">
    /// The quote has expired. A price nobody re-confirmed is not a price we can sell at, which is
    /// the other half of issue #29 — checkout (#42) calls this and must not swallow it.
    /// </exception>
    public static OrderLine FromQuote(
        PriceQuote quote,
        string titleSnapshot,
        string paxBreakdown,
        DateTimeOffset now,
        Guid? productId = null,
        Guid? supplierOfferId = null)
    {
        ArgumentNullException.ThrowIfNull(quote);
        ArgumentException.ThrowIfNullOrWhiteSpace(titleSnapshot);
        ArgumentException.ThrowIfNullOrWhiteSpace(paxBreakdown);

        // Throws if the quote has expired: the price is no longer ours to honour.
        quote.EnsureUsableAt(now);

        return new OrderLine
        {
            AgencyId = quote.AgencyId,
            ItemType = ItemTypeFor(quote.ProductType),
            ProductId = productId ?? quote.ProductId,
            SupplierOfferId = supplierOfferId,
            PriceQuoteId = quote.Id,
            TitleSnapshot = titleSnapshot.Trim(),
            PaxBreakdown = paxBreakdown,
            Currency = quote.Currency,
            NetAmountMinor = quote.NetAmountMinor,
            MarkupAmountMinor = quote.MarkupAmountMinor,
            TaxAmountMinor = quote.TaxAmountMinor,
            PlatformFeeMinor = quote.PlatformFeeMinor,
            GrossAmountMinor = quote.GrossAmountMinor,
            MarkupRuleId = quote.MarkupRuleId,
            FulfilmentStatus = FulfilmentStatus.Pending,
        };
    }

    public Guid OrderId { get; private set; }

    /// <inheritdoc />
    public Guid AgencyId { get; private set; }

    public OrderLineItemType ItemType { get; private set; }

    /// <summary>The catalog product, for a tour or a visa. Null for a supplier-sourced seat.</summary>
    public Guid? ProductId { get; private set; }

    /// <summary>The supplier offer this line was priced from, when it came from a search.</summary>
    public Guid? SupplierOfferId { get; private set; }

    /// <summary>Filled in when the supplier booking exists. One line, at most one booking.</summary>
    public Guid? SupplierBookingId { get; private set; }

    /// <summary>The quote every figure below was copied from.</summary>
    public Guid PriceQuoteId { get; private set; }

    /// <summary>What the traveller bought, as it read at the time — the flight and route, or the tour's name.</summary>
    public string TitleSnapshot { get; private set; }

    /// <summary>Passengers by type, as JSON: adults, children, infants.</summary>
    public string PaxBreakdown { get; private set; }

    public string Currency { get; private set; }

    public Money NetAmountMinor { get; private set; }

    public Money MarkupAmountMinor { get; private set; }

    public Money TaxAmountMinor { get; private set; }

    /// <summary>Trips' cut. Taken out of the agency's margin, never added to what the traveller pays.</summary>
    public Money PlatformFeeMinor { get; private set; }

    /// <summary>What the traveller pays: net + markup + tax.</summary>
    public Money GrossAmountMinor { get; private set; }

    /// <summary>The rule that decided the markup, so the margin stays explainable.</summary>
    public Guid? MarkupRuleId { get; private set; }

    public FulfilmentStatus FulfilmentStatus { get; private set; }

    public string? FailureReason { get; private set; }

    public ResolutionStatus? ResolutionStatus { get; private set; }

    public Guid? ResolvedBy { get; private set; }

    public DateTimeOffset? ResolvedAt { get; private set; }

    /// <summary>When the order was placed. Once set, the money columns are frozen by the database.</summary>
    public DateTimeOffset? PlacedAt { get; private set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>The agency's margin on this line, after Trips' fee.</summary>
    public Money AgentMarginMinor => MarkupAmountMinor - PlatformFeeMinor;

    internal void AttachTo(Guid orderId, DateTimeOffset placedAt)
    {
        OrderId = orderId;
        PlacedAt = placedAt;
    }

    /// <summary>Records the supplier booking that fulfils this line.</summary>
    public void AttachSupplierBooking(Guid supplierBookingId)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(supplierBookingId, Guid.Empty);
        SupplierBookingId = supplierBookingId;
    }

    /// <summary>Moves the line's fulfilment on. A failure must say why, and opens a resolution.</summary>
    public void RecordFulfilment(FulfilmentStatus status, DateTimeOffset now, string? failureReason = null)
    {
        if (status == Orders.FulfilmentStatus.FailedNeedsResolution)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(failureReason);
            FailureReason = failureReason.Trim();
            ResolutionStatus ??= Orders.ResolutionStatus.Open;
        }

        FulfilmentStatus = status;
        UpdatedAt = now;
    }

    /// <summary>Records how a failed line was put right.</summary>
    public void Resolve(ResolutionStatus resolution, Guid resolvedBy, DateTimeOffset now)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(resolvedBy, Guid.Empty);
        ResolutionStatus = resolution;
        ResolvedBy = resolvedBy;
        ResolvedAt = now;
        UpdatedAt = now;
    }

    internal static OrderLineItemType ItemTypeFor(PricedProductType productType) => productType switch
    {
        PricedProductType.Flight => OrderLineItemType.Flight,
        PricedProductType.Bus => OrderLineItemType.Bus,
        PricedProductType.Tour => OrderLineItemType.Tour,
        PricedProductType.Visa => OrderLineItemType.Visa,
        PricedProductType.GroupDeparture => OrderLineItemType.GroupDeparture,
        PricedProductType.Package => OrderLineItemType.Package,
        _ => throw new ArgumentOutOfRangeException(nameof(productType), productType, "Unknown priced product type."),
    };
}
