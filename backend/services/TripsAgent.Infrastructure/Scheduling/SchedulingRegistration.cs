using Hangfire;
using Hangfire.PostgreSql;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TripsAgent.Application.Payments;
using TripsAgent.Infrastructure.Payments;

namespace TripsAgent.Infrastructure.Scheduling;

/// <summary>
/// Wires Hangfire against the same PostgreSQL database the rest of the platform uses.
///
/// Two ways in, mirroring the messaging split:
///
///   AddJobScheduling  — knows about the jobs and can enqueue them. The API uses this so it can
///                       serve the dashboard; it does not run anything.
///   AddJobProcessing  — everything above, plus the background server that actually executes
///                       jobs. Only the Worker uses this.
///
/// Hangfire creates and migrates its own tables, in its own schema, the first time it connects.
/// They are not EF Core migrations and <c>scripts/ef.sh check</c> does not know about them — that
/// is expected, not an oversight.
/// </summary>
public static class SchedulingRegistration
{
    /// <summary>
    /// Registers Hangfire's storage and client. Enough to enqueue a job and to serve the
    /// dashboard; nothing in this process will execute a job.
    /// </summary>
    /// <param name="services">The host's service collection.</param>
    /// <param name="configuration">The host's configuration.</param>
    public static IServiceCollection AddJobScheduling(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var connectionString = configuration.GetConnectionString(DependencyInjection.PostgresConnectionName);

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                $"""
                 Hangfire needs the same PostgreSQL connection string EF Core uses, and none is
                 configured.

                 Add one under ConnectionStrings:{DependencyInjection.PostgresConnectionName} in
                 appsettings.Development.json, or set the environment variable
                 ConnectionStrings__{DependencyInjection.PostgresConnectionName}.
                 """);
        }

        var options = configuration.GetSection(HangfireOptions.SectionName).Get<HangfireOptions>()
                      ?? new HangfireOptions();

        return services.AddJobScheduling(connectionString, options);
    }

    /// <summary>
    /// Adds the background server that executes jobs. Worker only — calling this in the API would
    /// mean every API instance competes to run the cron jobs, which is exactly what issue #31 set
    /// out to avoid.
    /// </summary>
    /// <param name="services">The host's service collection.</param>
    /// <param name="configuration">The host's configuration.</param>
    public static IServiceCollection AddJobProcessing(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddJobScheduling(configuration);

        var options = configuration.GetSection(HangfireOptions.SectionName).Get<HangfireOptions>()
                      ?? new HangfireOptions();

        return services.AddJobServer(options);
    }

    /// <summary>
    /// The connection-string overload, kept separate so tests can register Hangfire without
    /// building an <see cref="IConfiguration"/> around it.
    /// </summary>
    /// <param name="services">The host's service collection.</param>
    /// <param name="connectionString">The same PostgreSQL connection string EF Core uses.</param>
    /// <param name="options">Dashboard and worker settings.</param>
    public static IServiceCollection AddJobScheduling(
        this IServiceCollection services,
        string connectionString,
        HangfireOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(options);

        services.AddSingleton(options);

        var schemaName = options.SchemaName;

        services.AddHangfire(hangfire => hangfire
            // Version_180 is Hangfire's current on-disk format. Pinned rather than left to
            // default so that upgrading the package never silently rewrites job payloads that a
            // still-running older Worker cannot read.
            .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
            .UseSimpleAssemblyNameTypeSerializer()
            .UseRecommendedSerializerSettings()
            .UsePostgreSqlStorage(
                storage => storage.UseNpgsqlConnection(connectionString),
                new PostgreSqlStorageOptions
                {
                    SchemaName = schemaName,

                    // Hangfire owns these tables, so let it create them. The trade-off is that
                    // the app's database user needs CREATE on this schema.
                    PrepareSchemaIfNecessary = true,

                    // Without this the API refuses to start whenever Postgres is briefly
                    // unreachable, purely because it wanted to show a dashboard.
                    StartupConnectionMaxRetries = 5,

                    // How often a worker looks for new work. The default of 15s makes a job
                    // scheduled "now" feel broken while you are testing it.
                    QueuePollInterval = TimeSpan.FromSeconds(5),

                    // Cheaper and safer than the transaction-scope alternative on PostgreSQL.
                    UseNativeDatabaseTransactions = true,
                }));

        // Registered here rather than in AddInfrastructure because this is where the Hangfire
        // client becomes available. Both hosts get it: the API enqueues, the Worker executes.
        services.AddScoped<IWebhookDispatcher, HangfireWebhookDispatcher>();

        return services;
    }

    /// <summary>Adds only the background server. Assumes storage is already registered.</summary>
    /// <param name="services">The host's service collection.</param>
    /// <param name="options">Worker settings.</param>
    public static IServiceCollection AddJobServer(
        this IServiceCollection services,
        HangfireOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        var workerCount = options.WorkerCount;

        services.AddHangfireServer(server =>
        {
            if (workerCount is { } count)
            {
                server.WorkerCount = count;
            }

            // Graceful shutdown, the Hangfire half. On SIGTERM the server stops taking new jobs
            // and gives whatever is running this long to finish. A job that does not finish in
            // time is not marked failed — its lock simply expires and another Worker picks it up,
            // which is why every job has to be safe to run twice.
            server.ShutdownTimeout = TimeSpan.FromSeconds(30);
            server.StopTimeout = TimeSpan.FromSeconds(30);
        });

        return services;
    }
}
