using System.Text.Json;

namespace TripsAgent.Domain.Pricing;

/// <summary>
/// Everything that went into a quote's price, written down at the moment it was worked out.
/// Stored as the quote's <c>breakdown</c> jsonb.
/// </summary>
/// <remarks>
/// <para>
/// The quote's own columns say <i>what</i> the figures were. This says <i>how</i>: which rule, what
/// that rule said, which VAT rate on which base, which fee rate on which base. It is deliberately
/// self-contained. A rule is never edited in place, so joining to it would give the same answer
/// today — but "today" is the only time anyone has checked that, and an explanation that needs no
/// join cannot be read differently later.
/// </para>
/// <para>
/// Money is plain minor units and enums are names, as everywhere outside the domain, so the JSON
/// reads the same in psql as it does here. <see cref="Version"/> says which shape this is, so a
/// later change can read old quotes without guessing.
/// </para>
/// </remarks>
public sealed record PriceQuoteBreakdown(
    int Version,
    string Currency,
    decimal FxRate,
    long NetAmountMinor,
    PriceQuoteBreakdown.RuleSnapshot? MarkupRule,
    bool MarkupRuleInherited,
    long MarkupAmountMinor,
    int VatRateBasisPoints,
    long VatBaseMinor,
    long TaxAmountMinor,
    int PlatformFeeBasisPoints,
    long PlatformFeeBaseMinor,
    long PlatformFeeMinor,
    long AgentMarginMinor,
    long GrossAmountMinor)
{
    public const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The terms of the winning rule, as they were — and the same in one readable line.</summary>
    public sealed record RuleSnapshot(
        Guid Id,
        string Scope,
        string? ProductType,
        Guid? ProductId,
        string? SupplierCode,
        string CalculationType,
        int? PercentBasisPoints,
        long? ValueMinor,
        long? MinMarkupMinor,
        long? MaxMarkupMinor,
        int Priority,
        string Summary);

    public static PriceQuoteBreakdown From(PriceBreakdown price)
    {
        ArgumentNullException.ThrowIfNull(price);

        return new PriceQuoteBreakdown(
            CurrentVersion,
            price.Currency,
            price.FxRate,
            price.NetAmountMinor.AmountMinor,
            price.MarkupRule is { } rule ? Snapshot(rule) : null,
            price.MarkupRuleInherited,
            price.MarkupAmountMinor.AmountMinor,
            price.VatRateBasisPoints,
            // VAT is charged on the markup alone — see MarkupEngine.Price for why, and why it is an
            // assumption. Stored as its own figure so the base is on record, not implied.
            price.MarkupAmountMinor.AmountMinor,
            price.TaxAmountMinor.AmountMinor,
            price.PlatformFeeBasisPoints,
            price.PlatformFeeBaseMinor.AmountMinor,
            price.PlatformFeeMinor.AmountMinor,
            price.AgentMarginMinor.AmountMinor,
            price.GrossAmountMinor.AmountMinor);
    }

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static PriceQuoteBreakdown FromJson(string json) =>
        JsonSerializer.Deserialize<PriceQuoteBreakdown>(json, Json)
        ?? throw new ArgumentException("A price breakdown cannot be empty.", nameof(json));

    private static RuleSnapshot Snapshot(MarkupRuleDefinition rule) =>
        new(
            rule.Id,
            rule.Terms.Scope.ToString(),
            rule.Terms.ProductType?.ToString(),
            rule.Terms.ProductId,
            rule.Terms.SupplierCode,
            rule.Terms.CalculationType.ToString(),
            rule.Terms.PercentBasisPoints,
            rule.Terms.ValueMinor?.AmountMinor,
            rule.Terms.MinMarkupMinor?.AmountMinor,
            rule.Terms.MaxMarkupMinor?.AmountMinor,
            rule.Terms.Priority,
            rule.Terms.Describe());
}
