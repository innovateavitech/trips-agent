using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TripsAgent.Application.Messaging;
using TripsAgent.Infrastructure.Messaging;

namespace TripsAgent.UnitTests.Messaging;

public class OutboxRegistrationTests
{
    private static IConfiguration EmptyConfiguration() =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();

    [Fact]
    public void AddOutbox_registers_the_ports_application_code_depends_on()
    {
        var services = new ServiceCollection().AddOutbox(EmptyConfiguration());

        services.Should().Contain(d => d.ServiceType == typeof(IOutbox) && d.ImplementationType == typeof(EfOutbox));
        services.Should().Contain(d => d.ServiceType == typeof(IInbox) && d.ImplementationType == typeof(EfInbox));
    }

    [Fact]
    public void AddOutbox_registers_options_built_from_defaults_when_no_section_is_configured()
    {
        var services = new ServiceCollection().AddOutbox(EmptyConfiguration());

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<OutboxOptions>();

        options.PollInterval.Should().Be(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void AddOutbox_binds_the_Outbox_configuration_section()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Outbox:BatchSize"] = "10",
            })
            .Build();

        var services = new ServiceCollection().AddOutbox(configuration);

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<OutboxOptions>().BatchSize.Should().Be(10);
    }

    [Fact]
    public void AddOutbox_fails_fast_when_the_configured_options_are_invalid()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Outbox:MaxAttempts"] = "0",
            })
            .Build();

        var act = () => new ServiceCollection().AddOutbox(configuration);

        // Fails at registration, not on the first message — the whole point of Validate().
        act.Should().Throw<InvalidOperationException>().WithMessage("*MaxAttempts*");
    }

    [Fact]
    public void AddOutboxDispatcher_registers_the_dispatcher_and_the_hosted_service()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new OutboxOptions());
        services.AddOutboxDispatcher();

        services.Should().Contain(d => d.ServiceType == typeof(OutboxDispatcher));
        services.Should().Contain(d => d.ServiceType == typeof(Microsoft.Extensions.Hosting.IHostedService));
    }
}
