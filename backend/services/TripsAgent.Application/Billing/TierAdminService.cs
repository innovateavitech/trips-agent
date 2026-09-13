using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TripsAgent.Application.Auditing;
using TripsAgent.Application.Persistence;
using TripsAgent.Application.Tenancy;
using TripsAgent.Domain.Billing;
using TripsAgent.Domain.Common;

namespace TripsAgent.Application.Billing;

/// <summary>What a tier looks like to the back office.</summary>
/// <param name="Subscribers">How many agencies are on it right now. Zero is what makes a tier deletable.</param>
public sealed record TierView(
    Guid Id,
    string Code,
    string Name,
    string? CustomerDescription,
    TierStatus Status,
    int TrialDays,
    int SortOrder,
    bool IsFallback,
    int Subscribers,
    DateTimeOffset? PublishedAt,
    DateTimeOffset? ArchivedAt,
    IReadOnlyList<TierPriceView> Prices,
    IReadOnlyList<TierEntitlementView> Entitlements);

/// <summary>One price on a tier.</summary>
public sealed record TierPriceView(
    Guid Id,
    string Currency,
    BillingInterval Interval,
    long AmountMinor,
    bool IsPromotional,
    DateTimeOffset EffectiveFrom,
    DateTimeOffset? EffectiveTo);

/// <summary>One entitlement, as a tier grants it.</summary>
public sealed record TierEntitlementView(
    string Code,
    string Name,
    EntitlementValueType ValueType,
    string Value,
    string Display);

/// <summary>What an admin is asking a tier to become.</summary>
public sealed record TierDraft(
    string Code,
    string Name,
    string? CustomerDescription,
    int TrialDays,
    int SortOrder,
    bool IsFallback);

/// <summary>One entitlement an admin is setting on a tier.</summary>
public sealed record EntitlementGrant(string Code, string Value);

/// <summary>One agency on a plan, as the back office's subscriber list shows it.</summary>
/// <param name="OutstandingMinor">What this agency owes Trips right now, in minor units.</param>
public sealed record SubscriberView(
    Guid AgencyId,
    string AgencyName,
    string AgencyStatus,
    Guid SubscriptionId,
    string TierName,
    SubscriptionStatus Status,
    string Currency,
    long? AmountMinor,
    DateTimeOffset CurrentPeriodStart,
    DateTimeOffset CurrentPeriodEnd,
    DateTimeOffset? TrialEndsAt,
    int DunningRetries,
    DateTimeOffset? NextDunningAttemptAt,
    long OutstandingMinor);

/// <summary>How a tier change went.</summary>
public abstract record TierChangeOutcome
{
    private TierChangeOutcome() { }

    /// <summary>It worked. <paramref name="SubscribersScheduled"/> is how many were told they are moving.</summary>
    public sealed record Saved(TierView Tier, int SubscribersScheduled = 0) : TierChangeOutcome;

    /// <summary>The request does not make sense, and the reason says why in words an admin can act on.</summary>
    public sealed record Invalid(string Reason) : TierChangeOutcome;

    /// <summary>No tier with that id.</summary>
    public sealed record NotFound : TierChangeOutcome;

    /// <summary>The tier is in a state that forbids this — archived, or in use.</summary>
    public sealed record Refused(string Reason) : TierChangeOutcome;
}

/// <summary>
/// The back office's view of subscription tiers: create them, price them, say what they grant,
/// publish them, and retire them without ever losing what an invoice refers to.
/// </summary>
/// <remarks>
/// <para>
/// Everything here reads and writes rows the platform owns, so every method opens an
/// <see cref="IPlatformScope"/> — audited and logged — rather than reaching for
/// <c>IgnoreQueryFilters</c>. Every write demands a reason, like every other back-office action,
/// and the reason travels into the audit log through <see cref="IAuditContext"/>.
/// </para>
/// <para>
/// Two records of a change are written and they are not redundant. The save interceptor writes the
/// tamper-evident before/after diff to <c>platform.audit_logs</c>; this service also writes
/// <c>billing.tier_change_log</c>, which carries the two things a diff cannot express — what the
/// admin chose to do about existing subscribers, and when those subscribers were told. FRD RS-6
/// and RS-7 ask for both.
/// </para>
/// </remarks>
public sealed class TierAdminService
{
    /// <summary>Short of this a reason is not a reason. Same rule as every other back-office write.</summary>
    public const int MinReasonLength = 10;

