using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using QuestPDF.Infrastructure;
using TripsAgent.Application.Documents;
using TripsAgent.Application.Notifications;
using TripsAgent.Application.Payments;
using TripsAgent.Application.Platform;
using TripsAgent.Documents;
using TripsAgent.Domain.Common;
using TripsAgent.Domain.Crm;
using TripsAgent.Domain.Documents;
using TripsAgent.Domain.Identity;
using TripsAgent.Domain.Notifications;
using TripsAgent.Domain.Orders;
using TripsAgent.Domain.Payments;
using TripsAgent.Domain.Platform;
using TripsAgent.Domain.Pricing;
using TripsAgent.Domain.Suppliers;
using TripsAgent.Domain.Tenancy;
using TripsAgent.Infrastructure.Assets;
using TripsAgent.Infrastructure.Documents;
using TripsAgent.Infrastructure.Messaging;
using TripsAgent.Infrastructure.Notifications;
using TripsAgent.Infrastructure.Payments;
using TripsAgent.Infrastructure.Persistence;
using TripsAgent.Infrastructure.Storage;
using TripsAgent.Infrastructure.Tenancy;
using TripsAgent.IntegrationTests.Persistence;
using UglyToad.PdfPig;

namespace TripsAgent.IntegrationTests.Platform;

