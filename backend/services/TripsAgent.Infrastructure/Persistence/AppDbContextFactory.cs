using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Logging.Abstractions;
using TripsAgent.Application.Tenancy;
using TripsAgent.Infrastructure.Tenancy;

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
    private const string FallbackConnectionString =
        "Host=localhost;Port=5432;Database=tripsagent;Username=postgres;Password=postgres";

    public AppDbContext CreateDbContext(string[] args)
    {
        var connectionString =
            Environment.GetEnvironmentVariable("ConnectionStrings__Postgres")
            ?? FallbackConnectionString;

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(connectionString, npgsql => npgsql.MigrationsAssembly(typeof(AppDbContext).Assembly.FullName))
            .UseSnakeCaseNamingConvention()
            .Options;

        // No tenant at design time, and none needed: query filters shape what a request can
        // read, not what the schema looks like, so they have no bearing on a generated migration.
        var tenantContext = new TenantContext();

        return new AppDbContext(
            options,
            TimeProvider.System,
            tenantContext,
            new PlatformScope(tenantContext, NullLogger<PlatformScope>.Instance));
    }
}
