using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using QuestPDF.Infrastructure;
using TripsAgent.Application.Documents;
using TripsAgent.Application.Identity;
using TripsAgent.Application.Notifications;
using TripsAgent.Contracts.Documents;
using TripsAgent.Documents;
using TripsAgent.Domain.Common;
using TripsAgent.Domain.Identity;
using TripsAgent.Domain.Orders;
using TripsAgent.Domain.Pricing;
using TripsAgent.Domain.Tenancy;
using TripsAgent.Infrastructure.Assets;
using TripsAgent.Infrastructure.Documents;
using TripsAgent.Infrastructure.Messaging;
using TripsAgent.Infrastructure.Notifications;
using TripsAgent.Infrastructure.Persistence;
using TripsAgent.Infrastructure.Storage;
using TripsAgent.IntegrationTests.Persistence;

namespace TripsAgent.IntegrationTests.Documents;

/// <summary>
/// The document routes (#46) through the real API host: who may list and reissue, and that both
/// download links hand over exactly the bytes that were issued — and nothing else.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class DocumentEndpointTests : IAsyncLifetime, IDisposable
{
    private const string OrderReference = "ORD-2026-000142";

    private readonly PostgresFixture _postgres;
    private readonly string _storageRoot = Path.Combine(Path.GetTempPath(), "tripsagent-document-api-tests", Guid.NewGuid().ToString("N"));

    private (string Key, string Value)[] _overrides = [];
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _api = null!;

    private string _database = string.Empty;
    private Guid _agencyId;
    private Guid _otherAgencyId;

    public DocumentEndpointTests(PostgresFixture postgres) => _postgres = postgres;

    public async Task InitializeAsync()
    {
        _database = $"documents_api_{Guid.NewGuid():N}";
        await SeedAsync();

        // Environment variables, because a configuration source added through the factory loses to
        // appsettings.Development.json. See RegistrationEndToEndTests.
        _overrides =
        [
            ("ConnectionStrings__Postgres", _postgres.ConnectionStringFor(_database, asApplicationRole: true)),
            ("ConnectionStrings__PostgresAdmin", _postgres.ConnectionStringFor(_database, asApplicationRole: false)),
            ("Storage__LocalRoot", _storageRoot),
        ];

        foreach (var (key, value) in _overrides)
        {
            Environment.SetEnvironmentVariable(key, value);
        }

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host => host.UseEnvironment("Development"));
        _api = _factory.CreateClient();

        // The Worker's half, run here directly: the API numbers and lists, but never renders.
        await IssueDocumentsAsync();
    }

    public async Task DisposeAsync()
    {
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }

        foreach (var (key, _) in _overrides)
        {
            Environment.SetEnvironmentVariable(key, null);
        }
    }

    public void Dispose() => _api?.Dispose();

    // ------------------------------------------------------------------ listing

    [Fact]
    public async Task The_bookings_documents_are_listed_each_with_a_signed_link_and_its_email()
    {
        var documents = await ListAsync();

        documents.Should().HaveCount(2);
        documents.Should().OnlyContain(d => d.Status == "Ready" && d.IssueNumber == 1 && d.DownloadUrl != null);
        documents.Select(d => d.DocumentType).Should().BeEquivalentTo("Invoice", "Voucher");
        documents.Single(d => d.DocumentType == "Voucher").ProductType.Should().Be("Flight");
        documents.Should().OnlyContain(d => d.Email != null && d.Email.Recipient == "ada.obi@example.test" && d.Email.Status == "queued");
    }

    [Fact]
    public async Task Another_agency_is_told_the_booking_does_not_exist()
    {
        using var response = await SendAsync(HttpMethod.Get, ListPath, _otherAgencyId, PermissionCodes.BookingSearch);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Listing_needs_the_booking_permission()
    {
        using var response = await SendAsync(HttpMethod.Get, ListPath, _agencyId, PermissionCodes.WalletView);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ------------------------------------------------------------------ downloading

    [Fact]
    public async Task The_signed_link_downloads_exactly_the_bytes_that_were_issued()
    {
        var invoice = (await ListAsync()).Single(d => d.DocumentType == "Invoice");

        // No Authorization header: the link is the credential, as it is for a PDF opened in a new tab.
        using var response = await _api.GetAsync(new Uri(invoice.DownloadUrl!, UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be(MediaTypes.Pdf);
        response.Content.Headers.ContentDisposition!.FileNameStar.Should().Be(invoice.FileName);

        var bytes = await response.Content.ReadAsByteArrayAsync();
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant().Should().Be(invoice.Checksum);
    }

    [Fact]
    public async Task A_link_with_its_signature_or_its_document_changed_is_refused()
    {
        var documents = await ListAsync();
        var invoice = documents.Single(d => d.DocumentType == "Invoice");
        var voucher = documents.Single(d => d.DocumentType == "Voucher");

        var tampered = invoice.DownloadUrl![..^4] + "AAAA";
        var redirected = invoice.DownloadUrl!.Replace(invoice.Id.ToString(), voucher.Id.ToString(), StringComparison.Ordinal);

        foreach (var link in new[] { tampered, redirected })
        {
            using var response = await _api.GetAsync(new Uri(link, UriKind.Relative));
            response.StatusCode.Should().Be(HttpStatusCode.Forbidden, link);
        }
    }

    [Fact]
    public async Task The_customers_link_serves_the_document_until_it_is_replaced()
    {
        var invoice = (await ListAsync()).Single(d => d.DocumentType == "Invoice");
        var customerLink = CustomerLinkFor(invoice.Id);

        using (var before = await _api.GetAsync(new Uri(customerLink, UriKind.Relative)))
        {
            before.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        using (var wrongToken = await _api.GetAsync(new Uri(customerLink[..^4] + "AAAA", UriKind.Relative)))
        {
            wrongToken.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }

        using (var reissue = await ReissueAsync(invoice.Id))
        {
            reissue.StatusCode.Should().Be(HttpStatusCode.Accepted);
        }

        // Gone, rather than a voucher the traveller should no longer use.
        using var after = await _api.GetAsync(new Uri(customerLink, UriKind.Relative));
        after.StatusCode.Should().Be(HttpStatusCode.Gone);
        (await after.Content.ReadAsStringAsync()).Should().NotContainEquivalentOf("Trips Agent");
    }

    // ------------------------------------------------------------------ reissuing

    [Fact]
    public async Task A_reissue_answers_with_issue_two_and_a_second_reissue_of_the_same_document_conflicts()
    {
        var invoice = (await ListAsync()).Single(d => d.DocumentType == "Invoice");

        using var first = await ReissueAsync(invoice.Id);
        first.StatusCode.Should().Be(HttpStatusCode.Accepted);

        var reissued = await first.Content.ReadFromJsonAsync<BookingDocumentResponse>();
        reissued!.IssueNumber.Should().Be(2);
        reissued.Status.Should().Be("Pending");
        reissued.SupersedesDocumentNumber.Should().Be(invoice.DocumentNumber);
        reissued.DownloadUrl.Should().BeNull("there is no file until the Worker renders it");

        using var second = await ReissueAsync(invoice.Id);
        second.StatusCode.Should().Be(HttpStatusCode.Conflict);

        var listed = await ListAsync();
        listed.Single(d => d.Id == invoice.Id).SupersededByDocumentNumber.Should().Be(reissued.DocumentNumber);
    }

    [Fact]
    public async Task Reissuing_needs_the_issue_permission()
    {
        var invoice = (await ListAsync()).Single(d => d.DocumentType == "Invoice");

        using var response = await SendAsync(
            HttpMethod.Post, $"/api/v1/documents/{invoice.Id}/reissue", _agencyId, PermissionCodes.BookingSearch);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ------------------------------------------------------------------ helpers

    private static string ListPath => $"/api/v1/documents?orderReference={OrderReference}";

    private async Task<List<BookingDocumentResponse>> ListAsync()
    {
        using var response = await SendAsync(HttpMethod.Get, ListPath, _agencyId, PermissionCodes.BookingSearch);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        return (await response.Content.ReadFromJsonAsync<List<BookingDocumentResponse>>())!;
    }

    private Task<HttpResponseMessage> ReissueAsync(Guid documentId) =>
        SendAsync(HttpMethod.Post, $"/api/v1/documents/{documentId}/reissue", _agencyId, PermissionCodes.BookingIssue);

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, Guid agencyId, params string[] permissions)
    {
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TokenFor(agencyId, permissions));
        return await _api.SendAsync(request);
    }

    /// <summary>A real token, signed by the API's own issuer, carrying exactly <paramref name="permissions"/>.</summary>
    private string TokenFor(Guid agencyId, IReadOnlyCollection<string> permissions)
    {
        var issuer = _factory.Services.GetRequiredService<IAccessTokenIssuer>();
        var user = User.ForAgency(agencyId, "counter@lagos-travel.test", "not-a-real-hash", "Ada", "Obi");

        return issuer.Issue(user, ["Agent"], permissions, agencyId).Value;
    }

    /// <summary>The customer's link, as the storefront will be given it — signed by the API's own key.</summary>
    private string CustomerLinkFor(Guid documentId)
    {
        using var scope = _factory.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<DocumentLinks>().PublicPathFor(documentId);
    }

    private async Task SeedAsync()
    {
        var tenancy = TestTenancy.None();

        await using var setup = await _postgres.CreateEmptyDatabaseAsync(_database, tenancy.Tenant, tenancy.Scope);
        await setup.Database.MigrateAsync();
        await NotificationTemplateSeeder.EnsureAsync(setup, TimeProvider.System);

        using var _ = tenancy.Scope.Enter("test setup — an agency with a confirmed order, and another agency");

        var lagos = Agency.RegisterPrincipal(
            "Lagos Travel Services Limited", "lagos-travel", "NG", "NGN", "Africa/Lagos", tradingName: "Lagos Travel");
        var abuja = Agency.RegisterPrincipal("Abuja Tours Limited", "abuja-tours", "NG", "NGN", "Africa/Lagos");

        setup.Agencies.AddRange(lagos, abuja);
        setup.AgencySettings.AddRange(AgencySettings.CreateDefault(lagos), AgencySettings.CreateDefault(abuja));
        setup.AgencyBranding.Add(AgencyBranding.CreateDefault(lagos));
        await setup.SaveChangesAsync();

        _agencyId = lagos.Id;
        _otherAgencyId = abuja.Id;

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
        var order = Order.Place(lagos.Id, OrderReference, "NGN", BuyerType.AgentAssisted, OrderChannel.Console, null, [line], now);
        setup.Orders.Add(order);
        setup.OrderTravellers.Add(OrderTraveller.Record(lagos.Id, line.Id, TravellerType.Adult, "Ada", "Obi"));
        await setup.SaveChangesAsync();

        line.RecordFulfilment(FulfilmentStatus.Confirmed, now);
        order.ChangeStatus(OrderStatus.Confirmed, now);
        await setup.SaveChangesAsync();
    }

    private async Task IssueDocumentsAsync()
    {
        var tenancy = TestTenancy.For(_agencyId);
        await using var db = _postgres.Connect(_database, tenancy.Tenant, tenancy.Scope);

        // The same directory the API's local storage reads from, set through Storage__LocalRoot.
        var storage = new LocalFileBlobStorage(new LocalBlobStorageOptions { RootPath = _storageRoot });

        var service = new OrderDocumentService(
            db,
            tenancy.Tenant,
            new DocumentNumberAllocator(db, tenancy.Tenant, TimeProvider.System),
            new EfTransactionRunner(db),
            new PostgresUniqueViolationDetector(),
            new QuestPdfDocumentRenderer(LicenseType.Community),
            storage,
            new AgencyLogoSource(db, storage, NullLogger<AgencyLogoSource>.Instance),
            new SupplierBookingReader(db),
            new Notifier(db, new EfOutbox(db, TimeProvider.System)),
            TimeProvider.System,
            NullLogger<OrderDocumentService>.Instance);

        var order = await db.Orders.AsNoTracking().SingleAsync();

        (await service.IssueForOrderAsync(new OrderDocumentsRequested(_agencyId, order.Id, "Ada Obi", "ada.obi@example.test")))
            .Should().Be(DocumentRunOutcome.Completed);
    }
}
