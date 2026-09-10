using Microsoft.EntityFrameworkCore;

namespace TripsAgent.Infrastructure.Persistence;

/// <summary>
/// One place that decides how <see cref="AppDbContext"/> talks to PostgreSQL.
///
/// The running app and the <c>dotnet ef</c> tooling both call this, so a migration is always
/// generated against exactly the model the app will use. When those two drift, you get
/// migrations that apply cleanly and then fail at runtime.
/// </summary>
public static class AppDbContextOptions
{
    /// <summary>
    /// EF's own bookkeeping table. Renamed from the default <c>__EFMigrationsHistory</c> so it
    /// does not need quoting in a database where every other identifier is snake_case.
    /// </summary>
    public const string MigrationsHistoryTable = "__ef_migrations_history";

    /// <summary>Applies the Npgsql provider and the naming convention.</summary>
    public static DbContextOptionsBuilder Configure(
        DbContextOptionsBuilder options,
        string connectionString)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        return options
            .UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable(MigrationsHistoryTable))

            // Postgres folds unquoted identifiers to lower case, so PascalCase table and column
            // names would have to be quoted in every hand-written query, forever. snake_case
            // keeps psql, migrations and EF reading the same way.
            .UseSnakeCaseNamingConvention();
    }
}
