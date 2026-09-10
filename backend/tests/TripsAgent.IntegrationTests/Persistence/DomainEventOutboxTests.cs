using System.Runtime.CompilerServices;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;
using Npgsql;
using TripsAgent.Domain.Common;
using TripsAgent.Infrastructure.Messaging;
using TripsAgent.Infrastructure.Persistence;

namespace TripsAgent.IntegrationTests.Persistence;

/// <summary>
/// Proves the transactional outbox at the point it actually matters: <see cref="AppDbContext"/>
/// writing an aggregate's own change and its domain event in the <b>same</b> database transaction.
/// </summary>
/// <remarks>
/// There is no real aggregate in the model yet, so — following the same pattern
/// <see cref="AuditTimestampTests"/> uses — this adds a probe aggregate to the <b>real</b>
/// <see cref="AppDbContext"/> through EF's <see cref="IModelCustomizer"/> seam. The behaviour under
/// test is <c>AppDbContext.CaptureDomainEvents</c> exactly as production code runs it, not a
/// re-implementation of it.
/// </remarks>
[Collection(PostgresCollection.Name)]
public class DomainEventOutboxTests
{
    private static readonly DateTimeOffset Now = new(2026, 3, 1, 8, 0, 0, TimeSpan.Zero);

    private readonly PostgresFixture _postgres;

    public DomainEventOutboxTests(PostgresFixture postgres) => _postgres = postgres;

    [Fact]
    public async Task Saving_an_aggregate_that_raised_an_event_writes_both_rows_in_one_transaction()
    {
        await using var context = await NewProbeContextAsync();

        var probe = new ProbeAggregate();
        probe.DoSomething("first");
        context.Add(probe);
        await context.SaveChangesAsync();

        var outboxRow = await context.OutboxMessages.SingleAsync();
        outboxRow.MessageType.Should().Contain(nameof(ProbeAggregateEvent));
        outboxRow.Payload.Should().Contain("first");
        outboxRow.Status.Should().Be(OutboxMessageStatus.Pending);
        outboxRow.AgencyId.Should().BeNull("ProbeAggregate does not implement ITenantScoped");
    }

    [Fact]
    public async Task An_aggregate_that_raised_no_event_writes_no_outbox_row()
    {
        await using var context = await NewProbeContextAsync();

        context.Add(new ProbeAggregate());
        await context.SaveChangesAsync();

        (await context.OutboxMessages.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task Raising_two_events_on_one_save_writes_two_outbox_rows_with_distinct_ids()
    {
        await using var context = await NewProbeContextAsync();

        var probe = new ProbeAggregate();
        probe.DoSomething("one");
        probe.DoSomething("two");
        context.Add(probe);
        await context.SaveChangesAsync();

        var rows = await context.OutboxMessages.ToListAsync();
        rows.Should().HaveCount(2);
        rows.Select(r => r.Id).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task If_the_save_rolls_back_neither_the_aggregate_nor_its_event_is_written()
    {
        // The point of the outbox: there is no state where the state change committed but its
        // event did not, or the other way round. Provoking a real constraint violation — rather
        // than just not calling SaveChanges — proves that guarantee at the database, not only in
        // the change tracker.
        await using var context = await NewProbeContextAsync();

        var probe = new ProbeAggregate(Guid.CreateVersion7());
        probe.DoSomething("will not survive");
        context.Add(probe);
        await context.SaveChangesAsync();

        context.ChangeTracker.Clear();

        // Same id again: SaveChanges below fails on the primary key, and the domain event captured
        // during *this* call must not have been written either.
        var duplicate = new ProbeAggregate(probe.Id);
        duplicate.DoSomething("also will not survive");
        context.Add(duplicate);

        var act = async () => await context.SaveChangesAsync();

        await act.Should().ThrowAsync<DbUpdateException>();

        context.ChangeTracker.Clear();
        (await context.OutboxMessages.CountAsync()).Should()
            .Be(1, "only the first, successful save's event should be here — the failed save's must not have leaked through");
    }

    private async Task<AppDbContext> NewProbeContextAsync([CallerMemberName] string testName = "")
    {
        var databaseName = testName.ToLowerInvariant()[..Math.Min(testName.Length, 60)];

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

        var context = new AppDbContext(options, TimeProvider.System, TestTenancy.None().Tenant, TestTenancy.None().Scope);

        await context.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE probe_aggregate (
                id  uuid PRIMARY KEY
            );
            """);

        return context;
    }

    /// <summary>Adds the probe aggregate to the real context's model.</summary>
    private sealed class ProbeModelCustomizer : ModelCustomizer
    {
        public ProbeModelCustomizer(ModelCustomizerDependencies dependencies)
            : base(dependencies)
        {
        }

        public override void Customize(ModelBuilder modelBuilder, DbContext context)
        {
            // Runs AppDbContext.OnModelCreating first, so the probe joins the real model — outbox
            // table included — rather than replacing it.
            base.Customize(modelBuilder, context);

            modelBuilder.Entity<ProbeAggregate>().ToTable("probe_aggregate");
        }
    }

    /// <summary>See the equivalent in the unit tests: EF keys its model cache on context type alone.</summary>
    private sealed class FreshModelFactory : IModelCacheKeyFactory
    {
        public object Create(DbContext context, bool designTime) =>
            (context?.GetType(), Guid.NewGuid(), designTime);
    }

    private sealed record ProbeAggregateEvent(Guid AggregateId, string Detail) : IDomainEvent;

    private sealed class ProbeAggregate : AggregateRoot
    {
        public ProbeAggregate()
        {
        }

        public ProbeAggregate(Guid id)
            : base(id)
        {
        }

        public void DoSomething(string detail) => Raise(new ProbeAggregateEvent(Id, detail));
    }
}
