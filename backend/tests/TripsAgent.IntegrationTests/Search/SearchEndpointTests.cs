using System.Diagnostics.Metrics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TripsAgent.Application.Identity;
using TripsAgent.Application.Pricing;
using TripsAgent.Application.Search;
using TripsAgent.Application.Suppliers;
using TripsAgent.Application.Tenancy;
using TripsAgent.Contracts.Search;
using TripsAgent.Domain.Common;
using TripsAgent.Domain.Identity;
using TripsAgent.Domain.Pricing;
using TripsAgent.Domain.Suppliers;
using TripsAgent.Domain.Tenancy;
using TripsAgent.Infrastructure.Search;
using TripsAgent.IntegrationTests.Persistence;
using TripsAgent.IntegrationTests.Pricing;

namespace TripsAgent.IntegrationTests.Search;

/// <summary>
/// Search through the real API, database and Redis, with the supplier replaced by a fake that counts
/// every call — so "served from the cache" is proven by the supplier not being asked (#33, #34, #40).
/// </summary>
/// <remarks>
/// Margin assertions read the raw JSON, for the reason <c>PricingEndpointTests</c> gives: a typed
/// model silently drops fields it does not declare, so a leak would deserialise into a clean object.
/// </remarks>
[Collection(PostgresCollection.Name)]
public sealed class SearchEndpointTests : IClassFixture<RedisFixture>, IAsyncLifetime, IDisposable
{
    // ₦123,456.78 — distinctive, so searching a body for it cannot collide with anything else.
    private const long Net = 12_345_678;
    private const long TenPercent = 1_234_568;     // 10% of Net, rounded half up
    private const long TwentyPercent = 2_469_136;  // 20% of Net

