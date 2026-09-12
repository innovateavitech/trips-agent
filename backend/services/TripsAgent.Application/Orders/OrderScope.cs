using Microsoft.EntityFrameworkCore;
using TripsAgent.Application.Persistence;
using TripsAgent.Application.Tenancy;

namespace TripsAgent.Application.Orders;

/// <summary>
/// The rule the booking hooks share about <i>whose</i> order they may touch: whatever the caller's
/// own scope can see, and nothing more.
/// </summary>
/// <remarks>
/// The hooks never open a platform scope of their own. Called from an agency's request, the tenant
/// filter decides, and another agency's order is simply not found. Called from the Worker, the
/// caller is already inside the scope it will save in — and that scope is what lets the rows staged
/// here be saved at all, because row-level security checks every insert.
/// </remarks>
internal static class OrderScope
{
    /// <summary>The agency <paramref name="orderId"/> belongs to, or null when the caller cannot see it.</summary>
    public static async Task<Guid?> AgencyOfAsync(
        IAppDbContext db,
        ITenantContext tenant,
        IPlatformScope platformScope,
        Guid orderId,
        CancellationToken cancellationToken)
    {
        EnsureScoped(tenant, platformScope);

        return await db.Orders
            .AsNoTracking()
            .Where(order => order.Id == orderId)
            .Select(order => (Guid?)order.AgencyId)
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <exception cref="InvalidOperationException">The caller is acting for no agency and has no platform scope.</exception>
    public static void EnsureScoped(ITenantContext tenant, IPlatformScope platformScope)
    {
        if (!tenant.HasTenant && !platformScope.IsActive)
        {
            throw new InvalidOperationException(
                """
                Call this acting as the order's agency (ITenantContext), or inside IPlatformScope — the
                same scope you are going to save in. With neither, the order cannot be read, and nothing
                staged here could be saved: row-level security would refuse the insert.
                """);
        }
    }
}
