using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using TripsAgent.Application.Security;
using TripsAgent.Application.Tenancy;
using TripsAgent.Domain.Common;
using TripsAgent.Domain.Orders;
using TripsAgent.Domain.Payments;
using TripsAgent.Domain.Pricing;
using TripsAgent.Domain.Tenancy;
using TripsAgent.Infrastructure.Persistence;
using TripsAgent.Infrastructure.Persistence.Encryption;
using TripsAgent.Infrastructure.Security;
using TripsAgent.Infrastructure.Tenancy;
using TripsAgent.IntegrationTests.Persistence;

namespace TripsAgent.IntegrationTests.Security;

/// <summary>
/// Personal data at rest (issue 104): what a passport number, its expiry and a bank account number look
/// like in the table, and what happens to the rows that were written before any of this existed.
/// </summary>
/// <remarks>
/// Read with raw SQL as the table's owner, deliberately: the application reads through the value
/// converter and would show the plaintext whether or not the column holds any.
/// </remarks>
[Collection(PostgresCollection.Name)]
public sealed class FieldEncryptionTests
{
    private const string Passport = "A01234567";
    private const string AccountNumber = "0123456789";
    private const string ObjectNotInPrerequisiteState = "55000";

    private readonly PostgresFixture _postgres;

    public FieldEncryptionTests(PostgresFixture postgres) => _postgres = postgres;

    // ------------------------------------------------------------------ what is in the table

    [Fact]
    public async Task A_travellers_passport_and_expiry_are_ciphertext_in_the_table()
    {
        await using var world = await WorldAsync();
        var travellerId = await world.AddTravellerAsync(Passport, new DateOnly(2031, 5, 17));

        var number = await world.ColumnAsync<byte[]>("orders.order_travellers", "passport_number_encrypted", travellerId);
        var expiry = await world.ColumnAsync<byte[]>("orders.order_travellers", "passport_expiry_encrypted", travellerId);

        Encoding.UTF8.GetString(number!).Should().NotContain(Passport);
        Encoding.UTF8.GetString(expiry!).Should().NotContain("2031");
        TestFieldEncryption.Encryptor.KeyIdOf(number!).Should().Be(TestFieldEncryption.KeyId);
        TestFieldEncryption.Encryptor.KeyIdOf(expiry!).Should().Be(TestFieldEncryption.KeyId);

        // And the application still sees what was stored, with nobody decrypting anything by hand.
        await using var reader = _postgres.Connect(world.Database, world.Tenant, world.Scope);
        var traveller = await reader.OrderTravellers.SingleAsync(t => t.Id == travellerId);

        traveller.PassportNumber.Should().Be(Passport);
        traveller.PassportExpiry.Should().Be(new DateOnly(2031, 5, 17));
    }

    [Fact]
    public async Task An_agencys_bank_account_number_is_ciphertext_in_the_table()
    {
        await using var world = await WorldAsync();
        var accountId = await world.AddBankAccountAsync(AccountNumber);

        var stored = await world.ColumnAsync<byte[]>("payments.agency_bank_accounts", "account_number_encrypted", accountId);

        Encoding.UTF8.GetString(stored!).Should().NotContain(AccountNumber);

        await using var reader = _postgres.Connect(world.Database, world.Tenant, world.Scope);
        var account = await reader.AgencyBankAccounts.SingleAsync(a => a.Id == accountId);

        account.AccountNumber.Should().Be(AccountNumber);
        account.MaskedNumber.Should().Be("******6789");
    }

