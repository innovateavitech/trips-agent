using System.Runtime.CompilerServices;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using TripsAgent.Domain.Auditing;
using TripsAgent.Domain.Common;
using TripsAgent.Infrastructure.Auditing;
using TripsAgent.Infrastructure.Persistence;
using TripsAgent.IntegrationTests.Persistence;

namespace TripsAgent.IntegrationTests.Auditing;

/// <summary>
/// The whole path against the real schema: an audited entity is saved through the real
/// <see cref="AppDbContext"/>, the interceptor writes a row, and that row lands in a partition of
/// <c>platform.audit_logs</c> with its state stored as <c>jsonb</c> and its secrets stripped.
/// </summary>
/// <remarks>
/// The unit tests cover the interceptor's decisions against SQLite. This covers what SQLite
/// cannot: the partitioned table, the jsonb column, and the real mapping. No audited entity exists
/// in the model yet, so a probe joins the real context through <see cref="IModelCustomizer"/> —
/// the same seam AuditTimestampTests uses.
/// </remarks>
[Collection(PostgresCollection.Name)]
public class AuditInterceptorEndToEndTests
{
    private readonly PostgresFixture _postgres;

    public AuditInterceptorEndToEndTests(PostgresFixture postgres) => _postgres = postgres;

    [Fact]
    public async Task Saving_an_audited_entity_writes_a_redacted_row_to_the_partitioned_table()
    {
        await using var context = await ProbeContextAsync();
        var probe = new AuditedProbe
        {
            AgencyId = Guid.CreateVersion7(),
            Name = "Kano Travels Ltd",
            PasswordHash = "$2a$12$abcdefghijklmnopqrstuv",
        };

        context.Add(probe);
        await context.SaveChangesAsync();

        var entry = await context.AuditLogs.AsNoTracking().SingleAsync();

        entry.EntityType.Should().Be(nameof(AuditedProbe));
        entry.EntityId.Should().Be(probe.Id.ToString());
        entry.AgencyId.Should().Be(probe.AgencyId);
        entry.Action.Should().Be(AuditActions.Created);
        entry.AfterState.Should().Contain("Kano Travels Ltd");
        entry.AfterState.Should().Contain(AuditRedactionPolicy.RedactedPlaceholder);
        entry.AfterState.Should().NotContain("2a$12", "a password hash never reaches the audit log");
    }

    /// <summary>A migrated database, the probe table, and the real context with the interceptor.</summary>
    private async Task<AppDbContext> ProbeContextAsync([CallerMemberName] string testName = "")
    {
        await using (var setup = await AuditDatabase.MigratedAsync(_postgres, testName))
        {
            await setup.Database.ExecuteSqlRawAsync(
                """
                CREATE TABLE audited_probe (
                    id             uuid PRIMARY KEY,
                    agency_id      uuid NOT NULL,
                    name           text NOT NULL,
                    password_hash  text NOT NULL
                );
                """);

            var actor = new FixedAuditContext(agencyId: null, AuditActorType.System);
            var interceptor = new AuditSaveChangesInterceptor(actor, new AuditRedactionPolicy(), TimeProvider.System);

            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseNpgsql(setup.Database.GetConnectionString())
                .UseSnakeCaseNamingConvention()
                .AddInterceptors(interceptor)
                .ReplaceService<IModelCustomizer, ProbeModelCustomizer>()
                .ReplaceService<IModelCacheKeyFactory, FreshModelFactory>()
                .Options;

            return new AppDbContext(options, TimeProvider.System, actor);
        }
    }

    private sealed class ProbeModelCustomizer : ModelCustomizer
    {
        public ProbeModelCustomizer(ModelCustomizerDependencies dependencies)
            : base(dependencies)
        {
        }

        public override void Customize(ModelBuilder modelBuilder, DbContext context)
        {
            // AppDbContext.OnModelCreating runs first, so the probe joins the real model —
            // audit_logs mapping and query filter included — rather than replacing it.
            base.Customize(modelBuilder, context);

            modelBuilder.Entity<AuditedProbe>().ToTable("audited_probe");
        }
    }

    /// <summary>EF caches the model per context type; the probe needs a model of its own.</summary>
    private sealed class FreshModelFactory : IModelCacheKeyFactory
    {
        public object Create(DbContext context, bool designTime) =>
            (context?.GetType(), Guid.NewGuid(), designTime);
    }

    private sealed class AuditedProbe : Entity, IAuditLogged, ITenantOwnedEntity
    {
        public Guid AgencyId { get; set; }

        public string Name { get; set; } = string.Empty;

        public string PasswordHash { get; set; } = string.Empty;
    }
}
