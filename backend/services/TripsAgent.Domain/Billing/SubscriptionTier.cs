using TripsAgent.Domain.Auditing;
using TripsAgent.Domain.Common;

namespace TripsAgent.Domain.Billing;

/// <summary>
/// A plan an agency can be on: what it costs, what it unlocks, and whether anyone may still join it.
/// </summary>
/// <remarks>
/// <para>
/// Platform-owned. There is no <c>agency_id</c> here and no tenant query filter, because a tier
/// belongs to Trips and the same row is referenced by every agency subscribed to it. Only a
/// back-office user with <c>subscription.manage</c> can read or write one; the agency-facing API
/// exposes published tiers through a projection that carries no internal fields.
/// </para>
/// <para>
/// <b>Never deleted.</b> There is no <c>Delete</c> on this class at all, and the service refuses to
/// remove a tier with subscribers. A tier is what an invoice says the agency was charged for; take
/// the row away and last month's invoice stops meaning anything. <see cref="Archive"/> is the
/// operation people reach for when they say "delete" — it takes the tier out of the picker and
/// leaves every existing subscriber exactly where they are.
/// </para>
/// </remarks>
public sealed class SubscriptionTier : Entity, IAuditableEntity, IAuditLogged
{
    private readonly List<TierPrice> _prices = [];
    private readonly List<TierEntitlement> _entitlements = [];

    private SubscriptionTier()
    {
        Code = string.Empty;
        Name = string.Empty;
    }

    private SubscriptionTier(string code, string name, string? customerDescription, int trialDays, int sortOrder)
    {
        Code = NormaliseCode(code);
        Name = Require(name, nameof(name));
        CustomerDescription = string.IsNullOrWhiteSpace(customerDescription) ? null : customerDescription.Trim();
        TrialDays = ValidateTrialDays(trialDays);
        SortOrder = sortOrder;
        Status = TierStatus.Draft;
    }

    /// <summary>The longest trial a tier may offer. Beyond this it is not a trial, it is free service.</summary>
    public const int MaxTrialDays = 90;

    /// <summary>Creates a tier in <see cref="TierStatus.Draft"/>. Nobody can subscribe until it is published.</summary>
    public static SubscriptionTier Draft(
        string code,
        string name,
        string? customerDescription = null,
        int trialDays = 0,
        int sortOrder = 0) =>
        new(code, name, customerDescription, trialDays, sortOrder);

    /// <summary>Stable, lowercase, machine-readable: <c>starter</c>, <c>growth</c>, <c>enterprise</c>.</summary>
    public string Code { get; private set; }

    /// <summary>What the back office and the plan picker call it.</summary>
    public string Name { get; private set; }

    /// <summary>The sentence an agency reads when choosing. Null when there is nothing to add.</summary>
    public string? CustomerDescription { get; private set; }

    public TierStatus Status { get; private set; }

    /// <summary>Free days before the first charge. Zero for no trial.</summary>
    public int TrialDays { get; private set; }

    /// <summary>Where it sits in the picker. Lower first.</summary>
    public int SortOrder { get; private set; }

    /// <summary>
    /// True when this is the tier an agency falls back to: no charge, and the plan they land on when
    /// dunning runs out.
    /// </summary>
    /// <remarks>
    /// At most one tier may be the fallback, which a partial unique index enforces. Without one, a
    /// failed payment can only end in suspension; with one, an agency that stops paying keeps a
    /// working account with nothing switched on beyond the free tier's entitlements.
    /// </remarks>
    public bool IsFallback { get; private set; }

    public DateTimeOffset? PublishedAt { get; private set; }

