using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using QuestPDF.Infrastructure;
using TripsAgent.Application.Documents;
using TripsAgent.Application.Notifications;
using TripsAgent.Documents;
using TripsAgent.Domain.Assets;
using TripsAgent.Domain.Common;
using TripsAgent.Domain.Documents;
using TripsAgent.Domain.Identity;
using TripsAgent.Domain.Notifications;
using TripsAgent.Domain.Orders;
using TripsAgent.Domain.Pricing;
using TripsAgent.Domain.Suppliers;
using TripsAgent.Domain.Tenancy;
using TripsAgent.Infrastructure.Assets;
using TripsAgent.Infrastructure.Documents;
using TripsAgent.Infrastructure.Identity;
using TripsAgent.Infrastructure.Messaging;
using TripsAgent.Infrastructure.Notifications;
using TripsAgent.Infrastructure.Persistence;
using TripsAgent.Infrastructure.Storage;
using TripsAgent.IntegrationTests.Persistence;
using UglyToad.PdfPig;

namespace TripsAgent.IntegrationTests.Documents;

/// <summary>
/// Issue #46 end to end against real PostgreSQL, as the policed application role: an order's
/// invoice and voucher are numbered, rendered, stored as assets and emailed — once, however many
/// times they are asked for — and a reprint is the same bytes while a reissue never touches them.
/// </summary>
/// <remarks>
/// <see cref="OrderDocumentService"/> is called directly, as <c>DocumentRenderConsumer</c> would call
/// it for each message, acting as the order's agency. The broker's part — redelivery, the error
/// queue — is MassTransit's own; what is tested here is that the rows and the files tell the story.
/// </remarks>
[Collection(PostgresCollection.Name)]
public sealed class OrderDocumentsTests
{
    private const string AgencyColour = "#0A7E3B";

    private static readonly QuestPdfDocumentRenderer Renderer = new(LicenseType.Community);

    private readonly PostgresFixture _postgres;

    public OrderDocumentsTests(PostgresFixture postgres) => _postgres = postgres;

    // ------------------------------------------------------------------ issuing

    [Fact]
    public async Task Confirming_an_order_issues_an_invoice_and_a_voucher_renders_them_stores_them_and_emails_both()
    {
        var world = await WorldAsync();

        (await world.IssueAsync()).Should().Be(DocumentRunOutcome.Completed);

        await using var read = world.AsAgency(world.AgencyA);
        var documents = await read.GeneratedDocuments.AsNoTracking().Where(d => d.OrderId == world.OrderId).ToListAsync();

        var invoice = documents.Should().ContainSingle(d => d.DocumentType == DocumentType.Invoice).Subject;
        var voucher = documents.Should().ContainSingle(d => d.DocumentType == DocumentType.Voucher).Subject;

        invoice.Status.Should().Be(DocumentStatus.Ready);
        invoice.IssueNumber.Should().Be(1);
        invoice.OrderLineId.Should().BeNull();
        invoice.TemplateKey.Should().Be(DocumentTemplates.Invoice.Key);
        voucher.OrderLineId.Should().Be(world.LineId);
        voucher.TemplateKey.Should().Be(DocumentTemplates.FlightVoucher.Key);

        // Stored as assets the platform made itself, servable at once.
        var assets = await read.Assets.AsNoTracking()
            .Where(a => a.Id == invoice.AssetId || a.Id == voucher.AssetId)
            .ToListAsync();

        assets.Should().HaveCount(2).And.OnlyContain(a =>
            a.Purpose == AssetPurpose.GeneratedDocument
            && a.ScanStatus == AssetScanStatus.NotRequired
            && a.ContentType == MediaTypes.Pdf);
        assets.Should().OnlyContain(a => a.IsServable);

        // The checksum on the row is the checksum of what is in storage.
        var stored = await world.ReadStoredAsync(assets.Single(a => a.Id == invoice.AssetId).StorageKey);
        Sha256(stored).Should().Be(invoice.Checksum);

        // In the agency's name and the customer's currency.
        var text = Squash(TextOf(stored));
        text.Should().Contain("LagosTravel").And.Contain("AdaObi").And.Contain("NGN1,107.50").And.Contain(invoice.DocumentNumber);

        // One email to the customer, carrying both.
        var email = await read.Notifications.AsNoTracking().SingleAsync();
        email.TemplateKey.Should().Be(NotificationTemplateCatalog.DocumentsIssued);
        email.RecipientAddress.Should().Be("ada.obi@example.test");
        email.AttachmentAssetIds.Should().BeEquivalentTo(new[] { invoice.AssetId!.Value, voucher.AssetId!.Value });
        invoice.EmailNotificationId.Should().Be(email.Id);
        voucher.EmailNotificationId.Should().Be(email.Id);
    }

