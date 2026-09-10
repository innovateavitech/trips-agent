using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace TripsAgent.Infrastructure.Persistence;

/// <summary>
/// Lets <c>dotnet ef</c> build an <see cref="AppDbContext"/> without starting the API.
///
/// Without this, every migration command needs a startup project and a full host boot, which
/// fails as soon as the API depends on something the tooling has no configuration for.
/// With it, this works from a clean checkout:
///
/// <code>
/// dotnet ef migrations add AddAgencies -p services/TripsAgent.Infrastructure
/// </code>
///
/// Generating a migration never connects to the database — the connection string only has to be
/// well-formed. Applying one does, and that is <c>dotnet run --project services/TripsAgent.Api
/// -- migrate</c>.
/// </summary>
public sealed class AppDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    /// <summary>
    /// Environment variable read at design time. The double underscore is .NET's separator for
    /// nested configuration, so this is the same setting as <c>ConnectionStrings:Postgres</c>
    /// in appsettings — export it and the tooling and the app agree.
    /// </summary>
    public const string ConnectionStringVariable = "ConnectionStrings__Postgres";

    /// <summary>
    /// Matches the Postgres service in docker-compose (issue #2). Local only, and no secret:
    /// these credentials reach nothing but a container on your own machine.
    /// </summary>
    public const string LocalDevelopmentConnectionString =
        "Host=localhost;Port=5432;Database=tripsagent;Username=tripsagent;Password=tripsagent";

    public AppDbContext CreateDbContext(string[] args)
    {
        var connectionString =
            Environment.GetEnvironmentVariable(ConnectionStringVariable) is { Length: > 0 } fromEnvironment
                ? fromEnvironment
                : LocalDevelopmentConnectionString;

        var options = new DbContextOptionsBuilder<AppDbContext>();
        AppDbContextOptions.Configure(options, connectionString);

        return new AppDbContext(options.Options);
    }
}
