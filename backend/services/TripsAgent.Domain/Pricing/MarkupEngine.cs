using TripsAgent.Domain.Common;

namespace TripsAgent.Domain.Pricing;

/// <summary>Which rule, if any, prices a subject — and whether it came from the parent agency.</summary>
public sealed record MarkupResolution(MarkupRuleDefinition? Rule, bool IsInherited)
{
    public static readonly MarkupResolution None = new(null, false);
}

/// <summary>
/// The rates that turn a marked-up price into a sell price: VAT, and the platform's fee.
/// </summary>
/// <param name="VatRateBasisPoints">The selling agency's VAT rate. 750 is Nigeria's 7.5%.</param>
/// <param name="PlatformFeeBasisPoints">
/// What Trips takes, from the agency's subscription tier. Zero until tiers exist (#64).
/// </param>
public sealed record PricingRates(int VatRateBasisPoints, int PlatformFeeBasisPoints)
{
    /// <summary>
    /// 100%. A VAT or fee rate above it is a units mistake — 750 meant, 7500 typed — not a policy.
    /// </summary>
    public const int MaxRateBasisPoints = BasisPoints.PerWhole;

    /// <summary>Throws unless both rates are between 0% and 100%.</summary>
    public PricingRates Validated()
    {
        ArgumentOutOfRangeException.ThrowIfNegative(VatRateBasisPoints);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(VatRateBasisPoints, MaxRateBasisPoints);
        ArgumentOutOfRangeException.ThrowIfNegative(PlatformFeeBasisPoints);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(PlatformFeeBasisPoints, MaxRateBasisPoints);
        return this;
    }
}

/// <summary>
/// A worked-out price: what it costs the agency, what they add, the VAT on it, what the traveller
/// pays — and what the platform takes out of the agency's side.
/// </summary>
/// <param name="MarkupRule">
/// The rule that decided the markup, or null when none applied and the markup is zero. Carried
/// whole, not just its id, so a quote can write down what the rule said as well as which it was.
/// </param>
/// <param name="TaxAmountMinor">VAT on the markup. Part of the gross.</param>
/// <param name="PlatformFeeMinor">
/// The platform's fee. <b>Not</b> part of the gross: it comes out of the agency's margin.
/// </param>
public sealed record PriceBreakdown(
    Money NetAmountMinor,
    Money MarkupAmountMinor,
    Money TaxAmountMinor,
    Money PlatformFeeMinor,
    Money GrossAmountMinor,
    string Currency,
    MarkupRuleDefinition? MarkupRule,
    bool MarkupRuleInherited,
    int VatRateBasisPoints,
    int PlatformFeeBasisPoints)
{
    /// <summary>
    /// Every price is in the agency's base currency for MVP (plan §7, open question 17), so the
    /// rate from the priced currency to the settled one is exactly one. Stored anyway, so the
    /// schema does not change when multi-currency arrives.
    /// </summary>
    public const decimal BaseCurrencyFxRate = 1m;

    /// <summary>Stored on every price so that a margin can always be traced back to its rule.</summary>
    public Guid? MarkupRuleId => MarkupRule?.Id;

    public decimal FxRate { get; } = BaseCurrencyFxRate;

    /// <summary>What the platform fee is a share of: the sell price before VAT.</summary>
    public Money PlatformFeeBaseMinor => NetAmountMinor + MarkupAmountMinor;

    /// <summary>
    /// What the agency keeps: its markup, less the platform's fee. Can be negative when a small
    /// markup meets a large fee — reported as it is, never hidden by clamping to zero.
    /// </summary>
    public Money AgentMarginMinor => MarkupAmountMinor - PlatformFeeMinor;
}

