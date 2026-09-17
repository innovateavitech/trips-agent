using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TripsAgent.Application.Security;
using TripsAgent.Application.Tenancy;
using TripsAgent.Infrastructure.Persistence.Encryption;
using TripsAgent.Infrastructure.Security;

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

                // Encrypting a column that already holds data is a data migration, and SQL cannot do it:
                // the key is in configuration, not in the database (issue 104). So the run stops at the
                // migration that adds the ciphertext columns, encrypts what is there, and only then
                // applies the one that drops the plaintext columns — which refuses to drop a column with
                // anything unencrypted still in it.
                if (pending.Any(migration => migration.EndsWith(EncryptionColumnsMigration, StringComparison.Ordinal)))
                {
                    LogPausingForBackfill(logger);

                    await dbContext.GetService<IMigrator>().MigrateAsync(EncryptionColumnsMigration, cancellationToken);
                    await BackfillEncryptionAsync(scope.ServiceProvider, dbContext, platformScope, logger, cancellationToken);
                }

                // The same shape again for the quote links (issue 175): hashing a stored token needs
                // the key, which is in configuration and not in the database. So the run stops at the
                // migration that adds the hash column, hashes what is there, and only then applies the
                // one that drops the plaintext column — which refuses while a token is still unhashed.
                if (pending.Any(migration => migration.EndsWith(QuoteLinkHashMigration, StringComparison.Ordinal)))
                {
                    LogPausingForQuoteLinks(logger);

                    await dbContext.GetService<IMigrator>().MigrateAsync(QuoteLinkHashMigration, cancellationToken);
                }

                // Asked separately from the pause above, so that a run interrupted between the two
                // migrations hashes what is left when it is started again rather than meeting the guard.
                if (pending.Any(migration => migration.EndsWith(QuoteLinkDropMigration, StringComparison.Ordinal)))
                {
                    await BackfillQuoteLinksAsync(scope.ServiceProvider, dbContext, platformScope, logger, cancellationToken);
                }

                await dbContext.Database.MigrateAsync(cancellationToken);

                LogApplied(logger);
            }

            // Always, even with nothing pending: this is also the key rotation pass, which re-encrypts
            // every value still under a retired key id.
            await BackfillEncryptionAsync(scope.ServiceProvider, dbContext, platformScope, logger, cancellationToken);

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

    /// <summary>The migration that adds the ciphertext columns; the backfill runs straight after it.</summary>
    public const string EncryptionColumnsMigration = "AddEncryptedPiiColumns";

    /// <summary>The migration that adds the quote link's hash column; the hashing runs straight after it.</summary>
    public const string QuoteLinkHashMigration = "AddQuoteLinkTokenHash";

    /// <summary>The migration that drops the plaintext quote link, once nothing in it is left unhashed.</summary>
    public const string QuoteLinkDropMigration = "DropPlaintextQuoteLinkToken";

    /// <summary>
    /// Hashes the quote links still stored in clear, between the two migrations that make the change.
    /// </summary>
    private static async Task BackfillQuoteLinksAsync(
        IServiceProvider services,
        AppDbContext dbContext,
        IPlatformScope platformScope,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        // The same hasher the application uses, and no fallback: a hash made under any other key
        // would lock every customer out of the quote they are holding a link to.
        var links = services.GetService<TripsAgent.Application.Crm.QuoteLinks>()
            ?? new TripsAgent.Application.Crm.QuoteLinks(
                services.GetRequiredService<TripsAgent.Application.Identity.ITokenHasher>());

        await new QuoteLinkTokenBackfill(dbContext, platformScope, links, logger).RunAsync(cancellationToken);
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Pausing after the migration that adds the quote link hash, to hash the links already stored.")]
    private static partial void LogPausingForQuoteLinks(ILogger logger);

    /// <summary>
    /// Encrypts traveller documents and bank account numbers that are still in clear, and re-encrypts
    /// anything under a retired key.
    /// </summary>
    private static async Task BackfillEncryptionAsync(
        IServiceProvider services,
        AppDbContext dbContext,
        IPlatformScope platformScope,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var backfill = new FieldEncryptionBackfill(
            dbContext,
            platformScope,

            // GetService, not GetRequiredService: a container built by hand for a one-off migration
            // need not know about keys, and the encryptor that refuses says exactly what is missing
            // if it turns out there is something to encrypt.
            services.GetService<IFieldEncryptor>() ?? UnconfiguredFieldEncryptor.Instance,

            // Resolved only if a row turns out to be in the pre-issue-104 format, so a database with none
            // needs no legacy key configured.
            services.GetRequiredService<ISecretProtector>,
            logger);

        var outcomes = await backfill.RunAsync(cancellationToken);
        var rows = outcomes.Sum(outcome => outcome.Rows);

        LogEncrypted(logger, rows);
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

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Pausing after the migration that adds the encrypted columns, to encrypt what is already stored.")]
    private static partial void LogPausingForBackfill(ILogger logger);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Field encryption: {Rows} stored value(s) encrypted or re-encrypted under the active key.")]
    private static partial void LogEncrypted(ILogger logger, int rows);
}
