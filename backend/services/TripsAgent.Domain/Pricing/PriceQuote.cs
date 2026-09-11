using TripsAgent.Domain.Common;

namespace TripsAgent.Domain.Pricing;

/// <summary>
/// A price as it was worked out at one moment, kept so it can be explained later.
/// </summary>
/// <remarks>
/// <para>
/// Every figure is copied in, not recalculated from the rule on read. Combined with rules whose
/// terms never change, <see cref="MarkupRuleId"/> is always the complete answer to "why was the
/// markup this much?" — and a markup rule edited next month cannot move this quote's margin.
/// </para>
/// <para>
/// The database refuses a non-zero markup with no rule id, so an unexplained margin cannot be
/// written at all. Tax, the platform fee, FX, expiry and the full breakdown are added by #29.
/// </para>
/// </remarks>
public sealed class PriceQuote : Entity, IAuditableEntity, ITenantScoped
{
    private PriceQuote() => Currency = string.Empty;

    /// <summary>Records <paramref name="price"/>, worked out for <paramref name="subject"/>, as <paramref name="agencyId"/>'s.</summary>
    public static PriceQuote Record(Guid agencyId, PricingSubject subject, PriceBreakdown price)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(agencyId, Guid.Empty);
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(price);

        if (!string.Equals(subject.Currency, price.Currency, StringComparison.Ordinal))
        {
            throw new ArgumentException("The price is in a different currency from the thing priced.", nameof(price));
        }

        return new PriceQuote
        {
            AgencyId = agencyId,
            ProductType = subject.ProductType,
            ProductId = subject.ProductId,
            SupplierCode = subject.SupplierCode,
            Currency = price.Currency,
            NetAmountMinor = price.NetAmountMinor,
            MarkupAmountMinor = price.MarkupAmountMinor,
            GrossAmountMinor = price.GrossAmountMinor,
            MarkupRuleId = price.MarkupRuleId,
        };
    }

    public Guid AgencyId { get; private set; }

    public PricedProductType ProductType { get; private set; }

    public Guid? ProductId { get; private set; }

    public string? SupplierCode { get; private set; }

    public string Currency { get; private set; }

    /// <summary>What the agency pays. Never shown to a traveller, nor to staff without <c>margin.view</c>.</summary>
    public Money NetAmountMinor { get; private set; }

    /// <summary>What the agency adds. Same visibility as the net rate.</summary>
    public Money MarkupAmountMinor { get; private set; }

    /// <summary>What the traveller pays.</summary>
    public Money GrossAmountMinor { get; private set; }

    /// <summary>The rule that produced the markup. Null only when the markup is zero.</summary>
    public Guid? MarkupRuleId { get; private set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