/// <summary>
/// NDPA erasure as anonymisation (issue 106), against a real database: the person stops being
/// identifiable, and the books, the bookings and the paperwork all still stand.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class CustomerErasureTests
{
    private const string CustomerName = "Adaeze Okafor";
    private const string CustomerEmail = "adaeze.okafor@example.test";
    private const string CustomerPhone = "0803 000 1122";
    private const string Passport = "A01234567";
    private const string Reason = "The traveller asked us to erase her details, by email on 12 September 2026.";

    private static readonly QuestPdfDocumentRenderer Renderer = new(LicenseType.Community);

    private readonly PostgresFixture _postgres;

    public CustomerErasureTests(PostgresFixture postgres) => _postgres = postgres;

    [Fact]
    public async Task Erasing_a_person_replaces_their_details_everywhere_and_leaves_the_records_standing()
    {
        await using var world = await WorldAsync();
        await world.IssueInvoiceAsync();

        var preview = await world.PreviewAsync();

        preview.Should().NotBeNull();
        preview!.Name.Should().Be(CustomerName);
        preview.Blockers.Should().BeEmpty();
        preview.Travellers.Should().Be(1);
        preview.TravelDocuments.Should().Be(1);
        preview.Notifications.Should().BeGreaterThanOrEqualTo(1);

        var ledgerBefore = await world.LedgerFingerprintAsync();
        var linesBefore = await world.OrderLineFingerprintAsync();

        var outcome = await world.EraseAsync();

        outcome.Completed.Should().BeTrue();
        outcome.Changed["crm.customers"].Should().Be(1);
        outcome.Changed["orders.order_travellers"].Should().Be(1);
        outcome.Changed["supplier.passenger_documents"].Should().Be(1);

        await using var read = world.Owner();

        // The person.
        var customer = await read.Customers.AsNoTracking().SingleAsync(c => c.Id == world.CustomerId);
        customer.Name.Should().Be(CustomerErasureService.ErasedName);
        customer.Email.Should().EndWith("@erased.invalid", "the table's rule is that a customer is contactable, so the address becomes one that cannot exist");
        customer.Email.Should().NotContain("okafor");
        customer.Phone.Should().BeNull();
        customer.PhoneKey.Should().BeNull();

        // Who travelled: the row stays with the sale and names nobody.
        var traveller = await read.OrderTravellers.AsNoTracking().SingleAsync(t => t.OrderLineId == world.LineId);
        traveller.FirstName.Should().Be(CustomerErasureService.ErasedFirstName);
        traveller.PassportNumber.Should().BeNull();
        traveller.PassportExpiry.Should().BeNull();
        traveller.BirthDate.Should().BeNull();

        var passenger = await read.SupplierBookingPassengers.AsNoTracking().SingleAsync(p => p.Id == world.PassengerId);
        passenger.Email.Should().BeNull();
        passenger.LastName.Should().Be(CustomerErasureService.ErasedLastName);
        passenger.TicketNumber.Should().Be("0632101234567", "the ticket ties the airline's invoice to ours and names nobody");

        (await read.PassengerDocuments.AsNoTracking().CountAsync()).Should().Be(0, "a travel document is destroyed, not anonymised");

        var notification = await read.Notifications.AsNoTracking().FirstAsync();
        notification.RecipientAddress.Should().EndWith("@erased.invalid");
        notification.RecipientAddress.Should().NotContain(CustomerEmail);
        notification.Payload.Should().Be("{}");

        var waitlisted = await read.DepartureWaitlist.AsNoTracking().SingleAsync();
        waitlisted.Email.Should().NotBe(CustomerEmail);

        // Nothing about the money moved.
        (await world.LedgerFingerprintAsync()).Should().Be(ledgerBefore, "erasure never touches the ledger");
        (await world.OrderLineFingerprintAsync()).Should().Be(linesBefore, "an order line's frozen prices are a financial record");
    }

    [Fact]
    public async Task After_an_erasure_the_ledger_audit_still_passes_and_a_historical_invoice_still_renders()
    {
        await using var world = await WorldAsync();
        var invoiceId = await world.IssueInvoiceAsync();

        (await world.EraseAsync()).Completed.Should().BeTrue();

        // The nightly proof that the books balance.
        var audit = await world.LedgerAuditAsync();
        audit.IsClean.Should().BeTrue("anonymising a person changes no entry, no account and no balance");

        // The invoice that was issued is still there, still opens, and is still a readable PDF.
        var pdf = await world.OpenInvoiceAsync(invoiceId);
        pdf.Should().NotBeEmpty();

        using var document = PdfDocument.Open(pdf);
        var text = string.Join(' ', document.GetPages().Select(page => page.Text));
        text.Should().Contain("INVOICE");

        // And a document rendered after the erasure builds from the anonymised data rather than failing.
        var reissued = await world.ReissueInvoiceAsync(invoiceId);
        using var replacement = PdfDocument.Open(await world.OpenInvoiceAsync(reissued));
        var replacementText = string.Join(' ', replacement.GetPages().Select(page => page.Text));

        replacementText.Should().NotContain("Okafor", "a document rendered after an erasure prints the placeholder");
        replacementText.Should().Contain(CustomerErasureService.ErasedName);
        replacementText.Should().NotContain(Passport);
    }

    [Fact]
    public async Task The_request_is_recorded_with_its_reason_and_holds_no_personal_detail()
    {
        await using var world = await WorldAsync();
        var outcome = await world.EraseAsync();

        await using var read = world.Owner();
        var request = await read.ErasureRequests.AsNoTracking().SingleAsync(r => r.Id == outcome.RequestId);

        request.Status.Should().Be(ErasureRequestStatus.Completed);
        request.Reason.Should().Be(Reason);
        request.CustomerId.Should().Be(world.CustomerId);
        request.CompletedAt.Should().NotBeNull();

        var row = JsonSerializer.Serialize(request);
        row.Should().NotContain(CustomerName).And.NotContain(CustomerEmail).And.NotContain(CustomerPhone);

        // The counts are there, so somebody can answer "what did it change?" without keeping what it changed.
        request.Outcome.Should().Contain("orders.order_travellers");

        // And it is audited like every other write, with the reason on the entry.
        var audited = await read.AuditLogs.AsNoTracking()
            .Where(entry => entry.EntityType == nameof(ErasureRequest))
            .ToListAsync();

        audited.Should().ContainSingle();
        audited[0].AfterState.Should().NotContain(CustomerEmail);
    }

    [Fact]
    public async Task An_order_still_in_flight_refuses_the_erasure_and_the_refusal_is_recorded()
    {
        await using var world = await WorldAsync(orderStatus: OrderStatus.PendingPayment);

        var preview = await world.PreviewAsync();
        preview!.Blockers.Should().ContainSingle().Which.Should().Contain("still being paid for");

        var outcome = await world.EraseAsync();

        outcome.Completed.Should().BeFalse();
        outcome.Changed.Should().BeEmpty();

        await using var read = world.Owner();

        var customer = await read.Customers.AsNoTracking().SingleAsync(c => c.Id == world.CustomerId);
        customer.Name.Should().Be(CustomerName, "a refused erasure changes nothing at all");

        var request = await read.ErasureRequests.AsNoTracking().SingleAsync();
        request.Status.Should().Be(ErasureRequestStatus.Refused);
        request.RefusalReason.Should().Contain("Complete or cancel them first");
    }

    [Fact]
    public async Task Uploaded_evidence_is_deleted_from_storage_and_the_asset_row_says_so()
    {
        await using var world = await WorldAsync();
        var assetId = await world.AddDisputeWithEvidenceAsync();

        (await world.PreviewAsync())!.EvidenceFiles.Should().Be(1);

        (await world.EraseAsync()).Completed.Should().BeTrue();

        await using var read = world.Owner();
        var asset = await read.Assets.AsNoTracking().SingleAsync(a => a.Id == assetId);

        asset.Status.Should().Be(Domain.Assets.AssetStatus.Erased);
        (await world.Storage.ExistsAsync(asset.StorageKey)).Should().BeFalse("the bytes are gone, not just unreferenced");

        var dispute = await read.Disputes.AsNoTracking().SingleAsync();
        dispute.EvidenceAssetIds.Should().BeNull();
        dispute.AmountMinor.AmountMinor.Should().Be(110_750, "the chargeback itself is a financial record");
    }

    // ------------------------------------------------------------------ the world

    private async Task<World> WorldAsync(
        OrderStatus orderStatus = OrderStatus.Confirmed,
        [CallerMemberName] string testName = "")
    {
        var database = $"erasure_{testName.ToLowerInvariant()}";
        database = database[..Math.Min(database.Length, 60)];

        var tenancy = TestTenancy.None();

        await using var setup = await _postgres.CreateEmptyDatabaseAsync(database, tenancy.Tenant, tenancy.Scope);
        await setup.Database.MigrateAsync();
        await NotificationTemplateSeeder.EnsureAsync(setup, TimeProvider.System);

        using var scope = tenancy.Scope.Enter("test setup — an agency, a customer, their booking and its paperwork");

        var now = DateTimeOffset.UtcNow;

        var agency = Agency.RegisterPrincipal("Lagos Travel Limited", "lagos-travel", "NG", "NGN", "Africa/Lagos");
        var branding = AgencyBranding.CreateDefault(agency);
        branding.SetContactAddress("12 Marina, Lagos");

        setup.Agencies.Add(agency);
        setup.AgencySettings.Add(AgencySettings.CreateDefault(agency));
        setup.AgencyBranding.Add(branding);
        setup.Users.Add(User.ForAgency(agency.Id, "owner@lagos-travel.test", "argon2id$hash", "Ngozi", "Adeyemi"));

        // The platform's own ledger accounts are seeded by the migrations; this is the agency's.
        setup.LedgerAccounts.Add(LedgerAccount.ForAgency(agency.Id, LedgerAccountType.AgencyWallet, "NGN", "Wallet"));
        setup.Wallets.Add(Wallet.OpenFor(agency.Id, "NGN"));
        await setup.SaveChangesAsync();

        var customer = Customer.Create(agency.Id, CustomerName, CustomerEmail, CustomerPhone, now);
        setup.Customers.Add(customer);
        await setup.SaveChangesAsync();

        var rule = MarkupRule.Create(agency.Id, new MarkupRuleTerms
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
            agency.Id,
            new PricingSubject(PricedProductType.Flight, "NGN"),
            new PriceBreakdown(
                new Money(100_000), new Money(10_000), new Money(750), new Money(500), new Money(110_750),
                "NGN", new MarkupRuleDefinition(rule.Id, agency.Id, rule.Terms), false, 750, 0),
            now,
            TimeSpan.FromMinutes(30));
        setup.PriceQuotes.Add(quote);
        await setup.SaveChangesAsync();

        var line = OrderLine.FromQuote(quote, "Lagos (LOS) to Abuja (ABV), Air Peace", """{"adults":1}""", now);
        var order = Order.Place(
            agency.Id, "ORD-2026-000142", "NGN", BuyerType.Customer, OrderChannel.Storefront, customer.Id, [line], now);

        setup.Orders.Add(order);
        setup.OrderTravellers.Add(OrderTraveller.Record(
            agency.Id, line.Id, TravellerType.Adult, "Adaeze", "Okafor",
            birthDate: new DateOnly(1988, 7, 12),
            passportNumber: Passport,
            passportExpiry: new DateOnly(2031, 5, 17),
            nationality: "NG"));
        await setup.SaveChangesAsync();

        if (orderStatus != OrderStatus.PendingPayment)
        {
            line.RecordFulfilment(FulfilmentStatus.Confirmed, now);
            order.ChangeStatus(orderStatus, now);
            await setup.SaveChangesAsync();
        }

        var supplier = Supplier.Register("trips_africa", "Trips Africa", SupplierKind.Multi, "https://api.example.test");
        setup.Suppliers.Add(supplier);
        await setup.SaveChangesAsync();

        var booking = SupplierBooking.Create(
            agency.Id, supplier.Id, line.Id, null, SupplierProductType.Flight, "Domestic", "Flight",
            "session-1", "NGN", $"idem-{Guid.NewGuid():N}");
        setup.SupplierBookings.Add(booking);
        await setup.SaveChangesAsync();

        var passenger = SupplierBookingPassenger.Add(
            agency.Id, booking.Id, PassengerType.Adult, "Adaeze", "Okafor", email: CustomerEmail);
        passenger.RecordTicketNumber("0632101234567");
        setup.SupplierBookingPassengers.Add(passenger);
        await setup.SaveChangesAsync();

        setup.PassengerDocuments.Add(PassengerDocument.Add(
            agency.Id, passenger.Id, TravelDocumentRecord.Docs, TravelDocumentKind.Passport,
            Passport, "NG", "NG", new DateOnly(2021, 5, 18), new DateOnly(2031, 5, 17)));

        setup.Notifications.Add(Notification.Queue(
            agency.Id,
            NotificationTemplateCatalog.BookingConfirmed,
            NotificationChannel.Email,
            "en",
            NotificationRecipientType.Traveller,
            CustomerEmail,
            CustomerName,
            $$"""{"orderNumber":"ORD-2026-000142","name":"{{CustomerName}}"}""",
            $"booking-confirmed:{order.Id}"));

        var product = Domain.Catalog.Product.CreateDraft(
            agency.Id,
            new Domain.Catalog.ProductContent
            {
                ProductType = Domain.Catalog.ProductType.Tour,
                Title = "Obudu, four days",
                Summary = "Four days on the plateau.",
                Description = "Four days on the plateau, guided.",
                DestinationCountry = "NG",
                DurationDays = 4,
                Currency = "NGN",
                BasePriceMinor = new Money(100_000),
            },
            "obudu-four-days");
        setup.Products.Add(product);
        await setup.SaveChangesAsync();

        var departure = Domain.Catalog.Departure.Create(
            agency.Id,
            product.Id,
            new Domain.Catalog.DepartureTerms
            {
                DepartureDate = DateOnly.FromDateTime(now.UtcDateTime).AddDays(90),
                IsGroupDeparture = true,
                MinPax = 2,
                CapacityTotal = 10,
                CutoffDaysBefore = 14,
                DepositType = Domain.Catalog.DepositType.None,
                PriceTiers = [new Domain.Catalog.PriceTierTerms(1, null, new Money(100_000))],
            },
            now.AddDays(76),
            now);
        setup.Departures.Add(departure);
        await setup.SaveChangesAsync();

        setup.DepartureWaitlist.Add(Domain.Catalog.DepartureWaitlistEntry.Join(
            agency.Id, departure.Id, CustomerName, CustomerEmail, 2, now));
        await setup.SaveChangesAsync();

        return new World(_postgres, database, agency.Id, customer.Id, order.Id, line.Id, passenger.Id);
    }

    private sealed class World(
        PostgresFixture postgres,
        string database,
        Guid agencyId,
        Guid customerId,
        Guid orderId,
        Guid lineId,
        Guid passengerId) : IAsyncDisposable
    {
        private readonly List<AppDbContext> _contexts = [];

        public Guid AgencyId { get; } = agencyId;

        public Guid CustomerId { get; } = customerId;

        public Guid LineId { get; } = lineId;

        public Guid PassengerId { get; } = passengerId;

        public LocalFileBlobStorage Storage { get; } = new(new LocalBlobStorageOptions
        {
            RootPath = Path.Combine(Path.GetTempPath(), "tripsagent-erasure-tests", $"{database}-{Guid.NewGuid():N}"),
        });

        /// <summary>The schema owner, for reading what is really stored.</summary>
        public AppDbContext Owner()
        {
            var tenancy = TestTenancy.For(AgencyId);
            return Track(postgres.Connect(database, tenancy.Tenant, tenancy.Scope, asApplicationRole: false));
        }

        public async Task<ErasurePreview?> PreviewAsync()
        {
            var (db, service) = Service();
            await using (db)
            {
                return await service.PreviewAsync(AgencyId, CustomerEmail);
            }
        }

        public async Task<ErasureOutcome> EraseAsync()
        {
            var (db, service) = Service();
            await using (db)
            {
                return await service.EraseAsync(AgencyId, CustomerId, Reason);
            }
        }

        public async Task<LedgerAuditResult> LedgerAuditAsync()
        {
            var tenancy = TestTenancy.None();
            await using var db = postgres.Connect(database, tenancy.Tenant, tenancy.Scope);

            var audit = new LedgerIntegrityAudit(
                db,
                new LedgerIntegrityQueries(db),
                tenancy.Scope,
                new NoAlerts(),
                TimeProvider.System,
                NullLogger<LedgerIntegrityAudit>.Instance);

            return await audit.RunAsync();
        }

        /// <summary>Issues and renders the order's paperwork, and returns the invoice.</summary>
        public async Task<Guid> IssueInvoiceAsync()
        {
            var (db, service) = Documents();
            await using (db)
            {
                await service.IssueForOrderAsync(new OrderDocumentsRequested(AgencyId, orderId, CustomerName, CustomerEmail));
            }

            await using var read = Owner();
            return (await read.GeneratedDocuments.AsNoTracking()
                .SingleAsync(document => document.DocumentType == DocumentType.Invoice)).Id;
        }

        public async Task<Guid> ReissueInvoiceAsync(Guid invoiceId)
        {
            await using var read = Owner();
            var original = await read.GeneratedDocuments.AsNoTracking().SingleAsync(document => document.Id == invoiceId);

            var tenancy = TestTenancy.For(AgencyId);
            await using var db = postgres.Connect(database, tenancy.Tenant, tenancy.Scope);

            var numbers = new DocumentNumberAllocator(db, tenancy.Tenant, TimeProvider.System);

            // Inside a transaction, because taking a number outside one leaves a gap when the save fails.
            var replacement = await new EfTransactionRunner(db).RunAsync(async token =>
            {
                var issued = await db.GeneratedDocuments.SingleAsync(document => document.Id == invoiceId, token);

                var next = issued.Reissue(
                    await numbers.NextAsync(DocumentType.Invoice, DateTimeOffset.UtcNow, token), DateTimeOffset.UtcNow);

                db.GeneratedDocuments.Add(next);
                await db.SaveChangesAsync(token);

                return next;
            });

            var (documentsDb, service) = Documents();
            await using (documentsDb)
            {
                (await service.RenderDocumentAsync(new DocumentRenderRequested(AgencyId, replacement.Id)))
                    .Should().Be(DocumentRunOutcome.Completed);
            }

            original.DocumentNumber.Should().NotBe(replacement.DocumentNumber);
            return replacement.Id;
        }

        public async Task<byte[]> OpenInvoiceAsync(Guid documentId)
        {
            await using var read = Owner();
            var document = await read.GeneratedDocuments.AsNoTracking().SingleAsync(d => d.Id == documentId);
            var asset = await read.Assets.AsNoTracking().SingleAsync(a => a.Id == document.AssetId);

            await using var content = await Storage.OpenReadAsync(asset.StorageKey);
            using var buffer = new MemoryStream();
            await content.CopyToAsync(buffer);

            return buffer.ToArray();
        }

        /// <summary>A resolved chargeback with an uploaded evidence file behind it.</summary>
        public async Task<Guid> AddDisputeWithEvidenceAsync()
        {
            var tenancy = TestTenancy.For(AgencyId);
            await using var db = postgres.Connect(database, tenancy.Tenant, tenancy.Scope, asApplicationRole: false);

            var now = DateTimeOffset.UtcNow;

            var asset = Domain.Assets.Asset.RecordGenerated(
                AgencyId, "evidence.pdf", $"agencies/{AgencyId}/evidence/{Guid.CreateVersion7()}.pdf",
                "application/pdf", 12, "checksum", now);

            using (var bytes = new MemoryStream([1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12]))
            {
                await Storage.StoreAsync(bytes, asset.StorageKey, "application/pdf");
            }

            db.Assets.Add(asset);

            var payment = PaymentTransaction.Start(
                AgencyId, null, PaymentPurpose.OrderPayment, new Money(110_750), "NGN",
                $"ref-{Guid.NewGuid():N}", orderId: orderId);
            db.PaymentTransactions.Add(payment);
            await db.SaveChangesAsync();

            var dispute = Dispute.Open(
                AgencyId, payment.Id, payment.Reference, $"DSP_{Guid.NewGuid():N}",
                new Money(110_750), "NGN", "fraud", "Traveller says she did not authorise it",
                now, now.AddDays(7), orderId);

            dispute.RecordEvidence(
                "Boarding passes and her email confirming the trip",
                $"[\"{asset.Id}\"]",
                "{\"customer_email\":\"" + CustomerEmail + "\"}",
                null,
                now.AddDays(1));

            dispute.MarkWon("won", now.AddDays(3), null);

            db.Disputes.Add(dispute);
            await db.SaveChangesAsync();

            return asset.Id;
        }

        /// <summary>Every ledger entry, hashed: proof that not one of them moved.</summary>
        public Task<string> LedgerFingerprintAsync() => FingerprintAsync("payments.ledger_entries");

        public Task<string> OrderLineFingerprintAsync() => FingerprintAsync("orders.order_lines");

        public async ValueTask DisposeAsync()
        {
            foreach (var context in _contexts)
            {
                await context.DisposeAsync();
            }
        }

        private async Task<string> FingerprintAsync(string table)
        {
            await using var connection = new NpgsqlConnection(postgres.ConnectionStringFor(database, asApplicationRole: false));
            await connection.OpenAsync();

            await using var command = connection.CreateCommand();

            // The table names here are constants in this file, never input.
            command.CommandText = $"SELECT coalesce(md5(string_agg(t::text, '|' ORDER BY t::text)), 'empty') FROM {table} AS t";

            return (string)(await command.ExecuteScalarAsync())!;
        }

        private (AppDbContext Db, CustomerErasureService Service) Service()
        {
            // No tenant: this is Trips staff acting across agencies, exactly as the endpoint does.
            var tenancy = TestTenancy.None();
            var db = postgres.Connect(database, tenancy.Tenant, tenancy.Scope, auditContext: new PlatformActor());

            var service = new CustomerErasureService(
                db,
                tenancy.Scope,
                new EfTransactionRunner(db),
                Storage,
                tenancy.Tenant,
                TimeProvider.System,
                NullLogger<CustomerErasureService>.Instance);

            return (db, service);
        }

        private (AppDbContext Db, OrderDocumentService Service) Documents()
        {
            var tenancy = TestTenancy.For(AgencyId);
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
                new SupplierBookingReader(db),
                new Notifier(db, new EfOutbox(db, TimeProvider.System)),
                TimeProvider.System,
                NullLogger<OrderDocumentService>.Instance);

            return (db, service);
        }

        private AppDbContext Track(AppDbContext context)
        {
            _contexts.Add(context);
            return context;
        }
    }

    /// <summary>Trips staff, with no agency of their own.</summary>
    private sealed class PlatformActor : Application.Auditing.IAuditContext
    {
        public Guid? ActorUserId => null;

        public Domain.Auditing.AuditActorType ActorType => Domain.Auditing.AuditActorType.User;

        public string? ActorIpAddress => null;

        public Guid? AgencyId => null;

        public string? CorrelationId => null;

        public string? Reason => "NDPA erasure (issue 106)";

        public void SetReason(string? reason)
        {
            // Fixed for the test.
        }
    }

    private sealed class NoAlerts : IPlatformAlerter
    {
        public Task RaiseAsync(PlatformAlert alert, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
