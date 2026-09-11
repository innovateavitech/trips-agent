using TripsAgent.Domain.Auditing;
using TripsAgent.Domain.Common;

namespace TripsAgent.Domain.Pricing;

/// <summary>
/// One of an agency's markup rules, as stored.
/// </summary>
/// <remarks>
/// <para>
/// <b>A rule's terms never change once saved.</b> "Editing" a rule retires it and creates a new one
/// with the new terms — see <see cref="ReplaceWith"/>. That is what makes the winning rule id
/// stored on a price worth storing: it points at exactly the terms that produced the price, for
/// as long as the price exists. If rules were edited in place, last month's quote would name a
/// rule that now says something different, and the margin on it could no longer be explained.
/// </para>
/// <para>
/// The database holds the same line: a trigger rejects any UPDATE that touches a term column, and
/// any DELETE. Only the end of the window and the pointer to the replacement may be written, and
/// only in the direction that closes a rule, never reopens it.
/// </para>
/// </remarks>
public sealed class MarkupRule : Entity, IAuditableEntity, ITenantScoped, IAuditLogged
{
    private MarkupRule() => Currency = string.Empty;

    /// <summary>Creates a rule for <paramref name="agencyId"/>. Throws if the terms do not hold together.</summary>
    public static MarkupRule Create(Guid agencyId, MarkupRuleTerms terms)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(agencyId, Guid.Empty);
        ArgumentNullException.ThrowIfNull(terms);

        var valid = terms.Validated();

        return new MarkupRule
        {
            AgencyId = agencyId,
            Scope = valid.Scope,
            ProductType = valid.ProductType,
            ProductId = valid.ProductId,
            SupplierCode = valid.SupplierCode,
            Currency = valid.Currency,
            CalculationType = valid.CalculationType,
            PercentBasisPoints = valid.PercentBasisPoints,
            ValueMinor = valid.ValueMinor,
            MinMarkupMinor = valid.MinMarkupMinor,
            MaxMarkupMinor = valid.MaxMarkupMinor,
            Priority = valid.Priority,
            AppliesToSubAgents = valid.AppliesToSubAgents,
            EffectiveFrom = valid.EffectiveFrom,
            EffectiveTo = valid.EffectiveTo,
        };
    }

    public Guid AgencyId { get; private set; }

    public MarkupScope Scope { get; private set; }

    public PricedProductType? ProductType { get; private set; }

    public Guid? ProductId { get; private set; }

    public string? SupplierCode { get; private set; }

    public string Currency { get; private set; }

    public MarkupCalculationType CalculationType { get; private set; }

    public int? PercentBasisPoints { get; private set; }

    public Money? ValueMinor { get; private set; }

    public Money? MinMarkupMinor { get; private set; }

    public Money? MaxMarkupMinor { get; private set; }

    public int Priority { get; private set; }

    public bool AppliesToSubAgents { get; private set; }

    public DateTimeOffset EffectiveFrom { get; private set; }

    public DateTimeOffset? EffectiveTo { get; private set; }

    /// <summary>The rule that replaced this one, when it was edited. Null otherwise.</summary>
    public Guid? SupersededById { get; private set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>The rule's terms as a plain value, for the engine and the cache.</summary>
    public MarkupRuleTerms Terms => new()
    {
        Scope = Scope,
        ProductType = ProductType,
        ProductId = ProductId,
        SupplierCode = SupplierCode,
        Currency = Currency,
        CalculationType = CalculationType,
        PercentBasisPoints = PercentBasisPoints,
        ValueMinor = ValueMinor,
        MinMarkupMinor = MinMarkupMinor,
        MaxMarkupMinor = MaxMarkupMinor,
        Priority = Priority,
        AppliesToSubAgents = AppliesToSubAgents,
        EffectiveFrom = EffectiveFrom,
        EffectiveTo = EffectiveTo,
    };

    public MarkupRuleDefinition ToDefinition() => new(Id, AgencyId, Terms);

    /// <summary>True once the rule's window has closed.</summary>
    public bool HasEnded(DateTimeOffset at) => EffectiveTo is { } end && end <= at;

    /// <summary>
    /// Stops the rule applying from <paramref name="at"/>. Retiring an ended rule changes nothing.
    /// </summary>
    /// <remarks>
    /// A rule that has not started yet is closed at its own start, which leaves it an empty window:
    /// it never applied, and the history says so. The window only ever shrinks, which is the one
    /// change the database trigger accepts.
    /// </remarks>
    public void Retire(DateTimeOffset at)
    {
        if (HasEnded(at))
        {
            return;
        }

        EffectiveTo = at < EffectiveFrom ? EffectiveFrom : at;
    }

    /// <summary>
    /// Retires this rule and returns the rule that takes its place. This is what "edit" means.
    /// </summary>
    /// <remarks>
    /// The replacement starts no earlier than <paramref name="at"/>. Letting it start in the past
    /// would claim it had been in force when prices were actually being worked out from this one.
    /// </remarks>
    public MarkupRule ReplaceWith(MarkupRuleTerms newTerms, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(newTerms);

        if (SupersededById is not null)
        {
            throw new InvalidOperationException(
                "This rule has already been replaced. Edit the rule that replaced it instead.");
        }

        if (HasEnded(at))
        {
            throw new InvalidOperationException(
                "This rule has already ended, so there is nothing to edit. Create a new rule instead.");
        }

        // Built before this rule is touched, so terms that fail validation leave it as it was.
        var replacement = Create(AgencyId, newTerms with
        {
            EffectiveFrom = newTerms.EffectiveFrom > at ? newTerms.EffectiveFrom : at,
        });

        Retire(at);
        SupersededById = replacement.Id;

        return replacement;
    }
}

/// <summary>A rule's identity, owner and terms — everything the engine needs, and nothing it does not.</summary>
public sealed record MarkupRuleDefinition(Guid Id, Guid AgencyId, MarkupRuleTerms Terms);
