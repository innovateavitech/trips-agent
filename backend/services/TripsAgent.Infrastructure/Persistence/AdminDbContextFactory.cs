using Microsoft.EntityFrameworkCore;
using TripsAgent.Application.Tenancy;

namespace TripsAgent.Infrastructure.Persistence;

/// <summary>
/// Builds an <see cref="AppDbContext"/> on the admin connection — the role that owns the schema
/// and bypasses row-level security. For migrations and DDL, never for requests. See ADR-0006.
/// </summary>
/// <remarks>
/// <para>
/// The application connects as <c>tripsagent_app</c>, which row-level security polices. A few jobs
/// genuinely need the owner: applying migrations, and the audit log's partition maintenance, which
/// creates and drops tables. They get this connection, and nothing else does.
/// </para>
/// <para>
/// Falls back to the application's connection string when <c>ConnectionStrings:PostgresAdmin</c> is
/// not set, so an environment that has not split its roles yet keeps working exactly as before.
/// </para>
/// </remarks>
public sealed class AdminDbContextFactory
{
    /// <summary>The DI key the scoped admin context is registered under.</summary>
    public const string ServiceKey = "admin";

    private readonly DbContextOptions<AppDbContext> _options;
    private readonly TimeProvider _clock;

    public AdminDbContextFactory(string connectionString, TimeProvider clock)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        _options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(connectionString, npgsql => npgsql.MigrationsAssembly(typeof(AppDbContext).Assembly.FullName))
            .UseSnakeCaseNamingConvention()
            .Options;

        _clock = clock;
    }

    /// <summary>
    /// A context on the admin connection sharing the caller's tenant context and platform scope,
    /// so entering the scope affects it exactly as it would the request's own context.
    /// </summary>
    public AppDbContext Create(ITenantContext tenantContext, IPlatformScope platformScope) =>
        new(_options, _clock, tenantContext, platformScope);
}
