using Microsoft.EntityFrameworkCore;
using TripsAgent.Application.Persistence;
using TripsAgent.Application.Tenancy;
using TripsAgent.Domain.Common;
using TripsAgent.Domain.Pricing;

namespace TripsAgent.Application.Pricing;

/// <summary>
/// Prices things for the calling agency: finds its markup rules (and its principal's), picks the
/// winner, and — when asked to quote — stores the result with the winning rule's id.
/// </summary>
/// <remarks>
/// <para>
/// The choosing is done by <see cref="MarkupEngine"/>, which is pure and unit-tested. This class
/// is the plumbing around it: whose rules, from where, and what gets written down.
/// </para>
/// <para>
/// <b>Reading the principal's rules crosses a tenant boundary</b>, and does it through
/// <see cref="IPlatformScope"/> with a stated reason, never by switching a filter off. It happens
/// only when the principal's rules are not already cached, and only ever for the calling agency's
/// own parent — the id comes from the caller's agency row, not from anything a request supplies.
/// </para>
/// </remarks>
public sealed class PricingService
{
    private readonly IAppDbContext _db;
    private readonly ITenantContext _tenant;
    private readonly IPlatformScope _platformScope;
    private readonly IMarkupRuleCache _cache;
    private readonly TimeProvider _clock;

    public PricingService(
        IAppDbContext db,
        ITenantContext tenant,
        IPlatformScope platformScope,
        IMarkupRuleCache cache,
        TimeProvider clock)
    {
        _db = db;
        _tenant = tenant;
        _platformScope = platformScope;
        _cache = cache;
        _clock = clock;
    }

    /// <summary>Works out what <paramref name="net"/> sells for, without recording anything.</summary>
    public async Task<PriceBreakdown> PriceAsync(
        PricingSubject subject,
        Money net,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(subject);

        var agencyId = RequireAgency();

        var own = await _cache.GetOrLoadAsync(
            agencyId, token => LoadOwnAsync(agencyId, token), cancellationToken);

        IReadOnlyList<MarkupRuleDefinition> candidates = own.Rules;

        if (own.ParentAgencyId is { } parentId)
        {
            var inherited = await _cache.GetOrLoadAsync(
                parentId, token => LoadParentAsync(parentId, token), cancellationToken);

            candidates = [.. own.Rules, .. inherited.Rules];
        }

        return MarkupEngine.Price(
            subject, net, _clock.GetUtcNow(), agencyId, own.ParentAgencyId, candidates);
    }

    /// <summary>
    /// Prices <paramref name="net"/> and stores the result as a quote, winning rule id included.
    /// </summary>
    /// <remarks>
    /// The quote is the record that makes a margin explainable later: it holds the figures as they
    /// were, and the id of the rule that produced them. Rules are never edited in place, so that
    /// id points at exactly the terms that were used, for as long as the quote exists.
    /// </remarks>
    public async Task<PriceQuote> QuoteAsync(
        PricingSubject subject,
        Money net,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(subject);

        var price = await PriceAsync(subject, net, cancellationToken);
        var quote = PriceQuote.Record(RequireAgency(), subject, price);

        _db.PriceQuotes.Add(quote);
        await _db.SaveChangesAsync(cancellationToken);

        return quote;
    }

    private Guid RequireAgency() =>
        _tenant.AgencyId ?? throw new InvalidOperationException(
            "Pricing needs an agency to price for, and none is resolved for this request. "
            + "Markup rules belong to an agency; without one there is no markup to apply.");

    /// <summary>The caller's own rules, through the ordinary tenant filter.</summary>
    private Task<MarkupRuleSet> LoadOwnAsync(Guid agencyId, CancellationToken cancellationToken) =>
        QueryAsync(agencyId, cancellationToken);

    /// <summary>The principal's rules, which the tenant filter would otherwise hide from a sub-agent.</summary>
    private async Task<MarkupRuleSet> LoadParentAsync(Guid parentId, CancellationToken cancellationToken)
    {
        using var _ = _platformScope.Enter(
            "markup inheritance — a sub-agent's price uses its own principal's markup rules");

        return await QueryAsync(parentId, cancellationToken);
    }

    private async Task<MarkupRuleSet> QueryAsync(Guid agencyId, CancellationToken cancellationToken)
    {
        var now = _clock.GetUtcNow();

        // The agency id is repeated in each query on purpose. Under the platform scope the filter
        // is off, and this predicate is then the only thing keeping the read to one agency.
        var agency = await _db.Agencies
            .AsNoTracking()
            .Where(candidate => candidate.Id == agencyId)
            .Select(candidate => new { candidate.ParentAgencyId })
            .SingleOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException($"Agency {agencyId} does not exist, so it has no markup rules.");

        // Every rule that has not ended — including ones that start later. The engine checks each
        // rule's window at the moment of pricing, so a cached set stays correct as time passes: a
        // rule starting in ten minutes is already in it, and applies from then.
        var rules = await _db.MarkupRules
            .AsNoTracking()
            .Where(rule => rule.AgencyId == agencyId && (rule.EffectiveTo == null || rule.EffectiveTo > now))
            .ToListAsync(cancellationToken);

        return new MarkupRuleSet(
            agencyId,
            agency.ParentAgencyId,
            rules.Select(rule => rule.ToDefinition()).ToList());
    }
}
