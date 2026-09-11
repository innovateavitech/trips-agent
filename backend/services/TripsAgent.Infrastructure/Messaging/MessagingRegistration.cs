using MassTransit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TripsAgent.Application.Messaging;
using TripsAgent.Infrastructure.Notifications;

namespace TripsAgent.Infrastructure.Messaging;

/// <summary>
/// Wires MassTransit over RabbitMQ.
///
/// There are two ways in, and the difference matters:
///
///   AddMessagePublishing  — the API. Can publish and send, listens to nothing.
///   AddMessageConsuming   — the Worker. Everything above, plus it runs the queues.
///
/// That split is what "the Worker runs as its own process, scalable independently of the API"
/// actually means in code. Ten API instances behind a load balancer do not each start ten copies
/// of every consumer; only Worker instances consume, and you scale those on queue depth.
/// </summary>
public static class MessagingRegistration
{
    /// <summary>
    /// The configuration key holding the broker's AMQP URI. Named to sit alongside
    /// <c>DependencyInjection.PostgresConnectionName</c>, and read from the same place.
    /// </summary>
    public const string RabbitMqConnectionName = "RabbitMq";

    /// <summary>
    /// Which consumer listens on which queue. A consumer not listed here receives nothing.
    /// </summary>
    /// <remarks>
    /// Explicit because MassTransit's <c>ConfigureConsumers(context)</c> attaches <em>every</em>
    /// registered consumer to the endpoint it is called on. Called on all eleven queues, as this
    /// class used to, one published message would be consumed eleven times — eleven emails.
    /// </remarks>
    public static IReadOnlyList<(MessageQueue Queue, Type Consumer)> ConsumerRoutes { get; } =
    [
        (MessageQueue.NotificationsEmail, typeof(NotificationQueuedConsumer)),
    ];

    /// <summary>Registers a publish-only bus. Use this in the API.</summary>
    /// <param name="services">The host's service collection.</param>
    /// <param name="configuration">The host's configuration.</param>
    public static IServiceCollection AddMessagePublishing(
        this IServiceCollection services,
        IConfiguration configuration) =>
        AddMessaging(services, configuration, runReceiveEndpoints: false, registerConsumers: null);

    /// <summary>
    /// Registers a bus that also consumes every queue in <see cref="MessageQueue.All"/>. Use this
    /// in the Worker.
    /// </summary>
    /// <param name="services">The host's service collection.</param>
    /// <param name="configuration">The host's configuration.</param>
    /// <param name="registerConsumers">
    /// Extra bus registration, e.g. for a saga. Consumers belong in <see cref="ConsumerRoutes"/>
    /// instead, which both registers them and decides their queue.
    /// </param>
    public static IServiceCollection AddMessageConsuming(
        this IServiceCollection services,
        IConfiguration configuration,
        Action<IBusRegistrationConfigurator>? registerConsumers = null) =>
        AddMessaging(services, configuration, runReceiveEndpoints: true, registerConsumers);

    private static IServiceCollection AddMessaging(
        IServiceCollection services,
        IConfiguration configuration,
        bool runReceiveEndpoints,
        Action<IBusRegistrationConfigurator>? registerConsumers)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var connectionString = configuration.GetConnectionString(RabbitMqConnectionName);

