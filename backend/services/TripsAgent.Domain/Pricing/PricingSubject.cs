namespace TripsAgent.Domain.Pricing;

/// <summary>
/// The thing being priced, described only as far as markup rules care about it.
/// </summary>
/// <remarks>
/// Deliberately not a flight offer or a catalog product. The engine must not care where a net rate
/// came from — that is what lets one set of rules price a Trips Africa fare and an agent-authored
/// tour the same way.
/// </remarks>
public sealed record PricingSubject
{
    public PricingSubject(
        PricedProductType productType,
        string currency,
        Guid? productId = null,
        string? supplierCode = null)
    {
        if (!Enum.IsDefined(productType))
        {
            throw new ArgumentOutOfRangeException(nameof(productType), productType, "Unknown product type.");
        }

        if (productId == Guid.Empty)
        {
            throw new ArgumentException("An empty product id identifies nothing; pass null instead.", nameof(productId));
        }

        ProductType = productType;
        Currency = MarkupRuleTerms.NormaliseCurrency(currency);
        ProductId = productId;
        SupplierCode = MarkupRuleTerms.NormaliseSupplierCode(supplierCode);
    }

    public PricedProductType ProductType { get; }

    /// <summary>ISO 4217, upper case. Only rules in the same currency can apply.</summary>
    public string Currency { get; }

    /// <summary>The catalog product, when there is one. Supplier fares have none.</summary>
    public Guid? ProductId { get; }

    /// <summary>Lower-case supplier code, e.g. <c>trips_africa</c>. Null for the agency's own products.</summary>
    public string? SupplierCode { get; }
}
