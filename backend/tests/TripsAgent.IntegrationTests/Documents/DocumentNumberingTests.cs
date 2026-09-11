using System.Runtime.CompilerServices;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using TripsAgent.Application.Documents;
using TripsAgent.Domain.Documents;
using TripsAgent.Domain.Tenancy;
using TripsAgent.Infrastructure.Documents;
using TripsAgent.Infrastructure.Persistence;
using TripsAgent.IntegrationTests.Identity;
using TripsAgent.IntegrationTests.Persistence;

namespace TripsAgent.IntegrationTests.Documents;

/// <summary>
/// Gapless document numbering against a real PostgreSQL: the row lock, the rollback, and the
/// constraints that make a duplicate or a gap impossible even for SQL that skips the allocator.
/// </summary>
/// <remarks>
/// These have to run against PostgreSQL. The whole guarantee is a row lock held until commit and an
/// increment that rolls back with the transaction — neither exists in an in-memory fake.
/// </remarks>
[Collection(PostgresCollection.Name)]
public class DocumentNumberingTests
{
    /// <summary>Mid-year in Lagos, so no test straddles a year boundary unless it means to.</summary>
    private static readonly DateTimeOffset MidYear = new(2026, 6, 15, 10, 0, 0, TimeSpan.Zero);

    private readonly PostgresFixture _postgres;

    public DocumentNumberingTests(PostgresFixture postgres) => _postgres = postgres;

    // --------------------------------------------------------------------------- concurrency

    [Fact]
    public async Task Fifty_parallel_invoices_get_fifty_unique_consecutive_numbers()
    {
        const int invoices = 50;

        await using var world = await WorldAsync(MidYear);

        // Each issue runs on its own context and its own connection, as fifty separate requests
        // would. Started together, so they all contend for the same counter row at once.
        var runs = Enumerable.Range(0, invoices).Select(async _ =>
        {
            await using var context = world.NewContext(world.AgencyId);

            return await world.Issuer(context, world.AgencyId).IssueAsync(DocumentType.Invoice);
        });

        var issued = await Task.WhenAll(runs);

        // No duplicates and no gaps: exactly 1..50, each exactly once.
        issued.Select(d => d.SequenceNumber).Order()
            .Should().Equal(Enumerable.Range(1, invoices).Select(n => (long)n));

        issued.Select(d => d.DocumentNumber)
            .Should().OnlyHaveUniqueItems()
            .And.BeEquivalentTo(Enumerable.Range(1, invoices).Select(n => $"{world.InvoicePrefix}-2026-{n:D6}"));

        // And what was committed agrees with what the callers were told.
        await using var reader = world.NewContext(world.AgencyId);

        var committed = await reader.GeneratedDocuments.AsNoTracking()
            .OrderBy(d => d.SequenceNumber)
            .Select(d => d.SequenceNumber)
            .ToListAsync();

        committed.Should().Equal(Enumerable.Range(1, invoices).Select(n => (long)n));

        var counter = await reader.DocumentNumberSequences.AsNoTracking().SingleAsync();
        counter.LastValue.Should().Be(invoices, "the counter must end on the last number handed out");
    }

    [Fact]
    public async Task A_second_issuer_waits_on_the_row_lock_and_reuses_a_rolled_back_number()
    {
        await using var world = await WorldAsync(MidYear);

        await using var first = world.NewContext(world.AgencyId);
        var firstTransaction = await first.Database.BeginTransactionAsync();

        var taken = await world.Allocator(first, world.AgencyId).NextAsync(DocumentType.Invoice, MidYear);
        taken.SequenceNumber.Should().Be(1);

        // The first transaction is still open, holding the counter row. The second must wait.
        await using var second = world.NewContext(world.AgencyId);
        var pending = world.Issuer(second, world.AgencyId).IssueAsync(DocumentType.Invoice);

        await WaitUntilABackendIsBlockedOnALockAsync(world.Database);
        pending.IsCompleted.Should().BeFalse("the counter row is locked until the first transaction ends");

        // The first request fails before its document is saved. Its increment must go with it.
        await firstTransaction.RollbackAsync();
        await firstTransaction.DisposeAsync();

        var document = await pending;

        document.SequenceNumber.Should().Be(1, "number 1 was never used, so handing out 2 would leave a gap");
        document.DocumentNumber.Should().Be($"{world.InvoicePrefix}-2026-000001");
    }