        // Whitespace, not just null: appsettings.json declares the key with an empty value so the
        // shape of the configuration is discoverable, and an empty string would otherwise reach
        // MassTransit and fail with something far less helpful than this.
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                $"""
                 No RabbitMQ connection string configured.

                 Add one under ConnectionStrings:{RabbitMqConnectionName} in appsettings.Development.json,
                 or set the environment variable ConnectionStrings__{RabbitMqConnectionName}.

                 Local default: amqp://trips:trips_local_dev@localhost:5672/
                 Start the broker with: docker compose up -d rabbitmq
                 The management UI is then at http://localhost:15672
                 """);
        }

        if (!Uri.TryCreate(connectionString, UriKind.Absolute, out var parsedUri))
        {
            throw new InvalidOperationException(
                $"""
                 ConnectionStrings:{RabbitMqConnectionName} is not a URI: '{connectionString}'

                 It must be a full AMQP address, credentials and all, for example:
                   amqp://trips:trips_local_dev@localhost:5672/

                 That is a different shape from the PostgreSQL connection string next to it — AMQP
                 has a URI form and Npgsql does not.
                 """);
        }

        // Copied into a non-nullable local before the lambdas below capture it: nullable flow
        // analysis does not follow a captured variable across a lambda boundary, and warnings are
        // build errors here.
        Uri brokerUri = parsedUri;

        var retry = configuration.GetSection(MessageRetryOptions.SectionName).Get<MessageRetryOptions>()
                    ?? new MessageRetryOptions();

        retry.Validate();

        services.AddMassTransit(bus =>
        {
            // Only affects endpoints we have not named ourselves. Every queue below is named
            // explicitly, so this is here for the ones a future consumer forgets to name.
            bus.SetKebabCaseEndpointNameFormatter();

            if (runReceiveEndpoints)
            {
                foreach (var (_, consumer) in ConsumerRoutes)
                {
                    bus.AddConsumer(consumer);
                }
            }

            registerConsumers?.Invoke(bus);

            bus.UsingRabbitMq((context, rabbit) =>
            {
                // Credentials come from the URI's userinfo, which is why the connection string
                // must carry them.
                rabbit.Host(brokerUri);

                if (!runReceiveEndpoints)
                {
                    return;
                }

                foreach (var queue in MessageQueue.All)
                {
                    var tuning = TuningFor(queue);

                    rabbit.ReceiveEndpoint(queue.Name, endpoint =>
                    {
                        // PrefetchCount is how many messages the broker hands over in advance;
                        // ConcurrentMessageLimit is how many we actually process at once. Prefetch
                        // higher than the limit keeps a worker fed without letting it grab work
                        // that a second, idle worker could have taken.
                        endpoint.PrefetchCount = tuning.PrefetchCount;
                        endpoint.ConcurrentMessageLimit = tuning.ConcurrentMessageLimit;

                        // Retry in memory, holding the message unacknowledged, with a growing gap
                        // between attempts. After RetryLimit attempts MassTransit moves the
                        // message to <queue>_error and acknowledges it, so a poison message can
                        // never block the queue behind it.
                        //
                        // Deliberately NOT UseDelayedRedelivery: that needs RabbitMQ's delayed
                        // message exchange plugin, which the rabbitmq:3-management-alpine image in
                        // docker-compose.yml does not ship. Turning it on without the plugin fails
                        // at runtime, not at startup.
                        endpoint.UseMessageRetry(policy => policy.Exponential(
                            retryLimit: RetryLimitFor(queue, retry),
                            minInterval: retry.RetryMinInterval,
                            maxInterval: retry.RetryMaxInterval,
                            intervalDelta: retry.RetryIntervalDelta));

                        // Only the consumers routed to this queue. An endpoint with none is a
                        // queue that exists and holds nothing, which is what most are in M1.
                        foreach (var (_, consumer) in ConsumerRoutes.Where(route => route.Queue == queue))
                        {
                            endpoint.ConfigureConsumer(context, consumer);
                        }

                        endpoint.ConfigureSagas(context);
                    });
                }
            });
        });

        // Graceful shutdown. On SIGTERM MassTransit stops accepting new deliveries and waits for
        // in-flight consumers to finish; anything still unacknowledged when the connection closes
        // is redelivered by RabbitMQ to another worker. Nothing is lost, some things are
        // delivered twice — which is why consumers must be idempotent (issue #30).
        //
        // WaitUntilStarted stays false so a broker that is slow to boot delays messages rather
        // than killing the process.
        services.AddOptions<MassTransitHostOptions>().Configure(host =>
        {
            host.WaitUntilStarted = false;
            host.StartTimeout = TimeSpan.FromSeconds(30);
            host.StopTimeout = TimeSpan.FromSeconds(30);
        });

        services.AddScoped<IMessageBus, MassTransitMessageBus>();

        // How the outbox dispatcher (issue #30) reaches the broker. Registered wherever there is a
        // bus, but only the Worker runs the dispatcher that uses it — see AddOutboxDispatcher.
        services.AddScoped<IOutboxPublisher, MassTransitOutboxPublisher>();

        return services;
    }

    /// <summary>
    /// Retries per queue. Notifications give up after <see cref="NotificationDispatcher.MaxAttempts"/>
    /// attempts in total — the first delivery plus four retries — so the broker dead-letters a
    /// message at the same moment the dispatcher marks its row failed.
    /// </summary>
    private static int RetryLimitFor(MessageQueue queue, MessageRetryOptions retry) =>
        queue == MessageQueue.NotificationsEmail
            ? Math.Min(retry.RetryLimit, NotificationDispatcher.MaxAttempts - 1)
            : retry.RetryLimit;

    /// <summary>
    /// Per-queue concurrency. One switch, so "how parallel is this queue" has a single answer you
    /// can read in ten seconds.
    /// </summary>
    private static (int PrefetchCount, int ConcurrentMessageLimit) TuningFor(MessageQueue queue)
    {
        // Long-running work gets its own small pool. Plan §3 calls reports.generate an "isolated
        // pool" for exactly this reason: one agency exporting a year of bookings must not occupy
        // every worker thread while tickets are waiting to be issued.
        if (queue == MessageQueue.ReportsGenerate || queue == MessageQueue.MediaProcess)
        {
            return (PrefetchCount: 2, ConcurrentMessageLimit: 1);
        }

        // The saga touches money and the supplier. Kept modest on purpose — the constraint is the
        // supplier's rate limit, not our CPU.
        if (queue == MessageQueue.BookingSaga || queue == MessageQueue.PaymentsReversal)
        {
            return (PrefetchCount: 8, ConcurrentMessageLimit: 4);
        }

        return (PrefetchCount: 16, ConcurrentMessageLimit: 8);
    }
}
