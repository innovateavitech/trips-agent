using TripsAgent.Domain.Common;

namespace TripsAgent.Domain.Pricing;

/// <summary>
/// A price as it was worked out at one moment, kept so it can be explained later.
/// </summary>
/// <remarks>
/// <para>
/// Every figure is copied in, not recalculated from the rule on read. Combined with rules whose
/// terms never change, <see cref="MarkupRuleId"/> and <see cref="Breakdown"/> are always the
/// complete answer to "why was the price this much?" — and a markup rule edited next month cannot
/// move this quote's margin.
/// </para>
/// <para>
/// The database holds the arithmetic too: gross must equal net + markup + VAT, a non-zero markup
/// must name its rule, and a trigger refuses every UPDATE and DELETE, so none of it can be
/// rewritten after the fact (CLAUDE.md rule 5).
/// </para>
/// </remarks>
public sealed class PriceQuote : Entity, IAuditableEntity, ITenantScoped
{
    private PriceQuote()
    {
        Currency = string.Empty;
        Breakdown = string.Empty;
    }

    /// <summary>
    /// Records <paramref name="price"/>, worked out for <paramref name="subject"/> at
    /// <paramref name="pricedAt"/>, as <paramref name="agencyId"/>'s — good for <paramref name="validity"/>.
    /// </summary>
    public static PriceQuote Record(
        Guid agencyId,
        PricingSubject subject,
        PriceBreakdown price,
        DateTimeOffset pricedAt,
        TimeSpan validity)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(agencyId, Guid.Empty);
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(price);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(validity, TimeSpan.Zero);

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
            TaxAmountMinor = price.TaxAmountMinor,
            PlatformFeeMinor = price.PlatformFeeMinor,
            GrossAmountMinor = price.GrossAmountMinor,
            MarkupRuleId = price.MarkupRuleId,
            FxRateBillionths = ToBillionths(price.FxRate),
            Breakdown = PriceQuoteBreakdown.From(price).ToJson(),
            ExpiresAt = pricedAt + validity,
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

    /// <summary>
    /// VAT on the markup. Same visibility as the markup: at a known rate it gives the markup away.
    /// </summary>
    public Money TaxAmountMinor { get; private set; }

    /// <summary>What the platform takes — out of the agency's margin, not on top of the gross.</summary>
    public Money PlatformFeeMinor { get; private set; }

    /// <summary>What the traveller pays: net + markup + VAT.</summary>
    public Money GrossAmountMinor { get; private set; }

    /// <summary>The rule that produced the markup. Null only when the markup is zero.</summary>
    public Guid? MarkupRuleId { get; private set; }

    /// <summary>
    /// Priced currency to settled currency, in billionths: 1,000,000,000 is a rate of exactly 1,
    /// which is what every quote carries while agencies sell only in their base currency.
    /// </summary>
    /// <remarks>
    /// A whole number, like <see cref="Tenancy.Agency.VatRateBasisPoints"/>, rather than a
    /// <c>numeric</c> column: the model rules forbid <c>numeric</c> anywhere, because it is how
    /// fractions of a kobo creep in, and a rate that multiplies money earns no exemption. Nine
    /// places, because a naira-to-dollar rate is around 0.00065 and still needs its digits.
    /// </remarks>
    public long FxRateBillionths { get; private set; }

    /// <summary>The same rate as a <see cref="decimal"/>, for reading. Not stored separately.</summary>
    public decimal FxRate => FxRateBillionths / (decimal)FxRateScale;

    /// <summary>The whole calculation as JSON — see <see cref="PriceQuoteBreakdown"/>.</summary>
    public string Breakdown { get; private set; }

    /// <summary>
    /// The first instant this price may no longer be charged. A supplier's fare moves; a quote that
    /// lived for ever would let a traveller check out at last week's price.
    /// </summary>
    public DateTimeOffset ExpiresAt { get; private set; }

    /// <summary>Billionths in a rate of 1.</summary>
    public const long FxRateScale = 1_000_000_000;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>True from <see cref="ExpiresAt"/> onwards. The boundary itself is expired.</summary>
    public bool IsExpiredAt(DateTimeOffset at) => at >= ExpiresAt;

    /// <summary>
    /// Throws <see cref="PriceQuoteExpiredException"/> if the quote can no longer be charged at
    /// <paramref name="at"/>. Checkout (#42) calls this before it takes any money.
    /// </summary>
    public void EnsureUsableAt(DateTimeOffset at)
    {
        if (IsExpiredAt(at))
        {
            throw new PriceQuoteExpiredException(Id, ExpiresAt);
        }
    }

    /// <summary>The stored breakdown, read back.</summary>
    public PriceQuoteBreakdown ReadBreakdown() => PriceQuoteBreakdown.FromJson(Breakdown);

    /// <summary>
    /// A rate in billionths. Refuses one with more than nine places rather than truncating it:
    /// a silently shortened rate is a price nobody agreed to.
    /// </summary>
    private static long ToBillionths(decimal rate)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(rate, 0m);

        var scaled = rate * FxRateScale;

        return scaled == decimal.Truncate(scaled)
            ? checked((long)scaled)
            : throw new ArgumentException($"An FX rate may have at most nine decimal places; {rate} has more.", nameof(rate));
    }
}

/// <summary>
/// A quote was presented after it expired. The fix is always to price again — never to extend it.
/// </summary>
public sealed class PriceQuoteExpiredException : InvalidOperationException
{
    public PriceQuoteExpiredException(Guid quoteId, DateTimeOffset expiredAt)
        : base($"Price quote {quoteId} expired at {expiredAt:O}. Price it again for a current price.")
    {
        QuoteId = quoteId;
        ExpiredAt = expiredAt;
    }

    public Guid QuoteId { get; }

    public DateTimeOffset ExpiredAt { get; }
}
