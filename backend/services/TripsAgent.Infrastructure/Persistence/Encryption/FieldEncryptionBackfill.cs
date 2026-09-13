using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using TripsAgent.Application.Security;
using TripsAgent.Application.Tenancy;

namespace TripsAgent.Infrastructure.Persistence.Encryption;

/// <summary>What one run encrypted, per column.</summary>
/// <param name="Table">The table, schema-qualified.</param>
/// <param name="Rows">How many rows were rewritten.</param>
public sealed record FieldEncryptionBackfillOutcome(string Table, int Rows);

/// <summary>
/// Encrypts what is already in the database (issue 104), and re-encrypts what is under a retired key.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists at all.</b> Turning a column into ciphertext is a data migration, and SQL cannot do
/// it: the key lives in configuration, never in the database. So <c>migrate</c> applies
/// <c>AddEncryptedPiiColumns</c>, runs this, and only then applies <c>DropPlaintextPiiColumns</c> — which
/// refuses to drop a column while anything in it is still unencrypted. Nothing is left behind, and nothing
/// with data in it is dropped.
/// </para>
/// <para>
/// <b>It is also the rotation pass.</b> A value carries the id of the key that made it, so after the active
/// key changes this finds every value under the old one and writes it again under the new one. Keep the old
/// key configured until a run reports nothing left to do; then it can be removed.
/// </para>
/// <para>
/// <b>Idempotent.</b> A row is only touched when it holds something to encrypt or re-encrypt, so a second
/// run in a row does nothing. Each table is done in batches, each batch in its own transaction, so an
/// interrupted run leaves whole rows either done or not done.
/// </para>
/// </remarks>
public sealed partial class FieldEncryptionBackfill
{
    /// <summary>How many rows are read and rewritten per transaction.</summary>
    public const int BatchSize = 500;

    private readonly AppDbContext _db;
    private readonly IPlatformScope _platformScope;
    private readonly IFieldEncryptor _encryptor;
    private readonly Func<ISecretProtector> _legacyProtector;
    private readonly ILogger _logger;

    /// <param name="db">The schema owner's context: this runs beside the migrations, as they do.</param>
    /// <param name="platformScope">Every agency's rows, which is what a platform-wide data migration is.</param>
    /// <param name="encryptor">The key ring. Only needed when there is something to encrypt.</param>
    /// <param name="legacyProtector">
    /// The supplier-credential protector, which is what passport numbers were stored under before issue 104.
    /// Resolved lazily, so a database with no such rows needs no legacy key configured.
    /// </param>
    /// <param name="logger">Where the per-table counts go.</param>
    public FieldEncryptionBackfill(
        AppDbContext db,
        IPlatformScope platformScope,
        IFieldEncryptor encryptor,
        Func<ISecretProtector> legacyProtector,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(platformScope);
        ArgumentNullException.ThrowIfNull(encryptor);
        ArgumentNullException.ThrowIfNull(legacyProtector);
        ArgumentNullException.ThrowIfNull(logger);

        _db = db;
        _platformScope = platformScope;
        _encryptor = encryptor;
        _legacyProtector = legacyProtector;
        _logger = logger;
    }

    /// <summary>Encrypts everything still in clear, and everything under a key that is no longer active.</summary>
    public async Task<IReadOnlyList<FieldEncryptionBackfillOutcome>> RunAsync(CancellationToken cancellationToken = default)
    {
        using var scope = _platformScope.Enter(
            "Field encryption: encrypting traveller documents and bank account numbers across all agencies (issue 104)");

        await _db.Database.OpenConnectionAsync(cancellationToken);

        try
        {
            var connection = (NpgsqlConnection)_db.Database.GetDbConnection();

            return
            [
                await TravellersAsync(connection, cancellationToken),
                await PassengerDocumentsAsync(connection, cancellationToken),
                await BankAccountsAsync(connection, cancellationToken),
            ];
        }
        finally
        {
            await _db.Database.CloseConnectionAsync();
        }
    }

    // ------------------------------------------------------------------ orders.order_travellers

    private async Task<FieldEncryptionBackfillOutcome> TravellersAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        const string table = "orders.order_travellers";
        var hasPlaintextExpiry = await HasColumnAsync(connection, "orders", "order_travellers", "passport_expiry", cancellationToken);

        var select =
            $"""
             SELECT id, passport_number_encrypted{(hasPlaintextExpiry ? ", passport_expiry" : ", NULL::date")}, passport_expiry_encrypted
               FROM {table}
              WHERE passport_number_encrypted IS NOT NULL
                 {(hasPlaintextExpiry ? "OR passport_expiry IS NOT NULL" : string.Empty)}
                 OR passport_expiry_encrypted IS NOT NULL
              ORDER BY id
              LIMIT {BatchSize} OFFSET @offset
             """;

