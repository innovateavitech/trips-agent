using FluentAssertions;
using Hangfire;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TripsAgent.Infrastructure;
using TripsAgent.Infrastructure.Scheduling;

namespace TripsAgent.UnitTests.Scheduling;

/// <summary>
/// As with the messaging tests, these check registration only. Resolving Hangfire's JobStorage
/// would connect to PostgreSQL and create its schema, which is integration-test territory.
/// </summary>
public class SchedulingRegistrationTests
{
    private const string UnusedConnectionString =
        "Host=never.connected.invalid;Database=none;Username=none;Password=none";

    private static IConfiguration Configuration(string? postgres = UnusedConnectionString) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"ConnectionStrings:{DependencyInjection.PostgresConnectionName}"] = postgres,
            })
            .Build();

    [Fact]
    public void AddJobScheduling_should_register_a_client_so_the_API_can_enqueue_and_show_jobs()
    {
        var services = new ServiceCollection().AddJobScheduling(Configuration());

        services.Should().Contain(descriptor => descriptor.ServiceType == typeof(IBackgroundJobClient));
    }

    [Fact]
    public void Only_AddJobProcessing_should_add_the_server_that_actually_runs_jobs()
    {
        // This is the guarantee that matters. If the API ever starts a job server, every API
        // instance behind the load balancer competes to run the same cron jobs — and the ones
        // that move money would run several times over.
        //
        // Compared as a difference rather than an absolute count, so that a future Hangfire
        // version quietly adding a hosted service of its own does not fail this for no reason.
        var schedulingOnly = HostedServices(new ServiceCollection().AddJobScheduling(Configuration()));
        var withProcessing = HostedServices(new ServiceCollection().AddJobProcessing(Configuration()));

        withProcessing.Should().BeGreaterThan(schedulingOnly);
    }

    [Fact]
    public void Options_should_be_resolvable_so_the_API_can_read_the_dashboard_settings()
    {
        var options = new HangfireOptions { SchemaName = "hangfire_test", DashboardEnabled = true };

        using var provider = new ServiceCollection()
            .AddJobScheduling(UnusedConnectionString, options)
            .BuildServiceProvider();

        provider.GetRequiredService<HangfireOptions>().SchemaName.Should().Be("hangfire_test");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_missing_connection_string_should_fail_loudly_at_startup(string? postgres)
    {
        var act = () => new ServiceCollection().AddJobScheduling(Configuration(postgres));

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("ConnectionStrings__Postgres");
    }

    [Fact]
    public void The_dashboard_should_be_off_unless_someone_turns_it_on()
    {
        // A dashboard that can requeue money-moving jobs should never appear just because a new
        // environment forgot to configure it.
        new HangfireOptions().DashboardEnabled.Should().BeFalse();
        new HangfireOptions().AllowLocalRequestsWithoutAuthentication.Should().BeFalse();
    }

    private static int HostedServices(IServiceCollection services) =>
        services.Count(descriptor => descriptor.ServiceType == typeof(IHostedService));
}