    public DateTimeOffset? ArchivedAt { get; private set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Its prices, one per currency and interval, with history.</summary>
    public IReadOnlyCollection<TierPrice> Prices => _prices;

    /// <summary>What it unlocks. An entitlement with no row here falls back to the catalogue default.</summary>
    public IReadOnlyCollection<TierEntitlement> Entitlements => _entitlements;

    /// <summary>True when an agency may subscribe to it today.</summary>
    public bool AcceptsNewSubscribers => Status == TierStatus.Published;

    /// <summary>Renames and re-describes. The code never changes: invoices and reports point at it.</summary>
    public void Describe(string name, string? customerDescription, int sortOrder)
    {
        Name = Require(name, nameof(name));
        CustomerDescription = string.IsNullOrWhiteSpace(customerDescription) ? null : customerDescription.Trim();
        SortOrder = sortOrder;
    }

    /// <summary>Sets the free period offered to a new subscriber.</summary>
    public void SetTrial(int trialDays) => TrialDays = ValidateTrialDays(trialDays);

    /// <summary>Marks this the tier an agency falls back to. Clearing it is the caller's job.</summary>
    public void SetFallback(bool isFallback) => IsFallback = isFallback;

    /// <summary>
    /// Makes the tier live.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// It is archived, or it has no price. A published tier with no price cannot be subscribed to
    /// and cannot be billed, so publishing one is a trap the tier builder should never set.
    /// </exception>
    public void Publish(DateTimeOffset at)
    {
        if (Status == TierStatus.Archived)
        {
            throw new InvalidOperationException(
                $"Tier '{Code}' is archived. Archiving is deliberate and final; create a new tier instead.");
        }

        if (!IsFallback && _prices.Count == 0)
        {
            throw new InvalidOperationException(
                $"Tier '{Code}' has no price, so nothing could be charged for it. Add a price before publishing.");
        }

        Status = TierStatus.Published;
        PublishedAt ??= at;
    }

    /// <summary>
    /// Takes the tier out of the picker, leaving every existing subscriber on it.
    /// </summary>
    /// <remarks>
    /// This is what "delete" means here. The row stays, the invoices that reference it stay
    /// readable, and the only thing that changes is that nobody new can join.
    /// </remarks>
    public void Archive(DateTimeOffset at)
    {
        if (IsFallback)
        {
            throw new InvalidOperationException(
                $"Tier '{Code}' is the fallback plan, which is where agencies land when a payment fails. "
                + "Point the fallback at another tier before archiving this one.");
        }

        Status = TierStatus.Archived;
        ArchivedAt ??= at;
    }

    /// <summary>Puts an archived tier back in the picker.</summary>
    public void Restore()
    {
        if (Status != TierStatus.Archived)
        {
            return;
        }

        Status = PublishedAt is null ? TierStatus.Draft : TierStatus.Published;
        ArchivedAt = null;
    }

    /// <summary>
    /// Sets the current price for one currency and interval, ending whatever was there.
    /// </summary>
    /// <remarks>
    /// The old row is closed rather than changed. A subscriber points at the exact
    /// <see cref="TierPrice"/> they agreed to, so rewriting it would rewrite what they were told
    /// they would pay — the same rule as a placed order line (CLAUDE.md rule 5).
    /// </remarks>
    public TierPrice SetPrice(string currency, BillingInterval interval, Money amount, DateTimeOffset from)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(currency);

        if (amount.IsNegative)
        {
            throw new ArgumentOutOfRangeException(nameof(amount), amount.AmountMinor, "A price cannot be negative.");
        }

        var code = currency.Trim().ToUpperInvariant();

        foreach (var existing in _prices.Where(price => price.Matches(code, interval) && price.IsCurrentAt(from)))
        {
            existing.EndAt(from);
        }

        var replacement = TierPrice.For(Id, code, interval, amount, from);
        _prices.Add(replacement);

        return replacement;
    }

    /// <summary>The price in force at <paramref name="at"/>, or null when the tier has none.</summary>
    public TierPrice? PriceAt(string currency, BillingInterval interval, DateTimeOffset at)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(currency);

        var code = currency.Trim().ToUpperInvariant();

        return _prices
            .Where(price => price.Matches(code, interval) && price.IsCurrentAt(at))
            .OrderByDescending(price => price.EffectiveFrom)
            .FirstOrDefault();
    }

    /// <summary>Grants an entitlement, or changes what it grants.</summary>
    /// <exception cref="ArgumentException">
    /// The value's shape does not match the entitlement's declared type — a ceiling where a flag
    /// belongs, say.
    /// </exception>
    public void Grant(Entitlement entitlement, EntitlementValue value)
    {
        ArgumentNullException.ThrowIfNull(entitlement);

        if (entitlement.ValueType != value.Type)
        {
            throw new ArgumentException(
                $"'{entitlement.Code}' is a {entitlement.ValueType} entitlement; a {value.Type} value cannot be stored for it.",
                nameof(value));
        }

        var existing = _entitlements.SingleOrDefault(grant => grant.EntitlementId == entitlement.Id);

        if (existing is null)
        {
            _entitlements.Add(TierEntitlement.For(Id, entitlement.Id, value));
            return;
        }

        existing.ChangeTo(value);
    }

    /// <summary>Removes a grant, so the entitlement falls back to the catalogue default.</summary>
    public void Revoke(Guid entitlementId)
    {
        var existing = _entitlements.SingleOrDefault(grant => grant.EntitlementId == entitlementId);

        if (existing is not null)
        {
            _entitlements.Remove(existing);
        }
    }

    private static int ValidateTrialDays(int trialDays)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(trialDays);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(trialDays, MaxTrialDays);

        return trialDays;
    }

    private static string NormaliseCode(string code)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);

        var trimmed = code.Trim().ToLowerInvariant();

        if (!trimmed.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_'))
        {
            throw new ArgumentException(
                "A tier code is lowercase letters, digits, hyphens and underscores — it appears in URLs and reports.",
                nameof(code));
        }

        return trimmed;
    }

    private static string Require(string value, string parameter)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameter);
        return value.Trim();
    }
}

