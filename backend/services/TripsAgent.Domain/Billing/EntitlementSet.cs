namespace TripsAgent.Domain.Billing;

/// <summary>
/// Everything one agency is entitled to, resolved.
/// </summary>
/// <remarks>
/// <para>
/// Built once from the agency's subscription and its tier, then asked as many questions as the
/// caller likes. It is immutable and has no database of its own, so every rule below — what an
/// unlimited limit means, what happens when a tier grants nothing, how a downgrade behaves while an
/// agency is over its new ceiling — is testable with nothing but this type.
/// </para>
/// <para>
/// An entitlement the tier does not grant falls back to <see cref="EntitlementCatalog"/>'s default,
/// which is always the restrictive answer. That matters: the alternative is that forgetting to add
/// an entitlement to a tier silently gives every subscriber unlimited everything.
/// </para>
/// </remarks>
public sealed class EntitlementSet
{
    private readonly IReadOnlyDictionary<string, EntitlementValue> _values;

    private EntitlementSet(IReadOnlyDictionary<string, EntitlementValue> values, Guid? tierId, string? tierName)
    {
        _values = values;
        TierId = tierId;
        TierName = tierName;
    }

    /// <summary>What an agency with no live subscription gets: the catalogue's defaults.</summary>
    public static EntitlementSet Fallback { get; } = new(EntitlementCatalog.Fallbacks, null, null);

    /// <summary>
    /// Resolves a tier's grants over the catalogue's defaults.
    /// </summary>
    /// <param name="grants">What the tier grants, by entitlement code. Unknown codes are ignored.</param>
    public static EntitlementSet From(
        Guid tierId,
        string tierName,
        IEnumerable<KeyValuePair<string, EntitlementValue>> grants)
    {
        ArgumentNullException.ThrowIfNull(grants);

        var values = new Dictionary<string, EntitlementValue>(EntitlementCatalog.Fallbacks, StringComparer.Ordinal);

        foreach (var (code, value) in grants)
        {
            // An entitlement the catalogue does not define cannot be asked about, so storing it
            // would only invite a caller to check a code that will never be answered.
            if (values.ContainsKey(code))
            {
                values[code] = value;
            }
        }

        return new EntitlementSet(values, tierId, tierName);
    }

    /// <summary>The tier these came from, or null when this is the fallback set.</summary>
    public Guid? TierId { get; }

    /// <summary>The tier's name, for the sentence a refusal shows the agency.</summary>
    public string? TierName { get; }

    /// <summary>True when the agency has no live subscription and is on the catalogue's defaults.</summary>
    public bool IsFallback => TierId is null;

    /// <summary>What the agency is entitled to for <paramref name="code"/>.</summary>
    public EntitlementValue Value(string code) =>
        _values.TryGetValue(code, out var value) ? value : EntitlementCatalog.Fallback(code);

    /// <summary>True when the flag entitlement <paramref name="code"/> is switched on.</summary>
    public bool IsEnabled(string code) => Value(code).IsEnabled;

    /// <summary>The ceiling for the limit entitlement <paramref name="code"/>.</summary>
    public int Ceiling(string code) => Value(code).Ceiling;

    /// <summary>The basis points for the rate entitlement <paramref name="code"/>.</summary>
    public int BasisPoints(string code) => Value(code).BasisPoints;

    /// <summary>Every resolved entitlement, for a screen that lists them.</summary>
    public IReadOnlyDictionary<string, EntitlementValue> All => _values;

    /// <summary>
    /// May the agency use the feature behind the flag <paramref name="code"/>?
    /// </summary>
    public EntitlementDecision MayUse(string code)
    {
        var definition = EntitlementCatalog.Definition(code);

        if (definition.ValueType != EntitlementValueType.Flag)
        {
            throw new ArgumentException(
                $"'{code}' is a {definition.ValueType} entitlement. Use MayAdd for a limit.", nameof(code));
        }

        return Value(code).IsEnabled
            ? EntitlementDecision.Allowed(code)
            : EntitlementDecision.Refused(
                code,
                $"{definition.Name} is not part of {PlanName}.",
                limit: null,
                current: null);
    }