    // ------------------------------------------------------------------------------ rollback

    [Fact]
    public async Task A_failure_after_taking_a_number_leaves_no_gap()
    {
        await using var world = await WorldAsync(MidYear);

        await using (var failing = world.NewContext(world.AgencyId))
        {
            var runner = new EfTransactionRunner(failing);
            var allocator = world.Allocator(failing, world.AgencyId);

            var act = () => runner.RunAsync<int>(async token =>
            {
                await allocator.NextAsync(DocumentType.Invoice, MidYear, token);
                throw new InvalidOperationException("the render or the save failed");
            });

            await act.Should().ThrowAsync<InvalidOperationException>();
        }

        await using var context = world.NewContext(world.AgencyId);

        var next = await world.Issuer(context, world.AgencyId).IssueAsync(DocumentType.Invoice);

        next.SequenceNumber.Should().Be(1);
    }

    [Fact]
    public async Task Issuing_inside_a_callers_transaction_rolls_back_with_it()
    {
        await using var world = await WorldAsync(MidYear);

        await using (var context = world.NewContext(world.AgencyId))
        {
            await using var transaction = await context.Database.BeginTransactionAsync();

            var document = await world.Issuer(context, world.AgencyId).IssueAsync(DocumentType.Invoice);
            document.SequenceNumber.Should().Be(1);

            // The caller's checkout fails after the invoice was issued inside its transaction.
            await transaction.RollbackAsync();
        }

        await using var reader = world.NewContext(world.AgencyId);

        (await reader.GeneratedDocuments.CountAsync()).Should().Be(0, "the document went with the transaction");
        (await reader.DocumentNumberSequences.CountAsync()).Should().Be(0, "and so did the number it took");

        var retried = await world.Issuer(reader, world.AgencyId).IssueAsync(DocumentType.Invoice);
        retried.SequenceNumber.Should().Be(1);
    }

