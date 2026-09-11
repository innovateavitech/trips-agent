namespace TripsAgent.Contracts.Pricing;

/// <summary>
/// A markup rule as the console writes it — to create one, or to replace one ("edit").
/// </summary>
/// <param name="Scope"><c>Global</c>, <c>Supplier</c>, <c>ProductType</c> or <c>Product</c>.</param>
/// <param name="ProductType">
/// <c>Flight</c>, <c>Bus</c>, <c>Tour</c>, <c>Package</c>, <c>Visa</c> or <c>GroupDeparture</c>. Required for the
/// <c>ProductType</c> and <c>Product</c> scopes, and only allowed there.
/// </param>
/// <param name="CalculationType"><c>Percentage</c> or <c>Fixed</c>.</param>
/// <param name="PercentBasisPoints">
/// For a percentage rule, in basis points: <c>1000</c> is 10%. A whole number so there is nothing
/// to round, the same reason money travels as minor units.
/// </param>
/// <param name="ValueMinor">For a fixed rule, the amount added in kobo. ₦2,000 is <c>200000</c>.</param>
/// <param name="MinMarkupMinor">Optional floor on a percentage markup, in kobo.</param>
/// <param name="MaxMarkupMinor">Optional ceiling on a percentage markup, in kobo.</param>
/// <param name="EffectiveFrom">
/// When the rule starts. Omitted, or in the past, means now: a rule cannot claim to have priced
/// sales that were actually priced by something else.
/// </param>
/// <param name="EffectiveTo">When the rule stops. Exclusive. Omitted means until retired.</param>
public sealed record MarkupRuleRequest(
    string Scope,
    string? ProductType,
    Guid? ProductId,
    string? SupplierCode,
    string Currency,
    string CalculationType,
    int? PercentBasisPoints,
    long? ValueMinor,
    long? MinMarkupMinor,
    long? MaxMarkupMinor,
    int Priority,
    bool AppliesToSubAgents,
    DateTimeOffset? EffectiveFrom,
    DateTimeOffset? EffectiveTo);

/// <summary>A markup rule as stored. Only ever shown to users holding <c>margin.view</c>.</summary>
/// <param name="SupersededById">The rule that replaced this one when it was edited, if it was.</param>
/// <param name="Status">
/// <c>Scheduled</c>, <c>InForce</c> or <c>Ended</c>, by the server's clock at the moment of the
/// response. Read this rather than comparing the timestamps with the browser's clock, which can
/// disagree with the server's by enough to show a rule just replaced as still in force.
/// </param>
public sealed record MarkupRuleResponse(
    Guid Id,
    string Scope,
    string? ProductType,
    Guid? ProductId,
    string? SupplierCode,
    string Currency,
    string CalculationType,
    int? PercentBasisPoints,
    long? ValueMinor,
    long? MinMarkupMinor,
    long? MaxMarkupMinor,
    int Priority,
    bool AppliesToSubAgents,
    DateTimeOffset EffectiveFrom,
    DateTimeOffset? EffectiveTo,
    Guid? SupersededById,
    string Status);

/// <summary>
/// One of the principal agency's rules that a sub-agent inherits. Read-only to the sub-agent.
/// </summary>
/// <remarks>
/// Only rules the principal marked as applying to sub-agents, and only those in force now: the
/// rules that can actually price one of the sub-agent's sales. Those kept from sub-agents are never
/// sent — they are the principal's own margin, not the sub-agent's.
/// </remarks>
/// <param name="Summary">The rule in one line: "10% of the net rate, at least NGN 2000.00".</param>
public sealed record InheritedMarkupRuleResponse(
    Guid Id,
    string Scope,
    string? ProductType,
    Guid? ProductId,
    string? SupplierCode,
    string Currency,
    string CalculationType,
    int? PercentBasisPoints,
    long? ValueMinor,
    long? MinMarkupMinor,
    long? MaxMarkupMinor,
    int Priority,
    DateTimeOffset EffectiveFrom,
    DateTimeOffset? EffectiveTo,
    string Summary);

