using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TripsAgent.Application.Tenancy;
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

                 Local default: Host=localhost;Port=5432;Database=tripsagent;Username=postgres;Password=postgres
                 Start the database with: docker compose up -d postgres
                 """);
        }

        // A clock we can replace in tests. Nothing should call DateTimeOffset.UtcNow directly.
        services.TryAddSingletonTimeProvider();

        // Tenancy is scoped: one resolved agency per request, and nothing shared between them.
        // TenantContext is registered as itself as well as behind the interface, because
        // middleware needs the concrete type to call SetTenant while everything downstream
        // should only be able to read.
        services.AddScoped<TenantContext>();
        services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantContext>());
        services.AddScoped<IPlatformScope, PlatformScope>();

        services.AddDbContext<AppDbContext>(options =>
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
                .UseSnakeCaseNamingConvention();
        });

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