    /// <summary>Longer than this is a document, not a reason.</summary>
    public const int MaxReasonLength = 1000;

    private static readonly JsonSerializerOptions Snapshot = new() { WriteIndented = false };

    private readonly IAppDbContext _db;
    private readonly IPlatformScope _platformScope;
    private readonly IAuditContext _audit;
    private readonly TimeProvider _clock;

    public TierAdminService(IAppDbContext db, IPlatformScope platformScope, IAuditContext audit, TimeProvider clock)
    {
        _db = db;
        _platformScope = platformScope;
        _audit = audit;
        _clock = clock;
    }

    /// <summary>Actions recorded in <c>billing.tier_change_log</c>.</summary>
    public static class Actions
    {
        public const string Created = "tier.created";
        public const string Updated = "tier.updated";
        public const string Repriced = "tier.repriced";
        public const string EntitlementsChanged = "tier.entitlements_changed";
        public const string Published = "tier.published";
        public const string Archived = "tier.archived";
        public const string Restored = "tier.restored";
        public const string Deleted = "tier.deleted";
    }

    /// <summary>Every tier, with its prices, what it grants and how many agencies are on it.</summary>
    public async Task<IReadOnlyList<TierView>> ListAsync(
        bool includeArchived = true,
        CancellationToken cancellationToken = default)
    {
        using var scope = _platformScope.Enter("back office — listing subscription tiers, which the platform owns");

        var tiers = await Tiers()
            .Where(tier => includeArchived || tier.Status != TierStatus.Archived)
            .OrderBy(tier => tier.SortOrder)
            .ThenBy(tier => tier.Name)
            .ToListAsync(cancellationToken);

        var counts = await SubscriberCountsAsync(cancellationToken);
        var catalogue = await CatalogueAsync(cancellationToken);

        return [.. tiers.Select(tier => ToView(tier, counts, catalogue))];
    }

    /// <summary>One tier, or null.</summary>
    public async Task<TierView?> GetAsync(Guid tierId, CancellationToken cancellationToken = default)
    {
        using var scope = _platformScope.Enter("back office — reading one subscription tier");

        var tier = await Tiers().FirstOrDefaultAsync(candidate => candidate.Id == tierId, cancellationToken);

        if (tier is null)
        {
            return null;
        }

        return ToView(tier, await SubscriberCountsAsync(cancellationToken), await CatalogueAsync(cancellationToken));
    }

    /// <summary>Creates a tier, in draft. Nobody can subscribe until it is published.</summary>
    public async Task<TierChangeOutcome> CreateAsync(
        TierDraft draft,
        string reason,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);

        if (Unusable(reason) is { } problem)
        {
            return new TierChangeOutcome.Invalid(problem);
        }

        using var scope = _platformScope.Enter("back office — creating a subscription tier");

        // Normalised here rather than inside the expression, so the comparison is an ordinary
        // equality the database can use the unique index for.
        var code = draft.Code?.Trim().ToLowerInvariant() ?? string.Empty;

        if (await _db.SubscriptionTiers.AnyAsync(tier => tier.Code == code, cancellationToken))
        {
            return new TierChangeOutcome.Invalid(
                $"A tier with the code '{draft.Code}' already exists. Codes appear in reports, so they cannot be reused.");
        }

        SubscriptionTier tier;

        try
        {
            tier = SubscriptionTier.Draft(
                code, draft.Name, draft.CustomerDescription, draft.TrialDays, draft.SortOrder);
        }
        catch (Exception ex) when (ex is ArgumentException or ArgumentOutOfRangeException)
        {
            return new TierChangeOutcome.Invalid(ex.Message);
        }

        if (draft.IsFallback)
        {
            await ClearExistingFallbackAsync(tier.Id, cancellationToken);
        }

