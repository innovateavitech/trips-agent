namespace TripsAgent.Contracts.Pricing;

/// <summary>
/// A markup rule as the console writes it — to create one, or to replace one ("edit").
/// </summary>
/// <param name="Scope"><c>Global</c>, <c>Supplier</c>, <c>ProductType</c> or <c>Product</c>.</param>
/// <param name="ProductType">
/// <c>Flight</c>, <c>Bus</c>, <c>Tour</c>, <c>Visa</c> or <c>GroupDeparture</c>. Required for the
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
    Guid? SupersededById);

/// <summary>
/// A price quote, for someone who may <b>not</b> see margin: only what the traveller pays.
/// </summary>
/// <remarks>
/// A separate type from <see cref="PriceQuoteWithMarginResponse"/> rather than the same type with
/// the margin fields left null. A null field is still a field — it appears in the JSON, and one
/// mistaken assignment fills it. A type that has no such property cannot leak it.
/// </remarks>
public sealed record PriceQuoteResponse(
    Guid Id,
    string ProductType,
    string Currency,
    long GrossAmountMinor,
    DateTimeOffset CreatedAt);

/// <summary>
/// A price quote for someone holding <c>margin.view</c>: the net rate, the markup and the rule that
/// produced it, as well as the price.
/// </summary>
/// <param name="MarkupRuleId">
/// The rule that decided the markup; null only when no rule applied and the markup is zero.
/// </param>
public sealed record PriceQuoteWithMarginResponse(
    Guid Id,
    string ProductType,
    string Currency,
    long GrossAmountMinor,
    long NetAmountMinor,
    long MarkupAmountMinor,
    Guid? MarkupRuleId,
    DateTimeOffset CreatedAt);
