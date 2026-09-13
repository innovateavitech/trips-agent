namespace TripsAgent.Domain.Billing;

/// <summary>
/// The entitlement codes features check against.
/// </summary>
/// <remarks>
/// Constants rather than strings at the call site, so a typo is a build error instead of a check
/// that silently never matches — and a check that never matches is a limit that is not enforced.
/// </remarks>
public static class EntitlementCodes
{
    /// <summary>How many agencies this one may run beneath it (F10). <see cref="EntitlementValueType.Limit"/>.</summary>
    public const string MaxSubAgents = "max_sub_agents";

    /// <summary>Whether the storefront may be served from the agency's own hostname (F4). <see cref="EntitlementValueType.Flag"/>.</summary>
    public const string CustomDomain = "custom_domain";

    /// <summary>
    /// The platform's share of each sale, in basis points. <see cref="EntitlementValueType.Rate"/>.
    /// </summary>
    /// <remarks>
    /// The plan calls this column <c>transaction_fee_pct</c>. The code says <c>_bps</c> because the
    /// value <i>is</i> basis points and naming it "pct" is how somebody eventually stores 2.5 where
    /// 250 belongs and undercharges every agency by a factor of a hundred. Same entitlement, honest
    /// name.
    /// </remarks>
    public const string TransactionFeeBasisPoints = "transaction_fee_bps";

    /// <summary>How many catalog products the agency may have published at once (F3). <see cref="EntitlementValueType.Limit"/>.</summary>
    public const string MaxCatalogListings = "max_catalog_listings";

    /// <summary>Whether the agency may run a loyalty programme (F13). <see cref="EntitlementValueType.Flag"/>.</summary>
    public const string LoyaltyProgram = "loyalty_program";

    /// <summary>Whether the agency may call the platform API directly. <see cref="EntitlementValueType.Flag"/>.</summary>
    public const string ApiAccess = "api_access";
}

/// <summary>One entitlement as it is seeded.</summary>
/// <param name="Code">From <see cref="EntitlementCodes"/>.</param>
/// <param name="Name">What the tier builder shows.</param>
/// <param name="Description">What it means, for the admin setting a value.</param>
/// <param name="ValueType">Which shape its value takes.</param>
/// <param name="Fallback">
/// What an agency gets with no subscription at all. The safe answer, never the generous one.
/// </param>
public sealed record EntitlementDefinition(
    string Code,
    string Name,
    string Description,
    EntitlementValueType ValueType,
    EntitlementValue Fallback);

/// <summary>
/// Every entitlement the platform knows about, and what an agency without a plan gets.
/// </summary>
/// <remarks>
/// <para>
/// This list is the source of truth; the <c>billing.entitlements</c> table is seeded from it. An
/// entitlement that is not here cannot be granted by a tier, so the set of questions a feature can
/// ask is fixed at build time and visible in one place.
/// </para>
/// <para>
/// <b>The fallbacks matter more than they look.</b> They are what an agency gets between signing up
/// and choosing a plan, and what it falls back to when dunning runs out. Every one of them is the
/// restrictive answer: no custom domain, no sub-agents, no API, no loyalty, and a single catalog
/// listing. The one that is not zero is the fee, and it is zero <i>because</i> charging a fee we
/// never agreed would come out of a real agency's margin. When in doubt about a fallback, the
/// question to ask is "if this is wrong, who loses money?"
/// </para>
/// </remarks>
public static class EntitlementCatalog
{
    /// <summary>The definitions, in the order the tier builder lists them.</summary>
    public static readonly IReadOnlyList<EntitlementDefinition> All =
    [
        new(
            EntitlementCodes.MaxSubAgents,
            "Sub-agents",
            "How many agencies this one may run beneath it.",
            EntitlementValueType.Limit,
            EntitlementValue.Limit(0)),
        new(
            EntitlementCodes.CustomDomain,
            "Custom domain",
            "Serve the storefront from the agency's own hostname rather than a Trips subdomain.",
            EntitlementValueType.Flag,
            EntitlementValue.Flag(false)),
        new(
            EntitlementCodes.TransactionFeeBasisPoints,
            "Transaction fee",
            "The platform's share of each sale, in basis points. It comes out of the agency's margin "
            + "and is never added to what a traveller pays.",
            EntitlementValueType.Rate,
            EntitlementValue.Rate(0)),
        new(
            EntitlementCodes.MaxCatalogListings,
            "Published listings",
            "How many tours, packages and visa products may be published at once.",
            EntitlementValueType.Limit,
            EntitlementValue.Limit(1)),
        new(
            EntitlementCodes.LoyaltyProgram,
            "Loyalty programme",
            "Whether the agency may run points and rewards for its own customers.",
            EntitlementValueType.Flag,
            EntitlementValue.Flag(false)),
        new(
            EntitlementCodes.ApiAccess,
            "API access",
            "Whether the agency may call the platform API directly from its own systems.",
            EntitlementValueType.Flag,
            EntitlementValue.Flag(false)),
    ];

    /// <summary>The definition for <paramref name="code"/>.</summary>
    /// <exception cref="ArgumentException">No entitlement has that code.</exception>
    public static EntitlementDefinition Definition(string code) =>
        All.SingleOrDefault(definition => definition.Code == code)
        ?? throw new ArgumentException($"'{code}' is not an entitlement. See EntitlementCodes.", nameof(code));

    /// <summary>What an agency with no subscription gets for <paramref name="code"/>.</summary>
    public static EntitlementValue Fallback(string code) => Definition(code).Fallback;

    /// <summary>The whole fallback set, for an agency with no subscription.</summary>
    public static IReadOnlyDictionary<string, EntitlementValue> Fallbacks =>
        All.ToDictionary(definition => definition.Code, definition => definition.Fallback, StringComparer.Ordinal);
}
