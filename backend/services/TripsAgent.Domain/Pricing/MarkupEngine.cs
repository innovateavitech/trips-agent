using TripsAgent.Domain.Common;

namespace TripsAgent.Domain.Pricing;

/// <summary>Which rule, if any, prices a subject — and whether it came from the parent agency.</summary>
public sealed record MarkupResolution(MarkupRuleDefinition? Rule, bool IsInherited)
{
    public static readonly MarkupResolution None = new(null, false);
}

/// <summary>
/// A worked-out price: what it costs the agency, what they add, and what the traveller pays.
/// </summary>
/// <param name="MarkupRuleId">
/// The rule that decided the markup, or null when none applied and the markup is zero. Stored on
/// every price so that a margin can always be traced back to the rule that produced it.
/// </param>
public sealed record PriceBreakdown(
    Money NetAmountMinor,
    Money MarkupAmountMinor,
    Money GrossAmountMinor,
    string Currency,
    Guid? MarkupRuleId,
    bool MarkupRuleInherited);

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
    public static PriceBreakdown Price(
        PricingSubject subject,
        Money net,
        DateTimeOffset at,
        Guid agencyId,
        Guid? parentAgencyId,
        IEnumerable<MarkupRuleDefinition> rules)
    {
        ArgumentNullException.ThrowIfNull(subject);

        if (net.IsNegative)
        {
            throw new ArgumentOutOfRangeException(nameof(net), net.AmountMinor, "A net rate cannot be negative.");
        }

        var resolution = Resolve(subject, at, agencyId, parentAgencyId, rules);
        var markup = resolution.Rule?.Terms.CalculateMarkup(net) ?? Money.Zero;

        return new PriceBreakdown(
            net,
            markup,
            net + markup,
            subject.Currency,
            resolution.Rule?.Id,
            resolution.IsInherited);
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
