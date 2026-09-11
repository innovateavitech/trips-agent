using Microsoft.EntityFrameworkCore;
using TripsAgent.Application.Persistence;
using TripsAgent.Application.Tenancy;
using TripsAgent.Domain.Pricing;

namespace TripsAgent.Application.Pricing;

/// <summary>What came of a change to a markup rule.</summary>
public abstract record MarkupRuleChangeOutcome
{
    private MarkupRuleChangeOutcome()
    {
    }

    /// <summary>The change was saved. <paramref name="Rule"/> is the rule now in force.</summary>
    public sealed record Saved(MarkupRule Rule) : MarkupRuleChangeOutcome;

    /// <summary>The terms do not hold together. <paramref name="Reason"/> is written for the agent.</summary>
    public sealed record Invalid(string Reason) : MarkupRuleChangeOutcome;

    /// <summary>No rule with that id belongs to this agency.</summary>
    public sealed record NotFound : MarkupRuleChangeOutcome;

    /// <summary>The rule has already ended or been replaced, so it cannot be changed.</summary>
    public sealed record NoLongerEditable(string Reason) : MarkupRuleChangeOutcome;
}

/// <summary>
/// Creates, edits and retires the calling agency's markup rules — and keeps the cache honest.
/// </summary>
/// <remarks>
/// <para>
/// Every change ends with <see cref="IMarkupRuleCache.InvalidateAsync"/>, <b>after</b> the save
/// has committed. Invalidating first would let another request refill the cache from the old rows
/// in the gap before the commit, and the change would then be invisible until the entry expired.
/// </para>
/// <para>
/// "Editing" is <see cref="MarkupRule.ReplaceWith"/>: the old rule is retired and a new one takes
/// its place. Quotes that name the old rule keep pointing at exactly the terms that priced them.
/// </para>
/// </remarks>
public sealed class MarkupRuleService
{
    private readonly IAppDbContext _db;
    private readonly ITenantContext _tenant;
    private readonly IMarkupRuleCache _cache;
    private readonly TimeProvider _clock;

    public MarkupRuleService(IAppDbContext db, ITenantContext tenant, IMarkupRuleCache cache, TimeProvider clock)
    {
        _db = db;
        _tenant = tenant;
        _cache = cache;
        _clock = clock;
    }

    /// <summary>The agency's rules, newest first — current, future and past.</summary>
    public async Task<IReadOnlyList<MarkupRule>> ListAsync(CancellationToken cancellationToken = default) =>
        await _db.MarkupRules
            .AsNoTracking()
            .OrderByDescending(rule => rule.EffectiveFrom)
            .ThenByDescending(rule => rule.Id)
            .ToListAsync(cancellationToken);

    public async Task<MarkupRuleChangeOutcome> CreateAsync(
        MarkupRuleTerms terms,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(terms);

        var agencyId = RequireAgency();
        var now = _clock.GetUtcNow();

        MarkupRule rule;

        try
        {
            rule = MarkupRule.Create(agencyId, StartingNoEarlierThan(terms, now));
        }
        catch (ArgumentException ex)
        {
            return new MarkupRuleChangeOutcome.Invalid(ex.Message);
        }

        _db.MarkupRules.Add(rule);
        await _db.SaveChangesAsync(cancellationToken);
        await _cache.InvalidateAsync(agencyId, cancellationToken);

        return new MarkupRuleChangeOutcome.Saved(rule);
    }

    /// <summary>Edits a rule: retires <paramref name="ruleId"/> and saves its replacement.</summary>
    public async Task<MarkupRuleChangeOutcome> ReplaceAsync(
        Guid ruleId,
        MarkupRuleTerms newTerms,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(newTerms);

        var agencyId = RequireAgency();

        // The tenant filter makes another agency's rule look exactly like a missing one — which is
        // what it should look like.
        var existing = await _db.MarkupRules.FirstOrDefaultAsync(rule => rule.Id == ruleId, cancellationToken);

        if (existing is null)
        {
            return new MarkupRuleChangeOutcome.NotFound();
        }

        MarkupRule replacement;

        try
        {
            replacement = existing.ReplaceWith(newTerms, _clock.GetUtcNow());
        }
        catch (ArgumentException ex)
        {
            return new MarkupRuleChangeOutcome.Invalid(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return new MarkupRuleChangeOutcome.NoLongerEditable(ex.Message);
        }

        _db.MarkupRules.Add(replacement);
        await _db.SaveChangesAsync(cancellationToken);
        await _cache.InvalidateAsync(agencyId, cancellationToken);

        return new MarkupRuleChangeOutcome.Saved(replacement);
    }

    /// <summary>Stops a rule applying from now on.</summary>
    public async Task<MarkupRuleChangeOutcome> RetireAsync(Guid ruleId, CancellationToken cancellationToken = default)
    {
        var agencyId = RequireAgency();

        var existing = await _db.MarkupRules.FirstOrDefaultAsync(rule => rule.Id == ruleId, cancellationToken);

        if (existing is null)
        {
            return new MarkupRuleChangeOutcome.NotFound();
        }

        existing.Retire(_clock.GetUtcNow());

        await _db.SaveChangesAsync(cancellationToken);
        await _cache.InvalidateAsync(agencyId, cancellationToken);

        return new MarkupRuleChangeOutcome.Saved(existing);
    }

    private Guid RequireAgency() =>
        _tenant.AgencyId ?? throw new InvalidOperationException(
            "Markup rules belong to an agency, and none is resolved for this request.");

    /// <summary>
    /// A new rule starts now at the earliest. One dated in the past would claim to have priced
    /// sales that were really priced by whatever rule was in force at the time.
    /// </summary>
    private static MarkupRuleTerms StartingNoEarlierThan(MarkupRuleTerms terms, DateTimeOffset now) =>
        terms.EffectiveFrom >= now ? terms : terms with { EffectiveFrom = now };
}