/// <summary>
/// A price quote, for someone who may <b>not</b> see margin: only what the traveller pays, and
/// until when.
/// </summary>
/// <remarks>
/// <para>
/// A separate type from <see cref="PriceQuoteWithMarginResponse"/> rather than the same type with
/// the margin fields left null. A null field is still a field — it appears in the JSON, and one
/// mistaken assignment fills it. A type that has no such property cannot leak it.
/// </para>
/// <para>
/// The VAT is deliberately absent too. It is charged on the markup alone, so at a known rate the
/// tax figure <i>is</i> the markup, divided by 7.5%, and the markup gives away the net rate.
/// </para>
/// </remarks>
public sealed record PriceQuoteResponse(
    Guid Id,
    string ProductType,
    string Currency,
    long GrossAmountMinor,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt);

/// <summary>
/// A price quote for someone holding <c>margin.view</c>: the net rate, the markup and the rule that
/// produced it, the VAT and the platform's fee, as well as the price.
/// </summary>
/// <param name="MarkupRuleId">
/// The rule that decided the markup; null only when no rule applied and the markup is zero.
/// </param>
/// <param name="PlatformFeeMinor">Comes out of the agency's margin; it is not part of the gross.</param>
/// <param name="FxRate">Priced currency to settled currency. Always 1 while agencies sell in their base currency.</param>
public sealed record PriceQuoteWithMarginResponse(
    Guid Id,
    string ProductType,
    string Currency,
    long GrossAmountMinor,
    long NetAmountMinor,
    long MarkupAmountMinor,
    long TaxAmountMinor,
    long PlatformFeeMinor,
    decimal FxRate,
    Guid? MarkupRuleId,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt);

/// <summary>
/// "What would this sell for?" — a sample net price for the pricing screen's preview. Nothing is
/// stored; no quote is made.
/// </summary>
/// <param name="ProductType"><c>Flight</c>, <c>Bus</c>, <c>Tour</c>, <c>Package</c>, <c>Visa</c> or <c>GroupDeparture</c>.</param>
/// <param name="ProductId">A specific product, to see whether a product rule wins.</param>
/// <param name="SupplierCode">A supplier, e.g. <c>trips_africa</c>, to see whether a supplier rule wins.</param>
/// <param name="Currency">Omitted means the agency's base currency.</param>
/// <param name="NetAmountMinor">The sample net rate, in kobo. ₦85,000 is <c>8500000</c>.</param>
public sealed record PricePreviewRequest(
    string ProductType,
    Guid? ProductId,
    string? SupplierCode,
    string? Currency,
    long NetAmountMinor);

/// <summary>The rule that won a preview, with the terms that made it win.</summary>
/// <param name="Inherited">True when the rule is the principal's, applying because this agency has none that matches.</param>
/// <param name="Summary">The rule in one line: "10% of the net rate, at least NGN 2000.00".</param>
public sealed record PricePreviewRuleResponse(
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
    bool Inherited,
    string Summary);

/// <summary>
/// What a sample net price would sell for, and why. Only ever shown to <c>margin.view</c>.
/// </summary>
/// <param name="WinningRule">Null when no rule applies — the markup is then zero.</param>
/// <param name="TaxAmountMinor">VAT, charged on the markup. Part of the gross.</param>
/// <param name="PlatformFeeMinor">The platform's fee, taken from the markup. Not part of the gross.</param>
/// <param name="AgentMarginMinor">What the agency keeps: markup less the platform fee. Can be negative.</param>
public sealed record PricePreviewResponse(
    string Currency,
    long NetAmountMinor,
    long MarkupAmountMinor,
    int VatRateBasisPoints,
    long TaxAmountMinor,
    int PlatformFeeBasisPoints,
    long PlatformFeeMinor,
    long AgentMarginMinor,
    long GrossAmountMinor,
    PricePreviewRuleResponse? WinningRule);

/// <summary>The calling agency's pricing context: what it sells in, and the rates on every price.</summary>
/// <param name="HasPrincipal">
/// True for a sub-agent. Its principal's rules apply wherever none of its own rules matches — and
/// any matching rule of its own, even a default, beats every inherited one.
/// </param>
/// <param name="HasSubAgents">True for a principal with sub-agents, who inherit its rules unless told not to.</param>
public sealed record PricingSettingsResponse(
    string Currency,
    int VatRateBasisPoints,
    int PlatformFeeBasisPoints,
    int QuoteValidityMinutes,
    bool HasPrincipal,
    bool HasSubAgents);
