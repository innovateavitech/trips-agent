using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TripsAgent.Domain.Auditing;
using TripsAgent.Domain.Common;
using TripsAgent.Infrastructure.Auditing;

namespace TripsAgent.UnitTests.Auditing;

/// <summary>An agency record that is audited, standing in for the real ones still to come.</summary>
internal sealed class AuditedAgency : IAuditLogged, ITenantOwnedEntity
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid AgencyId { get; set; }

    public string LegalName { get; set; } = string.Empty;

    public string Status { get; set; } = "pending";

    public string PasswordHash { get; set; } = string.Empty;

    public string PassportNumber { get; set; } = string.Empty;
}

/// <summary>A record that is deliberately not audited, to prove opting in is what matters.</summary>
internal sealed class UnauditedNote
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public string Text { get; set; } = string.Empty;
}

/// <summary>
/// A throwaway context over SQLite, used to exercise a real <c>SaveChanges</c> without a
/// container. What is under test is the interceptor's behaviour — which rows it writes and what
/// it puts in them — not the PostgreSQL mapping, which needs a real server and is covered by
/// TripsAgent.IntegrationTests.
///
/// <see cref="AuditLogEntry"/> is therefore mapped minimally here rather than through
/// <c>AuditLogEntryConfiguration</c>: that configuration asks for a <c>jsonb</c> column in a
/// <c>platform</c> schema, and SQLite has neither.
/// </summary>
internal sealed class AuditTestDbContext(DbContextOptions<AuditTestDbContext> options) : DbContext(options)
{
    public DbSet<AuditedAgency> Agencies => Set<AuditedAgency>();

    public DbSet<UnauditedNote> Notes => Set<UnauditedNote>();

    public DbSet<AuditLogEntry> AuditLogs => Set<AuditLogEntry>();

    /// <summary>
    /// Opens a fresh in-memory database wired to the interceptor.
    ///
    /// The connection is returned so the caller can dispose it: SQLite drops an in-memory
    /// database the moment its last connection closes, so the connection has to outlive the
    /// context for a test to save and then read back.
    /// </summary>
    public static (AuditTestDbContext Context, SqliteConnection Connection) Create(
        AuditSaveChangesInterceptor interceptor)
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<AuditTestDbContext>()
            .UseSqlite(connection)
            .AddInterceptors(interceptor)
            .Options;

        var context = new AuditTestDbContext(options);
        context.Database.EnsureCreated();

        return (context, connection);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<AuditedAgency>();
        modelBuilder.Entity<UnauditedNote>();

        modelBuilder.Entity<AuditLogEntry>(entry =>
        {
            entry.HasKey(log => new { log.Id, log.OccurredAt });
            entry.Property(log => log.ActorType).HasConversion<string>();
        });
    }
}
