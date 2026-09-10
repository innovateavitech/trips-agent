using System.Runtime.CompilerServices;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;
using Npgsql;
using TripsAgent.Domain.Common;
using TripsAgent.Infrastructure.Persistence;

namespace TripsAgent.IntegrationTests.Persistence;

/// <summary>
/// Covers the timestamp stamping in <see cref="AppDbContext"/>: every auditable row records
/// when it was created and last changed, without any handler having to remember to set them.
/// </summary>
/// <remarks>
/// <para>
/// There is no auditable entity in the model yet — the first ones arrive with the agencies (#10)
/// and identity (#13) schemas. Rather than wait, this test adds a probe entity to the <b>real</b>
/// <see cref="AppDbContext"/> through EF's <see cref="IModelCustomizer"/> seam, so the behaviour
/// under test is the production code path and not a re-implementation of it.
/// </para>
/// <para>
/// The clock is a stub, so "the updated timestamp moved" is asserted against an exact value
/// rather than by sleeping and hoping.
/// </para>
/// </remarks>
[Collection(PostgresCollection.Name)]
public class AuditTimestampTests
{
    private static readonly DateTimeOffset Created = new(2026, 3, 1, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Updated = new(2026, 3, 2, 9, 30, 0, TimeSpan.Zero);

    private readonly PostgresFixture _postgres;

    public AuditTimestampTests(PostgresFixture postgres) => _postgres = postgres;

    [Fact]
    public async Task Inserting_stamps_both_timestamps_from_the_clock()
    {
        var clock = new StubClock(Created);
        await using var context = await NewProbeContextAsync(clock);

        var probe = new AuditableProbe { Name = "first" };
        context.Add(probe);
        await context.SaveChangesAsync();

        probe.CreatedAt.Should().Be(Created);
        probe.UpdatedAt.Should().Be(Created);
    }

    [Fact]
    public async Task Updating_moves_only_the_updated_timestamp()
    {
        var clock = new StubClock(Created);
        await using var context = await NewProbeContextAsync(clock);

        var probe = new AuditableProbe { Name = "before" };
        context.Add(probe);
        await context.SaveChangesAsync();

        clock.Now = Updated;
        probe.Name = "after";
        await context.SaveChangesAsync();

        probe.UpdatedAt.Should().Be(Updated);
        probe.CreatedAt.Should().Be(Created, "a row does not get a new birthday when it is edited");
    }

    [Fact]
    public async Task An_edit_cannot_rewrite_the_created_timestamp()
    {
        var clock = new StubClock(Created);
        await using var context = await NewProbeContextAsync(clock);

        var probe = new AuditableProbe { Name = "original" };
        context.Add(probe);
        await context.SaveChangesAsync();

        // Someone assigns CreatedAt by hand — deliberately or by mapping a DTO over the entity.
        clock.Now = Updated;
        probe.CreatedAt = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
        probe.Name = "tampered";
        await context.SaveChangesAsync();

        context.ChangeTracker.Clear();
        var reloaded = await context.Set<AuditableProbe>().SingleAsync(p => p.Id == probe.Id);

        reloaded.CreatedAt.Should().Be(Created, "the change is suppressed before it reaches the database");
        reloaded.UpdatedAt.Should().Be(Updated);
    }

    [Fact]
    public async Task Timestamps_are_stored_as_UTC()
    {
        var clock = new StubClock(Created);
        await using var context = await NewProbeContextAsync(clock);

        context.Add(new AuditableProbe { Name = "utc" });
        await context.SaveChangesAsync();

        await using var connection = new NpgsqlConnection(context.Database.GetConnectionString());
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT created_at FROM auditable_probe LIMIT 1";

        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        var stored = await reader.GetFieldValueAsync<DateTimeOffset>(0);

        stored.Offset.Should().Be(TimeSpan.Zero);
        stored.Should().Be(Created);
    }

    /// <summary>A fresh database with the probe table created, plus a clock the test controls.</summary>
    private async Task<AppDbContext> NewProbeContextAsync(TimeProvider clock, [CallerMemberName] string testName = "")
    {
        var databaseName = testName.ToLowerInvariant();
        databaseName = databaseName[..Math.Min(databaseName.Length, 60)];

        await using (var setup = await _postgres.CreateEmptyDatabaseAsync(databaseName))
        {
            await setup.Database.MigrateAsync();
        }

        var builder = new NpgsqlConnectionStringBuilder(_postgres.ConnectionString) { Database = databaseName };

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(builder.ConnectionString)
            .UseSnakeCaseNamingConvention()
            .ReplaceService<IModelCustomizer, ProbeModelCustomizer>()
            .ReplaceService<IModelCacheKeyFactory, FreshModelFactory>()
            .Options;

        var tenancy = TestTenancy.None();
        var context = new AppDbContext(options, clock, tenancy.Tenant, tenancy.Scope);

        await context.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE auditable_probe (
                id          uuid PRIMARY KEY,
                name        text NOT NULL,
                created_at  timestamptz NOT NULL,
                updated_at  timestamptz NOT NULL
            );
            """);

        return context;
    }

    /// <summary>Adds the probe entity to the real context's model.</summary>
    private sealed class ProbeModelCustomizer : ModelCustomizer
    {
        public ProbeModelCustomizer(ModelCustomizerDependencies dependencies)
            : base(dependencies)
        {
        }

        public override void Customize(ModelBuilder modelBuilder, DbContext context)
        {
            // Runs AppDbContext.OnModelCreating first, so the probe joins the real model rather
            // than replacing it.
            base.Customize(modelBuilder, context);

            modelBuilder.Entity<AuditableProbe>().ToTable("auditable_probe");
        }
    }

    /// <summary>See the equivalent in the unit tests: EF keys its model cache on context type alone.</summary>
    private sealed class FreshModelFactory : IModelCacheKeyFactory
    {
        public object Create(DbContext context, bool designTime) =>
            (context?.GetType(), Guid.NewGuid(), designTime);
    }

    private sealed class AuditableProbe : Entity, IAuditableEntity
    {
        public string Name { get; set; } = string.Empty;

        public DateTimeOffset CreatedAt { get; set; }

        public DateTimeOffset UpdatedAt { get; set; }
    }

    private sealed class StubClock : TimeProvider
    {
        public StubClock(DateTimeOffset now) => Now = now;

        public DateTimeOffset Now { get; set; }

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
