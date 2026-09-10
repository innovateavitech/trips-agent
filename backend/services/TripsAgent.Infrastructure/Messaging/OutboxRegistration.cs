using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using TripsAgent.Application.Messaging;

namespace TripsAgent.Infrastructure.Messaging;

/// <summary>
/// Wires the outbox and the inbox. Three ways in, and the difference matters:
///
///   AddOutbox              — called by AddInfrastructure, so the Api and the Worker both get it. Lets
///                            a handler stage messages and a consumer deduplicate. Publishes nothing.
///   AddOutboxDispatcher    — the Worker only. Runs the loop that actually publishes.
///   AddOutboxBacklogCheck  — the Api's /health.
/// </summary>
public static class OutboxRegistration
{
    /// <summary>Registers <see cref="IOutbox"/>, <see cref="IInbox"/> and the backlog probe.</summary>
    /// <param name="services">The host's service collection.</param>
    /// <param name="configuration">The host's configuration; the <c>Outbox</c> section is optional.</param>
    public static IServiceCollection AddOutbox(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // Validated here, at startup, so a mistyped PollInterval stops the process with an explanation
        // rather than letting it run with a dispatcher that never fires.
        var options = configuration.GetSection(OutboxOptions.SectionName).Get<OutboxOptions>() ?? new OutboxOptions();
        options.Validate();
        services.AddSingleton(options);

        services.AddScoped<IOutbox, EfOutbox>();
        services.AddScoped<IInbox, EfInbox>();
        services.AddScoped<OutboxBacklogProbe>();

        return services;
    }

    /// <summary>
    /// Runs the dispatcher. Worker only: an Api that dispatched would add a publisher for every Api
    /// instance, and the Api is scaled on request rate, not on backlog.
    /// </summary>
    /// <remarks>
    /// Publishes through the <see cref="IOutboxPublisher"/> that <c>AddMessageConsuming</c> registers,
    /// so call that as well.
    /// </remarks>
    public static IServiceCollection AddOutboxDispatcher(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<OutboxDispatcher>();
        services.AddHostedService<OutboxDispatcherService>();

        return services;
    }

    /// <summary>Adds the backlog to the health report. See <see cref="OutboxBacklogHealthCheck"/>.</summary>
    public static IHealthChecksBuilder AddOutboxBacklogCheck(this IHealthChecksBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // failureStatus applies when the check itself throws — the database being unreachable. The
        // postgres check already reports that as Unhealthy; this one need not report it twice.
        return builder.AddCheck<OutboxBacklogHealthCheck>(
            OutboxBacklogHealthCheck.Name,
            failureStatus: HealthStatus.Degraded,
            tags: ["outbox"]);
    }
}
