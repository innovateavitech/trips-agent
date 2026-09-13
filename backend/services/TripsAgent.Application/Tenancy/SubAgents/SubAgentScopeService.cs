using Microsoft.EntityFrameworkCore;
using TripsAgent.Application.Persistence;
using TripsAgent.Domain.Suppliers;
using TripsAgent.Domain.Tenancy;
using TripsAgent.Domain.Tenancy.SubAgents;

namespace TripsAgent.Application.Tenancy.SubAgents;

/// <summary>One line of the scope editor: a product type, optionally narrowed to one supplier.</summary>
/// <param name="SupplierId">Null means every supplier of that type.</param>
/// <param name="SupplierName">The supplier's name, for display. Null when the scope names none.</param>
public sealed record SubAgentScopeRow(
    Guid Id,
    SellableProductType ProductType,
    Guid? SupplierId,
    string? SupplierName);

/// <summary>
/// What each sub-agent may sell, and the gate every selling path asks.
/// </summary>
/// <remarks>
/// <para>
/// <b>It fails closed.</b> A sub-agent with no scopes may sell nothing. A brand-new sub-agent is
/// therefore inert until its principal says what it is for, which is the safe way round: the
/// alternative is a new agency that can sell everything for as long as nobody gets to the screen.
/// </para>
/// <para>
/// <b>A principal is not scoped.</b> It sells whatever the platform sells, so
/// <see cref="MaySellAsync"/> answers yes for a principal without reading a row. Only agencies
/// with a parent are narrowed.
/// </para>
/// </remarks>
public sealed class SubAgentScopeService
{
    private readonly IAppDbContext _db;
    private readonly ITenantContext _tenant;

    public SubAgentScopeService(IAppDbContext db, ITenantContext tenant)
    {
        _db = db;
        _tenant = tenant;
    }

    /// <summary>
    /// May <paramref name="agencyId"/> sell this product type, through this supplier?
    /// </summary>
    /// <remarks>
    /// <para>
    /// Asked by search, by price confirmation and by checkout, so a hand-written API call cannot
    /// get round a button the console did not render.
    /// </para>
    /// <para>
    /// A scope with no supplier covers every supplier for that product type, so a match is "the
    /// product type, and either no supplier named or this one".
    /// </para>
    /// </remarks>
    public async Task<bool> MaySellAsync(
        Guid agencyId,
        SellableProductType productType,
        Guid? supplierId = null,
        CancellationToken cancellationToken = default)
    {
        var parentId = await _db.Agencies
            .AsNoTracking()
            .Where(agency => agency.Id == agencyId)
            .Select(agency => agency.ParentAgencyId)
            .SingleOrDefaultAsync(cancellationToken);

        if (parentId is null)
        {
            return true;
        }

        return await _db.SubAgentScopes
            .AsNoTracking()
            .AnyAsync(
                scope => scope.SubAgencyId == agencyId
                         && scope.ProductType == productType
                         && (scope.SupplierId == null || scope.SupplierId == supplierId),
                cancellationToken);
    }

    /// <summary>Every product type the caller's own agency may sell. Used to filter a search.</summary>
    public async Task<IReadOnlyList<SellableProductType>> OwnProductTypesAsync(
        CancellationToken cancellationToken = default)
    {
        var agencyId = _tenant.AgencyId ?? Guid.Empty;

        var parentId = await _db.Agencies
            .AsNoTracking()
            .Where(agency => agency.Id == agencyId)
            .Select(agency => agency.ParentAgencyId)
            .SingleOrDefaultAsync(cancellationToken);

        return parentId is null
            ? Enum.GetValues<SellableProductType>()
            : await _db.SubAgentScopes
                .AsNoTracking()
                .Where(scope => scope.SubAgencyId == agencyId)
                .Select(scope => scope.ProductType)
                .Distinct()
                .ToListAsync(cancellationToken);
    }

