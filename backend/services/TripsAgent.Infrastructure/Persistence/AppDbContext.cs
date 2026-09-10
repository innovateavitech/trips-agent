using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using TripsAgent.Application.Auditing;
using TripsAgent.Domain.Auditing;
using TripsAgent.Domain.Common;

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
    private readonly IAuditContext? _auditContext;

    /// <param name="options">Provider and connection.</param>
    /// <param name="clock">Source of every timestamp this context stamps.</param>
    /// <param name="auditContext">
    /// The current actor, which the audit log's query filter reads. Optional so that tooling and
    /// tests which construct a context by hand keep working; the application always supplies it
    /// through dependency injection. Without one, the context behaves as a platform-wide caller.
    /// </param>
    public AppDbContext(
        DbContextOptions<AppDbContext> options,
        TimeProvider clock,
        IAuditContext? auditContext = null)
        : base(options)
    {
        _clock = clock;
        _auditContext = auditContext;
    }

    /// <summary>
    /// The platform audit trail. Append-only — the database rejects updates and deletes — so this
    /// set is for reading and for the interceptor that writes it, and nothing else.
    /// </summary>
    public DbSet<AuditLogEntry> AuditLogs => Set<AuditLogEntry>();

    /// <summary>
    /// The agency whose audit rows the caller may see, or null for a platform-wide caller.
    /// </summary>
    /// <remarks>
    /// Read by the query filter below. It has to be an instance member of the context for EF to
    /// re-read it per instance; a captured local would be baked into the cached model, and every
    /// later request would be filtered by whoever happened to make the first one.
    /// </remarks>
    private Guid? CurrentAgencyId => _auditContext?.AgencyId;

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

        // An agency sees its own audit trail and nothing else. Platform-wide rows carry no
        // agency_id and are visible only to a caller with no agency of their own — a Trips
        // back-office user or a background job.
        //
        // This is the audit log's own filter, not the tenancy mechanism. ITenantContext and the
        // ITenantOwnedEntity filters are #11; when they land, the agency here should come from
        // there rather than from IAuditContext.
        modelBuilder.Entity<AuditLogEntry>()
            .HasQueryFilter(entry => CurrentAgencyId == null || entry.AgencyId == CurrentAgencyId);
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