    /// <summary>
    /// May the agency add <paramref name="adding"/> more of whatever <paramref name="code"/> counts,
    /// given it already has <paramref name="current"/>?
    /// </summary>
    /// <remarks>
    /// <para>
    /// The caller supplies the count because the caller is the one that knows how to count it — the
    /// catalog counts published products, the sub-agent network counts agencies. Pushing that here
    /// would mean this type knowing about every feature that has a limit, which is the coupling the
    /// whole point of a single enforcement place is meant to avoid.
    /// </para>
    /// <para>
    /// <b>Already over the ceiling is a refusal, not a reset.</b> Build-plan decision 15: a downgrade
    /// keeps existing usage and blocks new usage. Nothing here deletes or hides anything — it only
    /// ever says no to one more.
    /// </para>
    /// </remarks>
    public EntitlementDecision MayAdd(string code, int current, int adding = 1)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(current);
        ArgumentOutOfRangeException.ThrowIfNegative(adding);

        var definition = EntitlementCatalog.Definition(code);

        if (definition.ValueType != EntitlementValueType.Limit)
        {
            throw new ArgumentException(
                $"'{code}' is a {definition.ValueType} entitlement. Use MayUse for a flag.", nameof(code));
        }

        var value = Value(code);

        if (value.IsUnlimited)
        {
            return EntitlementDecision.Allowed(code);
        }

        var ceiling = value.Ceiling;

        if (current + adding <= ceiling)
        {
            return EntitlementDecision.Allowed(code);
        }

        var detail = current >= ceiling
            ? $"{PlanName} allows {Describe(definition.Name, ceiling)}, and the agency already has {current}."
            : $"{PlanName} allows {Describe(definition.Name, ceiling)}; the agency has {current} and asked for {adding} more.";

        return EntitlementDecision.Refused(code, detail, ceiling, current);
    }

    private string PlanName => TierName is { Length: > 0 } name ? $"The {name} plan" : "The agency's current plan";

    private static string Describe(string name, int ceiling) =>
        ceiling == 1 ? $"1 {name.TrimEnd('s').ToLowerInvariant()}" : $"{ceiling} {name.ToLowerInvariant()}";
}

/// <summary>
/// The answer to "may this agency do this?".
/// </summary>
/// <param name="Code">The entitlement that was asked about.</param>
/// <param name="IsAllowed">Whether to go ahead.</param>
/// <param name="Detail">
/// Why not, in a sentence the agency can act on. Empty when allowed. Never mentions the internals of
/// a plan the agency is not on.
/// </param>
/// <param name="Limit">The ceiling that was hit, for a limit entitlement. Null otherwise.</param>
/// <param name="Current">How many the agency already had. Null for a flag.</param>
public sealed record EntitlementDecision(string Code, bool IsAllowed, string Detail, int? Limit, int? Current)
{
    public static EntitlementDecision Allowed(string code) => new(code, true, string.Empty, null, null);

    public static EntitlementDecision Refused(string code, string detail, int? limit, int? current) =>
        new(code, false, detail, limit, current);

    /// <summary>Throws when the agency may not. For a caller with nowhere useful to put a refusal.</summary>
    /// <exception cref="EntitlementRefusedException">The entitlement does not allow it.</exception>
    public void EnsureAllowed()
    {
        if (!IsAllowed)
        {
            throw new EntitlementRefusedException(this);
        }
    }
}

/// <summary>Thrown when an agency's plan does not allow what it just tried to do.</summary>
public sealed class EntitlementRefusedException : InvalidOperationException
{
    public EntitlementRefusedException(EntitlementDecision decision)
        : base(decision?.Detail ?? "The agency's plan does not allow this.") =>
        Code = decision?.Code ?? string.Empty;

    public EntitlementRefusedException()
        : base("The agency's plan does not allow this.") => Code = string.Empty;

    public EntitlementRefusedException(string message)
        : base(message) => Code = string.Empty;

    public EntitlementRefusedException(string message, Exception innerException)
        : base(message, innerException) => Code = string.Empty;

    /// <summary>The entitlement that refused.</summary>
    public string Code { get; }
}