        var update =
            $"""
             UPDATE {table}
                SET passport_number_encrypted = @number,
                    passport_expiry_encrypted = @expiry
                    {(hasPlaintextExpiry ? ", passport_expiry = NULL" : string.Empty)}
              WHERE id = @id
             """;

        return await RewriteAsync(
            connection,
            table,
            select,
            update,
            reader =>
            {
                var id = reader.GetGuid(0);
                var number = reader.IsDBNull(1) ? null : (byte[])reader[1];
                var plainExpiry = reader.IsDBNull(2) ? (DateOnly?)null : DateOnly.FromDateTime(reader.GetDateTime(2));
                var encryptedExpiry = reader.IsDBNull(3) ? null : (byte[])reader[3];

                var newNumber = Reencrypt(number, EncryptedColumns.OrderTravellerPassportNumber);
                var newExpiry = ReencryptDate(encryptedExpiry, plainExpiry, EncryptedColumns.OrderTravellerPassportExpiry);

                var changed = !AreEqual(number, newNumber) || !AreEqual(encryptedExpiry, newExpiry) || plainExpiry is not null;

                return changed
                    ? new Rewrite(id, [Parameter("number", newNumber), Parameter("expiry", newExpiry)])
                    : null;
            },
            cancellationToken);
    }

    // ------------------------------------------------------------------ supplier.passenger_documents

    private async Task<FieldEncryptionBackfillOutcome> PassengerDocumentsAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        const string table = "supplier.passenger_documents";
        var hasPlaintextExpiry = await HasColumnAsync(connection, "supplier", "passenger_documents", "expires_on", cancellationToken);

        var select =
            $"""
             SELECT id, doc_number_encrypted{(hasPlaintextExpiry ? ", expires_on" : ", NULL::date")}, expires_on_encrypted
               FROM {table}
              ORDER BY id
              LIMIT {BatchSize} OFFSET @offset
             """;

        var update =
            $"""
             UPDATE {table}
                SET doc_number_encrypted = @number,
                    expires_on_encrypted = @expiry
                    {(hasPlaintextExpiry ? ", expires_on = NULL" : string.Empty)}
              WHERE id = @id
             """;

        return await RewriteAsync(
            connection,
            table,
            select,
            update,
            reader =>
            {
                var id = reader.GetGuid(0);
                var number = (byte[])reader[1];
                var plainExpiry = reader.IsDBNull(2) ? (DateOnly?)null : DateOnly.FromDateTime(reader.GetDateTime(2));
                var encryptedExpiry = reader.IsDBNull(3) ? null : (byte[])reader[3];

                var newNumber = Reencrypt(number, EncryptedColumns.PassengerDocumentNumber)!;
                var newExpiry = ReencryptDate(encryptedExpiry, plainExpiry, EncryptedColumns.PassengerDocumentExpiry);

                var changed = !AreEqual(number, newNumber) || !AreEqual(encryptedExpiry, newExpiry) || plainExpiry is not null;

                return changed
                    ? new Rewrite(id, [Parameter("number", newNumber), Parameter("expiry", newExpiry)])
                    : null;
            },
            cancellationToken);
    }

    // ------------------------------------------------------------------ payments.agency_bank_accounts

    private async Task<FieldEncryptionBackfillOutcome> BankAccountsAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        const string table = "payments.agency_bank_accounts";
        var hasPlaintext = await HasColumnAsync(connection, "payments", "agency_bank_accounts", "account_number", cancellationToken);

        var select =
            $"""
             SELECT id, {(hasPlaintext ? "account_number" : "NULL::text")}, account_number_encrypted
               FROM {table}
              ORDER BY id
              LIMIT {BatchSize} OFFSET @offset
             """;

        // The plaintext column is NOT NULL — it was the only copy until now — so it cannot be cleared
        // here. DropPlaintextPiiColumns, which migrate applies moments later, removes it outright.
        var update =
            $"""
             UPDATE {table}
                SET account_number_encrypted = @number
              WHERE id = @id
             """;

        return await RewriteAsync(
            connection,
            table,
            select,
            update,
            reader =>
            {
                var id = reader.GetGuid(0);
                var plain = reader.IsDBNull(1) ? null : reader.GetString(1).Trim();
                var encrypted = reader.IsDBNull(2) ? null : (byte[])reader[2];

                byte[]? rewritten;

                if (encrypted is null)
                {
                    rewritten = string.IsNullOrWhiteSpace(plain) ? null : _encryptor.Encrypt(plain, EncryptedColumns.AgencyBankAccountNumber);
                }
                else
                {
                    rewritten = Reencrypt(encrypted, EncryptedColumns.AgencyBankAccountNumber);
                }

                if (rewritten is null)
                {
                    // Nothing to encrypt and nothing encrypted: an impossible row, and not this job's to fix.
                    return null;
                }

                var changed = !AreEqual(encrypted, rewritten) || plain is not null;

                return changed ? new Rewrite(id, [Parameter("number", rewritten)]) : null;
            },
            cancellationToken);
    }

    // ------------------------------------------------------------------ the machinery

    /// <summary>One row to write again, and the values to write.</summary>
    private sealed record Rewrite(Guid Id, IReadOnlyList<NpgsqlParameter> Values);

    /// <summary>
    /// Reads every row of one table in batches and writes back the ones that changed.
    /// </summary>
    /// <remarks>
    /// The offset walks forward over an ordering by id, and a rewritten row keeps its place in that
    /// ordering, so nothing is skipped by the update. Reading and writing are separate commands because
    /// Npgsql keeps one reader open per connection at a time.
    /// </remarks>
    private async Task<FieldEncryptionBackfillOutcome> RewriteAsync(
        NpgsqlConnection connection,
        string table,
        string select,
        string update,
        Func<NpgsqlDataReader, Rewrite?> plan,
        CancellationToken cancellationToken)
    {
        var offset = 0;
        var rewritten = 0;

        while (true)
        {
            var batch = new List<Rewrite>();
            var read = 0;

            await using (var command = connection.CreateCommand())
            {
                command.CommandText = select;
                command.Parameters.AddWithValue("offset", offset);

                await using var reader = await command.ExecuteReaderAsync(cancellationToken);

                while (await reader.ReadAsync(cancellationToken))
                {
                    read++;

                    if (plan(reader) is { } rewrite)
                    {
                        batch.Add(rewrite);
                    }
                }
            }

            foreach (var rewrite in batch)
            {
                await using var command = connection.CreateCommand();
                command.CommandText = update;
                command.Parameters.AddWithValue("id", rewrite.Id);

                foreach (var value in rewrite.Values)
                {
                    command.Parameters.Add(value);
                }

                rewritten += await command.ExecuteNonQueryAsync(cancellationToken);
            }

            if (read < BatchSize)
            {
                break;
            }

            offset += read;
        }

        if (rewritten > 0)
        {
            LogRewrote(_logger, rewritten, table, _encryptor.ActiveKeyId);
        }

        return new FieldEncryptionBackfillOutcome(table, rewritten);
    }

    /// <summary>
    /// A value under the active key, whatever it is now: already current, under a retired key, or in the
    /// format passport numbers used before issue 104.
    /// </summary>
    private byte[]? Reencrypt(byte[]? stored, string purpose)
    {
        if (stored is null || stored.Length == 0)
        {
            return stored;
        }

        var keyId = _encryptor.KeyIdOf(stored);

        if (keyId == _encryptor.ActiveKeyId)
        {
            return stored;
        }

        // Not a field-encryption value at all: it is one AesGcmSecretProtector wrote, which is how
        // passport numbers were stored before this existed.
        var plaintext = keyId is null
            ? _legacyProtector().Unprotect(stored, purpose)
            : _encryptor.Decrypt(stored, purpose);

        return _encryptor.Encrypt(plaintext, purpose);
    }

    /// <summary>The encrypted expiry: the plaintext column's value when there is one, else the stored value under the active key.</summary>
    private byte[]? ReencryptDate(byte[]? stored, DateOnly? plaintext, string purpose)
    {
        if (plaintext is { } date)
        {
            return _encryptor.Encrypt(date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), purpose);
        }

        return Reencrypt(stored, purpose);
    }

    private static NpgsqlParameter Parameter(string name, byte[]? value) =>
        new(name, NpgsqlTypes.NpgsqlDbType.Bytea) { Value = (object?)value ?? DBNull.Value };

    private static bool AreEqual(byte[]? left, byte[]? right) =>
        left is null ? right is null : right is not null && left.AsSpan().SequenceEqual(right);

    private static async Task<bool> HasColumnAsync(
        NpgsqlConnection connection, string schema, string table, string column, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT count(*) FROM information_schema.columns
             WHERE table_schema = @schema AND table_name = @table AND column_name = @column
            """;
        command.Parameters.AddWithValue("schema", schema);
        command.Parameters.AddWithValue("table", table);
        command.Parameters.AddWithValue("column", column);

        return (long)(await command.ExecuteScalarAsync(cancellationToken))! > 0;
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Field encryption: rewrote {Rows} row(s) of {Table} under key {KeyId}.")]
    private static partial void LogRewrote(ILogger logger, int rows, string table, string keyId);
}
