using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using FluentAssertions;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using TripsAgent.Infrastructure.Messaging;
using TripsAgent.Infrastructure.Persistence;
using TripsAgent.IntegrationTests.Persistence;

namespace TripsAgent.IntegrationTests.Messaging;

/// <summary>An event a probe consumer listens for. Public: it has to survive round-tripping through <see cref="OutboxMessage.TypeNameOf"/>.</summary>
public sealed record ProbeOutboxEvent(string Detail);

/// <summary>What the probe consumer saw: the payload, and the broker's own message id for it.</summary>
public sealed record ProbeOutboxDelivery(Guid? MessageId, ProbeOutboxEvent Message);

/// <summary>Records every delivery it receives, for the test to inspect afterwards.</summary>
public sealed class ProbeOutboxEventLog
{
    public ConcurrentBag<ProbeOutboxDelivery> Received { get; } = [];
}

public sealed class ProbeOutboxEventConsumer(ProbeOutboxEventLog log) : IConsumer<ProbeOutboxEvent>
{
    public Task Consume(ConsumeContext<ProbeOutboxEvent> context)
    {
        log.Received.Add(new ProbeOutboxDelivery(context.MessageId, context.Message));
        return Task.CompletedTask;
    }
}

/// <summary>
/// End-to-end proof of issue #30's central acceptance criterion: a message committed to the outbox
/// survives the process that wrote it dying before dispatch, and is still delivered once a new
/// process picks it up.
/// </summary>
/// <remarks>
/// <para>
/// A test cannot honestly <c>kill -9</c> itself mid-assertion, so this proves the same guarantee
/// the only way that is actually meaningful: nothing here is carried in memory between the "before"
/// and "after" halves. The outbox row is written with one <see cref="AppDbContext"/> and one
/// MassTransit bus, both fully disposed before the second half starts. The second half builds a
/// brand new <see cref="AppDbContext"/> and a brand new bus — exactly what a restarted Worker
/// process does — and it is that second, unrelated instance that finds the row and delivers it. If
/// the guarantee did not hold, there would be nothing left for it to find.
/// </para>
/// <para>
/// The transport is MassTransit's in-memory provider rather than RabbitMQ. What is under test here
/// is <see cref="MassTransitOutboxPublisher"/>'s own logic — resolving the stored type, publishing
/// with the row's id as the message id — which is transport-independent; the RabbitMQ wiring itself
/// is already covered by <c>MessagingRegistrationTests</c>, without needing a broker running.
/// </para>
/// </remarks>
[Collection(PostgresCollection.Name)]
public class MassTransitOutboxPublisherTests
{
    private static readonly DateTimeOffset Now = new(2026, 3, 1, 8, 0, 0, TimeSpan.Zero);

    private readonly PostgresFixture _postgres;

    public MassTransitOutboxPublisherTests(PostgresFixture postgres) => _postgres = postgres;

    [Fact]
    public async Task A_message_committed_by_one_process_is_delivered_by_a_completely_unrelated_one()
    {
        var databaseName = await NewMigratedDatabaseAsync();
        Guid messageId;

        // ---- "before": a handler commits the outbox row, then this process dies ----
        await using (var writer = await OpenDbAsync(databaseName))
        {
            var message = OutboxMessage.Create(new ProbeOutboxEvent("delivered after a restart"), agencyId: null, Now);
            writer.OutboxMessages.Add(message);
            await writer.SaveChangesAsync();
            messageId = message.Id;
        } // The connection, the context and everything about "before" ends here.

        // ---- "after": a new process starts, with no memory of the one above ----
        var log = new ProbeOutboxEventLog();
        await using var restarted = await StartInMemoryBusAsync(log);
        await using var restartedDb = await OpenDbAsync(databaseName);

        var options = new OutboxOptions();
        var publisher = new MassTransitOutboxPublisher(restarted.Services.GetRequiredService<IPublishEndpoint>(), options);
        var dispatcher = new OutboxDispatcher(
            restartedDb,
            publisher,
            options,
            TimeProvider.System,
            NullLogger<OutboxDispatcher>.Instance);

        var result = await dispatcher.DispatchBatchAsync();

        result.Should().Be(new OutboxDispatchResult(Claimed: 1, Published: 1, Failed: 0));

        // In-memory delivery is asynchronous even within one process, so give it a moment.
        await WaitUntilAsync(() => log.Received.Count == 1, TimeSpan.FromSeconds(5));
        log.Received.Should().ContainSingle().Which.Message.Detail.Should().Be("delivered after a restart");

        var stored = await restartedDb.OutboxMessages.SingleAsync(m => m.Id == messageId);
        stored.Status.Should().Be(OutboxMessageStatus.Dispatched);
    }

