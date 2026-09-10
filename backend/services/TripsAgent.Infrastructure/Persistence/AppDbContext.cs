using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using TripsAgent.Domain.Common;
using TripsAgent.Domain.Tenancy;

namespace TripsAgent.Infrastructure.Persistence;

/// <summary>
/// The single EF Core context for the platform.
/// </summary>
/// <remarks>
/// <para>
/// One context, many PostgreSQL schemas (<c>identity</c>, <c>tenancy</c>, <c>orders</c>, …).
/// Splitting into a context per bounded context would buy isolation we do not need and cost us
/// the one thing we do need: a single transaction spanning a wallet debit and an order line.
/// </para>
/// <para>
/// Entities are configured with <c>IEntityTypeConfiguration&lt;T&gt;</c> classes under
/// <c>Persistence/Configurations/</c>, never with attributes on the domain type. That keeps
/// <c>TripsAgent.Domain</c> free of any EF Core reference — an architecture test fails the build
/// if that slips.
/// </para>
/// </remarks>
public class AppDbContext : DbContext
{
    private readonly TimeProvider _clock;

    public AppDbContext(DbContextOptions<AppDbContext> options, TimeProvider clock)
        : base(options)
    {
        _clock = clock;
    }

    /// <summary>Travel businesses — the tenant every other business row belongs to.</summary>
    public DbSet<Agency> Agencies => Set<Agency>();

    /// <summary>Per-agency operational preferences. One row per agency.</summary>
    public DbSet<AgencySettings> AgencySettings => Set<AgencySettings>();

    /// <summary>Per-agency logo, colours and contact details. One row per agency.</summary>
    public DbSet<AgencyBranding> AgencyBranding => Set<AgencyBranding>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Extensions the schema depends on. EF emits CREATE EXTENSION IF NOT EXISTS for each
        // in the migration, so a fresh database is usable without any manual psql step.
        //   citext — case-insensitive email, so Ada@x.com and ada@x.com cannot both register
        //   ltree  — the agency hierarchy path, GIST-indexed for subtree queries
        modelBuilder.HasPostgresExtension("citext");
        modelBuilder.HasPostgresExtension("ltree");

        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        base.ConfigureConventions(configurationBuilder);

        // Money is a long in the database, always. See MoneyConverter for why.
        Conventions.MoneyConventions.Apply(configurationBuilder);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        StampTimestamps();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        StampTimestamps();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    /// <summary>
    /// Sets <c>CreatedAt</c>/<c>UpdatedAt</c> on every touched auditable entity.
    /// </summary>
    /// <remarks>
    /// Done here rather than in each handler so it cannot be forgotten. The time comes from an
    /// injected <see cref="TimeProvider"/>, not <c>DateTimeOffset.UtcNow</c>, so a test can pin
    /// the clock and assert on expiry windows — OTP codes, ticket time limits, refresh tokens —
    /// without sleeping.
    /// </remarks>
    private void StampTimestamps()
    {
        var now = _clock.GetUtcNow();

        foreach (EntityEntry<IAuditableEntity> entry in ChangeTracker.Entries<IAuditableEntity>())
        {
            switch (entry.State)
            {
                case EntityState.Added:
                    entry.Entity.CreatedAt = now;
                    entry.Entity.UpdatedAt = now;
                    break;

                case EntityState.Modified:
                    entry.Entity.UpdatedAt = now;

                    // Guard against a detached-then-attached entity rewriting its own birthday.
                    entry.Property(e => e.CreatedAt).IsModified = false;
                    break;
            }
        }
    }
}
