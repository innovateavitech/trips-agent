using System.Runtime.CompilerServices;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using TripsAgent.Domain.Auditing;
using TripsAgent.Infrastructure.Persistence;
using TripsAgent.IntegrationTests.Persistence;

namespace TripsAgent.IntegrationTests.Auditing;

/// <summary>
/// What the AddPlatformAuditLog migration actually built. Partitioning, the append-only guard and
/// the indexes are raw SQL that EF Core cannot express and therefore cannot verify — so these run
/// against a real PostgreSQL and interrogate the catalogue, not the model.
/// </summary>
[Collection(PostgresCollection.Name)]
public class AuditLogSchemaTests
{
    private readonly PostgresFixture _postgres;

    public AuditLogSchemaTests(PostgresFixture postgres) => _postgres = postgres;

    [Fact]
    public async Task The_table_is_range_partitioned()
    {
        await using var context = await MigratedAsync();

        var strategy = await context.Database
            .SqlQuery<string>(
                $"""
                 SELECT p.partstrat::text AS "Value"
                 FROM pg_partitioned_table p
                 JOIN pg_class c ON c.oid = p.partrelid
                 JOIN pg_namespace n ON n.oid = c.relnamespace
                 WHERE n.nspname = 'platform' AND c.relname = 'audit_logs'
                 """)
            .SingleOrDefaultAsync();

        strategy.Should().Be("r", "'r' is PostgreSQL's marker for RANGE partitioning");
    }

    [Fact]
    public async Task Partitions_exist_for_this_month_and_the_next()
    {
        // Without one for this month the first insert after deploying fails; the next month's is
        // the runway that stops midnight on the 1st from being an outage.
        await using var context = await MigratedAsync();
        var now = TimeProvider.System.GetUtcNow();

        var partitions = await AuditDatabase.PartitionNamesAsync(context);

        partitions.Should().Contain($"audit_logs_{now:yyyy_MM}");
        partitions.Should().Contain($"audit_logs_{now.AddMonths(1):yyyy_MM}");
    }

    [Fact]
    public async Task An_entry_lands_in_the_partition_for_its_month()
    {
        await using var context = await MigratedAsync();
        var entry = AuditDatabase.NewEntry();

        context.AuditLogs.Add(entry);
        await context.SaveChangesAsync();

        var partition = await context.Database
            .SqlQuery<string>(
                $"""SELECT tableoid::regclass::text AS "Value" FROM platform.audit_logs WHERE id = {entry.Id}""")
            .SingleAsync();

        partition.Should().Be($"platform.audit_logs_{entry.OccurredAt:yyyy_MM}");
    }

    [Fact]
    public async Task Updating_an_entry_is_refused_by_the_database()
    {
        await using var context = await MigratedAsync();
        var entry = await SavedEntryAsync(context);

        var update = async () => await context.Database.ExecuteSqlAsync(
            $"UPDATE platform.audit_logs SET reason = 'tampered' WHERE id = {entry.Id}");

        (await update.Should().ThrowAsync<Exception>()).Which.Message.Should().Contain("append-only");
    }

    [Fact]
    public async Task Deleting_an_entry_is_refused_by_the_database()
    {
        await using var context = await MigratedAsync();
        var entry = await SavedEntryAsync(context);

        var delete = async () => await context.Database.ExecuteSqlAsync(
            $"DELETE FROM platform.audit_logs WHERE id = {entry.Id}");

        (await delete.Should().ThrowAsync<Exception>()).Which.Message.Should().Contain("append-only");
    }

    [Fact]
    public async Task Updating_a_partition_directly_is_refused_too()
    {
        // The guard is a row-level trigger on the parent precisely so PostgreSQL clones it onto
        // every partition. A statement-level trigger would not fire on this route, and a REVOKE on
        // the parent alone would not cover it either.
        await using var context = await MigratedAsync();
        var entry = await SavedEntryAsync(context);

        // Concatenated rather than interpolated into ExecuteSqlRaw: the analyser rightly objects
        // to interpolation there, and this is a table name we computed, not input.
        var sql = "UPDATE platform.audit_logs_" + entry.OccurredAt.ToString("yyyy_MM", null)
                  + " SET reason = 'tampered'";
        var update = async () => await context.Database.ExecuteSqlRawAsync(sql);

        (await update.Should().ThrowAsync<Exception>()).Which.Message.Should().Contain("append-only");
    }

    [Fact]
    public async Task The_indexes_the_acceptance_criteria_ask_for_exist()
    {
        await using var context = await MigratedAsync();

        var indexes = await context.Database
            .SqlQuery<string>(
                $"""
                 SELECT indexname::text AS "Value"
                 FROM pg_indexes
                 WHERE schemaname = 'platform' AND tablename = 'audit_logs'
                 """)
            .ToListAsync();

        indexes.Should().Contain("ix_audit_logs_entity", "\"what happened to this entity\"");
        indexes.Should().Contain("ix_audit_logs_actor", "\"what did this user do\"");
        indexes.Should().Contain("ix_audit_logs_agency", "an agency reading its own trail");
    }

    [Fact]
    public async Task An_agency_sees_only_its_own_audit_rows()
    {
        await using var platform = await MigratedAsync();
        var kano = Guid.CreateVersion7();
        var lagos = Guid.CreateVersion7();

        platform.AuditLogs.AddRange(
            AuditDatabase.NewEntry(kano),
            AuditDatabase.NewEntry(lagos),
            AuditDatabase.NewEntry(agencyId: null));
        await platform.SaveChangesAsync();

        await using var asKano = AuditDatabase.As(platform, new FixedAuditContext(kano, AuditActorType.User));

        var visible = await asKano.AuditLogs.Select(entry => entry.AgencyId).ToListAsync();

        visible.Should().ContainSingle().Which.Should().Be(kano,
            "another agency's rows and platform-wide rows are both out of bounds");
    }

    [Fact]
    public async Task A_platform_wide_caller_sees_every_row()
    {
        await using var platform = await MigratedAsync();

        platform.AuditLogs.AddRange(
            AuditDatabase.NewEntry(Guid.CreateVersion7()),
            AuditDatabase.NewEntry(agencyId: null));
        await platform.SaveChangesAsync();

        await using var asBackOffice = AuditDatabase.As(
            platform, new FixedAuditContext(agencyId: null, AuditActorType.PlatformAdmin));

        (await asBackOffice.AuditLogs.CountAsync()).Should().Be(2);
    }

    private static async Task<AuditLogEntry> SavedEntryAsync(AppDbContext context)
    {
        var entry = AuditDatabase.NewEntry();
        context.AuditLogs.Add(entry);
        await context.SaveChangesAsync();
        return entry;
    }

    private Task<AppDbContext> MigratedAsync([CallerMemberName] string testName = "") =>
        AuditDatabase.MigratedAsync(_postgres, testName);
}
