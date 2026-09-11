using System.Text.RegularExpressions;
using TripsAgent.Domain.Common;

namespace TripsAgent.Domain.Pricing;

/// <summary>
/// What a markup rule says: what it applies to, how much it adds, and when.
/// </summary>
/// <remarks>
/// <para>
/// A plain value, separate from the <see cref="MarkupRule"/> entity, so the engine can price from
/// a cached copy without a database — and so a unit test can build one in a line.
/// </para>
/// <para>
/// Percentages are basis points in an <see cref="int"/> (1000 = 10%), never a decimal. Same reason
/// money is a <see cref="long"/> of kobo: one exact representation, nothing to round by accident.
/// </para>
/// </remarks>
public sealed partial record MarkupRuleTerms
{
    /// <summary>
    /// 1000%. Far above any real markup; it exists to catch a units mistake — 10 typed where
    /// 1000 basis points was meant is harmless, 100000 typed where 1000 was meant is not.
    /// </summary>
    public const int MaxPercentBasisPoints = 100_000;

    /// <summary>Basis points in 100%.</summary>
    public const int BasisPointsPerWhole = BasisPoints.PerWhole;

    public const int MaxSupplierCodeLength = 40;

    public required MarkupScope Scope { get; init; }

    /// <summary>Required for <see cref="MarkupScope.ProductType"/> and <see cref="MarkupScope.Product"/>.</summary>
    public PricedProductType? ProductType { get; init; }

    /// <summary>Required for <see cref="MarkupScope.Product"/>, and only allowed there.</summary>
    public Guid? ProductId { get; init; }

    /// <summary>Required for <see cref="MarkupScope.Supplier"/>, and only allowed there.</summary>
    public string? SupplierCode { get; init; }

    /// <summary>The currency of <see cref="ValueMinor"/> and the caps. Only prices in it can match.</summary>
    public required string Currency { get; init; }

    public required MarkupCalculationType CalculationType { get; init; }

    /// <summary>For a percentage rule: basis points, 1000 = 10%.</summary>
    public int? PercentBasisPoints { get; init; }

    /// <summary>For a fixed rule: the amount added, in minor units.</summary>
    public Money? ValueMinor { get; init; }

    /// <summary>Optional floor on a percentage markup — "10%, but never less than ₦2,000".</summary>
    public Money? MinMarkupMinor { get; init; }

    /// <summary>Optional ceiling on a percentage markup — "10%, but never more than ₦50,000".</summary>
    public Money? MaxMarkupMinor { get; init; }

    /// <summary>Breaks ties between rules of the same scope. Higher wins. Never beats a narrower scope.</summary>
    public int Priority { get; init; }

    /// <summary>Whether this agency's sub-agents inherit the rule when they have none of their own.</summary>
    public bool AppliesToSubAgents { get; init; } = true;

    /// <summary>The first instant the rule applies. Inclusive.</summary>
    public required DateTimeOffset EffectiveFrom { get; init; }

    /// <summary>The instant it stops applying. Exclusive; null means until retired.</summary>
    public DateTimeOffset? EffectiveTo { get; init; }

    /// <summary>True when the rule applies at <paramref name="at"/>.</summary>
    public bool IsEffectiveAt(DateTimeOffset at) =>
        EffectiveFrom <= at && (EffectiveTo is not { } end || at < end);

    /// <summary>True when the rule is aimed at <paramref name="subject"/>.</summary>
    public bool Matches(PricingSubject subject)
    {
        ArgumentNullException.ThrowIfNull(subject);

        if (!string.Equals(Currency, subject.Currency, StringComparison.Ordinal))
        {
            return false;
        }

        return Scope switch
        {
            MarkupScope.Global => true,
            MarkupScope.Supplier => SupplierCode is not null
                                    && string.Equals(SupplierCode, subject.SupplierCode, StringComparison.Ordinal),
            MarkupScope.ProductType => ProductType == subject.ProductType,
            MarkupScope.Product => ProductId is not null
                                   && ProductId == subject.ProductId
                                   && ProductType == subject.ProductType,
            _ => false,
        };
    }

    /// <summary>
    /// The markup this rule adds to <paramref name="net"/>, after any caps.
    /// </summary>
    /// <remarks>
    /// A percentage that lands on half a kobo rounds up, and anything less rounds down — ordinary
    /// rounding, done in whole numbers so it is exact. The caps apply after rounding, so a floor
    /// of ₦2,000 means exactly ₦2,000.
    /// </remarks>
    public Money CalculateMarkup(Money net)
    {
        if (net.IsNegative)
        {
            throw new ArgumentOutOfRangeException(nameof(net), net.AmountMinor, "A net rate cannot be negative.");
        }

        var markup = CalculationType switch
        {
            MarkupCalculationType.Percentage => BasisPoints.Of(net, PercentBasisPoints ?? 0),
            MarkupCalculationType.Fixed => ValueMinor ?? Money.Zero,
            _ => throw new InvalidOperationException($"Unknown calculation type {CalculationType}."),
        };

        if (MinMarkupMinor is { } floor && markup.AmountMinor < floor.AmountMinor)
        {
            markup = floor;
        }

        if (MaxMarkupMinor is { } ceiling && markup.AmountMinor > ceiling.AmountMinor)
        {
            markup = ceiling;
        }

        return markup;
    }

