using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.Logging.Abstractions;
using TripsAgent.Application.Tenancy;
using TripsAgent.Domain.Common;
using TripsAgent.Domain.Tenancy;
using TripsAgent.Infrastructure.Persistence;
using TripsAgent.Infrastructure.Tenancy;

namespace TripsAgent.UnitTests.Persistence;

/// <summary>
/// Fails the build if a tenant-scoped entity reaches the model without a query filter.
/// </summary>
/// <remarks>
/// <para>
/// This is the cheapest possible insurance against the most expensive bug in the system. Filters
/// are applied by convention over <see cref="ITenantScoped"/>, so in principle nothing can be
/// missed — but conventions get edited, someone adds an entity that shadows the marker, and the
/// failure mode is silent: the query works, returns more rows than it should, and nobody notices
/// until an agency sees a competitor's prices.
/// </para>
/// <para>
/// So the model is enumerated and checked directly rather than trusted.
/// </para>
/// </remarks>
public class TenantFilterCoverageTests
{
    [Fact]
    public void Every_tenant_scoped_entity_has_a_query_filter()
    {
        var unfiltered = TenantScopedEntities()
            .Where(entity => entity.GetDeclaredQueryFilters().Count == 0)
            .Select(entity => entity.DisplayName())
            .ToArray();

        unfiltered.Should().BeEmpty(
            $"""
             every entity implementing ITenantScoped must be filtered by agency.

             Unfiltered: {string.Join(", ", unfiltered)}

             A missing filter means a query returns every agency's rows, which is the leak
             CLAUDE.md rule 3 exists to prevent. Filters are applied by convention in
             AppDbContext.ApplyTenantQueryFilters — check the entity really implements
             ITenantScoped.
             """);
    }

    [Fact]
    public void There_is_at_least_one_tenant_scoped_entity_to_check()
    {
        // Without this, the test above passes triumphantly on an empty model and tells us
        // nothing at all.
        TenantScopedEntities().Should().NotBeEmpty();
    }

    [Fact]
    public void Every_tenant_scoped_entity_has_an_agency_id_column()
    {
        foreach (var entity in TenantScopedEntities())
        {
            entity.FindProperty(nameof(ITenantScoped.AgencyId))
                .Should().NotBeNull($"{entity.DisplayName()} is tenant-scoped, so it must map agency_id");
        }
    }

    [Fact]
    public void Every_tenant_scoped_entity_indexes_agency_id_first()
    {
        // Every tenant-scoped read carries `WHERE agency_id = …`, so an index that does not lead
        // with agency_id cannot serve it. This is a performance rule rather than a safety one,
        // but it is far cheaper to enforce now than to retrofit across a live schema.
        var missing = new List<string>();

        foreach (var entity in TenantScopedEntities())
        {
            var leadsWithAgencyId = entity.GetIndexes()
                .Any(index => index.Properties[0].Name == nameof(ITenantScoped.AgencyId));

            if (!leadsWithAgencyId)
            {
                missing.Add(entity.DisplayName());
            }
        }

        missing.Should().BeEmpty(
            $"these tenant-scoped entities have no index leading with agency_id: {string.Join(", ", missing)}");
    }

    [Fact]
    public void The_agency_table_itself_is_filtered()
    {
        // Agency is not ITenantScoped — it *is* the tenant — so the convention above skips it.
        // It still needs a filter, or `context.Agencies.ToList()` hands back every travel
        // business on the platform.
        Model().FindEntityType(typeof(Agency))!
            .GetDeclaredQueryFilters().Should().NotBeEmpty();
    }

    private static IEnumerable<IEntityType> TenantScopedEntities() =>
        Model().GetEntityTypes()
            .Where(entity => typeof(ITenantScoped).IsAssignableFrom(entity.ClrType));

    private static IModel Model()
    {
        var tenant = new TenantContext();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=model-building-only;Database=none")
            .UseSnakeCaseNamingConvention()
            .Options;

        using var context = new AppDbContext(
            options,
            TimeProvider.System,
            tenant,
            new PlatformScope(tenant, NullLogger<PlatformScope>.Instance));

        return context.GetService<IDesignTimeModel>().Model;
    }
}
