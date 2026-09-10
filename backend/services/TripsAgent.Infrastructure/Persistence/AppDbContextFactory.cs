using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace TripsAgent.Infrastructure.Persistence;

/// <summary>
/// Lets <c>dotnet ef</c> build an <see cref="AppDbContext"/> without starting the API.
/// </summary>
/// <remarks>
/// <para>
/// Without this, <c>dotnet ef migrations add</c> boots the whole web host to find the context —
/// which means it needs Redis, RabbitMQ and every other dependency to be up just to write a
/// migration file. This factory sidesteps all of it.
/// </para>
/// <para>
/// The connection string here is only used to pick the provider and generate SQL; the design
/// tools never open it. Override with <c>ConnectionStrings__Postgres</c> if your local database
/// differs.
/// </para>
/// </remarks>
public sealed class AppDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    /// <summary>
    /// The local database that <c>docker compose up</c> creates. Must match
    /// <c>ConnectionStrings__Postgres</c> in <c>.env.example</c> and the Postgres entry in
    /// <c>appsettings.Development.json</c> — <c>LocalConnectionStringTests</c> fails if any of the
    /// three drift apart.
    /// </summary>
    public const string LocalDevelopmentConnectionString =
        "Host=localhost;Port=5432;Database=trips_agent;Username=trips;Password=trips_local_dev";

    public AppDbContext CreateDbContext(string[] args)
    {
        var connectionString =
            Environment.GetEnvironmentVariable("ConnectionStrings__Postgres")
            ?? LocalDevelopmentConnectionString;

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(connectionString, npgsql => npgsql.MigrationsAssembly(typeof(AppDbContext).Assembly.FullName))
            .UseSnakeCaseNamingConvention()
            .Options;

        return new AppDbContext(options, TimeProvider.System);
    }
}
