using TripsAgent.Domain.Auditing;
using TripsAgent.Domain.Common;

namespace TripsAgent.Domain.Tenancy.SubAgents;

/// <summary>
/// One thing a sub-agent is allowed to sell: a product type, optionally narrowed to one supplier.
/// </summary>
/// <remarks>
/// <para>
/// <b>No rows means nothing is allowed.</b> The scope fails closed, so a sub-agent created this
/// morning cannot sell anything until its principal says what it may sell. The alternative —
/// "everything until told otherwise" — makes the dangerous state the default one.
/// </para>
/// <para>
/// <b><see cref="AgencyId"/> is the principal, not the sub-agent.</b> Every row in this feature is
/// owned by the agency that writes it, so the tenant filter and the row-level security policy stay
/// the plain <c>agency_id = current agency</c> that every other table uses, and no cross-tenant
/// write path has to exist. The sub-agent reads its own rows through
/// <see cref="SubAgencyId"/>, which is a read-only widening of the policy — see the migration.
/// </para>
/// <para>
/// A null <see cref="SupplierId"/> means every supplier for that product type. It is not "no
/// supplier": a scope with no supplier is the common case, and naming one is the narrowing.
/// </para>
/// </remarks>
public sealed class SubAgentScope : Entity, IAuditableEntity, ITenantScoped, IAuditLogged
{
    private SubAgentScope()
    {
    }

    /// <summary>Lets <paramref name="subAgencyId"/> sell <paramref name="productType"/>.</summary>
    /// <param name="agencyId">The principal granting it. Owns the row.</param>
    /// <param name="subAgencyId">The sub-agent the grant is about.</param>
    /// <param name="productType">What may be sold.</param>
    /// <param name="supplierId">One supplier, or null for every supplier of that type.</param>
    public static SubAgentScope Grant(
        Guid agencyId,
        Guid subAgencyId,
        SellableProductType productType,
        Guid? supplierId = null)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(agencyId, Guid.Empty);
        ArgumentOutOfRangeException.ThrowIfEqual(subAgencyId, Guid.Empty);

        if (agencyId == subAgencyId)
        {
            throw new InvalidOperationException(
                "An agency does not scope itself. A scope is what a principal allows a sub-agent to sell.");
        }

        return new SubAgentScope
        {
            AgencyId = agencyId,
            SubAgencyId = subAgencyId,
            ProductType = productType,
            SupplierId = supplierId,
        };
    }

    /// <summary>The principal that granted this scope, and owns the row.</summary>
    public Guid AgencyId { get; private set; }

    /// <summary>The sub-agent the scope is about.</summary>
    public Guid SubAgencyId { get; private set; }

    public SellableProductType ProductType { get; private set; }

    /// <summary>One supplier, or null for all of them.</summary>
    public Guid? SupplierId { get; private set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