    [Fact]
    public async Task The_audit_log_records_no_part_of_an_encrypted_value()
    {
        await using var world = await WorldAsync();

        // agency_bank_accounts is audited, so adding one and then retiring it writes both an insert and
        // an update — the two places a before-and-after state could carry the number.
        var accountId = await world.AddAuditedBankAccountAsync(AccountNumber);

        var rows = await world.Owner.AuditLogs
            .Where(entry => entry.EntityType == nameof(AgencyBankAccount))
            .Select(entry => new { entry.BeforeState, entry.AfterState })
            .ToListAsync();

        var states = rows.Select(row => (row.BeforeState ?? string.Empty) + (row.AfterState ?? string.Empty)).ToList();

        states.Should().NotBeEmpty();
        states.Should().OnlyContain(state => !state.Contains(AccountNumber, StringComparison.Ordinal));

        // Not even the last four digits, which the name-based policy would otherwise have kept.
        states.Should().OnlyContain(state => !state.Contains("6789", StringComparison.Ordinal));
        states.Should().Contain(state => state.Contains("[redacted]", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------ the rows that already existed

    [Fact]
    public async Task The_migration_encrypts_the_bank_account_numbers_already_in_the_table()
    {
        // The database as it was before issue 104: the account number in a char(10) column.
        var database = $"field_enc_backfill_{Guid.NewGuid():N}";
        var agencyId = await MigrateToEncryptedColumnsAsync(database);

        await using var owner = _postgres.Connect(database, asApplicationRole: false);
        var accountId = await InsertPlaintextBankAccountAsync(owner, agencyId, AccountNumber);

        await RunBackfillAsync(owner);

        // Only now may the plaintext column go, and the migration checks that for itself.
        await owner.Database.MigrateAsync();

        var tenancy = TestTenancy.For(agencyId);
        await using var reader = _postgres.Connect(database, tenancy.Tenant, tenancy.Scope, asApplicationRole: false);
        var account = await reader.AgencyBankAccounts.SingleAsync(a => a.Id == accountId);

        account.AccountNumber.Should().Be(AccountNumber, "the number that was in clear is now encrypted, not lost");

        var stored = await ColumnAsync<byte[]>(database, "payments.agency_bank_accounts", "account_number_encrypted", accountId);
        TestFieldEncryption.Encryptor.KeyIdOf(stored!).Should().Be(TestFieldEncryption.KeyId);

        var columns = await ColumnNamesAsync(database, "payments", "agency_bank_accounts");
        columns.Should().NotContain("account_number");
    }

    [Fact]
    public async Task A_plaintext_column_is_not_dropped_while_anything_in_it_is_still_unencrypted()
    {
        var database = $"field_enc_guard_{Guid.NewGuid():N}";
        var agencyId = await MigrateToEncryptedColumnsAsync(database);

        await using var owner = _postgres.Connect(database, asApplicationRole: false);
        await InsertPlaintextBankAccountAsync(owner, agencyId, AccountNumber);

        // Migrating without the backfill — `dotnet ef database update`, or a deploy that skipped a step.
        var migrate = () => owner.Database.MigrateAsync();

        // EF wraps what the migration raised; the guard is the innermost cause.
        var failure = (await migrate.Should().ThrowAsync<Exception>()).Which;
        var refusal = Innermost(failure).Should().BeOfType<PostgresException>().Which;

        refusal.SqlState.Should().Be(ObjectNotInPrerequisiteState);
        refusal.MessageText.Should().Contain("unencrypted");

        // And it changed nothing: the row is still there, still readable.
        var stillThere = await ColumnAsync<string>(database, "payments.agency_bank_accounts", "account_number", agencyId, "agency_id");
        stillThere.Should().Be(AccountNumber);
    }

    [Fact]
    public async Task A_passport_stored_in_the_format_that_came_before_is_encrypted_again()
    {
        await using var world = await WorldAsync();
        var travellerId = await world.AddTravellerAsync(Passport, new DateOnly(2031, 5, 17));

        // Put the row back the way the old code left it: AesGcmSecretProtector's format, no key id.
        var legacyKey = RandomNumberGenerator.GetBytes(AesGcmSecretProtector.KeyBytes);
        var legacy = new AesGcmSecretProtector(legacyKey);

        await world.Owner.Database.ExecuteSqlRawAsync(
            "UPDATE orders.order_travellers SET passport_number_encrypted = {0} WHERE id = {1}",
            legacy.Protect(Passport, EncryptedColumns.OrderTravellerPassportNumber),
            travellerId);

        await RunBackfillAsync(world.Owner, legacyProtector: legacy);

        var stored = await world.ColumnAsync<byte[]>("orders.order_travellers", "passport_number_encrypted", travellerId);
        TestFieldEncryption.Encryptor.KeyIdOf(stored!).Should().Be(TestFieldEncryption.KeyId);

        await using var reader = _postgres.Connect(world.Database, world.Tenant, world.Scope);
        (await reader.OrderTravellers.SingleAsync(t => t.Id == travellerId)).PassportNumber.Should().Be(Passport);
    }

    [Fact]
    public async Task Rotating_the_key_re_encrypts_what_the_old_one_wrote()
    {
        const string retiredId = "key_retired";
        const string newId = "key_rotated";

        var retiredKey = RandomNumberGenerator.GetBytes(AesGcmFieldEncryptor.KeyBytes);
        var newKey = RandomNumberGenerator.GetBytes(AesGcmFieldEncryptor.KeyBytes);

        var retired = new AesGcmFieldEncryptor(retiredId, new Dictionary<string, byte[]> { [retiredId] = retiredKey });

        // Both keys: the new one encrypts, the retired one is kept only so its values can still be read.
        var rotated = new AesGcmFieldEncryptor(
            newId, new Dictionary<string, byte[]> { [newId] = newKey, [retiredId] = retiredKey });

        var world = await WorldAsync(retired);
        await using (world)
        {
            var accountId = await world.AddBankAccountAsync(AccountNumber);
            var before = await world.ColumnAsync<byte[]>("payments.agency_bank_accounts", "account_number_encrypted", accountId);
            before.Should().NotBeNull();
            retired.KeyIdOf(before!).Should().Be(retiredId);

            await using var owner = _postgres.Connect(world.Database, asApplicationRole: false, fieldEncryptor: rotated);
            await RunBackfillAsync(owner, rotated);

            var after = await world.ColumnAsync<byte[]>("payments.agency_bank_accounts", "account_number_encrypted", accountId);
            rotated.KeyIdOf(after!).Should().Be(newId, "a rotation rewrites every value under the new key");

            await using var reader = _postgres.Connect(world.Database, world.Tenant, world.Scope, fieldEncryptor: rotated);
            (await reader.AgencyBankAccounts.SingleAsync(a => a.Id == accountId)).AccountNumber.Should().Be(AccountNumber);
        }
    }

    // ------------------------------------------------------------------ helpers

    private static Exception Innermost(Exception exception)
    {
        while (exception.InnerException is { } inner)
        {
            exception = inner;
        }

        return exception;
    }

    private async Task<Guid> MigrateToEncryptedColumnsAsync(string database)
    {
        await using var setup = await _postgres.CreateEmptyDatabaseAsync(database);

        // Everything up to and including the migration that adds the ciphertext columns — the state a
        // running system is in when the backfill starts.
        await setup.GetService<IMigrator>().MigrateAsync(DatabaseMigrator.EncryptionColumnsMigration);

        var agency = Agency.RegisterPrincipal("Lagos Travel Limited", "lagos-travel", "NG", "NGN", "Africa/Lagos");
        setup.Agencies.Add(agency);
        await setup.SaveChangesAsync();

        return agency.Id;
    }

    private static async Task<Guid> InsertPlaintextBankAccountAsync(AppDbContext owner, Guid agencyId, string accountNumber)
    {
        var id = Guid.CreateVersion7();

        await owner.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO payments.agency_bank_accounts
                (id, agency_id, bank_code, bank_name, account_number, account_name_provided, currency,
                 status, is_default, created_at, updated_at)
            VALUES ({0}, {1}, '058', 'GTBank', {2}, 'Lagos Travel', 'NGN', 'PendingVerification', false, now(), now())
            """,
            id, agencyId, accountNumber);

        return id;
    }

    private static Task<IReadOnlyList<FieldEncryptionBackfillOutcome>> RunBackfillAsync(AppDbContext owner, IFieldEncryptor? encryptor = null, ISecretProtector? legacyProtector = null) =>
        new FieldEncryptionBackfill(
            owner,
            TestTenancy.None().Scope,
            encryptor ?? TestFieldEncryption.Encryptor,
            () => legacyProtector ?? throw new InvalidOperationException("No legacy key was expected."),
            NullLogger.Instance).RunAsync();

    private async Task<T?> ColumnAsync<T>(string database, string table, string column, Guid id, string keyColumn = "id")
    {
        await using var connection = new NpgsqlConnection(_postgres.ConnectionStringFor(database, asApplicationRole: false));
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();

        // The table and column names are constants in this file, never input.
        command.CommandText = $"SELECT {column} FROM {table} WHERE {keyColumn} = $1";
        command.Parameters.AddWithValue(id);

        var value = await command.ExecuteScalarAsync();

        return value is null or DBNull ? default : (T)value;
    }

    private async Task<World> WorldAsync(IFieldEncryptor? encryptor = null)
    {
        var database = $"field_enc_{Guid.NewGuid():N}";

        Guid agencyId;
        await using (var setup = await _postgres.CreateEmptyDatabaseAsync(database))
        {
            await setup.Database.MigrateAsync();

            var agency = Agency.RegisterPrincipal("Lagos Travel Limited", "lagos-travel", "NG", "NGN", "Africa/Lagos");
            setup.Agencies.Add(agency);
            await setup.SaveChangesAsync();
            agencyId = agency.Id;
        }

        var tenancy = TestTenancy.For(agencyId);

        return new World(
            this,
            database,
            agencyId,
            tenancy.Tenant,
            tenancy.Scope,
            _postgres.Connect(database, tenancy.Tenant, tenancy.Scope, fieldEncryptor: encryptor),
            _postgres.Connect(database, asApplicationRole: false, fieldEncryptor: encryptor),
            _postgres.Connect(
                database,
                tenancy.Tenant,
                tenancy.Scope,
                auditContext: new Auditing.FixedAuditContext(agencyId, Domain.Auditing.AuditActorType.User),
                fieldEncryptor: encryptor));
    }

    private async Task<IReadOnlyList<string>> ColumnNamesAsync(string database, string schema, string table)
    {
        await using var connection = new NpgsqlConnection(_postgres.ConnectionStringFor(database, asApplicationRole: false));
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT column_name FROM information_schema.columns WHERE table_schema = $1 AND table_name = $2";
        command.Parameters.AddWithValue(schema);
        command.Parameters.AddWithValue(table);

        var names = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    private sealed class World(
        FieldEncryptionTests tests,
        string database,
        Guid agencyId,
        TenantContext tenant,
        PlatformScope scope,
        AppDbContext db,
        AppDbContext owner,
        AppDbContext audited) : IAsyncDisposable
    {
        public string Database { get; } = database;

        public Guid AgencyId { get; } = agencyId;

        public TenantContext Tenant { get; } = tenant;

        public PlatformScope Scope { get; } = scope;

        /// <summary>As the tenant, under the policed application role — how production connects.</summary>
        public AppDbContext Db { get; } = db;

        /// <summary>The same database as its owner, for reading what is actually stored.</summary>
        public AppDbContext Owner { get; } = owner;

        public Task<T?> ColumnAsync<T>(string table, string column, Guid id) =>
            tests.ColumnAsync<T>(Database, table, column, id);

        /// <summary>An order with one traveller on it, through the domain.</summary>
        public async Task<Guid> AddTravellerAsync(string passportNumber, DateOnly expiry)
        {
            var now = DateTimeOffset.UtcNow;

            var rule = MarkupRule.Create(AgencyId, new MarkupRuleTerms
            {
                Scope = MarkupScope.Global,
                Currency = "NGN",
                CalculationType = MarkupCalculationType.Percentage,
                PercentBasisPoints = 1_000,
                EffectiveFrom = now.AddDays(-1),
            });
            Db.MarkupRules.Add(rule);
            await Db.SaveChangesAsync();

            var quote = PriceQuote.Record(
                AgencyId,
                new PricingSubject(PricedProductType.Flight, "NGN"),
                new PriceBreakdown(
                    new Money(100_000), new Money(10_000), new Money(750), new Money(500), new Money(110_750),
                    "NGN", new MarkupRuleDefinition(rule.Id, AgencyId, rule.Terms), false, 750, 0),
                now,
                TimeSpan.FromMinutes(30));
            Db.PriceQuotes.Add(quote);
            await Db.SaveChangesAsync();

            var line = OrderLine.FromQuote(quote, "LOS → ABV, Air Peace", """{"adults":1}""", now);
            var order = Order.Place(
                AgencyId, $"ORD-2026-{Random.Shared.Next(100_000, 999_999)}", "NGN",
                BuyerType.AgentAssisted, OrderChannel.Console, null, [line], now);

            Db.Orders.Add(order);
            await Db.SaveChangesAsync();

            var traveller = OrderTraveller.Record(
                AgencyId, line.Id, TravellerType.Adult, "Ngozi", "Adeyemi",
                passportNumber: passportNumber, passportExpiry: expiry);

            Db.OrderTravellers.Add(traveller);
            await Db.SaveChangesAsync();

            return traveller.Id;
        }

        public async Task<Guid> AddBankAccountAsync(string accountNumber) => await AddBankAccountAsync(accountNumber, Db);

        /// <summary>The same, on the context that writes audit rows.</summary>
        public async Task<Guid> AddAuditedBankAccountAsync(string accountNumber)
        {
            var id = await AddBankAccountAsync(accountNumber, audited);

            var account = await audited.AgencyBankAccounts.SingleAsync(a => a.Id == id);
            account.MarkVerified("LAGOS TRAVEL LIMITED", "RCP_test", DateTimeOffset.UtcNow);
            await audited.SaveChangesAsync();

            return id;
        }

        private async Task<Guid> AddBankAccountAsync(string accountNumber, AppDbContext context)
        {
            var account = AgencyBankAccount.Capture(AgencyId, "058", "GTBank", accountNumber, "Lagos Travel", "NGN", null);
            context.AgencyBankAccounts.Add(account);
            await context.SaveChangesAsync();

            return account.Id;
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await Owner.DisposeAsync();
            await audited.DisposeAsync();
        }
    }
}
