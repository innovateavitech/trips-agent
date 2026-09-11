using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TripsAgent.Application.Tenancy;

namespace TripsAgent.Infrastructure.Persistence;

/// <summary>
/// Applies pending EF Core migrations, then exits. Invoked by <c>dotnet run -- migrate</c>.
/// </summary>
/// <remarks>
/// <para>
/// Migrations are applied by an explicit command, never automatically on startup. If the API
/// migrated itself as it booted, a rolling deploy would have several instances racing to alter
/// the same tables, and a bad migration would take the whole service down with it instead of
/// failing one clearly-labelled step in the pipeline.
/// </para>
/// <para>
/// The log methods below are written with <c>[LoggerMessage]</c> rather than
/// <c>logger.LogInformation(...)</c>. The source generator turns each into a strongly-typed call
/// that allocates nothing when the level is disabled — which is also what stops analyser CA1873
/// complaining about boxing an <c>int</c> into a params array on every call.
/// </para>
/// </remarks>
public static partial class DatabaseMigrator
{
    /// <summary>The argument that triggers a migration run instead of serving traffic.</summary>
    public const string CommandName = "migrate";

    /// <summary>True when the process was started to migrate rather than to serve.</summary>
    public static bool IsMigrationCommand(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        return args.Contains(CommandName, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Applies every pending migration. Returns a process exit code: 0 on success, 1 on failure,
    /// so CI and the deploy pipeline can branch on it.
    /// </summary>
    public static async Task<int> RunAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(services);

        await using var scope = services.CreateAsyncScope();

        var logger = scope.ServiceProvider
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger(typeof(DatabaseMigrator));

        // The schema owner, not the application role: creating tables is DDL, and row-level security
        // must not be what decides whether reference data can be seeded (ADR-0006).
        var dbContext = scope.ServiceProvider.GetRequiredKeyedService<AppDbContext>(AdminDbContextFactory.ServiceKey);
        var platformScope = scope.ServiceProvider.GetRequiredService<IPlatformScope>();

        try
        {
            var pending = (await dbContext.Database.GetPendingMigrationsAsync(cancellationToken)).ToArray();

            if (pending.Length == 0)
            {
                LogAlreadyUpToDate(logger);
            }
            else
            {
                // Joined into a local first: this runs once per migrate command so the cost is
                // irrelevant, and CA1873 objects to building a string inline in a logging argument.
                var migrationNames = string.Join(", ", pending);
                LogApplying(logger, pending.Length, migrationNames);

                await dbContext.Database.MigrateAsync(cancellationToken);

                LogApplied(logger);
            }

            // Always, even with no schema change: a permission added in code needs no migration,
            // and would otherwise never reach a production database.
            await ReferenceDataSeeder.EnsureAsync(dbContext, platformScope, cancellationToken);
            LogReferenceDataEnsured(logger);

            return 0;
        }
        catch (Exception ex)
        {
            LogFailed(logger, ex);
            return 1;
        }
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Database is already up to date; no migrations to apply.")]
    private static partial void LogAlreadyUpToDate(ILogger logger);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Applying {Count} pending migration(s): {Migrations}")]
    private static partial void LogApplying(ILogger logger, int count, string migrations);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Migrations applied successfully.")]
    private static partial void LogApplied(ILogger logger);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Reference data (permissions and system roles) is present.")]
    private static partial void LogReferenceDataEnsured(ILogger logger);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Migration failed. The database has been left at its previous version — each "
                  + "migration runs in its own transaction, so nothing is half-applied.")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}