    /// <summary>
    /// The rule in one line, for a quote's breakdown and the pricing screen: "10% of the net rate,
    /// at least NGN 2000.00" or "a fixed NGN 1500.00".
    /// </summary>
    /// <remarks>
    /// Stored on the quote as written at the time, so the explanation of a price never depends on
    /// re-reading a rule — or on this wording, should it change later.
    /// </remarks>
    public string Describe()
    {
        if (CalculationType == MarkupCalculationType.Fixed)
        {
            return $"a fixed {Currency} {(ValueMinor ?? Money.Zero).ToString()}";
        }

        var text = $"{BasisPoints.Format(PercentBasisPoints ?? 0)} of the net rate";

        if (MinMarkupMinor is { } floor)
        {
            text += $", at least {Currency} {floor.ToString()}";
        }

        if (MaxMarkupMinor is { } ceiling)
        {
            text += $", at most {Currency} {ceiling.ToString()}";
        }

        return text;
    }

    /// <summary>
    /// A normalised copy, or an <see cref="ArgumentException"/> saying what is wrong.
    /// </summary>
    /// <remarks>
    /// The messages reach the agent through the console, so they are written for a person who
    /// runs a travel business, not for a developer. The database enforces the same shape with
    /// CHECK constraints, so a row written by hand cannot skip these rules.
    /// </remarks>
    public MarkupRuleTerms Validated()
    {
        if (!Enum.IsDefined(Scope))
        {
            throw new ArgumentException($"Unknown rule scope {Scope}.", nameof(Scope));
        }

        if (!Enum.IsDefined(CalculationType))
        {
            throw new ArgumentException($"Unknown calculation type {CalculationType}.", nameof(CalculationType));
        }

        if (ProductType is { } type && !Enum.IsDefined(type))
        {
            throw new ArgumentException($"Unknown product type {type}.", nameof(ProductType));
        }

        var supplierCode = NormaliseSupplierCode(SupplierCode);

        switch (Scope)
        {
            case MarkupScope.Global:
                Forbid(ProductType is not null || ProductId is not null || supplierCode is not null,
                    "A rule for everything cannot also name a product type, product or supplier.");
                break;

            case MarkupScope.Supplier:
                Forbid(supplierCode is null, "A supplier rule needs the supplier it applies to.");
                Forbid(ProductType is not null || ProductId is not null,
                    "A supplier rule applies to everything from that supplier; it cannot also name a product.");
                break;

            case MarkupScope.ProductType:
                Forbid(ProductType is null, "A product-type rule needs the product type it applies to.");
                Forbid(ProductId is not null || supplierCode is not null,
                    "A product-type rule cannot also name a single product or a supplier.");
                break;

            case MarkupScope.Product:
                Forbid(ProductId is null || ProductId == Guid.Empty, "A product rule needs the product it applies to.");
                Forbid(ProductType is null, "A product rule needs the product's type.");
                Forbid(supplierCode is not null, "A product rule cannot also name a supplier.");
                break;
        }

        switch (CalculationType)
        {
            case MarkupCalculationType.Percentage:
                Forbid(PercentBasisPoints is null, "A percentage rule needs a percentage.");
                Forbid(PercentBasisPoints is < 0 or > MaxPercentBasisPoints,
                    $"A percentage markup must be between 0% and {MaxPercentBasisPoints / 100}%.");
                Forbid(ValueMinor is not null, "A percentage rule cannot also have a fixed amount.");
                break;

            case MarkupCalculationType.Fixed:
                Forbid(ValueMinor is null, "A fixed rule needs an amount.");
                Forbid(ValueMinor is { IsNegative: true }, "A markup cannot be negative.");
                Forbid(PercentBasisPoints is not null, "A fixed rule cannot also have a percentage.");

                // A floor or ceiling on a number that never moves would do nothing, and an agent
                // who set one would reasonably think it did something.
                Forbid(MinMarkupMinor is not null || MaxMarkupMinor is not null,
                    "Minimum and maximum caps only apply to percentage rules.");
                break;
        }

        Forbid(MinMarkupMinor is { IsNegative: true } || MaxMarkupMinor is { IsNegative: true },
            "A minimum or maximum markup cannot be negative.");

        Forbid(MinMarkupMinor is { } min && MaxMarkupMinor is { } max && min.AmountMinor > max.AmountMinor,
            "The minimum markup cannot be more than the maximum.");

        Forbid(EffectiveTo is { } end && end <= EffectiveFrom,
            "A rule must end after it starts.");

        return this with
        {
            SupplierCode = supplierCode,
            Currency = NormaliseCurrency(Currency),
        };
    }

    /// <summary>Upper-cases and checks an ISO 4217 code.</summary>
    public static string NormaliseCurrency(string currency)
    {
        var normalised = (currency ?? string.Empty).Trim().ToUpperInvariant();

        return CurrencyPattern().IsMatch(normalised)
            ? normalised
            : throw new ArgumentException($"'{currency}' is not an ISO 4217 currency code, e.g. 'NGN'.", nameof(currency));
    }

    /// <summary>Lower-cases and checks a supplier code; blank becomes null.</summary>
    public static string? NormaliseSupplierCode(string? supplierCode)
    {
        if (string.IsNullOrWhiteSpace(supplierCode))
        {
            return null;
        }

        var normalised = supplierCode.Trim().ToLowerInvariant();

        return normalised.Length <= MaxSupplierCodeLength && SupplierCodePattern().IsMatch(normalised)
            ? normalised
            : throw new ArgumentException(
                $"'{supplierCode}' is not a supplier code. Use lower-case letters, digits and underscores, e.g. 'trips_africa'.",
                nameof(supplierCode));
    }

    private static void Forbid(bool condition, string message)
    {
        if (condition)
        {
            throw new ArgumentException(message);
        }
    }

    [GeneratedRegex("^[A-Z]{3}$")]
    private static partial Regex CurrencyPattern();

    [GeneratedRegex("^[a-z0-9_]+$")]
    private static partial Regex SupplierCodePattern();
}
