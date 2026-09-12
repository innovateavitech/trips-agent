using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TripsAgent.Application.Identity;
using TripsAgent.Contracts.Catalog;
using TripsAgent.Domain.Catalog;
using TripsAgent.Domain.Common;
using TripsAgent.Domain.Identity;
using TripsAgent.Domain.Orders;
using TripsAgent.Domain.Pricing;
using TripsAgent.Domain.Tenancy;
using TripsAgent.IntegrationTests.Persistence;

namespace TripsAgent.IntegrationTests.Catalog;

/// <summary>
/// The group departures API (#57) through the real host, against the departures contract in the
/// build plan's F6 section — routes, permissions, tenant isolation, the version guard, and what
/// cancelling does to the bookings on a departure (decision 12).
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class DepartureEndpointTests : IAsyncLifetime, IDisposable
{
    private const string Departures = "/api/v1/catalog/departures";

    private static readonly string[] Everything =
        [PermissionCodes.CatalogView, PermissionCodes.CatalogEdit, PermissionCodes.CatalogPublish];

    private readonly PostgresFixture _postgres;

    private (string Key, string Value)[] _overrides = [];
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _api = null!;
    private string _database = string.Empty;

    private Guid _agencyA;
    private Guid _agencyB;
    private Guid _tourA;
    private Guid _visaA;
    private Guid _tourB;

    public DepartureEndpointTests(PostgresFixture postgres) => _postgres = postgres;

    private static DateOnly Leaves => DateOnly.FromDateTime(DateTime.UtcNow).AddDays(180);

    public async Task InitializeAsync()
    {
        _database = $"departures_api_{Guid.NewGuid():N}";

        await using (var setup = await _postgres.CreateEmptyDatabaseAsync(_database))
        {
            await setup.Database.MigrateAsync();

            var a = Agency.RegisterPrincipal("Lagos Travel Limited", "lagos-travel", "NG", "NGN", "Africa/Lagos");
            var b = Agency.RegisterPrincipal("Abuja Tours Limited", "abuja-tours", "NG", "NGN", "Africa/Lagos");

            var tourA = Product.CreateDraft(a.Id, Tour("Kilimanjaro Trek"), "kilimanjaro");
            var visaA = Product.CreateDraft(a.Id, Visa(), "schengen-visa");
            var tourB = Product.CreateDraft(b.Id, Tour("Obudu Ranch"), "obudu-ranch");

            setup.Agencies.AddRange(a, b);
            setup.Products.AddRange(tourA, visaA, tourB);
            await setup.SaveChangesAsync();

            (_agencyA, _agencyB) = (a.Id, b.Id);
            (_tourA, _visaA, _tourB) = (tourA.Id, visaA.Id, tourB.Id);
        }

        _overrides =
        [
            ("ConnectionStrings__Postgres", _postgres.ConnectionStringFor(_database, asApplicationRole: true)),
            ("ConnectionStrings__PostgresAdmin", _postgres.ConnectionStringFor(_database, asApplicationRole: false)),
        ];

        foreach (var (key, value) in _overrides)
        {
            Environment.SetEnvironmentVariable(key, value);
        }

        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(host => host.UseEnvironment("Development"));

        _api = _factory.CreateClient();
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

    public void Dispose()
    {
        _api?.Dispose();
        _api = null!;
    }

    // ------------------------------------------------------------------ permissions

    [Fact]
    public async Task Without_a_token_nothing_is_answered()
    {
        using var response = await _api.GetAsync(Departures);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Reading_needs_catalog_view()
    {
        var departure = await CreateAsync();

        (await StatusOf(HttpMethod.Get, Departures, null, PermissionCodes.CatalogEdit)).Should().Be(HttpStatusCode.Forbidden);
        (await StatusOf(HttpMethod.Get, $"{Departures}/{departure.Id}", null, PermissionCodes.CatalogEdit)).Should().Be(HttpStatusCode.Forbidden);
        (await StatusOf(HttpMethod.Get, $"{Departures}/{departure.Id}/manifest", null, PermissionCodes.CatalogEdit)).Should().Be(HttpStatusCode.Forbidden);
        (await StatusOf(HttpMethod.Get, $"{Departures}/{departure.Id}/waitlist", null, PermissionCodes.CatalogEdit)).Should().Be(HttpStatusCode.Forbidden);

        (await StatusOf(HttpMethod.Get, Departures, null, PermissionCodes.CatalogView)).Should().Be(HttpStatusCode.OK);
        (await StatusOf(HttpMethod.Get, $"{Departures}/{departure.Id}", null, PermissionCodes.CatalogView)).Should().Be(HttpStatusCode.OK);
        (await StatusOf(HttpMethod.Get, $"{Departures}/{departure.Id}/manifest", null, PermissionCodes.CatalogView)).Should().Be(HttpStatusCode.OK);
        (await StatusOf(HttpMethod.Get, $"{Departures}/{departure.Id}/waitlist", null, PermissionCodes.CatalogView)).Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Creating_and_saving_need_catalog_edit()
    {
        var departure = await CreateAsync();
        string[] readAndPublish = [PermissionCodes.CatalogView, PermissionCodes.CatalogPublish];

        (await StatusOf(HttpMethod.Post, $"/api/v1/catalog/products/{_tourA}/departures", Request(), readAndPublish))
            .Should().Be(HttpStatusCode.Forbidden);
        (await StatusOf(HttpMethod.Put, $"{Departures}/{departure.Id}", Request(version: departure.Version), readAndPublish))
            .Should().Be(HttpStatusCode.Forbidden);

        (await StatusOf(HttpMethod.Post, $"/api/v1/catalog/products/{_tourA}/departures", Request(), PermissionCodes.CatalogEdit))
            .Should().Be(HttpStatusCode.Created);
        (await StatusOf(HttpMethod.Put, $"{Departures}/{departure.Id}", Request(version: departure.Version), PermissionCodes.CatalogEdit))
            .Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Closing_reopening_and_cancelling_need_catalog_publish()
    {
        var departure = await CreateAsync();
        string[] readAndEdit = [PermissionCodes.CatalogView, PermissionCodes.CatalogEdit];

        foreach (var action in new[] { "close", "reopen", "cancel" })
        {
            (await StatusOf(HttpMethod.Post, $"{Departures}/{departure.Id}/{action}", null, readAndEdit))
                .Should().Be(HttpStatusCode.Forbidden, $"{action} changes what travellers can buy");
        }

        (await StatusOf(HttpMethod.Post, $"{Departures}/{departure.Id}/close", null, PermissionCodes.CatalogPublish)).Should().Be(HttpStatusCode.OK);
        (await StatusOf(HttpMethod.Post, $"{Departures}/{departure.Id}/reopen", null, PermissionCodes.CatalogPublish)).Should().Be(HttpStatusCode.OK);
        (await StatusOf(HttpMethod.Post, $"{Departures}/{departure.Id}/cancel", null, PermissionCodes.CatalogPublish)).Should().Be(HttpStatusCode.OK);
    }

    // ------------------------------------------------------------------ tenant isolation

    [Fact]
    public async Task Another_agencys_departure_does_not_exist_as_far_as_you_can_tell()
    {
        var departure = await CreateAsync();

        (await StatusOf(HttpMethod.Get, $"{Departures}/{departure.Id}", null, _agencyB, Everything)).Should().Be(HttpStatusCode.NotFound);
        (await StatusOf(HttpMethod.Put, $"{Departures}/{departure.Id}", Request(version: 1), _agencyB, Everything)).Should().Be(HttpStatusCode.NotFound);
        (await StatusOf(HttpMethod.Get, $"{Departures}/{departure.Id}/manifest", null, _agencyB, Everything)).Should().Be(HttpStatusCode.NotFound);
        (await StatusOf(HttpMethod.Get, $"{Departures}/{departure.Id}/waitlist", null, _agencyB, Everything)).Should().Be(HttpStatusCode.NotFound);

        foreach (var action in new[] { "close", "reopen", "cancel" })
        {
            (await StatusOf(HttpMethod.Post, $"{Departures}/{departure.Id}/{action}", null, _agencyB, Everything))
                .Should().Be(HttpStatusCode.NotFound);
        }

        (await ListAsync(_agencyB)).Should().BeEmpty();
        (await GetAsync(departure.Id)).Status.Should().Be("Open", "nothing agency B sent touched it");
    }

    [Fact]
    public async Task A_departure_cannot_be_hung_on_another_agencys_product()
    {
        (await StatusOf(HttpMethod.Post, $"/api/v1/catalog/products/{_tourB}/departures", Request(), _agencyA, Everything))
            .Should().Be(HttpStatusCode.NotFound);
    }

    // ------------------------------------------------------------------ the contract

    [Fact]
    public async Task A_created_departure_comes_back_with_everything_the_server_works_out()
    {
        var departure = await CreateAsync();

        departure.ProductId.Should().Be(_tourA);
        departure.ProductTitle.Should().Be("Kilimanjaro Trek");
        departure.Currency.Should().Be("NGN");
        departure.Status.Should().Be("Open");
        departure.CapacityReserved.Should().Be(0);
        departure.CapacityConfirmed.Should().Be(0);
        departure.SeatsLeft.Should().Be(20);
        departure.WaitlistCount.Should().Be(0);
        departure.Version.Should().Be(1);
        departure.DepositType.Should().Be("Percent");
        departure.DepositPercentBasisPoints.Should().Be(2_500);
        departure.PriceTiers.Should().HaveCount(2);
        departure.Installments.Select(item => item.DueBasis).Should().AllBe("BeforeDeparture");

        // 14 days before it leaves, at midnight in Lagos — which is 23:00 UTC the day before.
        departure.CutoffAt.Should().Be(
            new DateTimeOffset(Leaves.AddDays(-15).ToDateTime(new TimeOnly(23, 0)), TimeSpan.Zero));
    }

    [Fact]
    public async Task A_departure_that_always_runs_is_guaranteed_from_the_start()
    {
        var departure = await CreateAsync(Request() with { IsGroupDeparture = false, MinPax = 6 });

        departure.Status.Should().Be("Guaranteed");
        departure.MinPax.Should().Be(1, "a departure that always runs has no minimum");
    }

    [Fact]
    public async Task The_list_filters_by_product_and_by_date()
    {
        var soon = await CreateAsync(Request(leaves: Leaves));
        var later = await CreateAsync(Request(leaves: Leaves.AddDays(30)));

        (await ListAsync(_agencyA)).Select(row => row.Id).Should().Equal(soon.Id, later.Id);
        (await ListAsync(_agencyA, $"?productId={_tourA}")).Should().HaveCount(2);
        (await ListAsync(_agencyA, $"?productId={Guid.NewGuid()}")).Should().BeEmpty();
        (await ListAsync(_agencyA, $"?from={Leaves.AddDays(1):yyyy-MM-dd}")).Select(row => row.Id).Should().Equal(later.Id);
    }

    [Fact]
    public async Task A_visa_has_no_departures()
    {
        using var response = await SendAsync(
            HttpMethod.Post, $"/api/v1/catalog/products/{_visaA}/departures", Request(), _agencyA, Everything);

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task Everything_wrong_comes_back_at_once_keyed_by_its_field()
    {
        using var response = await SendAsync(
            HttpMethod.Post,
            $"/api/v1/catalog/products/{_tourA}/departures",
            Request() with
            {
                DepartureDate = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-1),
                CapacityTotal = 0,
                PriceTiers = [],
                Installments = [new InstallmentRequest(1, "BeforeDeparture", 30, 4_000)],
            },
            _agencyA,
            Everything);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var fields = ErrorFields(await JsonAsync(response));
        fields.Should().Contain(["departureDate", "capacityTotal", "priceTiers", "installments"]);
    }

    [Fact]
    public async Task A_save_replaces_the_whole_departure_and_bumps_the_version()
    {
        var departure = await CreateAsync();

        var saved = await SaveAsync(
            departure.Id,
            Request(version: departure.Version) with
            {
                CapacityTotal = 30,
                DepositType = "None",
                DepositPercentBasisPoints = null,
                PriceTiers = [new PriceTierRequest(1, null, 80_000)],
                Installments = [],
            });

        saved.Version.Should().Be(2);
        saved.CapacityTotal.Should().Be(30);
        saved.SeatsLeft.Should().Be(30);
        saved.DepositType.Should().Be("None");
        saved.DepositPercentBasisPoints.Should().BeNull();
        saved.PriceTiers.Should().ContainSingle();
        saved.Installments.Should().BeEmpty();
    }

    [Fact]
    public async Task A_stale_version_is_refused_rather_than_undoing_somebody_elses_work()
    {
        var departure = await CreateAsync();
        await SaveAsync(departure.Id, Request(version: departure.Version));

        using var response = await SendAsync(
            HttpMethod.Put, $"{Departures}/{departure.Id}", Request(version: departure.Version), _agencyA, Everything);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await GetAsync(departure.Id)).Version.Should().Be(2);
    }

    [Fact]
    public async Task Closing_stops_new_bookings_reopening_gives_back_the_seats_status_and_neither_can_be_repeated()
    {
        var departure = await CreateAsync();

        (await ActAsync(departure.Id, "close")).Status.Should().Be("Closed");
        (await StatusOf(HttpMethod.Post, $"{Departures}/{departure.Id}/close", null, _agencyA, Everything))
            .Should().Be(HttpStatusCode.Conflict);

        (await ActAsync(departure.Id, "reopen")).Status.Should().Be("Open");
        (await StatusOf(HttpMethod.Post, $"{Departures}/{departure.Id}/reopen", null, _agencyA, Everything))
            .Should().Be(HttpStatusCode.Conflict, "it is not closed");
    }

    [Fact]
    public async Task A_cancelled_departure_can_never_be_edited_or_cancelled_again()
    {
        var departure = await CreateAsync();

        (await ActAsync(departure.Id, "cancel")).Status.Should().Be("Cancelled");

        (await StatusOf(HttpMethod.Post, $"{Departures}/{departure.Id}/cancel", null, _agencyA, Everything))
            .Should().Be(HttpStatusCode.Conflict);
        (await StatusOf(HttpMethod.Put, $"{Departures}/{departure.Id}", Request(version: 2), _agencyA, Everything))
            .Should().Be(HttpStatusCode.Conflict);
    }

    // ------------------------------------------------------------------ the waitlist

    [Fact]
    public async Task Somebody_joins_the_waitlist_once_however_they_capitalised_their_address()
    {
        var departure = await CreateAsync();

        var joined = await JoinAsync(departure.Id, new JoinWaitlistRequest("Ada Obi", "ada@example.test", 2));
        joined.Status.Should().Be("Waiting");
        joined.PaxCount.Should().Be(2);
        joined.OfferedAt.Should().BeNull();

        var again = await JoinAsync(departure.Id, new JoinWaitlistRequest("Ada Obi", "ada@example.test", 3));
        again.Id.Should().Be(joined.Id, "joining again keeps the place in the queue they already have");

        (await WaitlistAsync(departure.Id)).Should().ContainSingle();
        (await GetAsync(departure.Id)).WaitlistCount.Should().Be(1);
    }

    [Fact]
    public async Task A_waitlist_entry_with_no_name_or_no_email_is_refused()
    {
        var departure = await CreateAsync();

        (await StatusOf(HttpMethod.Post, $"{Departures}/{departure.Id}/waitlist", new JoinWaitlistRequest(" ", "ada@example.test", 1), _agencyA, Everything))
            .Should().Be(HttpStatusCode.BadRequest);
        (await StatusOf(HttpMethod.Post, $"{Departures}/{departure.Id}/waitlist", new JoinWaitlistRequest("Ada", " ", 1), _agencyA, Everything))
            .Should().Be(HttpStatusCode.BadRequest);
        (await StatusOf(HttpMethod.Post, $"{Departures}/{departure.Id}/waitlist", new JoinWaitlistRequest("Ada", "ada@example.test", 0), _agencyA, Everything))
            .Should().Be(HttpStatusCode.BadRequest);
    }

    // ------------------------------------------------------------------ the manifest and decision 12

    [Fact]
    public async Task The_manifest_names_everybody_on_the_departure_and_says_whether_they_are_paid()
    {
        var departure = await CreateAsync();
        await BookAsync(departure.Id, "Ada", "Obi", room: "Twin 3");

        var manifest = await ManifestAsync(departure.Id);

        manifest.Should().ContainSingle();
        manifest[0].TravellerName.Should().Be("Ada Obi");
        manifest[0].PaxType.Should().Be("Adult");
        manifest[0].Room.Should().Be("Twin 3");
        manifest[0].Status.Should().Be("Confirmed");
    }

    [Fact]
    public async Task Cancelling_puts_every_paid_booking_on_the_resolution_queue_as_a_full_refund()
    {
        var departure = await CreateAsync();
        var lineId = await BookAsync(departure.Id, "Ada", "Obi");

        (await ActAsync(departure.Id, "cancel")).Status.Should().Be("Cancelled");

        var tenancy = TestTenancy.For(_agencyA);
        await using var db = _postgres.Connect(_database, tenancy.Tenant, tenancy.Scope);

        var line = await db.OrderLines.SingleAsync(candidate => candidate.Id == lineId);

        line.FulfilmentStatus.Should().Be(FulfilmentStatus.FailedNeedsResolution);
        line.ResolutionStatus.Should().Be(ResolutionStatus.Open);
        line.FailureReason.Should().Contain("Refund the traveller in full");
    }

    // ------------------------------------------------------------------ helpers

    private static DepartureRequest Request(DateOnly? leaves = null, int version = 0) => new(
        leaves ?? Leaves,
        IsGroupDeparture: true,
        MinPax: 6,
        CapacityTotal: 20,
        CutoffDaysBefore: 14,
        DepositType: "Percent",
        DepositPercentBasisPoints: 2_500,
        DepositAmountMinor: null,
        PriceTiers: [new PriceTierRequest(1, 3, 100_000), new PriceTierRequest(4, null, 90_000)],
        Installments:
        [
            new InstallmentRequest(1, "BeforeDeparture", 90, 5_000),
            new InstallmentRequest(2, "BeforeDeparture", 30, 5_000),
        ],
        Version: version);

    private async Task<DepartureResponse> CreateAsync(DepartureRequest? request = null)
    {
        using var response = await SendAsync(
            HttpMethod.Post, $"/api/v1/catalog/products/{_tourA}/departures", request ?? Request(), _agencyA, Everything);

        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        response.Headers.Location.Should().NotBeNull();

        return (await response.Content.ReadFromJsonAsync<DepartureResponse>())!;
    }

    private async Task<DepartureResponse> SaveAsync(Guid departureId, DepartureRequest request)
    {
        using var response = await SendAsync(HttpMethod.Put, $"{Departures}/{departureId}", request, _agencyA, Everything);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<DepartureResponse>())!;
    }

    private async Task<DepartureResponse> GetAsync(Guid departureId)
    {
        using var response = await SendAsync(HttpMethod.Get, $"{Departures}/{departureId}", null, _agencyA, Everything);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<DepartureResponse>())!;
    }

    private async Task<DepartureResponse> ActAsync(Guid departureId, string action)
    {
        using var response = await SendAsync(HttpMethod.Post, $"{Departures}/{departureId}/{action}", null, _agencyA, Everything);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<DepartureResponse>())!;
    }

    private async Task<List<DepartureResponse>> ListAsync(Guid agencyId, string query = "")
    {
        using var response = await SendAsync(HttpMethod.Get, $"{Departures}{query}", null, agencyId, Everything);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<List<DepartureResponse>>())!;
    }

    private async Task<List<ManifestEntryResponse>> ManifestAsync(Guid departureId)
    {
        using var response = await SendAsync(HttpMethod.Get, $"{Departures}/{departureId}/manifest", null, _agencyA, Everything);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<List<ManifestEntryResponse>>())!;
    }

    private async Task<List<WaitlistEntryResponse>> WaitlistAsync(Guid departureId)
    {
        using var response = await SendAsync(HttpMethod.Get, $"{Departures}/{departureId}/waitlist", null, _agencyA, Everything);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<List<WaitlistEntryResponse>>())!;
    }

    private async Task<WaitlistEntryResponse> JoinAsync(Guid departureId, JoinWaitlistRequest request)
    {
        using var response = await SendAsync(HttpMethod.Post, $"{Departures}/{departureId}/waitlist", request, _agencyA, Everything);
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<WaitlistEntryResponse>())!;
    }

    /// <summary>
    /// A paid, confirmed booking of a seat on the departure, written straight to the database:
    /// customer commerce (F5) is not built yet, so nothing sells one over HTTP.
    /// </summary>
    /// <returns>The order line that bought the seat.</returns>
    private async Task<Guid> BookAsync(Guid departureId, string firstName, string lastName, string? room = null)
    {
        var tenancy = TestTenancy.For(_agencyA);
        await using var db = _postgres.Connect(_database, tenancy.Tenant, tenancy.Scope);

        var now = DateTimeOffset.UtcNow;

        var rule = MarkupRule.Create(_agencyA, new MarkupRuleTerms
        {
            Scope = MarkupScope.Global,
            Currency = "NGN",
            CalculationType = MarkupCalculationType.Percentage,
            PercentBasisPoints = 1_000,
            EffectiveFrom = now.AddDays(-1),
        });
        db.MarkupRules.Add(rule);
        await db.SaveChangesAsync();

        var quote = PriceQuote.Record(
            _agencyA,
            new PricingSubject(PricedProductType.GroupDeparture, "NGN"),
            new PriceBreakdown(
                new Money(100_000), new Money(10_000), new Money(750), new Money(500), new Money(110_750),
                "NGN", new MarkupRuleDefinition(rule.Id, _agencyA, rule.Terms), false, 750, 0),
            now,
            TimeSpan.FromMinutes(30));
        db.PriceQuotes.Add(quote);
        await db.SaveChangesAsync();

        var line = OrderLine.FromQuote(quote, "Kilimanjaro Trek", """{"adults":1}""", now);
        var order = Order.Place(
            _agencyA, $"ORD-2026-{Random.Shared.Next(100_000, 999_999)}", "NGN",
            BuyerType.AgentAssisted, OrderChannel.Console, null, [line], now);

        var traveller = OrderTraveller.Record(_agencyA, line.Id, TravellerType.Adult, firstName, lastName);

        db.Orders.Add(order);
        db.OrderTravellers.Add(traveller);
        await db.SaveChangesAsync();

        line.RecordFulfilment(FulfilmentStatus.Confirmed, now);
        order.ChangeStatus(OrderStatus.Confirmed, now);
        db.PaxManifests.Add(PaxManifestEntry.Create(_agencyA, departureId, line.Id, traveller.Id, room, now));
        await db.SaveChangesAsync();

        return line.Id;
    }

    private async Task<HttpStatusCode> StatusOf(HttpMethod method, string path, object? body, params string[] permissions) =>
        await StatusOf(method, path, body, _agencyA, permissions);

    private async Task<HttpStatusCode> StatusOf(HttpMethod method, string path, object? body, Guid agencyId, params string[] permissions)
    {
        using var response = await SendAsync(method, path, body, agencyId, permissions);
        return response.StatusCode;
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string path,
        object? body,
        Guid agencyId,
        params string[] permissions)
    {
        using var request = new HttpRequestMessage(method, path);

        if (body is not null)
        {
            request.Content = JsonContent.Create(body, body.GetType());
        }

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TokenFor(agencyId, permissions));

        return await _api.SendAsync(request);
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    private static List<string> ErrorFields(JsonElement body) =>
        body.GetProperty("errors").EnumerateObject().Select(property => property.Name).ToList();

    private string TokenFor(Guid agencyId, IReadOnlyCollection<string> permissions)
    {
        var issuer = _factory.Services.GetRequiredService<IAccessTokenIssuer>();
        var user = User.ForAgency(agencyId, "departures@lagos-travel.test", "not-a-real-hash", "Ada", "Obi");

        return issuer.Issue(user, ["Manager"], permissions, agencyId).Value;
    }

    private static ProductContent Tour(string title) => new()
    {
        ProductType = ProductType.Tour,
        Title = title,
        Summary = "Eight days on the Machame route.",
        Description = "Eight days on the Machame route, with a guide and a porter for every two climbers.",
        Currency = "NGN",
        BasePriceMinor = new Money(100_000),
    };

    private static ProductContent Visa() => new()
    {
        ProductType = ProductType.Visa,
        Title = "Schengen visa",
        Summary = "Short-stay visa handling.",
        Description = "We prepare and submit a short-stay Schengen application on your behalf.",
        Currency = "NGN",
        BasePriceMinor = new Money(50_000),
    };
}
