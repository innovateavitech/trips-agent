using System.Globalization;
using TripsAgent.Domain.Common;

namespace TripsAgent.Domain.Billing;

/// <summary>What kind of answer an entitlement gives.</summary>
public enum EntitlementValueType
{
    /// <summary>Yes or no: may this agency use a custom domain at all?</summary>
    Flag = 1,

    /// <summary>A ceiling: how many sub-agents, how many catalog listings. <see cref="EntitlementValue.Unlimited"/> for no ceiling.</summary>
    Limit = 2,

    /// <summary>A share of a sale, in whole basis points — 1% is 100. Never a percentage as a decimal.</summary>
    Rate = 3,
}

/// <summary>
/// The value a tier gives for one entitlement.
/// </summary>
/// <remarks>
/// <para>
/// One type rather than three columns, because every place that reads an entitlement has to handle
/// "the tier does not grant this" the same way, and three nullable columns is three chances to
/// forget. The stored form is a JSON scalar — <c>true</c>, <c>25</c>, <c>-1</c>, <c>150</c> — which
/// is what the plan's <c>tier_entitlements.value jsonb</c> means.
/// </para>
/// <para>
/// A rate is basis points and nothing else. CLAUDE.md rule 2 is about money, and a percentage held
/// as <c>decimal</c> is the same mistake one step removed: 2.5% of ₦1,000,000 differs by real kobo
/// depending on where the rounding happens. <see cref="Pricing.BasisPoints"/> owns that rounding.
/// </para>
/// </remarks>
public readonly record struct EntitlementValue
{
    /// <summary>The <see cref="EntitlementValueType.Limit"/> value meaning "no ceiling".</summary>
    public const int Unlimited = -1;

    private EntitlementValue(EntitlementValueType type, long raw)
    {
        Type = type;
        Raw = raw;
    }

    /// <summary>Which of the three shapes this is.</summary>
    public EntitlementValueType Type { get; }

    /// <summary>
    /// The value, as one number: 0 or 1 for a flag, the ceiling for a limit, the basis points for a
    /// rate. Stored so a single column can hold any of them.
    /// </summary>
    public long Raw { get; }

    public static EntitlementValue Flag(bool enabled) => new(EntitlementValueType.Flag, enabled ? 1 : 0);

    /// <summary>A ceiling. Pass <see cref="Unlimited"/> for none; anything else must be zero or above.</summary>
    public static EntitlementValue Limit(int ceiling)
    {
        if (ceiling < Unlimited)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ceiling), ceiling, $"A limit is {Unlimited} for unlimited, or zero and above.");
        }

        return new EntitlementValue(EntitlementValueType.Limit, ceiling);
    }

    /// <summary>A share of a sale in basis points: 0 to 10,000 inclusive.</summary>
    public static EntitlementValue Rate(int basisPoints)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(basisPoints);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(basisPoints, Pricing.BasisPoints.PerWhole);

        return new EntitlementValue(EntitlementValueType.Rate, basisPoints);
    }

    /// <summary>True when this is a flag that is switched on.</summary>
    public bool IsEnabled => Type == EntitlementValueType.Flag && Raw == 1;

    /// <summary>True when this is a limit with no ceiling.</summary>
    public bool IsUnlimited => Type == EntitlementValueType.Limit && Raw == Unlimited;

    /// <summary>The ceiling, for a limit. <see cref="Unlimited"/> when there is none.</summary>
    public int Ceiling => Type == EntitlementValueType.Limit
        ? (int)Raw
        : throw new InvalidOperationException($"{Type} is not a limit, so it has no ceiling.");

    /// <summary>The basis points, for a rate.</summary>
    public int BasisPoints => Type == EntitlementValueType.Rate
        ? (int)Raw
        : throw new InvalidOperationException($"{Type} is not a rate, so it has no basis points.");

    /// <summary>The JSON scalar stored in <c>tier_entitlements.value</c>.</summary>
    public string ToJson() => Type switch
    {
        EntitlementValueType.Flag => Raw == 1 ? "true" : "false",
        _ => Raw.ToString(CultureInfo.InvariantCulture),
    };

    /// <summary>Reads back what <see cref="ToJson"/> wrote, given the entitlement's declared type.</summary>
    /// <exception cref="FormatException">The stored scalar does not match <paramref name="type"/>.</exception>
    public static EntitlementValue FromJson(EntitlementValueType type, string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);

        var text = json.Trim();

        if (type == EntitlementValueType.Flag)
        {
            return text switch
            {
                "true" => Flag(true),
                "false" => Flag(false),
                _ => throw new FormatException($"A flag entitlement is stored as true or false, not '{text}'."),
            };
        }

        if (!int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number))
        {
            throw new FormatException($"A {type} entitlement is stored as a whole number, not '{text}'.");
        }

        return type == EntitlementValueType.Limit ? Limit(number) : Rate(number);
    }

    /// <summary>For a screen and for the change log: "on", "25", "unlimited", "1.5%".</summary>
    public override string ToString() => Type switch
    {
        EntitlementValueType.Flag => Raw == 1 ? "on" : "off",
        EntitlementValueType.Limit => Raw == Unlimited ? "unlimited" : Raw.ToString(CultureInfo.InvariantCulture),
        _ => Pricing.BasisPoints.Format((int)Raw),
    };
}

/// <summary>
/// One thing a tier can grant, in the catalogue every tier picks from.
/// </summary>
/// <remarks>
/// Platform-owned reference data: there is no <c>agency_id</c> and no tenant filter, because the
/// catalogue is ours, not any agency's. Seeded from <see cref="EntitlementCatalog"/> so the codes a
/// feature checks against cannot drift from the rows in the table.
/// </remarks>
public sealed class Entitlement : Entity, IAuditableEntity
{
    private Entitlement()
    {
        Code = string.Empty;
        Name = string.Empty;
        Description = string.Empty;
    }

    public Entitlement(string code, string name, string description, EntitlementValueType valueType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        Code = code;
        Name = name;
        Description = description ?? string.Empty;
        ValueType = valueType;
    }

    /// <summary>The stable identifier a feature checks against — <c>custom_domain</c>, <c>max_sub_agents</c>.</summary>
    public string Code { get; private set; }

    /// <summary>What the tier builder calls it.</summary>
    public string Name { get; private set; }

    /// <summary>What it means, in a sentence, for the admin choosing a value.</summary>
    public string Description { get; private set; }

    public EntitlementValueType ValueType { get; private set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Refreshes what an admin reads about it. The code and the value type never change.</summary>
    public void Describe(string name, string description)
    {
        Name = name;
        Description = description;
    }
}