    /// <summary>The scopes a principal has granted one of its sub-agents.</summary>
    public async Task<IReadOnlyList<SubAgentScopeRow>> ListAsync(
        Guid subAgencyId,
        CancellationToken cancellationToken = default)
    {
        var principalId = await RequireOwnedAsync(subAgencyId, cancellationToken);

        var scopes = await _db.SubAgentScopes
            .AsNoTracking()
            .Where(scope => scope.AgencyId == principalId && scope.SubAgencyId == subAgencyId)
            .Select(scope => new { scope.Id, scope.ProductType, scope.SupplierId })
            .ToListAsync(cancellationToken);

        // Suppliers are platform reference data, so this join needs no tenant gymnastics.
        var suppliers = await _db.Suppliers
            .AsNoTracking()
            .Select(supplier => new { supplier.Id, supplier.Name })
            .ToListAsync(cancellationToken);

        return
        [
            .. scopes
                .OrderBy(scope => scope.ProductType)
                .Select(scope => new SubAgentScopeRow(
                    scope.Id,
                    scope.ProductType,
                    scope.SupplierId,
                    suppliers.Find(supplier => supplier.Id == scope.SupplierId)?.Name)),
        ];
    }

    /// <summary>Lets a sub-agent sell something. Granting the same thing twice changes nothing.</summary>
    public async Task GrantAsync(
        Guid subAgencyId,
        SellableProductType productType,
        Guid? supplierId,
        CancellationToken cancellationToken = default)
    {
        var principalId = await RequireOwnedAsync(subAgencyId, cancellationToken);

        if (supplierId is { } id
            && !await _db.Suppliers.AsNoTracking().AnyAsync(supplier => supplier.Id == id && supplier.IsActive, cancellationToken))
        {
            throw new SubAgentRefusedException(
                SubAgentRefusal.Invalid,
                "That supplier is not one you can sell through.",
                "A scope can only name a supplier the platform has switched on.");
        }

        var exists = await _db.SubAgentScopes
            .AnyAsync(
                scope => scope.SubAgencyId == subAgencyId
                         && scope.ProductType == productType
                         && scope.SupplierId == supplierId,
                cancellationToken);

        if (exists)
        {
            return;
        }

        _db.SubAgentScopes.Add(SubAgentScope.Grant(principalId, subAgencyId, productType, supplierId));
        await _db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Takes a scope away. The sub-agent stops being able to sell it on its next request.</summary>
    public async Task RevokeAsync(Guid subAgencyId, Guid scopeId, CancellationToken cancellationToken = default)
    {
        var principalId = await RequireOwnedAsync(subAgencyId, cancellationToken);

        var scope = await _db.SubAgentScopes
            .SingleOrDefaultAsync(
                candidate => candidate.Id == scopeId
                             && candidate.AgencyId == principalId
                             && candidate.SubAgencyId == subAgencyId,
                cancellationToken);

        if (scope is null)
        {
            return;
        }

        _db.SubAgentScopes.Remove(scope);
        await _db.SaveChangesAsync(cancellationToken);
    }

    private async Task<Guid> RequireOwnedAsync(Guid subAgencyId, CancellationToken cancellationToken)
    {
        var principalId = _tenant.AgencyId
            ?? throw new SubAgentRefusedException(
                SubAgentRefusal.Forbidden, "This request has no agency, so it has no network.");

        var owned = await _db.Agencies
            .AsNoTracking()
            .AnyAsync(agency => agency.Id == subAgencyId && agency.ParentAgencyId == principalId, cancellationToken);

        return owned
            ? principalId
            : throw new SubAgentRefusedException(
                SubAgentRefusal.NotFound, "That sub-agent is not one of yours.");
    }
}

/// <summary>
/// Raised when an agency is asked to sell something its principal has not allowed it to.
/// </summary>
/// <remarks>
/// Its own type rather than a generic refusal so search, price confirmation and checkout can each
/// turn it into the right answer for their own surface — a 403 with an explanation, rather than an
/// empty result list that looks like the supplier is down.
/// </remarks>
public sealed class SupplierScopeException : Exception
{
    public SupplierScopeException(string message)
        : base(message)
    {
    }

    public SupplierScopeException()
        : base("This agency is not allowed to sell that.")
    {
    }

    public SupplierScopeException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Maps the supplier's product vocabulary onto the one a scope is written in.</summary>
/// <remarks>
/// Two enums on purpose — see <see cref="SellableProductType"/> — so this is the one place that
/// knows a supplier's <c>Flight</c> and a scope's <c>Flight</c> are the same thing.
/// </remarks>
public static class SubAgentScopes
{
    public static SellableProductType For(SupplierProductType productType) => productType switch
    {
        SupplierProductType.Bus => SellableProductType.Bus,
        _ => SellableProductType.Flight,
    };
}
