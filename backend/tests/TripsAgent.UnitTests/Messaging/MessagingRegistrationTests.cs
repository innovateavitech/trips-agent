using FluentAssertions;
using MassTransit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TripsAgent.Application.Messaging;
using TripsAgent.Infrastructure.Messaging;

namespace TripsAgent.UnitTests.Messaging;

/// <summary>
/// These assert what was <em>registered</em>, and deliberately never resolve the bus itself.
/// Resolving it would build MassTransit's RabbitMQ topology, and a unit test that needs a broker
/// running is a unit test that fails in CI for reasons unrelated to the change under review.
/// Anything that needs a real RabbitMQ belongs in TripsAgent.IntegrationTests.
/// </summary>
public class MessagingRegistrationTests
{
    private const string UnusedBroker = "amqp://none:none@broker.never.connected.invalid:5672/";

    private static IConfiguration Configuration(string? rabbitMq = UnusedBroker, int? retryLimit = null)
    {
        var values = new Dictionary<string, string?>
        {
            [$"ConnectionStrings:{MessagingRegistration.RabbitMqConnectionName}"] = rabbitMq,
        };

        if (retryLimit is { } limit)
        {
            values[$"{MessageRetryOptions.SectionName}:RetryLimit"] = limit.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    [Fact]
    public void AddMessagePublishing_should_register_the_port_the_application_depends_on()
    {
        var services = new ServiceCollection().AddMessagePublishing(Configuration());

        // The whole point of the port: application code asks for IMessageBus and gets whatever
        // Infrastructure decided to implement it with today.
        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(IMessageBus)
            && descriptor.ImplementationType == typeof(MassTransitMessageBus));
    }

    [Fact]
    public void AddMessageConsuming_should_register_the_port_too()
    {
        var services = new ServiceCollection().AddMessageConsuming(Configuration());

        services.Should().Contain(descriptor => descriptor.ServiceType == typeof(IMessageBus));
    }

    [Fact]
    public void The_bus_should_be_given_a_stop_timeout_so_shutdown_is_graceful()
    {
        using var provider = new ServiceCollection()
            .AddMessageConsuming(Configuration())
            .BuildServiceProvider();

        // Resolving IOptions does not construct the bus, so this stays offline.
        var hostOptions = provider.GetRequiredService<IOptions<MassTransitHostOptions>>().Value;

        // Without a stop timeout, SIGTERM tears the connection down under a consumer that was
        // halfway through issuing a ticket. With one, MassTransit lets it finish; anything still
        // unacknowledged goes back to RabbitMQ for another worker.
        hostOptions.StopTimeout.Should().NotBeNull().And.BePositive();

        // A broker that is slow to boot should delay messages, not kill the process.
        hostOptions.WaitUntilStarted.Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_missing_connection_string_should_fail_loudly_at_startup(string? rabbitMq)
    {
        var act = () => new ServiceCollection().AddMessagePublishing(Configuration(rabbitMq));

        // The message is the point — whoever hits this has usually just not started the broker.
        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().ContainAll(
                "ConnectionStrings__RabbitMq",
                "appsettings.Development.json",
                "docker compose up -d rabbitmq");
    }

    [Fact]
    public void A_connection_string_that_is_not_a_URI_should_say_so()
    {
        // The easy mistake: copying the shape of the Postgres connection string next to it.
        var act = () => new ServiceCollection()
            .AddMessagePublishing(Configuration("Host=localhost;Username=trips;Password=x"));

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("amqp://");
    }

    [Fact]
    public void An_invalid_retry_policy_should_fail_at_registration_not_on_the_first_failure()
    {
        var act = () => new ServiceCollection().AddMessagePublishing(Configuration(retryLimit: -1));

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("RetryLimit");
    }
}