/// <summary>
/// Picks the markup rule for a price, and applies it. Pure: no database, no clock, no cache.
/// </summary>
/// <remarks>
/// <para>
/// <b>Resolution is deterministic.</b> Among the rules in force that match the subject:
/// </para>
/// <list type="number">
/// <item>the narrowest scope wins — product, then product type, then supplier, then global;</item>
/// <item>within a scope, the higher <see cref="MarkupRuleTerms.Priority"/> wins;</item>
/// <item>then the most recent <see cref="MarkupRuleTerms.EffectiveFrom"/>;</item>
/// <item>then the larger id — so two otherwise identical rules still give one answer, every time.</item>
/// </list>
/// <para>
/// Priority never crosses scopes. A global rule at priority 1,000 still loses to a product rule at
/// priority 0: an agent who wrote a rule for one tour meant that tour, whatever else they wrote.
/// </para>
/// <para>
/// <b>Sub-agents.</b> A sub-agent's own rules are consulted first. Only if none of them matches
/// does it inherit its parent's — and only the parent's rules marked
/// <see cref="MarkupRuleTerms.AppliesToSubAgents"/>. Writing any rule that matches is how a
/// sub-agent overrides; a sub-agent's global rule therefore overrides even the parent's
/// product-specific rules, because it is the sub-agent's own decision about its own prices.
/// </para>
/// </remarks>
public static class MarkupEngine
{
    /// <summary>The rule that applies to <paramref name="subject"/> at <paramref name="at"/>.</summary>
    /// <param name="agencyId">The agency doing the selling.</param>
    /// <param name="parentAgencyId">Its principal, if it is a sub-agent. Null or equal to <paramref name="agencyId"/> for a principal.</param>
    /// <param name="rules">
    /// Candidate rules. Rules belonging to any other agency are ignored, so passing too many is
    /// safe; passing too few is the caller's bug.
    /// </param>
    public static MarkupResolution Resolve(
        PricingSubject subject,
        DateTimeOffset at,
        Guid agencyId,
        Guid? parentAgencyId,
        IEnumerable<MarkupRuleDefinition> rules)
    {
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(rules);

        var inForce = rules
            .Where(rule => rule.Terms.IsEffectiveAt(at) && rule.Terms.Matches(subject))
            .ToList();

        var own = Best(inForce.Where(rule => rule.AgencyId == agencyId));

        if (own is not null)
        {
            return new MarkupResolution(own, IsInherited: false);
        }

        if (parentAgencyId is { } parent && parent != agencyId)
        {
            var inherited = Best(inForce.Where(rule => rule.AgencyId == parent && rule.Terms.AppliesToSubAgents));

            if (inherited is not null)
            {
                return new MarkupResolution(inherited, IsInherited: true);
            }
        }

        return MarkupResolution.None;
    }

    /// <summary>Resolves the rule and works out the full price of <paramref name="net"/>.</summary>
    /// <remarks>
    /// <para>
    /// <b>gross = net + markup + VAT.</b> The platform fee is worked out and recorded, but it is
    /// taken from the agency's margin, never added to what the traveller pays (plan §7, open
    /// question 4): adding it would make agencies on cheaper tiers visibly dearer to their own
    /// customers.
    /// </para>
    /// <para>
    /// <b>VAT is charged on the markup only — an assumption, not a settled rule.</b> The supplier's
    /// net fare already carries the airline's own taxes; what the agency adds is its service, and
    /// that is the new taxable supply. Who the merchant of record for VAT is remains open (plan §7,
    /// open question 25); if it turns out VAT is due on the whole sell price, this line and the
    /// quote CHECK change together. One consequence to know: VAT on the markup alone reveals the
    /// markup (tax ÷ rate), so the tax figure is margin and is shown only to <c>margin.view</c>.
    /// </para>
    /// <para>
    /// The platform fee is a share of the sell price before VAT — the agency's turnover on the
    /// sale, not a tax the agency collects for the state.
    /// </para>
    /// </remarks>
    public static PriceBreakdown Price(
        PricingSubject subject,
        Money net,
        DateTimeOffset at,
        Guid agencyId,
        Guid? parentAgencyId,
        IEnumerable<MarkupRuleDefinition> rules,
        PricingRates rates)
    {
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(rates);

        if (net.IsNegative)
        {
            throw new ArgumentOutOfRangeException(nameof(net), net.AmountMinor, "A net rate cannot be negative.");
        }

        rates.Validated();

        var resolution = Resolve(subject, at, agencyId, parentAgencyId, rules);
        var markup = resolution.Rule?.Terms.CalculateMarkup(net) ?? Money.Zero;
        var tax = BasisPoints.Of(markup, rates.VatRateBasisPoints);
        var platformFee = BasisPoints.Of(net + markup, rates.PlatformFeeBasisPoints);

        return new PriceBreakdown(
            net,
            markup,
            tax,
            platformFee,
            net + markup + tax,
            subject.Currency,
            resolution.Rule,
            resolution.IsInherited,
            rates.VatRateBasisPoints,
            rates.PlatformFeeBasisPoints);
    }

    /// <summary>How narrow a scope is. Higher wins. Spelled out so the enum's numbering cannot matter.</summary>
    public static int Precedence(MarkupScope scope) => scope switch
    {
        MarkupScope.Product => 4,
        MarkupScope.ProductType => 3,
        MarkupScope.Supplier => 2,
        MarkupScope.Global => 1,
        _ => 0,
    };

    private static MarkupRuleDefinition? Best(IEnumerable<MarkupRuleDefinition> rules) =>
        rules
            .OrderByDescending(rule => Precedence(rule.Terms.Scope))
            .ThenByDescending(rule => rule.Terms.Priority)
            .ThenByDescending(rule => rule.Terms.EffectiveFrom)
            .ThenByDescending(rule => rule.Id)
            .FirstOrDefault();
}