    [Fact]
    public async Task The_email_arrives_with_both_pdfs_under_the_agencys_name()
    {
        var world = await WorldAsync();
        await world.IssueAsync();

        var sent = await world.DispatchQueuedEmailsAsync();

        var email = sent.Should().ContainSingle().Subject;
        email.FromName.Should().Be("Lagos Travel");
        email.Attachments.Should().HaveCount(2).And.OnlyContain(a => a.ContentType == MediaTypes.Pdf && a.ContentId == null);

        await using var read = world.AsAgency(world.AgencyA);
        var checksums = await read.GeneratedDocuments.AsNoTracking().Select(d => d.Checksum).ToListAsync();

        email.Attachments!.Select(a => Sha256(a.Content)).Should().BeEquivalentTo(checksums, "the customer is sent the issued bytes");

        foreach (var part in new[] { email.Subject, email.HtmlBody, email.TextBody })
        {
            part.Should().NotContainEquivalentOf("Trips Agent").And.NotContainEquivalentOf("tripsagent");
        }
    }

    [Fact]
    public async Task Asking_three_times_issues_each_document_once_and_emails_once()
    {
        var world = await WorldAsync();

        for (var run = 0; run < 3; run++)
        {
            (await world.IssueAsync()).Should().Be(DocumentRunOutcome.Completed);
        }

        await world.ExpectOneSetAsync();
    }

    [Fact]
    public async Task Three_deliveries_at_the_same_moment_issue_each_document_once_and_email_once()
    {
        var world = await WorldAsync();

        // At-least-once delivery means a message can arrive three times at once. A delivery that
        // loses a race throws, and the broker redelivers it — which the last call here stands for.
        await Task.WhenAll(world.TryIssueAsync(), world.TryIssueAsync(), world.TryIssueAsync());
        (await world.IssueAsync()).Should().Be(DocumentRunOutcome.Completed);

        await world.ExpectOneSetAsync();
    }

