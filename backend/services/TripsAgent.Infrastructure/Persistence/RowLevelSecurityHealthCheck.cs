using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;

namespace TripsAgent.Infrastructure.Persistence;

/// <summary>
/// Reports whether the application's database role is actually policed by row-level security.
/// </summary>
/// <remarks>
/// <para>
/// The failure this exists for is silent: a superuser or a BYPASSRLS role skips every policy, so a
/// deployment connecting as one runs with the tenant backstop switched off and nothing complains.
/// Before this change that was every environment, and nobody could have told.
/// </para>
/// <para>
/// Unhealthy in Production, so a load balancer pulls the instance rather than serve with isolation
/// off. Degraded everywhere else: local development connects as the superuser docker compose creates,
/// and should see the warning without being stopped by it. See ADR-0006.
/// </para>
/// </remarks>
public sealed class RowLevelSecurityHealthCheck : IHealthCheck
{
    public const string Name = "row-level-security";

    private readonly AppDbContext _db;
    private readonly IHostEnvironment _environment;

    public RowLevelSecurityHealthCheck(AppDbContext db, IHostEnvironment environment)
    {
        _db = db;
        _environment = environment;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var role = await _db.Database
            .SqlQuery<RoleRow>(
                $"""
                 SELECT current_user::text AS name, (rolsuper OR rolbypassrls) AS bypasses
                   FROM pg_roles
                  WHERE rolname = current_user
                 """)
            .SingleAsync(cancellationToken);

        if (!role.Bypasses)
        {
            return HealthCheckResult.Healthy($"Connected as {role.Name}; row-level security is enforced.");
        }

        var message =
            $"Connected as {role.Name}, which bypasses row-level security, so the tenant-isolation "
            + "backstop is off. Connect the application as tripsagent_app — see docs/adr/0006.";

        return _environment.IsProduction()
            ? HealthCheckResult.Unhealthy(message)
            : HealthCheckResult.Degraded(message);
    }

    private sealed record RoleRow(string Name, bool Bypasses);
}

/// <summary>Registers <see cref="RowLevelSecurityHealthCheck"/>.</summary>
public static class RowLevelSecurityHealthCheckRegistration
{
    public static IHealthChecksBuilder AddRowLevelSecurityCheck(this IHealthChecksBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.AddCheck<RowLevelSecurityHealthCheck>(RowLevelSecurityHealthCheck.Name, tags: ["database"]);
    }
}