    private static readonly DateOnly TravelDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(21));

    private readonly PostgresFixture _postgres;
    private readonly RedisFixture _redis;
    private readonly FakeSupplier _supplier = new();

    private (string Key, string Value)[] _overrides = [];
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _api = null!;
    private string _database = string.Empty;
    private Guid _agencyA;
    private Guid _agencyB;

    public SearchEndpointTests(PostgresFixture postgres, RedisFixture redis)
    {
        _postgres = postgres;
        _redis = redis;
    }

    public async Task InitializeAsync()
    {
        _database = $"search_api_{Guid.NewGuid():N}";

        await using (var setup = await _postgres.CreateEmptyDatabaseAsync(_database))
        {
            await setup.Database.MigrateAsync();

            var a = Agency.RegisterPrincipal("Lagos Travel Limited", "lagos-travel", "NG", "NGN", "Africa/Lagos");
            var b = Agency.RegisterPrincipal("Abuja Tours Limited", "abuja-tours", "NG", "NGN", "Africa/Lagos");
            setup.Agencies.AddRange(a, b);

            // What `migrate` seeds in every real environment; the test migrates with EF alone.
            setup.Suppliers.Add(Supplier.Register("trips_africa", "Trips Africa", SupplierKind.Multi, "https://supplier.test"));
            await setup.SaveChangesAsync();

            _agencyA = a.Id;
            _agencyB = b.Id;
        }

        // Environment variables, because a configuration source added through the factory loses to
        // appsettings.Development.json. See RegistrationEndToEndTests.
        _overrides =
        [
            ("ConnectionStrings__Postgres", _postgres.ConnectionStringFor(_database, asApplicationRole: true)),
            ("ConnectionStrings__PostgresAdmin", _postgres.ConnectionStringFor(_database, asApplicationRole: false)),
            ("ConnectionStrings__Redis", _redis.ConnectionString),
        ];

        foreach (var (key, value) in _overrides)
        {
            Environment.SetEnvironmentVariable(key, value);
        }

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host =>
        {
            host.UseEnvironment("Development");

            // The real adapters would call Trips Africa's staging API. These tests are about what the
            // API does with an answer, so the answer comes from here.
            host.ConfigureTestServices(services =>
            {
                services.RemoveAll<ISupplierAdapter>();
                services.AddSingleton<ISupplierAdapter>(_supplier);
            });
        });

        _api = _factory.CreateClient();

        await AddRuleAsync(_agencyA, MarkupScope.Global, basisPoints: 1_000);
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

    // ---------------------------------------------------------------------------- pricing

    [Fact]
    public async Task A_search_is_priced_with_the_agency_s_markup_on_the_supplier_s_net_rate()
    {
        using var response = await SearchFlightsAsync(_agencyA, PermissionCodes.BookingSearch, PermissionCodes.MarginView);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var price = json.RootElement.GetProperty("offers")[0].GetProperty("price");
        var margin = price.GetProperty("margin");

        margin.GetProperty("netMinor").GetInt64().Should().Be(Net);
        margin.GetProperty("markupMinor").GetInt64().Should().Be(TenPercent);
        price.GetProperty("sellMinor").GetInt64()
            .Should().Be(Net + TenPercent + margin.GetProperty("taxMinor").GetInt64());
    }

    [Fact]
    public async Task Without_margin_view_the_json_carries_the_sell_price_and_no_trace_of_the_margin()
    {
        using var response = await SearchFlightsAsync(_agencyA, PermissionCodes.BookingSearch);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var raw = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(raw);
        var price = json.RootElement.GetProperty("offers")[0].GetProperty("price");

        price.GetProperty("margin").ValueKind.Should().Be(JsonValueKind.Null);
        price.GetProperty("sellMinor").GetInt64().Should().BeGreaterThan(Net);

        raw.Should().NotContain(Net.ToString(CultureInfo.InvariantCulture), "the net rate is what Trips charges the agency")
            .And.NotContain(TenPercent.ToString(CultureInfo.InvariantCulture));
    }

    // ------------------------------------------------------------------------------ the cache

    [Fact]
    public async Task A_repeated_search_comes_from_the_cache_without_asking_the_supplier_again()
    {
        using var first = await SearchFlightsAsync(_agencyA, PermissionCodes.BookingSearch);
        using var second = await SearchFlightsAsync(_agencyA, PermissionCodes.BookingSearch);

        (await FromCacheAsync(first)).Should().BeFalse();
        (await FromCacheAsync(second)).Should().BeTrue();
        _supplier.Searches.Should().ContainSingle("the second search was answered without the supplier");
    }

    [Fact]
    public async Task A_markup_change_prices_the_very_next_search_even_one_served_from_the_cache()
    {
        using (await SearchFlightsAsync(_agencyA, PermissionCodes.BookingSearch))
        {
        }

        await AddRuleAsync(_agencyA, MarkupScope.ProductType, basisPoints: 2_000);

        using var after = await SearchFlightsAsync(_agencyA, PermissionCodes.BookingSearch, PermissionCodes.MarginView);
        using var json = JsonDocument.Parse(await after.Content.ReadAsStringAsync());

        json.RootElement.GetProperty("fromCache").GetBoolean().Should().BeTrue("the net rates were reused");
        json.RootElement.GetProperty("offers")[0].GetProperty("price").GetProperty("margin").GetProperty("markupMinor").GetInt64()
            .Should().Be(TwentyPercent, "markup is applied as the result is read, never cached with it");
        _supplier.Searches.Should().ContainSingle();
    }

    [Fact]
    public async Task Another_agency_is_never_served_the_first_agency_s_cached_search()
    {
        using (await SearchFlightsAsync(_agencyA, PermissionCodes.BookingSearch))
        {
        }

        using var theirs = await SearchFlightsAsync(_agencyB, PermissionCodes.BookingSearch);

        theirs.StatusCode.Should().Be(HttpStatusCode.OK);
        (await FromCacheAsync(theirs)).Should().BeFalse("the same criteria, but a different agency's search");
        _supplier.Searches.Should().HaveCount(2);
        RedisSearchResultCache.KeyFor(_agencyA, "same").Should().NotBe(RedisSearchResultCache.KeyFor(_agencyB, "same"));
    }

    [Fact]
    public async Task Every_lookup_is_counted_as_a_hit_or_a_miss()
    {
        var results = new List<string>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, subscription) =>
        {
            if (instrument.Meter.Name == SupplierSearchService.MeterName)
            {
                subscription.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            foreach (var tag in tags)
            {
                if (tag.Key == "result")
                {
                    lock (results)
                    {
                        results.Add((string)tag.Value!);
                    }
                }
            }
        });
        listener.Start();

        using (await SearchFlightsAsync(_agencyA, PermissionCodes.BookingSearch))
        using (await SearchFlightsAsync(_agencyA, PermissionCodes.BookingSearch))
        {
        }

        results.Should().Equal("miss", "hit");
    }

    // ------------------------------------------------------------------------------ records

    [Fact]
    public async Task A_search_is_recorded_for_its_agency_and_invisible_to_any_other()
    {
        using (await SearchFlightsAsync(_agencyA, PermissionCodes.BookingSearch))
        {
        }

        var a = TestTenancy.For(_agencyA);
        await using (var asA = _postgres.Connect(_database, a.Tenant, a.Scope))
        {
            (await asA.SearchRequests.SingleAsync()).ResultCount.Should().Be(1);
            (await asA.SupplierOffers.CountAsync()).Should().Be(1);
            (await asA.FlightSegments.CountAsync()).Should().Be(1);
        }

        var b = TestTenancy.For(_agencyB);
        await using var asB = _postgres.Connect(_database, b.Tenant, b.Scope);
        (await asB.SearchRequests.CountAsync()).Should().Be(0);
        (await asB.SupplierOffers.CountAsync()).Should().Be(0);
    }

    // ------------------------------------------------------------------------------ failures

    [Fact]
    public async Task A_supplier_that_does_not_answer_is_a_503_that_says_nothing_was_booked()
    {
        _supplier.Failure = new SupplierUnavailableException("Trips Africa did not answer.");

        using var response = await SearchFlightsAsync(_agencyA, PermissionCodes.BookingSearch);

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await response.Content.ReadAsStringAsync()).Should().Contain("Nothing has been booked or charged");

        var a = TestTenancy.For(_agencyA);
        await using var asA = _postgres.Connect(_database, a.Tenant, a.Scope);
        (await asA.SearchRequests.SingleAsync()).ErrorCode.Should().Be("supplier_unavailable", "failures feed the error-rate report");
    }

    [Fact]
    public async Task A_search_for_a_date_that_has_passed_is_refused_before_any_supplier_is_asked()
    {
        var yesterday = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-2));

        using var response = await PostAsync(
            "/api/v1/search/flights",
            new FlightSearchRequest("one_way", [new SearchLegRequest("LOS", "ABV", yesterday)], 1, 0, 0, null),
            _agencyA,
            PermissionCodes.BookingSearch);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        _supplier.Searches.Should().BeEmpty();
    }

    [Fact]
    public async Task Searching_needs_booking_search()
    {
        using var response = await SearchFlightsAsync(_agencyA);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        _supplier.Searches.Should().BeEmpty();
    }

    // ----------------------------------------------------------------------------------- bus

    [Fact]
    public async Task A_bus_return_is_searched_as_two_one_way_trips_and_comes_back_as_two_legs()
    {
        using var response = await PostAsync(
            "/api/v1/search/buses",
            new BusSearchRequest("round_trip", "60", "51", TravelDate, TravelDate.AddDays(3), 1),
            _agencyA,
            PermissionCodes.BookingSearch);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        _supplier.Searches.Should().HaveCount(2)
            .And.OnlyContain(query => query.Legs.Count == 1 && query.TripShape == SupplierTripShape.OneWay);
        _supplier.Searches[1].Legs[0].Origin.Should().Be("51", "the return leaves from where the outbound arrived");

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("offers").EnumerateArray().Select(offer => offer.GetProperty("leg").GetInt32())
            .Should().Equal(0, 1);
        json.RootElement.GetProperty("offers")[0].GetProperty("busTrips")[0].GetProperty("vehicle").GetString()
            .Should().Be("Hiace");
    }

    // ------------------------------------------------------------------------------ helpers

    private Task<HttpResponseMessage> SearchFlightsAsync(Guid agencyId, params string[] permissions) =>
        PostAsync(
            "/api/v1/search/flights",
            new FlightSearchRequest("one_way", [new SearchLegRequest("LOS", "ABV", TravelDate)], 1, 0, 0, "economy"),
            agencyId,
            permissions);

    private async Task<HttpResponseMessage> PostAsync(string path, object body, Guid agencyId, params string[] permissions)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TokenFor(agencyId, permissions));
        return await _api.SendAsync(request);
    }

    private static async Task<bool> FromCacheAsync(HttpResponseMessage response)
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("fromCache").GetBoolean();
    }

    /// <summary>A real token, signed by the API's own issuer, carrying exactly these permissions.</summary>
    private string TokenFor(Guid agencyId, IReadOnlyCollection<string> permissions)
    {
        var issuer = _factory.Services.GetRequiredService<IAccessTokenIssuer>();
        var user = User.ForAgency(agencyId, "counter@lagos-travel.test", "not-a-real-hash", "Ada", "Obi");

        return issuer.Issue(user, ["Agent"], permissions, agencyId).Value;
    }

    private async Task AddRuleAsync(Guid agencyId, MarkupScope scope, int basisPoints)
    {
        await using var services = _factory.Services.CreateAsyncScope();
        services.ServiceProvider.GetRequiredService<TenantContext>().SetTenant(agencyId, agencyId);

        var outcome = await services.ServiceProvider.GetRequiredService<MarkupRuleService>().CreateAsync(new MarkupRuleTerms
        {
            Scope = scope,
            ProductType = scope == MarkupScope.ProductType ? PricedProductType.Flight : null,
            Currency = "NGN",
            CalculationType = MarkupCalculationType.Percentage,
            PercentBasisPoints = basisPoints,
            EffectiveFrom = DateTimeOffset.UtcNow,
        });

        outcome.Should().BeOfType<MarkupRuleChangeOutcome.Saved>();
    }

    /// <summary>
    /// A supplier that sells one offer per search, at <see cref="Net"/>, and remembers every search
    /// it was asked for — so a cache hit is proven by its absence here.
    /// </summary>
    private sealed class FakeSupplier : ISupplierAdapter
    {
        private readonly List<SupplierSearchQuery> _searches = [];

        public string SupplierCode => "trips_africa";

        public IReadOnlyCollection<SupplierProductType> Products { get; } = [SupplierProductType.Flight, SupplierProductType.Bus];

        public Exception? Failure { get; set; }

        public IReadOnlyList<SupplierSearchQuery> Searches
        {
            get
            {
                lock (_searches)
                {
                    return [.. _searches];
                }
            }
        }

        public Task<SupplierSearchResult> SearchAsync(
            SupplierCallContext context,
            SupplierSearchQuery query,
            CancellationToken cancellationToken = default)
        {
            lock (_searches)
            {
                _searches.Add(query);
            }

            if (Failure is not null)
            {
                throw Failure;
            }

            var leg = query.Legs[0];
            var departs = new DateTimeOffset(leg.DepartureDate.ToDateTime(new TimeOnly(7, 30)), TimeSpan.FromHours(1));

            var offer = query.ProductType == SupplierProductType.Flight
                ? new SupplierOfferQuote(
                    "5:2:0:7:-",
                    new SupplierOfferReference("5", "2", "0", "7"),
                    "NGN",
                    new Money(Net),
                    new Money(Net),
                    [new SupplierFlightSegmentQuote(
                        0, 0, "QI", null, "QI 0321", leg.Origin, leg.Destination, departs, departs.AddMinutes(75),
                        "Economy", "20 kg", "XOW", "Ibom Air", 75)],
                    [],
                    """{"supplier":"fake"}""",
                    null)
                : new SupplierOfferQuote(
                    "169:103:1:0:0",
                    new SupplierOfferReference("169", "103", "1", "0", 0),
                    "NGN",
                    new Money(Net),
                    new Money(Net),
                    [],
                    [new SupplierBusSegmentQuote(
                        "LIBRA Motors", leg.Origin, leg.Destination, departs, departs.AddHours(7), 12, ["1", "4"], "res-1", "Hiace")],
                    """{"supplier":"fake"}""",
                    null);

            return Task.FromResult(new SupplierSearchResult($"sess-{Guid.NewGuid():N}", null, null, [offer]));
        }

        public Task<SupplierPriceConfirmation> ConfirmPriceAsync(
            SupplierCallContext context, SupplierPriceConfirmationRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<SupplierIssueResult> IssueAsync(
            SupplierCallContext context, SupplierIssueRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<SupplierStatusResult> GetStatusAsync(
            SupplierCallContext context, SupplierStatusQuery query, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<SupplierFareRules> GetRulesAsync(
            SupplierCallContext context, SupplierRulesQuery query, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<SupplierCancellationResult> CancelAsync(
            SupplierCallContext context, SupplierCancellationRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
