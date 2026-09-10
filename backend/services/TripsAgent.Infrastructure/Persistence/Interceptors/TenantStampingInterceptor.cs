using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using TripsAgent.Application.Tenancy;
using TripsAgent.Domain.Common;

namespace TripsAgent.Infrastructure.Persistence.Interceptors;

/// <summary>
/// Fills in <c>agency_id</c> on every new tenant-scoped row, and refuses any attempt to write a
/// row into a different agency.
/// </summary>
/// <remarks>
/// <para>
/// Two jobs, both about the same failure. The query filter stops you <i>reading</i> another
/// agency's rows; nothing in EF stops you <i>writing</i> one. A handler that forgets to set
/// <c>AgencyId</c> would insert a row with an empty tenant — invisible to its owner and, worse,
/// potentially visible to whoever the filter happens to match.
/// </para>
/// <para>
/// So the interceptor stamps the value from <see cref="ITenantContext"/>, and throws if the entity
/// already carries a different one. That second check is the one that matters: it turns a silent
/// cross-tenant write into a loud failure at the point it happens.
/// </para>
/// </remarks>
public sealed class TenantStampingInterceptor : SaveChangesInterceptor
{
    private readonly ITenantContext _tenantContext;
    private readonly IPlatformScope _platformScope;

    public TenantStampingInterceptor(ITenantContext tenantContext, IPlatformScope platformScope)
    {
        _tenantContext = tenantContext;
        _platformScope = platformScope;
    }

    /// <summary>The shadow-safe property name every tenant-scoped entity exposes.</summary>
    private const string AgencyIdProperty = nameof(ITenantScoped.AgencyId);

    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData,
        InterceptionResult<int> result)
    {
        ArgumentNullException.ThrowIfNull(eventData);

        Stamp(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventData);

        Stamp(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private void Stamp(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        foreach (var entry in context.ChangeTracker.Entries<ITenantScoped>())
        {
            switch (entry.State)
            {
                case EntityState.Added:
                    StampInsert(entry);
                    break;

                case EntityState.Modified:
                    GuardAgainstTenantChange(entry);
                    break;

                default:
                    break;
            }
        }
    }

    private void StampInsert(EntityEntry<ITenantScoped> entry)
    {
        var property = entry.Property(AgencyIdProperty);
        var assigned = (Guid?)property.CurrentValue ?? Guid.Empty;

        // A platform-scoped operation — seeding, an admin import — has no tenant of its own, so
        // whatever the entity carries is what it meant. It must still carry something.
        if (!_tenantContext.HasTenant)
        {
            if (assigned == Guid.Empty && !_platformScope.IsActive)
            {
                throw new InvalidOperationException(
                    $"""
                     Cannot insert {entry.Entity.GetType().Name}: no agency on the entity and no
                     tenant resolved for this request.

                     A row with an empty agency_id belongs to nobody — its owner will never see it.
                     Either populate ITenantContext (middleware does this from the caller's token),
                     or set AgencyId explicitly if this is a platform operation.
                     """);
            }

            return;
        }

        var currentTenant = _tenantContext.AgencyId!.Value;

        if (assigned == Guid.Empty)
        {
            property.CurrentValue = currentTenant;
            return;
        }

        if (assigned != currentTenant && !_platformScope.IsActive)
        {
            throw new InvalidOperationException(
                $"""
                 Refusing to insert {entry.Entity.GetType().Name} into agency {assigned} while
                 acting as agency {currentTenant}.

                 Writing a row into another agency is how one travel agency's data ends up in
                 another's account. If this is deliberate platform work, do it inside
                 IPlatformScope.Enter(reason) so it is recorded.
                 """);
        }
    }

    private void GuardAgainstTenantChange(EntityEntry<ITenantScoped> entry)
    {
        var property = entry.Property(AgencyIdProperty);

        if (!property.IsModified || _platformScope.IsActive)
        {
            return;
        }

        throw new InvalidOperationException(
            $"""
             Refusing to move {entry.Entity.GetType().Name} from agency {property.OriginalValue}
             to agency {property.CurrentValue}.

             Rows do not change owner. If a genuine transfer is ever needed it belongs in an
             explicit, audited operation — not a stray property assignment.
             """);
    }
}