    [Fact]
    public async Task The_outbox_rows_id_becomes_the_brokers_message_id()
    {
        // What lets IInbox tell a genuine redelivery apart from a new message: the id on the wire
        // has to be the outbox row's own id, not one MassTransit makes up per publish.
        var databaseName = await NewMigratedDatabaseAsync();
        await using var db = await OpenDbAsync(databaseName);

        var message = OutboxMessage.Create(new ProbeOutboxEvent("id check"), agencyId: null, Now);
        db.OutboxMessages.Add(message);
        await db.SaveChangesAsync();

        var log = new ProbeOutboxEventLog();
        await using var bus = await StartInMemoryBusAsync(log);

        var publisher = new MassTransitOutboxPublisher(bus.Services.GetRequiredService<IPublishEndpoint>(), new OutboxOptions());
        var dispatcher = new OutboxDispatcher(db, publisher, new OutboxOptions(), TimeProvider.System, NullLogger<OutboxDispatcher>.Instance);

        await dispatcher.DispatchBatchAsync();
        await WaitUntilAsync(() => log.Received.Count == 1, TimeSpan.FromSeconds(5));

        log.Received.Should().ContainSingle().Which.MessageId.Should().Be(message.Id);
    }

    private async Task<string> NewMigratedDatabaseAsync([CallerMemberName] string testName = "")
    {
        var databaseName = testName.ToLowerInvariant()[..Math.Min(testName.Length, 60)];

        await using var setup = await _postgres.CreateEmptyDatabaseAsync(databaseName);
        await setup.Database.MigrateAsync();

        return databaseName;
    }

    private async Task<AppDbContext> OpenDbAsync(string databaseName)
    {
        var builder = new NpgsqlConnectionStringBuilder(_postgres.ConnectionString) { Database = databaseName };

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(builder.ConnectionString)
            .UseSnakeCaseNamingConvention()
            .Options;

        return new AppDbContext(options, TimeProvider.System);
    }

    private static async Task<InMemoryBus> StartInMemoryBusAsync(ProbeOutboxEventLog log)
    {
        var services = new ServiceCollection();
        services.AddSingleton(log);

        services.AddMassTransit(bus =>
        {
            bus.AddConsumer<ProbeOutboxEventConsumer>();
            bus.UsingInMemory((context, cfg) => cfg.ConfigureEndpoints(context));
        });

        var provider = services.BuildServiceProvider();
        var busControl = provider.GetRequiredService<IBusControl>();
        await busControl.StartAsync();

        return new InMemoryBus(provider, busControl);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        while (!condition())
        {
            if (cts.IsCancellationRequested)
            {
                throw new TimeoutException($"Condition was not met within {timeout}.");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(25));
        }
    }

    /// <summary>A running in-memory MassTransit bus, disposed alongside its own DI container.</summary>
    private sealed class InMemoryBus(ServiceProvider provider, IBusControl busControl) : IAsyncDisposable
    {
        public IServiceProvider Services { get; } = provider;

        public async ValueTask DisposeAsync()
        {
            await busControl.StopAsync();
            await provider.DisposeAsync();
        }
    }
}
