using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using TripsAgent.Application.Messaging;
using TripsAgent.Infrastructure;
using TripsAgent.Infrastructure.Messaging;

namespace TripsAgent.UnitTests.Messaging;

/// <summary>
/// Like <c>MessagingRegistrationTests</c>, these assert what got registered and never resolve
/// anything that would need a live database or broker.
/// </summary>
public class OutboxRegistrationTests
{
    // Well-formed but never opened: AddInfrastructure only needs a connection string that parses,
    // and design-time / registration never connects.
    private const string UnusedPostgres =
        "Host=model.building.invalid;Database=none;Username=none;Password=none";

    private static IConfiguration Configuration() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Postgres"] = UnusedPostgres,
            })
            .Build();

    [Fact]
    public void AddInfrastructure_should_register_the_outbox_ports()
    {
        var services = new ServiceCollection().AddInfrastructure(Configuration());

        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(IOutboxWriter) && descriptor.ImplementationType == typeof(OutboxWriter));
        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(IInboxDeduplicator) && descriptor.ImplementationType == typeof(InboxDeduplicator));
        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(IOutboxDispatcher) && descriptor.ImplementationType == typeof(OutboxDispatcher));
    }

    [Fact]
    public void The_outbox_ports_should_be_scoped()
    {
        // Scoped, not singleton: they share the request's or job's AppDbContext instance, which
        // is itself scoped. A singleton outbox writer would hold onto whichever DbContext was
        // current on its first resolution forever.
        var services = new ServiceCollection().AddInfrastructure(Configuration());

        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(IOutboxWriter) && descriptor.Lifetime == ServiceLifetime.Scoped);
        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(IInboxDeduplicator) && descriptor.Lifetime == ServiceLifetime.Scoped);
        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(IOutboxDispatcher) && descriptor.Lifetime == ServiceLifetime.Scoped);
    }

    [Fact]
    public void AddInfrastructure_should_bind_OutboxOptions_from_configuration()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Postgres"] = UnusedPostgres,
                [$"{OutboxOptions.SectionName}:BatchSize"] = "17",
            })
            .Build();

        using var provider = new ServiceCollection().AddInfrastructure(configuration).BuildServiceProvider();

        // Resolving IOptions binds and validates but touches no database.
        provider.GetRequiredService<IOptions<OutboxOptions>>().Value.BatchSize.Should().Be(17);
    }

    [Fact]
    public void Invalid_OutboxOptions_should_fail_loudly_at_startup()
    {
        // Matches MessageRetryOptions: the check happens synchronously inside AddInfrastructure,
        // not lazily on the first IOptions<OutboxOptions> resolution — so a bad setting stops
        // the container from ever coming up rather than surfacing on the outbox's first tick.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Postgres"] = UnusedPostgres,
                [$"{OutboxOptions.SectionName}:BatchSize"] = "0",
            })
            .Build();

        var act = () => new ServiceCollection().AddInfrastructure(configuration);

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("BatchSize");
    }

    [Fact]
    public void AddOutboxDispatching_should_register_the_hosted_service()
    {
        var services = new ServiceCollection().AddOutboxDispatching();

        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(IHostedService)
            && descriptor.ImplementationType == typeof(OutboxDispatcherHostedService));
    }
}