    [Fact]
    public async Task Allocating_outside_a_transaction_is_refused_and_takes_nothing()
    {
        await using var world = await WorldAsync(MidYear);
        await using var context = world.NewContext(world.AgencyId);

        // Outside a transaction the increment would commit on its own and survive a failed save.
        var act = () => world.Allocator(context, world.AgencyId).NextAsync(DocumentType.Invoice, MidYear);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*inside the transaction*");
        (await context.DocumentNumberSequences.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Issuing_with_no_agency_resolved_is_refused()
    {
        await using var world = await WorldAsync(MidYear);

        var none = TestTenancy.None();
        await using var context = _postgres.Connect(world.Database, none.Tenant, none.Scope, world.Clock);

        var issuer = new DocumentIssuer(
            context,
            new DocumentNumberAllocator(context, none.Tenant, world.Clock),
            new EfTransactionRunner(context),
            none.Tenant,
            world.Clock);

        var act = () => issuer.IssueAsync(DocumentType.Invoice);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*no agency*");
    }

    // ------------------------------------------------------------------------- independence

    [Fact]
    public async Task Each_agency_and_each_document_type_counts_on_its_own()
    {
        await using var world = await WorldAsync(MidYear);

        await using var lagos = world.NewContext(world.AgencyId);
        var lagosIssuer = world.Issuer(lagos, world.AgencyId);

        (await lagosIssuer.IssueAsync(DocumentType.Invoice)).SequenceNumber.Should().Be(1);
        (await lagosIssuer.IssueAsync(DocumentType.Invoice)).SequenceNumber.Should().Be(2);

        // A voucher never moves the invoice counter.
        (await lagosIssuer.IssueAsync(DocumentType.Voucher)).SequenceNumber.Should().Be(1);

        // Another agency's first invoice is its own number 1, under its own prefix.
        await using var abuja = world.NewContext(world.OtherAgencyId);
        var abujaInvoice = await world.Issuer(abuja, world.OtherAgencyId).IssueAsync(DocumentType.Invoice);

        abujaInvoice.SequenceNumber.Should().Be(1);
        abujaInvoice.DocumentNumber.Should().Be($"{world.OtherInvoicePrefix}-2026-000001");
        abujaInvoice.AgencyId.Should().Be(world.OtherAgencyId);
    }

    // ------------------------------------------------------------------------------- format

    [Fact]
    public async Task An_agency_configures_its_prefix_padding_and_year()
    {
        await using var world = await WorldAsync(MidYear);
        await using var context = world.NewContext(world.AgencyId);

        var outcome = await world.Configure(context, world.AgencyId).HandleAsync(new ConfigureDocumentNumberingCommand(
            DocumentType.Invoice, Prefix: "acme/inv", Padding: 4, IncludeYear: false, ResetsYearly: false));

        outcome.Should().BeOfType<ConfigureDocumentNumberingOutcome.Configured>();

        var issuer = world.Issuer(context, world.AgencyId);

        (await issuer.IssueAsync(DocumentType.Invoice)).DocumentNumber.Should().Be("ACME/INV-0001");
        (await issuer.IssueAsync(DocumentType.Invoice)).DocumentNumber.Should().Be("ACME/INV-0002");

        // The other agency never chose, so it keeps the default.
        await using var other = world.NewContext(world.OtherAgencyId);
        (await world.Issuer(other, world.OtherAgencyId).IssueAsync(DocumentType.Invoice)).DocumentNumber
            .Should().Be($"{world.OtherInvoicePrefix}-2026-000001");
    }

    [Fact]
    public async Task Changing_the_format_carries_the_counter_on()
    {
        await using var world = await WorldAsync(MidYear);
        await using var context = world.NewContext(world.AgencyId);
        var issuer = world.Issuer(context, world.AgencyId);

        await issuer.IssueAsync(DocumentType.Invoice);
        await issuer.IssueAsync(DocumentType.Invoice);

        await world.Configure(context, world.AgencyId).HandleAsync(new ConfigureDocumentNumberingCommand(
            DocumentType.Invoice, "LTL", Padding: 3, IncludeYear: true, ResetsYearly: true));

        // Restarting at 1 under a new prefix would give the tax year two invoice number ones.
        (await issuer.IssueAsync(DocumentType.Invoice)).DocumentNumber.Should().Be("LTL-2026-003");
    }

    [Fact]
    public async Task An_invalid_format_is_refused_and_nothing_is_saved()
    {
        await using var world = await WorldAsync(MidYear);
        await using var context = world.NewContext(world.AgencyId);

        var outcome = await world.Configure(context, world.AgencyId).HandleAsync(new ConfigureDocumentNumberingCommand(
            DocumentType.Invoice, "INV", Padding: 6, IncludeYear: false, ResetsYearly: true));

        outcome.Should().BeOfType<ConfigureDocumentNumberingOutcome.Invalid>()
            .Which.Reason.Should().Contain("year");

        (await context.DocumentNumberFormats.CountAsync()).Should().Be(0);
    }

    // ------------------------------------------------------------------------ yearly reset

    [Fact]
    public async Task Turning_yearly_reset_off_after_issuing_is_refused_and_issuing_carries_on()
    {
        await using var world = await WorldAsync(MidYear);
        await using var context = world.NewContext(world.AgencyId);
        var issuer = world.Issuer(context, world.AgencyId);

        await world.Configure(context, world.AgencyId).HandleAsync(new ConfigureDocumentNumberingCommand(
            DocumentType.Invoice, "LTL", Padding: 6, IncludeYear: true, ResetsYearly: true));

        (await issuer.IssueAsync(DocumentType.Invoice)).DocumentNumber.Should().Be("LTL-2026-000001");

        // Switching to the continuous counter would start it at 1 and print LTL-2026-000001 again.
        // The duplicate would be refused on every attempt, and no invoice could be issued at all.
        var outcome = await world.Configure(context, world.AgencyId).HandleAsync(new ConfigureDocumentNumberingCommand(
            DocumentType.Invoice, "LTL", Padding: 6, IncludeYear: true, ResetsYearly: false));

        outcome.Should().BeOfType<ConfigureDocumentNumberingOutcome.Invalid>()
            .Which.Reason.Should().Contain("restarts every year");

        var next = await issuer.IssueAsync(DocumentType.Invoice);

        next.DocumentNumber.Should().Be("LTL-2026-000002", "the number must be one not issued before");
        next.SequenceYear.Should().Be(2026, "the refused change must not have switched counters");

        await using var reader = world.NewContext(world.AgencyId);
        (await reader.DocumentNumberFormats.AsNoTracking().SingleAsync()).ResetsYearly.Should().BeTrue();
    }

    [Fact]
    public async Task Turning_yearly_reset_on_after_issuing_is_refused()
    {
        await using var world = await WorldAsync(MidYear);
        await using var context = world.NewContext(world.AgencyId);
        var issuer = world.Issuer(context, world.AgencyId);

        await world.Configure(context, world.AgencyId).HandleAsync(new ConfigureDocumentNumberingCommand(
            DocumentType.Invoice, "LTL", Padding: 6, IncludeYear: true, ResetsYearly: false));

        await issuer.IssueAsync(DocumentType.Invoice);
        await issuer.IssueAsync(DocumentType.Invoice);

        // This year's fresh counter would reach 1, then 2 — both already printed by the continuous one.
        var outcome = await world.Configure(context, world.AgencyId).HandleAsync(new ConfigureDocumentNumberingCommand(
            DocumentType.Invoice, "LTL", Padding: 6, IncludeYear: true, ResetsYearly: true));

        outcome.Should().BeOfType<ConfigureDocumentNumberingOutcome.Invalid>();

        (await issuer.IssueAsync(DocumentType.Invoice)).DocumentNumber.Should().Be("LTL-2026-000003");
    }

    [Fact]
    public async Task Turning_off_the_default_yearly_reset_after_issuing_is_refused_and_nothing_is_saved()
    {
        await using var world = await WorldAsync(MidYear);
        await using var context = world.NewContext(world.AgencyId);

        // No format row: the agency is on the default, which resets yearly.
        await world.Issuer(context, world.AgencyId).IssueAsync(DocumentType.Invoice);

        var outcome = await world.Configure(context, world.AgencyId).HandleAsync(new ConfigureDocumentNumberingCommand(
            DocumentType.Invoice, world.InvoicePrefix, Padding: 6, IncludeYear: true, ResetsYearly: false));

        outcome.Should().BeOfType<ConfigureDocumentNumberingOutcome.Invalid>();

        await using var reader = world.NewContext(world.AgencyId);
        (await reader.DocumentNumberFormats.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task The_rest_of_the_format_can_still_change_after_issuing()
    {
        await using var world = await WorldAsync(MidYear);
        await using var context = world.NewContext(world.AgencyId);
        var issuer = world.Issuer(context, world.AgencyId);

        await world.Configure(context, world.AgencyId).HandleAsync(new ConfigureDocumentNumberingCommand(
            DocumentType.Invoice, "LTL", Padding: 6, IncludeYear: true, ResetsYearly: false));
        await issuer.IssueAsync(DocumentType.Invoice);

        // Same counter, so a new look cannot reprint an old number.
        var outcome = await world.Configure(context, world.AgencyId).HandleAsync(new ConfigureDocumentNumberingCommand(
            DocumentType.Invoice, "LTL", Padding: 4, IncludeYear: false, ResetsYearly: false));

        outcome.Should().BeOfType<ConfigureDocumentNumberingOutcome.Configured>();
        (await issuer.IssueAsync(DocumentType.Invoice)).DocumentNumber.Should().Be("LTL-0002");
    }

    [Fact]
    public async Task Another_document_types_numbers_do_not_lock_yearly_reset()
    {
        await using var world = await WorldAsync(MidYear);
        await using var context = world.NewContext(world.AgencyId);

        await world.Issuer(context, world.AgencyId).IssueAsync(DocumentType.Voucher);

        var outcome = await world.Configure(context, world.AgencyId).HandleAsync(new ConfigureDocumentNumberingCommand(
            DocumentType.Invoice, "LTL", Padding: 6, IncludeYear: true, ResetsYearly: false));

        outcome.Should().BeOfType<ConfigureDocumentNumberingOutcome.Configured>();
    }

    [Fact]
    public async Task A_reset_change_waits_for_a_first_document_still_being_issued_and_then_refuses()
    {
        await using var world = await WorldAsync(MidYear);

        // The agency's first invoice is mid-issue: numbered and inserted, not yet committed.
        await using var issuing = world.NewContext(world.AgencyId);
        var transaction = await issuing.Database.BeginTransactionAsync();

        var number = await world.Allocator(issuing, world.AgencyId).NextAsync(DocumentType.Invoice, MidYear);
        issuing.GeneratedDocuments.Add(GeneratedDocument.Issue(world.AgencyId, number, MidYear));
        await issuing.SaveChangesAsync();

        // Without the lock, the change would see no invoices, be accepted, and then collide.
        await using var configuring = world.NewContext(world.AgencyId);
        var pending = world.Configure(configuring, world.AgencyId).HandleAsync(new ConfigureDocumentNumberingCommand(
            DocumentType.Invoice, world.InvoicePrefix, Padding: 6, IncludeYear: true, ResetsYearly: false));

        await WaitUntilABackendIsBlockedOnALockAsync(world.Database);
        pending.IsCompleted.Should().BeFalse("the change must wait for the invoice being issued");

        await transaction.CommitAsync();
        await transaction.DisposeAsync();

        (await pending).Should().BeOfType<ConfigureDocumentNumberingOutcome.Invalid>();
    }

    // -------------------------------------------------------------------------------- years

    [Fact]
    public async Task A_yearly_sequence_restarts_at_one_in_the_agencys_new_year()
    {
        // 22:00 UTC on 31 December is 23:00 in Lagos — still 2026 on the agency's wall clock.
        await using var world = await WorldAsync(new DateTimeOffset(2026, 12, 31, 22, 0, 0, TimeSpan.Zero));
        await using var context = world.NewContext(world.AgencyId);
        var issuer = world.Issuer(context, world.AgencyId);

        (await issuer.IssueAsync(DocumentType.Invoice)).DocumentNumber.Should().Be($"{world.InvoicePrefix}-2026-000001");
        (await issuer.IssueAsync(DocumentType.Invoice)).DocumentNumber.Should().Be($"{world.InvoicePrefix}-2026-000002");

        // 23:30 UTC is 00:30 on 1 January 2027 in Lagos. New tax year, new count.
        world.Clock.Advance(TimeSpan.FromMinutes(90));

        var firstOf2027 = await issuer.IssueAsync(DocumentType.Invoice);

        firstOf2027.DocumentNumber.Should().Be($"{world.InvoicePrefix}-2027-000001");
        firstOf2027.SequenceYear.Should().Be(2027);
    }

    [Fact]
    public async Task A_sequence_configured_not_to_reset_carries_on_across_the_year()
    {
        await using var world = await WorldAsync(new DateTimeOffset(2026, 12, 31, 22, 0, 0, TimeSpan.Zero));
        await using var context = world.NewContext(world.AgencyId);

        await world.Configure(context, world.AgencyId).HandleAsync(new ConfigureDocumentNumberingCommand(
            DocumentType.Invoice, "LTL", Padding: 6, IncludeYear: true, ResetsYearly: false));

        var issuer = world.Issuer(context, world.AgencyId);

        (await issuer.IssueAsync(DocumentType.Invoice)).DocumentNumber.Should().Be("LTL-2026-000001");

        world.Clock.Advance(TimeSpan.FromMinutes(90));

        // The year printed follows the calendar; the counter does not restart.
        var next = await issuer.IssueAsync(DocumentType.Invoice);

        next.DocumentNumber.Should().Be("LTL-2027-000002");
        next.SequenceYear.Should().Be(DocumentNumberFormat.ContinuousSequenceYear);
    }

    // -------------------------------------------------------------------------- constraints

    [Fact]
    public async Task The_database_refuses_a_duplicate_number_within_an_agency_and_type()
    {
        await using var world = await WorldAsync(MidYear);

        var act = async () =>
        {
            await InsertDocumentAsync(world, world.AgencyId, "Invoice", "INV-DUP-1", sequenceNumber: 1);
            await InsertDocumentAsync(world, world.AgencyId, "Invoice", "INV-DUP-1", sequenceNumber: 2);
        };

        (await act.Should().ThrowAsync<PostgresException>())
            .Which.ConstraintName.Should().Be("ix_generated_documents_agency_id_document_type_document_number");
    }

    [Fact]
    public async Task The_same_number_is_allowed_for_another_agency_or_another_type()
    {
        await using var world = await WorldAsync(MidYear);

        await InsertDocumentAsync(world, world.AgencyId, "Invoice", "DOC-1", sequenceNumber: 1);

        var act = async () =>
        {
            await InsertDocumentAsync(world, world.OtherAgencyId, "Invoice", "DOC-1", sequenceNumber: 1);
            await InsertDocumentAsync(world, world.AgencyId, "Voucher", "DOC-1", sequenceNumber: 1);
        };

        await act.Should().NotThrowAsync("uniqueness is per agency and per document type");
    }

    [Fact]
    public async Task The_database_refuses_changing_or_deleting_an_issued_number()
    {
        await using var world = await WorldAsync(MidYear);
        await using var context = world.NewContext(world.AgencyId);

        var document = await world.Issuer(context, world.AgencyId).IssueAsync(DocumentType.Invoice);

        var renumber = async () => await world.Db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE documents.generated_documents SET document_number = 'INV-EDITED' WHERE id = {document.Id}");

        (await renumber.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be(PostgresErrorCodes.RestrictViolation);

        var delete = async () => await world.Db.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM documents.generated_documents WHERE id = {document.Id}");

        (await delete.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be(PostgresErrorCodes.RestrictViolation);
    }

    [Theory]
    [InlineData("UPDATE documents.document_number_sequences SET last_value = last_value + 2")]
    [InlineData("UPDATE documents.document_number_sequences SET last_value = last_value - 1")]
    [InlineData("UPDATE documents.document_number_sequences SET year = 2031")]
    [InlineData("DELETE FROM documents.document_number_sequences")]
    public async Task The_database_refuses_moving_a_counter_other_than_forward_by_one(string sql)
    {
        await using var world = await WorldAsync(MidYear);
        await using var context = world.NewContext(world.AgencyId);

        var issuer = world.Issuer(context, world.AgencyId);
        await issuer.IssueAsync(DocumentType.Invoice);
        await issuer.IssueAsync(DocumentType.Invoice);

        var act = async () => await world.Db.Database.ExecuteSqlRawAsync(sql);

        (await act.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be(PostgresErrorCodes.RestrictViolation);
    }

    // ------------------------------------------------------------------------------ helpers

    private static async Task InsertDocumentAsync(
        World world,
        Guid agencyId,
        string documentType,
        string documentNumber,
        long sequenceNumber) =>
        await world.Db.Database.ExecuteSqlInterpolatedAsync(
            $"""
             INSERT INTO documents.generated_documents
                 (id, agency_id, document_type, document_number, sequence_year, sequence_number,
                  issued_at, created_at, updated_at)
             VALUES ({Guid.CreateVersion7()}, {agencyId}, {documentType}, {documentNumber}, 2026,
                     {sequenceNumber}, now(), now(), now())
             """);

    /// <summary>
    /// Waits until some connection to <paramref name="database"/> is blocked on a lock — proof the
    /// second issuer really is queued behind the first, rather than a guess made by sleeping.
    /// </summary>
    private async Task WaitUntilABackendIsBlockedOnALockAsync(string database)
    {
        await using var admin = new NpgsqlConnection(_postgres.ConnectionString);
        await admin.OpenAsync();

        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);

        while (DateTimeOffset.UtcNow < deadline)
        {
            await using var command = admin.CreateCommand();
            command.CommandText =
                "SELECT count(*) FROM pg_stat_activity WHERE datname = @db AND wait_event_type = 'Lock'";
            command.Parameters.AddWithValue("db", database);

            if ((long)(await command.ExecuteScalarAsync())! > 0)
            {
                return;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException("The second issuer never blocked on the counter row's lock.");
    }

    private async Task<World> WorldAsync(DateTimeOffset now, [CallerMemberName] string testName = "")
    {
        var name = ("docnum_" + testName).ToLowerInvariant();
        name = name[..Math.Min(name.Length, 60)];

        var clock = new ManualClock(now);
        var tenancy = TestTenancy.None();

        var db = await _postgres.CreateEmptyDatabaseAsync(name, tenancy.Tenant, tenancy.Scope, clock);
        await db.Database.MigrateAsync();

        using var _ = tenancy.Scope.Enter("test setup — registering two agencies");

        var lagos = Agency.RegisterPrincipal("Lagos Travel Limited", "lagos-travel", "NG", "NGN", "Africa/Lagos");
        var abuja = Agency.RegisterPrincipal("Abuja Tours Limited", "abuja-tours", "NG", "NGN", "Africa/Lagos");
        var lagosSettings = AgencySettings.CreateDefault(lagos);
        var abujaSettings = AgencySettings.CreateDefault(abuja);

        db.Agencies.AddRange(lagos, abuja);
        db.AgencySettings.AddRange(lagosSettings, abujaSettings);
        await db.SaveChangesAsync();

        return new World(
            db,
            clock,
            name,
            _postgres,
            lagos.Id,
            lagosSettings.InvoicePrefix,
            abuja.Id,
            abujaSettings.InvoicePrefix);
    }

    private sealed class World(
        AppDbContext db,
        ManualClock clock,
        string database,
        PostgresFixture postgres,
        Guid agencyId,
        string invoicePrefix,
        Guid otherAgencyId,
        string otherInvoicePrefix) : IAsyncDisposable
    {
        /// <summary>A context with no tenant, for raw SQL that must reach past the allocator.</summary>
        public AppDbContext Db { get; } = db;

        public ManualClock Clock { get; } = clock;

        public string Database { get; } = database;

        public Guid AgencyId { get; } = agencyId;

        public string InvoicePrefix { get; } = invoicePrefix;

        public Guid OtherAgencyId { get; } = otherAgencyId;

        public string OtherInvoicePrefix { get; } = otherInvoicePrefix;

        /// <summary>A fresh context acting as <paramref name="agency"/>, as a separate request would.</summary>
        public AppDbContext NewContext(Guid agency)
        {
            var tenancy = TestTenancy.For(agency);
            return postgres.Connect(Database, tenancy.Tenant, tenancy.Scope, Clock);
        }

        public DocumentNumberAllocator Allocator(AppDbContext context, Guid agency) =>
            new(context, TestTenancy.For(agency).Tenant, Clock);

        public ConfigureDocumentNumberingHandler Configure(AppDbContext context, Guid agency) =>
            new(context, TestTenancy.For(agency).Tenant, new EfTransactionRunner(context), Allocator(context, agency));

        public DocumentIssuer Issuer(AppDbContext context, Guid agency)
        {
            var tenant = TestTenancy.For(agency).Tenant;

            return new DocumentIssuer(
                context,
                new DocumentNumberAllocator(context, tenant, Clock),
                new EfTransactionRunner(context),
                tenant,
                Clock);
        }

        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }
}