        tier.SetFallback(draft.IsFallback);
        _db.SubscriptionTiers.Add(tier);

        RecordChange(tier, Actions.Created, before: null, after: Describe(tier), TierMigrationPolicy.NewOnly, reason);

        await SaveAsync($"{Actions.Created}: {reason.Trim()}", cancellationToken);

        return await SavedAsync(tier.Id, cancellationToken);
    }

    /// <summary>Renames, re-describes and re-orders a tier. The code never changes.</summary>
    public async Task<TierChangeOutcome> UpdateAsync(
        Guid tierId,
        TierDraft draft,
        string reason,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);

        if (Unusable(reason) is { } problem)
        {
            return new TierChangeOutcome.Invalid(problem);
        }

        using var scope = _platformScope.Enter("back office — editing a subscription tier");

        var tier = await Tiers().FirstOrDefaultAsync(candidate => candidate.Id == tierId, cancellationToken);

        if (tier is null)
        {
            return new TierChangeOutcome.NotFound();
        }

        var before = Describe(tier);

        try
        {
            tier.Describe(draft.Name, draft.CustomerDescription, draft.SortOrder);
            tier.SetTrial(draft.TrialDays);
        }
        catch (Exception ex) when (ex is ArgumentException or ArgumentOutOfRangeException)
        {
            return new TierChangeOutcome.Invalid(ex.Message);
        }

        if (draft.IsFallback != tier.IsFallback)
        {
            if (draft.IsFallback)
            {
                await ClearExistingFallbackAsync(tier.Id, cancellationToken);
            }

            tier.SetFallback(draft.IsFallback);
        }

        RecordChange(tier, Actions.Updated, before, Describe(tier), TierMigrationPolicy.NewOnly, reason);

        await SaveAsync($"{Actions.Updated}: {reason.Trim()}", cancellationToken);

        return await SavedAsync(tier.Id, cancellationToken);
    }

    /// <summary>
    /// Sets the current price for one currency and interval.
    /// </summary>
    /// <remarks>
    /// The old price row is closed rather than changed, so existing subscribers keep paying what
    /// they agreed. Moving them to the new price is <see cref="MigrateSubscribersAsync"/>, which is
    /// a separate, deliberate act with notice attached — because the alternative is that repricing a
    /// tier silently changes what every subscriber's next invoice says.
    /// </remarks>
    public async Task<TierChangeOutcome> SetPriceAsync(
        Guid tierId,
        string currency,
        BillingInterval interval,
        long amountMinor,
        string reason,
        CancellationToken cancellationToken = default)
    {
        if (Unusable(reason) is { } problem)
        {
            return new TierChangeOutcome.Invalid(problem);
        }

        if (amountMinor < 0)
        {
            return new TierChangeOutcome.Invalid("A price cannot be negative.");
        }

        using var scope = _platformScope.Enter("back office — repricing a subscription tier");

        var tier = await Tiers().FirstOrDefaultAsync(candidate => candidate.Id == tierId, cancellationToken);

        if (tier is null)
        {
            return new TierChangeOutcome.NotFound();
        }

        if (tier.Status == TierStatus.Archived)
        {
            return new TierChangeOutcome.Refused(
                $"'{tier.Name}' is archived, so nobody can subscribe to it and a new price would never be charged.");
        }

        var before = Describe(tier);

        try
        {
            tier.SetPrice(currency, interval, new Money(amountMinor), _clock.GetUtcNow());
        }
        catch (Exception ex) when (ex is ArgumentException or ArgumentOutOfRangeException)
        {
            return new TierChangeOutcome.Invalid(ex.Message);
        }

        RecordChange(tier, Actions.Repriced, before, Describe(tier), TierMigrationPolicy.NewOnly, reason);

        await SaveAsync($"{Actions.Repriced}: {reason.Trim()}", cancellationToken);

        return await SavedAsync(tier.Id, cancellationToken);
    }

    /// <summary>
    /// Replaces what a tier grants.
    /// </summary>
    /// <remarks>
    /// Any entitlement left out of <paramref name="grants"/> is revoked, so the request is the whole
    /// truth about the tier rather than a patch. A patch would make "why does this tier still grant
    /// loyalty?" a question about the history of the requests rather than about the tier.
    /// </remarks>
    public async Task<TierChangeOutcome> SetEntitlementsAsync(
        Guid tierId,
        IReadOnlyList<EntitlementGrant> grants,
        string reason,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(grants);

        if (Unusable(reason) is { } problem)
        {
            return new TierChangeOutcome.Invalid(problem);
        }

        using var scope = _platformScope.Enter("back office — setting what a subscription tier grants");

        var tier = await Tiers().FirstOrDefaultAsync(candidate => candidate.Id == tierId, cancellationToken);

        if (tier is null)
        {
            return new TierChangeOutcome.NotFound();
        }

        var catalogue = await CatalogueAsync(cancellationToken);
        var before = Describe(tier);

        foreach (var grant in grants)
        {
            if (!catalogue.TryGetValue(grant.Code, out var entitlement))
            {
                return new TierChangeOutcome.Invalid(
                    $"'{grant.Code}' is not an entitlement. The catalogue is fixed in code — see EntitlementCodes.");
            }

            EntitlementValue value;

            try
            {
                value = EntitlementValue.FromJson(entitlement.ValueType, grant.Value);
            }
            catch (Exception ex) when (ex is FormatException or ArgumentOutOfRangeException)
            {
                return new TierChangeOutcome.Invalid($"{entitlement.Name}: {ex.Message}");
            }

            tier.Grant(entitlement, value);
        }

        var keeping = grants.Select(grant => grant.Code).ToHashSet(StringComparer.Ordinal);

        foreach (var entitlement in catalogue.Values.Where(entitlement => !keeping.Contains(entitlement.Code)))
        {
            tier.Revoke(entitlement.Id);
        }

        RecordChange(tier, Actions.EntitlementsChanged, before, Describe(tier), TierMigrationPolicy.NewOnly, reason);

        await SaveAsync($"{Actions.EntitlementsChanged}: {reason.Trim()}", cancellationToken);

        return await SavedAsync(tier.Id, cancellationToken);
    }

    /// <summary>Makes a tier live, so agencies may subscribe to it.</summary>
    public async Task<TierChangeOutcome> PublishAsync(
        Guid tierId,
        string reason,
        CancellationToken cancellationToken = default) =>
        await TransitionAsync(
            tierId,
            reason,
            Actions.Published,
            "back office — publishing a subscription tier",
            tier => tier.Publish(_clock.GetUtcNow()),
            cancellationToken);

    /// <summary>
    /// Retires a tier: out of the picker, subscribers untouched.
    /// </summary>
    /// <remarks>This is what "delete" means for a tier that anybody has ever been on. FRD RS-6.</remarks>
    public async Task<TierChangeOutcome> ArchiveAsync(
        Guid tierId,
        string reason,
        CancellationToken cancellationToken = default) =>
        await TransitionAsync(
            tierId,
            reason,
            Actions.Archived,
            "back office — archiving a subscription tier",
            tier => tier.Archive(_clock.GetUtcNow()),
            cancellationToken);

    /// <summary>Puts an archived tier back in the picker.</summary>
    public async Task<TierChangeOutcome> RestoreAsync(
        Guid tierId,
        string reason,
        CancellationToken cancellationToken = default) =>
        await TransitionAsync(
            tierId,
            reason,
            Actions.Restored,
            "back office — restoring an archived subscription tier",
            tier => tier.Restore(),
            cancellationToken);

    /// <summary>
    /// Throws away a draft nobody ever saw.
    /// </summary>
    /// <remarks>
    /// The only delete in this module, and it is narrow on purpose: a tier that was ever published,
    /// or that any agency has ever been on, is archived instead. The database refuses the same thing
    /// through a trigger, because this is the sort of delete nobody can undo.
    /// </remarks>
    public async Task<TierChangeOutcome> DeleteAsync(
        Guid tierId,
        string reason,
        CancellationToken cancellationToken = default)
    {
        if (Unusable(reason) is { } problem)
        {
            return new TierChangeOutcome.Invalid(problem);
        }

        using var scope = _platformScope.Enter("back office — deleting an unpublished subscription tier");

        var tier = await Tiers().FirstOrDefaultAsync(candidate => candidate.Id == tierId, cancellationToken);

        if (tier is null)
        {
            return new TierChangeOutcome.NotFound();
        }

        var subscribers = await _db.Subscriptions.CountAsync(
            subscription => subscription.TierId == tierId, cancellationToken);

        if (subscribers > 0)
        {
            return new TierChangeOutcome.Refused(
                $"'{tier.Name}' has {subscribers} {(subscribers == 1 ? "subscriber" : "subscribers")}, so it cannot be deleted. "
                + "Archive it instead: existing subscribers keep it and nobody new can join.");
        }

        if (tier.Status != TierStatus.Draft || tier.PublishedAt is not null)
        {
            return new TierChangeOutcome.Refused(
                $"'{tier.Name}' has been published, so it cannot be deleted even with nobody on it. "
                + "Archive it instead — invoices and reports name the tier they billed for.");
        }

        // The change log outlives the tier, so the row is written first and its foreign key is
        // dropped with the tier. Nothing else in the log ever loses its tier.
        _db.TierEntitlements.RemoveRange(
            await _db.TierEntitlements.Where(grant => grant.TierId == tierId).ToListAsync(cancellationToken));
        _db.SubscriptionTiers.Remove(tier);

        await SaveAsync($"{Actions.Deleted}: {reason.Trim()}", cancellationToken);

        return new TierChangeOutcome.Saved(
            new TierView(tier.Id, tier.Code, tier.Name, tier.CustomerDescription, TierStatus.Draft,
                tier.TrialDays, tier.SortOrder, tier.IsFallback, 0, null, null, [], []));
    }

    /// <summary>
    /// Schedules every agency on <paramref name="fromTierId"/> to move to <paramref name="toTierId"/>,
    /// after the notice period.
    /// </summary>
    /// <remarks>
    /// Nothing moves today. Each subscriber gets a <see cref="SubscriptionMigration"/> dated
    /// <see cref="SubscriptionMigration.NoticePeriod"/> ahead, and the billing job applies them when
    /// the day comes. That is what FRD RS-7's "advance notice" means in practice: the agency can see
    /// the change coming on its own plan screen, with time to object.
    /// </remarks>
    public async Task<TierChangeOutcome> MigrateSubscribersAsync(
        Guid fromTierId,
        Guid toTierId,
        string reason,
        CancellationToken cancellationToken = default)
    {
        if (Unusable(reason) is { } problem)
        {
            return new TierChangeOutcome.Invalid(problem);
        }

        if (fromTierId == toTierId)
        {
            return new TierChangeOutcome.Invalid("The two tiers are the same, so there is nothing to migrate.");
        }

        using var scope = _platformScope.Enter("back office — scheduling a tier's subscribers to move to another tier");

        var from = await Tiers().FirstOrDefaultAsync(tier => tier.Id == fromTierId, cancellationToken);
        var to = await Tiers().FirstOrDefaultAsync(tier => tier.Id == toTierId, cancellationToken);

        if (from is null || to is null)
        {
            return new TierChangeOutcome.NotFound();
        }

        if (!to.AcceptsNewSubscribers)
        {
            return new TierChangeOutcome.Refused(
                $"'{to.Name}' is {to.Status.ToString().ToLowerInvariant()}, so subscribers cannot be moved onto it. "
                + "Publish it first.");
        }

        var now = _clock.GetUtcNow();
        var scheduledFor = now.Add(SubscriptionMigration.NoticePeriod);

        var subscriptions = await _db.Subscriptions
            .Where(subscription => subscription.TierId == fromTierId)
            .Where(subscription => subscription.Status == SubscriptionStatus.Trialing
                                || subscription.Status == SubscriptionStatus.Active
                                || subscription.Status == SubscriptionStatus.PastDue)
            .ToListAsync(cancellationToken);

        var alreadyScheduled = await _db.SubscriptionMigrations
            .Where(migration => migration.ToTierId == toTierId && migration.AppliedAt == null && migration.CancelledAt == null)
            .Select(migration => migration.SubscriptionId)
            .ToListAsync(cancellationToken);

        var pending = alreadyScheduled.ToHashSet();
        var scheduled = 0;

        foreach (var subscription in subscriptions.Where(subscription => !pending.Contains(subscription.Id)))
        {
            _db.SubscriptionMigrations.Add(SubscriptionMigration.Schedule(
                subscription.AgencyId,
                subscription.Id,
                fromTierId,
                toTierId,
                SubscriptionChangeReason.AdminMigration,
                $"Trips is moving every agency on the {from.Name} plan to {to.Name}. {reason.Trim()}",
                scheduledFor));

            scheduled++;
        }

        var entry = RecordChange(
            from, Actions.Updated, Describe(from), Describe(to), TierMigrationPolicy.MigrateExisting, reason);
        entry.RecordNotice(now, scheduled);

        await SaveAsync($"tier.subscribers_migrated: {reason.Trim()}", cancellationToken);

        var view = await GetAsync(fromTierId, cancellationToken);

        return view is null
            ? new TierChangeOutcome.NotFound()
            : new TierChangeOutcome.Saved(view, scheduled);
    }

    /// <summary>
    /// Every agency on a plan, with what it owes.
    /// </summary>
    /// <param name="tierId">Narrow it to one tier, or null for every subscriber.</param>
    /// <remarks>
    /// Reads across agencies, which is the whole point of a back-office screen, so it goes through
    /// an audited platform scope. Nothing here is money an agency owes another agency — it is what
    /// each agency owes Trips.
    /// </remarks>
    public async Task<IReadOnlyList<SubscriberView>> SubscribersAsync(
        Guid? tierId = null,
        CancellationToken cancellationToken = default)
    {
        using var scope = _platformScope.Enter(
            "back office — listing the agencies on a subscription tier, across every agency");

        var subscriptions = await _db.Subscriptions
            .Where(subscription => tierId == null || subscription.TierId == tierId)
            .Where(subscription => subscription.Status != SubscriptionStatus.Cancelled)
            .OrderBy(subscription => subscription.CurrentPeriodEnd)
            .Take(500)
            .ToListAsync(cancellationToken);

        if (subscriptions.Count == 0)
        {
            return [];
        }

        var agencyIds = subscriptions.ConvertAll(subscription => subscription.AgencyId);
        var tierIds = subscriptions.Select(subscription => subscription.TierId).Distinct().ToList();
        var priceIds = subscriptions
            .Where(subscription => subscription.TierPriceId is not null)
            .Select(subscription => subscription.TierPriceId!.Value)
            .Distinct()
            .ToList();

        var agencies = await _db.Agencies
            .Where(agency => agencyIds.Contains(agency.Id))
            .Select(agency => new { agency.Id, agency.LegalName, agency.TradingName, agency.Status })
            .ToDictionaryAsync(agency => agency.Id, cancellationToken);

        var tiers = await _db.SubscriptionTiers
            .Where(tier => tierIds.Contains(tier.Id))
            .ToDictionaryAsync(tier => tier.Id, tier => tier.Name, cancellationToken);

        var prices = await _db.TierPrices
            .Where(price => priceIds.Contains(price.Id))
            .ToDictionaryAsync(price => price.Id, price => price.AmountMinor.AmountMinor, cancellationToken);

        var subscriptionIds = subscriptions.ConvertAll(subscription => subscription.Id);

        var outstanding = await _db.SubscriptionInvoices
            .Where(invoice => subscriptionIds.Contains(invoice.SubscriptionId))
            .Where(invoice => invoice.Status == SubscriptionInvoiceStatus.Open
                           || invoice.Status == SubscriptionInvoiceStatus.PastDue)
            .GroupBy(invoice => invoice.SubscriptionId)
            .Select(group => new { SubscriptionId = group.Key, Total = group.Sum(invoice => invoice.TotalMinor.AmountMinor) })
            .ToDictionaryAsync(row => row.SubscriptionId, row => row.Total, cancellationToken);

        return
        [
            .. subscriptions.Select(subscription =>
            {
                var agency = agencies.GetValueOrDefault(subscription.AgencyId);

                return new SubscriberView(
                    subscription.AgencyId,
                    agency?.TradingName ?? agency?.LegalName ?? "Unknown agency",
                    agency?.Status.ToString() ?? "Unknown",
                    subscription.Id,
                    tiers.GetValueOrDefault(subscription.TierId, "Unknown plan"),
                    subscription.Status,
                    subscription.Currency,
                    subscription.TierPriceId is { } priceId ? prices.GetValueOrDefault(priceId) : null,
                    subscription.CurrentPeriodStart,
                    subscription.CurrentPeriodEnd,
                    subscription.TrialEndsAt,
                    subscription.DunningRetries,
                    subscription.NextDunningAttemptAt,
                    outstanding.GetValueOrDefault(subscription.Id));
            }),
        ];
    }

    /// <summary>The entitlement catalogue, for the tier builder's list of things to set.</summary>
    public async Task<IReadOnlyList<Entitlement>> CatalogueListAsync(CancellationToken cancellationToken = default)
    {
        using var scope = _platformScope.Enter("back office — reading the entitlement catalogue");

        var catalogue = await CatalogueAsync(cancellationToken);
        var order = EntitlementCatalog.All.Select((definition, index) => (definition.Code, index))
            .ToDictionary(pair => pair.Code, pair => pair.index, StringComparer.Ordinal);

        return [.. catalogue.Values.OrderBy(entitlement => order.GetValueOrDefault(entitlement.Code, int.MaxValue))];
    }

    private async Task<TierChangeOutcome> TransitionAsync(
        Guid tierId,
        string reason,
        string action,
        string scopeReason,
        Action<SubscriptionTier> transition,
        CancellationToken cancellationToken)
    {
        if (Unusable(reason) is { } problem)
        {
            return new TierChangeOutcome.Invalid(problem);
        }

        using var scope = _platformScope.Enter(scopeReason);

        var tier = await Tiers().FirstOrDefaultAsync(candidate => candidate.Id == tierId, cancellationToken);

        if (tier is null)
        {
            return new TierChangeOutcome.NotFound();
        }

        var before = Describe(tier);

        try
        {
            transition(tier);
        }
        catch (InvalidOperationException ex)
        {
            return new TierChangeOutcome.Refused(ex.Message);
        }

        RecordChange(tier, action, before, Describe(tier), TierMigrationPolicy.NewOnly, reason);

        await SaveAsync($"{action}: {reason.Trim()}", cancellationToken);

        return await SavedAsync(tier.Id, cancellationToken);
    }

    /// <summary>
    /// Takes the fallback flag off whatever had it, so the new one can take it.
    /// </summary>
    /// <remarks>
    /// Moved rather than refused. There is a partial unique index on the flag, so setting a second
    /// one without clearing the first is a constraint violation the admin would have to decode; and
    /// "make this the free plan" is unambiguous about what should happen to the old one.
    /// </remarks>
    private async Task ClearExistingFallbackAsync(Guid keepingId, CancellationToken cancellationToken)
    {
        var existing = await _db.SubscriptionTiers
            .Where(tier => tier.IsFallback && tier.Id != keepingId)
            .ToListAsync(cancellationToken);

        foreach (var tier in existing)
        {
            tier.SetFallback(false);
        }
    }

    private IQueryable<SubscriptionTier> Tiers() =>
        _db.SubscriptionTiers
            .Include(tier => tier.Prices)
            .Include(tier => tier.Entitlements);

    private async Task<Dictionary<Guid, int>> SubscriberCountsAsync(CancellationToken cancellationToken) =>
        await _db.Subscriptions
            .Where(subscription => subscription.Status == SubscriptionStatus.Trialing
                                || subscription.Status == SubscriptionStatus.Active
                                || subscription.Status == SubscriptionStatus.PastDue)
            .GroupBy(subscription => subscription.TierId)
            .Select(group => new { TierId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(row => row.TierId, row => row.Count, cancellationToken);

    private async Task<Dictionary<string, Entitlement>> CatalogueAsync(CancellationToken cancellationToken) =>
        await _db.Entitlements.ToDictionaryAsync(entitlement => entitlement.Code, StringComparer.Ordinal, cancellationToken);

    private TierChangeLogEntry RecordChange(
        SubscriptionTier tier,
        string action,
        string? before,
        string? after,
        TierMigrationPolicy policy,
        string reason)
    {
        var entry = TierChangeLogEntry.Record(
            tier.Id, _audit.ActorUserId, action, before, after, policy, reason.Trim(), _clock.GetUtcNow());

        _db.TierChangeLog.Add(entry);

        return entry;
    }

    private async Task SaveAsync(string reason, CancellationToken cancellationToken)
    {
        // Set as late as possible: the reason travels with everything in this SaveChanges.
        _audit.SetReason(reason);
        await _db.SaveChangesAsync(cancellationToken);
    }

    private async Task<TierChangeOutcome> SavedAsync(Guid tierId, CancellationToken cancellationToken)
    {
        var view = await GetAsync(tierId, cancellationToken);

        return view is null ? new TierChangeOutcome.NotFound() : new TierChangeOutcome.Saved(view);
    }

    private static TierView ToView(
        SubscriptionTier tier,
        IReadOnlyDictionary<Guid, int> subscribers,
        IReadOnlyDictionary<string, Entitlement> catalogue)
    {
        var byId = catalogue.Values.ToDictionary(entitlement => entitlement.Id);

        return new TierView(
            tier.Id,
            tier.Code,
            tier.Name,
            tier.CustomerDescription,
            tier.Status,
            tier.TrialDays,
            tier.SortOrder,
            tier.IsFallback,
            subscribers.GetValueOrDefault(tier.Id),
            tier.PublishedAt,
            tier.ArchivedAt,
            [.. tier.Prices
                .OrderBy(price => price.Currency)
                .ThenBy(price => price.Interval)
                .ThenByDescending(price => price.EffectiveFrom)
                .Select(price => new TierPriceView(
                    price.Id,
                    price.Currency,
                    price.Interval,
                    price.AmountMinor.AmountMinor,
                    price.IsPromotional,
                    price.EffectiveFrom,
                    price.EffectiveTo))],
            [.. tier.Entitlements
                .Where(grant => byId.ContainsKey(grant.EntitlementId))
                .Select(grant => (Grant: grant, Entitlement: byId[grant.EntitlementId]))
                .OrderBy(pair => pair.Entitlement.Code, StringComparer.Ordinal)
                .Select(pair => new TierEntitlementView(
                    pair.Entitlement.Code,
                    pair.Entitlement.Name,
                    pair.Grant.ValueType,
                    pair.Grant.Value,
                    pair.Grant.TypedValue.ToString()))]);
    }

    private static string Describe(SubscriptionTier tier) =>
        JsonSerializer.Serialize(
            new
            {
                tier.Code,
                tier.Name,
                tier.CustomerDescription,
                Status = tier.Status.ToString(),
                tier.TrialDays,
                tier.SortOrder,
                tier.IsFallback,
                Prices = tier.Prices
                    .Where(price => price.EffectiveTo is null)
                    .Select(price => new
                    {
                        price.Currency,
                        Interval = price.Interval.ToString(),
                        price.AmountMinor.AmountMinor,
                    }),
                Entitlements = tier.Entitlements.Select(grant => new
                {
                    grant.EntitlementId,
                    ValueType = grant.ValueType.ToString(),
                    grant.Value,
                }),
            },
            Snapshot);

    private static string? Unusable(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length < MinReasonLength)
        {
            return $"Say why, in at least {MinReasonLength} characters. A tier change is recorded and someone will read this later.";
        }

        return reason.Trim().Length > MaxReasonLength
            ? $"The reason is longer than {MaxReasonLength} characters."
            : null;
    }
}