    [Fact]
    public async Task Nothing_confirmed_means_nothing_issued()
    {
        var world = await WorldAsync(confirmed: false);

        (await world.IssueAsync()).Should().Be(DocumentRunOutcome.NothingToDo);

        await using var read = world.AsAgency(world.AgencyA);
        (await read.GeneratedDocuments.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task The_voucher_prints_the_airlines_reference_and_every_ticket_number()
    {
        var world = await WorldAsync();
        var supplier = new FixedSupplierBookings(new SupplierBookingSnapshot(
            world.LineId,
            "QX7K2P",
            [new SupplierPassengerSnapshot("Mrs", "Ada", null, "Obi", PassengerType.Adult, "0741234567890", null)]));

        await world.IssueAsync(supplier);

        var voucher = await world.OpenAsync(DocumentType.Voucher);
        Squash(TextOf(voucher)).Should().Contain("QX7K2P").And.Contain("0741234567890");
    }

    [Fact]
    public async Task Issuing_as_one_agency_for_another_agencys_order_is_refused()
    {
        var world = await WorldAsync();
        var (db, service) = world.Worker(world.AgencyB);

        await using (db)
        {
            var issue = () => service.IssueForOrderAsync(world.Request(agencyId: world.AgencyA));

            await issue.Should().ThrowAsync<InvalidOperationException>();
        }
    }

    // ------------------------------------------------------------------ reprint

    [Fact]
    public async Task A_reprint_is_byte_identical_to_the_original()
    {
        var world = await WorldAsync();
        await world.IssueAsync();

        await using var read = world.AsAgency(world.AgencyA);
        var invoice = await read.GeneratedDocuments.AsNoTracking().SingleAsync(d => d.DocumentType == DocumentType.Invoice);
        var asset = await read.Assets.AsNoTracking().SingleAsync(a => a.Id == invoice.AssetId);

        var first = await world.OpenAsync(DocumentType.Invoice);
        var second = await world.OpenAsync(DocumentType.Invoice);

        second.Should().Equal(first);
        Sha256(first).Should().Be(invoice.Checksum);
        first.Should().Equal(await world.ReadStoredAsync(asset.StorageKey));
    }

    [Fact]
    public async Task A_file_changed_in_storage_is_refused_rather_than_served()
    {
        var world = await WorldAsync();
        await world.IssueAsync();

        await using var read = world.AsAgency(world.AgencyA);
        var invoice = await read.GeneratedDocuments.AsNoTracking().SingleAsync(d => d.DocumentType == DocumentType.Invoice);
        var asset = await read.Assets.AsNoTracking().SingleAsync(a => a.Id == invoice.AssetId);

        using (var forged = new MemoryStream("%PDF-1.7 not what was issued"u8.ToArray()))
        {
            await world.Storage.StoreAsync(forged, asset.StorageKey, MediaTypes.Pdf);
        }

        var outcome = await world.OpenOutcomeAsync(invoice.Id, forCustomer: false);

        outcome.Should().BeOfType<DocumentFileOutcome.Corrupted>();
    }

    // ------------------------------------------------------------------ reissue

    [Fact]
    public async Task A_reissue_is_a_new_document_with_issue_two_and_the_original_is_never_touched()
    {
        var world = await WorldAsync();
        await world.IssueAsync();

        var original = await world.FindAsync(DocumentType.Invoice);
        var before = await world.RowAsync(original.Id);

        var outcome = await world.ReissueAsync(original.Id);

        var reissued = outcome.Should().BeOfType<ReissueDocumentOutcome.Reissued>().Subject.Document;
        reissued.Document.IssueNumber.Should().Be(2);
        reissued.Document.SupersedesDocumentId.Should().Be(original.Id);
        reissued.Document.DocumentNumber.Should().NotBe(original.DocumentNumber);
        reissued.Document.Status.Should().Be(DocumentStatus.Pending);
        reissued.SupersedesDocumentNumber.Should().Be(original.DocumentNumber);

        (await world.RowAsync(original.Id)).Should().Be(before, "the original is never mutated");

        // The Worker renders it and sends it on, saying what it replaces.
        (await world.RenderAsync(reissued.Document.Id)).Should().Be(DocumentRunOutcome.Completed);

        var text = Squash(TextOf(await world.OpenAsync(reissued.Document.Id)));
        text.Should().Contain("Issue2").And.Contain($"replaces{original.DocumentNumber}");

        await using var read = world.AsAgency(world.AgencyA);
        var email = await read.Notifications.AsNoTracking()
            .SingleAsync(n => n.TemplateKey == NotificationTemplateCatalog.DocumentsReissued);
        email.AttachmentAssetIds.Should().ContainSingle();

        (await world.RowAsync(original.Id)).Should().Be(before, "not even rendering the replacement touches it");
    }

    [Fact]
    public async Task A_document_can_be_replaced_only_once_even_by_two_people_at_the_same_moment()
    {
        var world = await WorldAsync();
        await world.IssueAsync();
        var original = await world.FindAsync(DocumentType.Invoice);

        var outcomes = await Task.WhenAll(world.ReissueAsync(original.Id), world.ReissueAsync(original.Id));

        outcomes.Should().ContainSingle(o => o is ReissueDocumentOutcome.Reissued);
        outcomes.Should().ContainSingle(o => o is ReissueDocumentOutcome.AlreadySuperseded);

        // The loser's number went back with its row: the invoice counter shows one original, one reissue.
        await using var owner = world.Owner();
        var lastInvoiceNumber = await owner.DocumentNumberSequences.AsNoTracking().IgnoreQueryFilters()
            .Where(s => s.AgencyId == world.AgencyA && s.DocumentType == DocumentType.Invoice)
            .Select(s => s.LastValue)
            .SingleAsync();

        lastInvoiceNumber.Should().Be(2);
    }

    [Fact]
    public async Task A_customer_asking_for_a_replaced_document_is_told_so_while_the_agent_can_still_reprint_it()
    {
        var world = await WorldAsync();
        await world.IssueAsync();
        var original = await world.FindAsync(DocumentType.Voucher);

        var reissued = (ReissueDocumentOutcome.Reissued)await world.ReissueAsync(original.Id);

        (await world.OpenOutcomeAsync(original.Id, forCustomer: true))
            .Should().BeOfType<DocumentFileOutcome.Superseded>()
            .Which.ByDocumentNumber.Should().Be(reissued.Document.Document.DocumentNumber);

        (await world.OpenOutcomeAsync(original.Id, forCustomer: false)).Should().BeOfType<DocumentFileOutcome.File>();
    }

    [Fact]
    public async Task The_database_refuses_to_change_an_issued_documents_identity_or_its_file()
    {
        var world = await WorldAsync();
        await world.IssueAsync();
        var invoice = await world.FindAsync(DocumentType.Invoice);

        await using var owner = world.Owner();

        // As the owner, whom no REVOKE binds: only the trigger stands between a hand-written fix and
        // an invoice that no longer matches the one the customer holds.
        foreach (var change in new[]
                 {
                     "checksum = repeat('0', 64)",
                     "asset_id = NULL",
                     "status = 'Pending'",
                     "issue_number = 7",
                     "recipient_email = 'someone.else@example.test'",
                 })
        {
            // The column list is a constant of this test; the id is a parameter.
            var sql = "UPDATE documents.generated_documents SET " + change + " WHERE id = {0}";
            var act = () => owner.Database.ExecuteSqlRawAsync(sql, invoice.Id);

            (await act.Should().ThrowAsync<PostgresException>()).Which.SqlState
                .Should().Be(PostgresErrorCodes.RestrictViolation, change);
        }

        var delete = () => owner.Database.ExecuteSqlAsync(
            $"DELETE FROM documents.generated_documents WHERE id = {invoice.Id}");
        (await delete.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.RestrictViolation);

        // Which email carried it is bookkeeping, not the document; that may still be recorded.
        (await owner.Database.ExecuteSqlAsync(
            $"UPDATE documents.generated_documents SET email_notification_id = {Guid.CreateVersion7()} WHERE id = {invoice.Id}"))
            .Should().Be(1);
    }

    // ------------------------------------------------------------------ the rules around it

    [Fact]
    public async Task An_agency_whose_name_would_show_our_brand_gets_no_document_rather_than_one_that_does()
    {
        var world = await WorldAsync(tradingName: "Trips Agent Travels");

        (await world.IssueAsync()).Should().Be(DocumentRunOutcome.GaveUp);

        await using var read = world.AsAgency(world.AgencyA);
        var invoice = await read.GeneratedDocuments.AsNoTracking().SingleAsync(d => d.DocumentType == DocumentType.Invoice);

        invoice.Status.Should().Be(DocumentStatus.Failed, "retrying cannot change the agency's name");
        invoice.LastRenderError.Should().Contain("rule 4");
        (await read.Assets.CountAsync()).Should().Be(0);
        (await read.Notifications.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Another_agency_cannot_see_the_documents_even_without_the_filter()
    {
        var world = await WorldAsync();
        await world.IssueAsync();

        await using var asB = world.AsAgency(world.AgencyB);
        await using var asA = world.AsAgency(world.AgencyA);

        // Row-level security, not the EF filter, is what answers here.
        (await asB.GeneratedDocuments.IgnoreQueryFilters().CountAsync()).Should().Be(0);
        (await asA.GeneratedDocuments.IgnoreQueryFilters().CountAsync()).Should().Be(2);
    }

    // ------------------------------------------------------------------ helpers

    private static string Sha256(byte[] content) => Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

    private static string TextOf(byte[] pdf)
    {
        using var document = PdfDocument.Open(pdf);
        return string.Join('\n', document.GetPages().Select(page => page.Text));
    }

    private static string Squash(string text) => string.Concat(text.Where(c => !char.IsWhiteSpace(c)));

    private async Task<World> WorldAsync(
        string tradingName = "Lagos Travel",
        bool confirmed = true,
        [CallerMemberName] string testName = "")
    {
        var database = $"docs_{testName.ToLowerInvariant()}";
        database = database[..Math.Min(database.Length, 60)];

        var tenancy = TestTenancy.None();

        await using var setup = await _postgres.CreateEmptyDatabaseAsync(database, tenancy.Tenant, tenancy.Scope);
        await setup.Database.MigrateAsync();
        await NotificationTemplateSeeder.EnsureAsync(setup, TimeProvider.System);

        using var _ = tenancy.Scope.Enter("test setup — two agencies and an order from one of them");

        var lagos = Agency.RegisterPrincipal(
            "Lagos Travel Services Limited", "lagos-travel", "NG", "NGN", "Africa/Lagos", tradingName: tradingName);
        var abuja = Agency.RegisterPrincipal("Abuja Tours Limited", "abuja-tours", "NG", "NGN", "Africa/Lagos");

        var branding = AgencyBranding.CreateDefault(lagos);
        branding.SetColors(AgencyColour, null);
        branding.SetContactAddress("12 Marina, Lagos");

        setup.Agencies.AddRange(lagos, abuja);
        setup.AgencySettings.AddRange(AgencySettings.CreateDefault(lagos), AgencySettings.CreateDefault(abuja));
        setup.AgencyBranding.Add(branding);
        setup.Users.Add(User.ForAgency(lagos.Id, "owner@lagos-travel.test", "argon2id$hash", "Ngozi", "Adeyemi"));
        await setup.SaveChangesAsync();

        var now = DateTimeOffset.UtcNow;

        var rule = MarkupRule.Create(lagos.Id, new MarkupRuleTerms
        {
            Scope = MarkupScope.Global,
            Currency = "NGN",
            CalculationType = MarkupCalculationType.Percentage,
            PercentBasisPoints = 1_000,
            EffectiveFrom = now.AddDays(-1),
        });
        setup.MarkupRules.Add(rule);
        await setup.SaveChangesAsync();

        var quote = PriceQuote.Record(
            lagos.Id,
            new PricingSubject(PricedProductType.Flight, "NGN"),
            new PriceBreakdown(
                new Money(100_000), new Money(10_000), new Money(750), new Money(500), new Money(110_750),
                "NGN", new MarkupRuleDefinition(rule.Id, lagos.Id, rule.Terms), false, 750, 0),
            now,
            TimeSpan.FromMinutes(30));
        setup.PriceQuotes.Add(quote);
        await setup.SaveChangesAsync();

        var line = OrderLine.FromQuote(quote, "Lagos (LOS) to Abuja (ABV), Air Peace", """{"adults":1}""", now);
        var order = Order.Place(
            lagos.Id, "ORD-2026-000142", "NGN", BuyerType.AgentAssisted, OrderChannel.Console, null, [line], now);

        setup.Orders.Add(order);
        setup.OrderTravellers.Add(OrderTraveller.Record(lagos.Id, line.Id, TravellerType.Adult, "Ada", "Obi"));
        await setup.SaveChangesAsync();

        if (confirmed)
        {
            line.RecordFulfilment(FulfilmentStatus.Confirmed, now);
            order.ChangeStatus(OrderStatus.Confirmed, now);
            await setup.SaveChangesAsync();
        }

        return new World(_postgres, database, lagos.Id, abuja.Id, order.Id, line.Id);
    }

    private sealed class World(
        PostgresFixture postgres,
        string database,
        Guid agencyA,
        Guid agencyB,
        Guid orderId,
        Guid lineId)
    {
        private readonly DocumentLinks _links = new(new HmacTokenHasher(RandomNumberGenerator.GetBytes(32)), TimeProvider.System);

        public Guid AgencyA { get; } = agencyA;

        public Guid AgencyB { get; } = agencyB;

        public Guid OrderId { get; } = orderId;

        public Guid LineId { get; } = lineId;

        public LocalFileBlobStorage Storage { get; } = new(new LocalBlobStorageOptions
        {
            RootPath = Path.Combine(Path.GetTempPath(), "tripsagent-document-tests", $"{database}-{Guid.NewGuid():N}"),
        });

        public OrderDocumentsRequested Request(Guid? agencyId = null) =>
            new(agencyId ?? AgencyA, OrderId, "Ada Obi", "Ada.Obi@example.test");

        /// <summary>One delivery of the order's request, as the consumer makes it: acting as the agency.</summary>
        public async Task<DocumentRunOutcome> IssueAsync(ISupplierBookingReader? supplier = null)
        {
            var (db, service) = Worker(AgencyA, supplier);
            await using (db)
            {
                return await service.IssueForOrderAsync(Request());
            }
        }

        /// <summary>A delivery that may lose a race; losing throws, as it would to the broker.</summary>
        public async Task TryIssueAsync()
        {
            try
            {
                await IssueAsync();
            }
            catch (DbUpdateException)
            {
                // Lost a race to another delivery of the same message. The broker would redeliver.
            }
        }

        public async Task<DocumentRunOutcome> RenderAsync(Guid documentId)
        {
            var (db, service) = Worker(AgencyA);
            await using (db)
            {
                return await service.RenderDocumentAsync(new DocumentRenderRequested(AgencyA, documentId));
            }
        }

        public (AppDbContext Db, OrderDocumentService Service) Worker(Guid agencyId, ISupplierBookingReader? supplier = null)
        {
            var tenancy = TestTenancy.For(agencyId);
            var db = postgres.Connect(database, tenancy.Tenant, tenancy.Scope);

            var service = new OrderDocumentService(
                db,
                tenancy.Tenant,
                new DocumentNumberAllocator(db, tenancy.Tenant, TimeProvider.System),
                new EfTransactionRunner(db),
                new PostgresUniqueViolationDetector(),
                Renderer,
                Storage,
                new AgencyLogoSource(db, Storage, NullLogger<AgencyLogoSource>.Instance),
                supplier ?? new SupplierBookingReader(db),
                new Notifier(db, new EfOutbox(db, TimeProvider.System)),
                TimeProvider.System,
                NullLogger<OrderDocumentService>.Instance);

            return (db, service);
        }

        /// <summary>The console's reissue, in a request acting as the agency.</summary>
        public async Task<ReissueDocumentOutcome> ReissueAsync(Guid documentId)
        {
            var tenancy = TestTenancy.For(AgencyA);
            await using var db = postgres.Connect(database, tenancy.Tenant, tenancy.Scope);

            return await Handler(db, tenancy.Tenant, tenancy.Scope).ReissueAsync(documentId);
        }

        /// <summary>A signed download, which carries no tenant of its own.</summary>
        public async Task<DocumentFileOutcome> OpenOutcomeAsync(Guid documentId, bool forCustomer)
        {
            var tenancy = TestTenancy.None();
            await using var db = postgres.Connect(database, tenancy.Tenant, tenancy.Scope);

            return await Handler(db, tenancy.Tenant, tenancy.Scope).OpenAsync(documentId, forCustomer);
        }

        public async Task<byte[]> OpenAsync(Guid documentId) =>
            (await OpenOutcomeAsync(documentId, forCustomer: false)).Should().BeOfType<DocumentFileOutcome.File>().Subject.Content;

        public async Task<byte[]> OpenAsync(DocumentType type) => await OpenAsync((await FindAsync(type)).Id);

        public async Task<GeneratedDocument> FindAsync(DocumentType type)
        {
            await using var read = AsAgency(AgencyA);
            return await read.GeneratedDocuments.AsNoTracking()
                .Where(d => d.DocumentType == type && d.SupersedesDocumentId == null)
                .SingleAsync();
        }

        /// <summary>
        /// Every column that matters about a document, as the database holds it. Read as the owner,
        /// past the tenant filter — which, with no tenant and no scope, would otherwise match nothing.
        /// </summary>
        public async Task<string> RowAsync(Guid documentId)
        {
            await using var owner = Owner();
            var row = await owner.GeneratedDocuments.AsNoTracking().IgnoreQueryFilters().SingleAsync(d => d.Id == documentId);

            return string.Join(
                '|',
                row.DocumentNumber,
                row.IssueNumber,
                row.SupersedesDocumentId,
                row.Status,
                row.AssetId,
                row.Checksum,
                row.SizeBytes,
                row.TemplateKey,
                row.TemplateVersion,
                row.RenderedAt?.ToUnixTimeMilliseconds(),
                row.RecipientEmail,
                row.UpdatedAt.ToUnixTimeMilliseconds());
        }

        /// <summary>Two documents, one first issue of each, one email — and no number taken twice.</summary>
        public async Task ExpectOneSetAsync()
        {
            await using var read = AsAgency(AgencyA);

            (await read.GeneratedDocuments.CountAsync()).Should().Be(2);
            (await read.GeneratedDocuments.CountAsync(d => d.Status == DocumentStatus.Ready)).Should().Be(2);
            (await read.Notifications.CountAsync()).Should().Be(1);

            await using var owner = Owner();
            var counters = await owner.DocumentNumberSequences.AsNoTracking().IgnoreQueryFilters()
                .Where(s => s.AgencyId == AgencyA && s.DocumentType != DocumentType.Order)
                .Select(s => s.LastValue)
                .ToListAsync();

            counters.Should().Equal(1, 1);
        }

        public async Task<IReadOnlyList<EmailMessage>> DispatchQueuedEmailsAsync()
        {
            var sent = new List<EmailMessage>();
            var tenancy = TestTenancy.None();
            await using var db = postgres.Connect(database, tenancy.Tenant, tenancy.Scope);

            List<Guid> queued;
            using (tenancy.Scope.Enter("test — the Worker's view of the queue"))
            {
                queued = await db.Notifications.Where(n => n.Status == NotificationStatus.Queued).Select(n => n.Id).ToListAsync();
            }

            var dispatcher = new NotificationDispatcher(
                db,
                new CapturingSender(sent),
                tenancy.Scope,
                new AgencyLogoSource(db, Storage, NullLogger<AgencyLogoSource>.Instance),
                Storage,
                TimeProvider.System,
                NullLogger<NotificationDispatcher>.Instance);

            foreach (var id in queued)
            {
                await dispatcher.DispatchAsync(id);
            }

            return sent;
        }

        public async Task<byte[]> ReadStoredAsync(string key)
        {
            await using var stream = await Storage.OpenReadAsync(key);
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer);
            return buffer.ToArray();
        }

        public AppDbContext AsAgency(Guid agencyId)
        {
            var tenancy = TestTenancy.For(agencyId);
            return postgres.Connect(database, tenancy.Tenant, tenancy.Scope);
        }

        public AppDbContext Owner() => postgres.Connect(database, asApplicationRole: false);

        private BookingDocumentsHandler Handler(
            AppDbContext db,
            TripsAgent.Application.Tenancy.ITenantContext tenant,
            TripsAgent.Application.Tenancy.IPlatformScope scope) =>
            new(
                db,
                new DocumentNumberAllocator(db, tenant, TimeProvider.System),
                new EfTransactionRunner(db),
                new PostgresUniqueViolationDetector(),
                new EfOutbox(db, TimeProvider.System),
                tenant,
                scope,
                Storage,
                _links,
                TimeProvider.System,
                NullLogger<BookingDocumentsHandler>.Instance);
    }

    private sealed class FixedSupplierBookings(params SupplierBookingSnapshot[] bookings) : ISupplierBookingReader
    {
        public Task<IReadOnlyList<SupplierBookingSnapshot>> ForOrderLinesAsync(
            IReadOnlyCollection<Guid> orderLineIds,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SupplierBookingSnapshot>>(
                bookings.Where(booking => orderLineIds.Contains(booking.OrderLineId)).ToList());
    }

    private sealed class CapturingSender(List<EmailMessage> sent) : IEmailSender
    {
        public Task<EmailReceipt> SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
        {
            sent.Add(message);
            return Task.FromResult(new EmailReceipt($"<{Guid.NewGuid():N}@test>"));
        }
    }
}
