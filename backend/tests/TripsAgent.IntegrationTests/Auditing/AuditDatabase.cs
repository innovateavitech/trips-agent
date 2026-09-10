using Microsoft.EntityFrameworkCore;
using TripsAgent.Application.Auditing;
using TripsAgent.Domain.Auditing;
using TripsAgent.Infrastructure.Persistence;
using TripsAgent.IntegrationTests.Persistence;

namespace TripsAgent.IntegrationTests.Auditing;

/// <summary>Shared set-up for the audit log tests, on top of <see cref="PostgresFixture"/>.</summary>
internal static class AuditDatabase
{
    /// <summary>A fresh database with every migration applied, so the real audit schema exists.</summary>
    public static async Task<AppDbContext> MigratedAsync(PostgresFixture postgres, string testName)
    {
        // Lower-cased and truncated for the same reasons as MigrationTests: unquoted identifiers
        // fold to lower case, and PostgreSQL stops at 63 bytes.
        var name = testName.ToLowerInvariant();
        name = name[..Math.Min(name.Length, 60)];

        var context = await postgres.CreateEmptyDatabaseAsync(name);
        await context.Database.MigrateAsync();
        return context;
    }

    /// <summary>A second context on the same database, acting as <paramref name="actor"/>.</summary>
    public static AppDbContext As(AppDbContext existing, IAuditContext actor)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(existing.Database.GetConnectionString())
            .UseSnakeCaseNamingConvention()
            .Options;

        return new AppDbContext(options, TimeProvider.System, actor);
    }

    /// <summary>A minimal valid row. The time comes from the clock, never DateTimeOffset.UtcNow.</summary>
    public static AuditLogEntry NewEntry(Guid? agencyId = null) => new()
    {
        OccurredAt = TimeProvider.System.GetUtcNow(),
        AgencyId = agencyId,
        ActorType = AuditActorType.System,
        Action = AuditActions.Created,
        EntityType = "Agency",
        EntityId = Guid.CreateVersion7().ToString(),
        AfterState = """{"LegalName":"Kano Travels Ltd"}""",
    };

    /// <summary>The names of every partition currently attached to platform.audit_logs.</summary>
    public static Task<List<string>> PartitionNamesAsync(DbContext context) =>
        context.Database
            .SqlQuery<string>(
                $"""
                 SELECT child.relname::text AS "Value"
                 FROM pg_inherits i
                 JOIN pg_class child ON child.oid = i.inhrelid
                 JOIN pg_class parent ON parent.oid = i.inhparent
                 JOIN pg_namespace n ON n.oid = parent.relnamespace
                 WHERE n.nspname = 'platform' AND parent.relname = 'audit_logs'
                 """)
            .ToListAsync();
}

/// <summary>An actor fixed for the life of a test.</summary>
internal sealed class FixedAuditContext(Guid? agencyId, AuditActorType actorType) : IAuditContext
{
    public Guid? ActorUserId => null;

    public AuditActorType ActorType => actorType;

    public string? ActorIpAddress => null;

    public Guid? AgencyId => agencyId;

    public string? CorrelationId => null;

    public string? Reason => null;

    public void SetReason(string? reason)
    {
        // Fixed on purpose; these tests are not about reasons.
    }
}