/// <summary>
/// What a tier costs, for one currency and interval, over one stretch of time.
/// </summary>
/// <remarks>
/// Rows are closed, never edited: a subscriber references the row they agreed to, so yesterday's
/// price has to stay readable exactly as it was. That is the same reason an order line freezes its
/// money (CLAUDE.md rule 5), applied one level up.
/// </remarks>
public sealed class TierPrice : Entity, IAuditableEntity
{
    private TierPrice() => Currency = string.Empty;

    private TierPrice(Guid tierId, string currency, BillingInterval interval, Money amount, DateTimeOffset from)
    {
        TierId = tierId;
        Currency = currency;
        Interval = interval;
        AmountMinor = amount;
        EffectiveFrom = from;
    }

    internal static TierPrice For(Guid tierId, string currency, BillingInterval interval, Money amount, DateTimeOffset from) =>
        new(tierId, currency, interval, amount, from);

    public Guid TierId { get; private set; }

    /// <summary>ISO 4217, uppercase. NGN for now — build-plan decision 17.</summary>
    public string Currency { get; private set; }

    public BillingInterval Interval { get; private set; }

    /// <summary>The price, in minor units. ₦25,000.00 is 2,500,000 kobo.</summary>
    public Money AmountMinor { get; private set; }

    /// <summary>
    /// True when this price was only ever offered as a promotion.
    /// </summary>
    /// <remarks>
    /// Carried because the plan's schema carries it and because an invoice should be able to say
    /// "this was a promotional rate". Promotions themselves are out of the MVP — build plan, F9 —
    /// so nothing sets this yet.
    /// </remarks>
    public bool IsPromotional { get; private set; }

    public DateTimeOffset EffectiveFrom { get; private set; }

    /// <summary>When it stopped being offered. Null while it is the current price.</summary>
    public DateTimeOffset? EffectiveTo { get; private set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    internal bool Matches(string currency, BillingInterval interval) =>
        string.Equals(Currency, currency, StringComparison.Ordinal) && Interval == interval;

    internal bool IsCurrentAt(DateTimeOffset at) => EffectiveFrom <= at && (EffectiveTo is null || EffectiveTo > at);

    internal void EndAt(DateTimeOffset at) => EffectiveTo = at;

    internal void MarkPromotional() => IsPromotional = true;
}

/// <summary>One entitlement, as one tier grants it.</summary>
public sealed class TierEntitlement : Entity, IAuditableEntity
{
    private TierEntitlement() => Value = string.Empty;

    private TierEntitlement(Guid tierId, Guid entitlementId, EntitlementValue value)
    {
        TierId = tierId;
        EntitlementId = entitlementId;
        ValueType = value.Type;
        Value = value.ToJson();
    }

    internal static TierEntitlement For(Guid tierId, Guid entitlementId, EntitlementValue value) =>
        new(tierId, entitlementId, value);

    public Guid TierId { get; private set; }

    public Guid EntitlementId { get; private set; }

    /// <summary>
    /// The entitlement's shape, copied here so the JSON scalar can be read back without joining.
    /// </summary>
    /// <remarks>
    /// Denormalised on purpose: the entitlement resolver reads every grant for an agency on a hot
    /// path, and a join to <c>entitlements</c> for a value that never changes is a join for nothing.
    /// The tier's own <c>Grant</c> is the only writer and it refuses a mismatch.
    /// </remarks>
    public EntitlementValueType ValueType { get; private set; }

    /// <summary>The JSON scalar: <c>true</c>, <c>25</c>, <c>-1</c>, <c>150</c>.</summary>
    public string Value { get; private set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>The value, read back into its typed form.</summary>
    public EntitlementValue TypedValue => EntitlementValue.FromJson(ValueType, Value);

    internal void ChangeTo(EntitlementValue value)
    {
        ValueType = value.Type;
        Value = value.ToJson();
    }
}
