using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TripsAgent.Application.Tenancy;
using TripsAgent.Domain.Tenancy;

namespace TripsAgent.Infrastructure.Persistence;

/// <summary>
/// Loads the development dataset. Invoked by <c>dotnet run -- seed</c>.
/// </summary>
/// <remarks>
/// <para>
/// The point is that a developer who has just cloned the repository can sign in and see a
/// populated console, rather than an empty one they cannot tell apart from a broken one.
/// </para>
/// <para>
/// Every insert is keyed on a stable slug and skipped when the row already exists, so running
/// this twice is harmless. It refuses to run against a database that is missing migrations,
/// because a half-migrated schema produces confusing errors rather than useful ones.
/// </para>
/// </remarks>
public static partial class DatabaseSeeder
{
    /// <summary>The argument that triggers a seed run instead of serving traffic.</summary>
    public const string CommandName = "seed";

    /// <summary>Slug of the demo principal agency.</summary>
    public const string PrincipalSlug = "lagos-travel";

    /// <summary>Slug of the demo sub-agent, managed by the principal.</summary>
    public const string SubAgentSlug = "ikeja-branch";

    /// <summary>True when the process was started to seed rather than to serve.</summary>
    public static bool IsSeedCommand(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        return args.Contains(CommandName, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Seeds the development dataset. Returns a process exit code: 0 on success, 1 on failure.
    /// </summary>
    public static async Task<int> RunAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(services);

        await using var scope = services.CreateAsyncScope();

        var logger = scope.ServiceProvider
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger(typeof(DatabaseSeeder));

        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var platformScope = scope.ServiceProvider.GetRequiredService<IPlatformScope>();

        try
        {
            var pending = (await dbContext.Database.GetPendingMigrationsAsync(cancellationToken)).ToArray();

            if (pending.Length > 0)
            {
                LogPendingMigrations(logger, pending.Length);
                return 1;
            }

            var created = await SeedAsync(dbContext, platformScope, cancellationToken);

            if (created == 0)
            {
                LogNothingToDo(logger);
            }
            else
            {
                LogSeeded(logger, created);
            }

            return 0;
        }
        catch (Exception ex)
        {
            LogFailed(logger, ex);
            return 1;
        }
    }

    /// <summary>
    /// Inserts the demo agencies if they are not already there. Returns how many were created.
    /// </summary>
    /// <remarks>
    /// Exposed separately from <see cref="RunAsync"/> so integration tests can seed a database
    /// without building a service provider first.
    /// </remarks>
    public static async Task<int> SeedAsync(
        AppDbContext dbContext,
        IPlatformScope platformScope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(platformScope);

        // Seeding is platform work: it creates rows for several agencies, and its "have I run
        // already?" check has to see across all of them. Without the scope the tenant filter
        // hides the rows it just wrote and the seeder would insert duplicates on every run.
        using var _ = platformScope.Enter("database seeding — writes and verifies rows across agencies");

        if (await dbContext.Agencies.AnyAsync(a => a.Slug == PrincipalSlug, cancellationToken))
        {
            return 0;
        }

        // A principal with one sub-agent beneath it: the smallest dataset that exercises the
        // hierarchy, so a subtree query has something to return.
        var principal = Agency.RegisterPrincipal(
            legalName: "Lagos Travel Services Limited",
            slug: PrincipalSlug,
            countryCode: "NG",
            baseCurrency: "NGN",
            timezone: "Africa/Lagos",
            tradingName: "Lagos Travel");

        var subAgent = Agency.RegisterSubAgent(
            principal,
            legalName: "Ikeja Branch Travel Limited",
            slug: SubAgentSlug,
            tradingName: "Ikeja Branch");

        dbContext.Agencies.AddRange(principal, subAgent);

        dbContext.AgencySettings.AddRange(
            AgencySettings.CreateDefault(principal),
            AgencySettings.CreateDefault(subAgent));

        dbContext.AgencyBranding.AddRange(
            AgencyBranding.CreateDefault(principal),
            AgencyBranding.CreateDefault(subAgent));

        await dbContext.SaveChangesAsync(cancellationToken);

        return 2;
    }

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Database has {Count} migration(s) still to apply. Run `dotnet run --project "
                  + "services/TripsAgent.Api -- migrate` first — seeding a half-built schema fails "
                  + "in ways that are hard to read.")]
    private static partial void LogPendingMigrations(ILogger logger, int count);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Seed data is already present; nothing to do.")]
    private static partial void LogNothingToDo(ILogger logger);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Seeded {Count} agencies.")]
    private static partial void LogSeeded(ILogger logger, int count);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Seeding failed. Nothing was committed — the whole seed runs in one transaction.")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}
