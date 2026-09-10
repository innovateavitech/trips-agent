using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TripsAgent.Application.Auditing;
using TripsAgent.Application.Messaging;
using TripsAgent.Infrastructure.Auditing;
using TripsAgent.Infrastructure.Messaging;
using TripsAgent.Infrastructure.Persistence;

namespace TripsAgent.Infrastructure;

/// <summary>
/// The one place Infrastructure is wired into the container. The API and the Worker both call
/// <see cref="AddInfrastructure"/>, so they cannot drift apart in how they talk to the database.
/// </summary>
public static class DependencyInjection
{
    /// <summary>The configuration key holding the PostgreSQL connection string.</summary>
    public const string PostgresConnectionName = "Postgres";

    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var connectionString = configuration.GetConnectionString(PostgresConnectionName);

        // Whitespace, not just null: appsettings.json declares the key with an empty value so
        // the shape of the configuration is discoverable, and an empty string would otherwise
        // sail through to Npgsql and fail with something far less helpful than this.
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                $"""
                 No PostgreSQL connection string configured.

                 Add one under ConnectionStrings:{PostgresConnectionName} in appsettings.Development.json,
                 or set the environment variable ConnectionStrings__{PostgresConnectionName}.

                 Local default: {AppDbContextFactory.LocalDevelopmentConnectionString}
                 Start the database with: docker compose up -d postgres
                 """);
        }

        // A clock we can replace in tests. Nothing should call DateTimeOffset.UtcNow directly.
        services.TryAddSingletonTimeProvider();

        services.AddAuditing(configuration);
        services.AddOutboxMessaging(configuration);

        services.AddDbContext<AppDbContext>((serviceProvider, options) =>
        {
            options
                .UseNpgsql(connectionString, npgsql =>
                {
                    npgsql.MigrationsAssembly(typeof(AppDbContext).Assembly.FullName);

                    // Transient network blips are worth a retry; a deadlock is not.
                    npgsql.EnableRetryOnFailure(
                        maxRetryCount: 3,
                        maxRetryDelay: TimeSpan.FromSeconds(5),
                        errorCodesToAdd: null);
                })
                // users.email is citext, agencies.path is ltree, and every column is snake_case.
                .UseSnakeCaseNamingConvention()

                // Resolved from the scoped provider so the interceptor sees the actor for *this*
                // request. A singleton would freeze whoever made the first request into every
                // audit row that followed.
                .AddInterceptors(serviceProvider.GetRequiredService<AuditSaveChangesInterceptor>());
        });

        return services;
    }

    /// <summary>
    /// The audit trail: the ambient actor, the redaction policy, the interceptor that turns a save
    /// into a record of who changed what, and the partition maintenance job.
    /// </summary>
    private static void AddAuditing(this IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection(AuditLogOptions.SectionName);

        // Bound, then validated. A zero in configuration must reach the check below and fail it —
        // not be skipped over in favour of the default, which would quietly keep data for seven
        // years that someone had configured to keep for none.
        services.AddOptions<AuditLogOptions>()
            .Configure(options => section.Bind(options))
            .Validate(
                options => options.RetentionMonths >= 1 && options.PartitionsCreatedAhead >= 1,
                "AuditLog:RetentionMonths and AuditLog:PartitionsCreatedAhead must both be at least 1. "
                + "A retention of zero would drop the month still being written to.");

        // Stateless once built, so one instance serves every request.
        services.AddSingleton<AuditRedactionPolicy>();

        // One actor per request or job run. AuditContext is registered as itself as well, so the
        // edge — authentication middleware, a job host — can populate what IAuditContext only
        // exposes for reading.
        services.AddScoped<AuditContext>();
        services.AddScoped<IAuditContext>(provider => provider.GetRequiredService<AuditContext>());

        services.AddScoped<AuditSaveChangesInterceptor>();
        services.AddScoped<IAuditLogMaintenance, AuditLogPartitionMaintenance>();
    }

    /// <summary>
    /// The outbox and inbox: <see cref="IOutboxWriter"/> to stage events, <see cref="IInboxDeduplicator"/>
    /// to dedupe consumption, and <see cref="IOutboxDispatcher"/> to publish what is staged.
    /// </summary>
    /// <remarks>
    /// Available in every host, Api and Worker alike — any request handler or consumer may need
    /// to stage an event or dedupe a delivery. What is <em>not</em> registered here is the
    /// dispatcher's clock: see <c>AddOutboxDispatching</c>, which only the Worker calls.
    /// </remarks>
    private static void AddOutboxMessaging(this IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection(OutboxOptions.SectionName);

        // Bound and validated twice on purpose, matching MessageRetryOptions just above. This
        // throwaway instance fails the app at startup, synchronously, with a message naming the
        // setting — the same reason DependencyInjection.AddInfrastructure checks the Postgres
        // connection string up front instead of waiting for the first request to hit a bad
        // config. The AddOptions<T> registration below is what OutboxDispatcher and
        // OutboxDispatcherHostedService actually resolve later; it needs no second validation
        // step because this one already proved the section is sound.
        (section.Get<OutboxOptions>() ?? new OutboxOptions()).Validate();

        services.AddOptions<OutboxOptions>().Configure(options => section.Bind(options));

        // Scoped, against the same AppDbContext instance as everything else in the unit of
        // work — that shared instance is the entire transactional guarantee.
        services.AddScoped<IOutboxWriter, OutboxWriter>();
        services.AddScoped<IInboxDeduplicator, InboxDeduplicator>();
        services.AddScoped<IOutboxDispatcher, OutboxDispatcher>();
    }

    /// <summary>
    /// Puts the outbox dispatcher on a clock. Call this from the Worker only — see
    /// <see cref="OutboxDispatcherHostedService"/> for why running it from the Api as well would
    /// mean every Api instance polling the same table on the same schedule.
    /// </summary>
    public static IServiceCollection AddOutboxDispatching(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddHostedService<OutboxDispatcherHostedService>();

        return services;
    }

    private static void TryAddSingletonTimeProvider(this IServiceCollection services)
    {
        if (services.All(descriptor => descriptor.ServiceType != typeof(TimeProvider)))
        {
            services.AddSingleton(TimeProvider.System);
        }
    }
}
