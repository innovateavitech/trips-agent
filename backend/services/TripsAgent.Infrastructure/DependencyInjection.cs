using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TripsAgent.Application.Auditing;
using TripsAgent.Application.Identity;
using TripsAgent.Application.Tenancy;
using TripsAgent.Infrastructure.Auditing;
using TripsAgent.Infrastructure.Identity;
using TripsAgent.Infrastructure.Persistence;
using TripsAgent.Infrastructure.Tenancy;

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

        // Tenancy is scoped: one resolved agency per request, and nothing shared between them.
        // TenantContext is registered as itself as well as behind the interface, because
        // middleware needs the concrete type to call SetTenant while everything downstream
        // should only be able to read.
        services.AddScoped<TenantContext>();
        services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantContext>());
        services.AddScoped<IPlatformScope, PlatformScope>();

        // Stateless and thread-safe, so one instance serves the whole process.
        services.AddSingleton<IPasswordHasher, Argon2PasswordHasher>();

        // The service-provider overload: the audit interceptor below has to come from the scoped
        // provider so it sees the actor for *this* request.
        services.AddDbContext<AppDbContext>((serviceProvider, options) =>
        {
            // The tenant write guard is not registered here: AppDbContext installs it in
            // OnConfiguring, so it is present however the context was constructed.
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

    private static void TryAddSingletonTimeProvider(this IServiceCollection services)
    {
        if (services.All(descriptor => descriptor.ServiceType != typeof(TimeProvider)))
        {
            services.AddSingleton(TimeProvider.System);
        }
    }
}
