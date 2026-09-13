using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using TripsAgent.Application.Crm;
using TripsAgent.Application.Tenancy;

namespace TripsAgent.Infrastructure.Persistence;

/// <summary>
/// Hashes the quote links that are still stored in clear (issue 175).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> Turning a stored token into its hash is a data migration, and SQL cannot do
/// it: the key lives in configuration, never in the database — the same reason
/// <see cref="Encryption.FieldEncryptionBackfill"/> exists. So <c>migrate</c> applies
/// <c>AddQuoteLinkTokenHash</c>, runs this, and only then applies <c>DropPlaintextQuoteLinkToken</c>,
/// which refuses to drop the column while any token in it is still unhashed.
/// </para>
/// <para>
/// The customers' links go on working: what they hold is the token, and the row now holds its hash,
/// which is what a presented link is looked up by. What is lost is the console's copy of those links —
/// a hash cannot be turned back into a token, and only links minted after this change are derived from
/// the quote's id and so can be worked out again.
/// </para>
/// <para>
/// Idempotent, and a no-op once the plaintext column has been dropped. Each batch is read and rewritten
/// in turn, and a row loses its plaintext as it is hashed, so an interrupted run simply resumes.
/// </para>
/// </remarks>
public sealed partial class QuoteLinkTokenBackfill
{
    /// <summary>How many rows are read and rewritten per batch.</summary>
    public const int BatchSize = 500;

    private readonly AppDbContext _db;
    private readonly IPlatformScope _platformScope;
    private readonly QuoteLinks _links;
    private readonly ILogger _logger;

    /// <param name="db">The schema owner's context: this runs beside the migrations, as they do.</param>
    /// <param name="platformScope">Every agency's rows, which is what a platform-wide data migration is.</param>
    /// <param name="links">Makes the hash that is stored, under the server's key.</param>
    /// <param name="logger">Where the count goes.</param>
    public QuoteLinkTokenBackfill(
        AppDbContext db,
        IPlatformScope platformScope,
        QuoteLinks links,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(platformScope);
        ArgumentNullException.ThrowIfNull(links);
        ArgumentNullException.ThrowIfNull(logger);

        _db = db;
        _platformScope = platformScope;
        _links = links;
        _logger = logger;
    }

    /// <summary>Hashes every quote link still held in clear. Returns how many rows were rewritten.</summary>
    public async Task<int> RunAsync(CancellationToken cancellationToken = default)
    {
        using var scope = _platformScope.Enter(
            "quote links: hashing the tokens still stored in clear, across every agency (issue 175)");

        await _db.Database.OpenConnectionAsync(cancellationToken);

        try
        {
            var connection = (NpgsqlConnection)_db.Database.GetDbConnection();

            // Nothing to do once DropPlaintextQuoteLinkToken has run, which is the ordinary case on a
            // database that was never on the old schema at all.
            if (!await HasPlaintextColumnAsync(connection, cancellationToken))
            {
                return 0;
            }

            var rewritten = 0;

            while (true)
            {
                var batch = new List<(Guid Id, string Token)>();

                await using (var read = connection.CreateCommand())
                {
                    read.CommandText =
                        $"""
                         SELECT id, public_token
                           FROM crm.quotes
                          WHERE public_token IS NOT NULL
                          ORDER BY id
                          LIMIT {BatchSize}
                         """;

                    await using var reader = await read.ExecuteReaderAsync(cancellationToken);

                    while (await reader.ReadAsync(cancellationToken))
                    {
                        batch.Add((reader.GetGuid(0), reader.GetString(1)));
                    }
                }

                // Each pass clears the plaintext of everything it hashes, so the next asks the same
                // question and gets the next rows. An empty answer means there is nothing left.
                if (batch.Count == 0)
                {
                    break;
                }

                foreach (var (id, token) in batch)
                {
                    await using var write = connection.CreateCommand();

                    write.CommandText =
                        """
                        UPDATE crm.quotes
                           SET public_token_hash = @hash,
                               public_token = NULL
                         WHERE id = @id
                        """;

                    write.Parameters.AddWithValue("hash", _links.HashOf(token));
                    write.Parameters.AddWithValue("id", id);

                    rewritten += await write.ExecuteNonQueryAsync(cancellationToken);
                }
            }

            if (rewritten > 0)
            {
                LogHashed(_logger, rewritten);
            }

            return rewritten;
        }
        finally
        {
            await _db.Database.CloseConnectionAsync();
        }
    }

    private static async Task<bool> HasPlaintextColumnAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();

        command.CommandText =
            """
            SELECT count(*) FROM information_schema.columns
             WHERE table_schema = 'crm' AND table_name = 'quotes' AND column_name = 'public_token'
            """;

        return (long)(await command.ExecuteScalarAsync(cancellationToken))! > 0;
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Quote links: hashed {Rows} token(s) that were stored in clear. The links themselves still work.")]
    private static partial void LogHashed(ILogger logger, int rows);
}
