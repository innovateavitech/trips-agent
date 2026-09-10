using Microsoft.Extensions.DependencyInjection;
using TripsAgent.Infrastructure.Persistence;

namespace TripsAgent.Infrastructure;

/// <summary>
/// Where the Api and the Worker wire up everything Infrastructure provides.
/// Keeping registration here means neither host has to know that PostgreSQL is behind it.
/// </summary>
public static class DependencyInjection
{
    /// <summary>Name of the connection string in appsettings and in the environment.</summary>
    public const string ConnectionStringName = "Postgres";

    /// <summary>
    /// Registers <see cref="AppDbContext"/> against PostgreSQL.
    /// </summary>
    /// <param name="services">The host's service collection.</param>
    /// <param name="connectionString">
    /// Usually <c>configuration.GetConnectionString("Postgres")</c>.
    /// </param>
    public static IServiceCollection AddPersistence(
        this IServiceCollection services,
        string? connectionString)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                $$"""
                  No '{{ConnectionStringName}}' connection string was found, so the application cannot
                  reach the database.

                  Set it in one of these, in order of precedence:
                    ConnectionStrings__{{ConnectionStringName}} as an environment variable
                    "ConnectionStrings": { "{{ConnectionStringName}}": "..." } in appsettings.Development.json

                  Running locally? Start the database first:  docker compose up -d
                  """);
        }

        services.AddDbContext<AppDbContext>(options => AppDbContextOptions.Configure(options, connectionString));

        return services;
    }
}
