using Microsoft.Extensions.Logging.Abstractions;
using TripsAgent.Application.Tenancy;
using TripsAgent.Infrastructure.Tenancy;

namespace TripsAgent.IntegrationTests.Persistence;

/// <summary>
/// Builds the tenant services a <c>AppDbContext</c> needs, without a web request behind them.
/// </summary>
internal static class TestTenancy
{
    /// <summary>A request acting as <paramref name="agencyId"/>.</summary>
    internal static (TenantContext Tenant, PlatformScope Scope) For(Guid agencyId, Guid? rootAgencyId = null)
    {
        var tenant = new TenantContext();
        tenant.SetTenant(agencyId, rootAgencyId);

        return (tenant, NewScope(tenant));
    }

    /// <summary>
    /// A context with no tenant resolved — what a migration, a background job or an
    /// unauthenticated request looks like. Tenant-scoped queries return nothing.
    /// </summary>
    internal static (TenantContext Tenant, PlatformScope Scope) None()
    {
        var tenant = new TenantContext();
        return (tenant, NewScope(tenant));
    }

    private static PlatformScope NewScope(TenantContext tenant) =>
        new(tenant, NullLogger<PlatformScope>.Instance);
}
