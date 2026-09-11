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
using TripsAgent.Application.Pricing;
using TripsAgent.Application.Tenancy;
using TripsAgent.Contracts.Pricing;
using TripsAgent.Domain.Common;
using TripsAgent.Domain.Identity;
using TripsAgent.Domain.Pricing;
using TripsAgent.Domain.Tenancy;
using TripsAgent.Infrastructure.Persistence;
using TripsAgent.IntegrationTests.Persistence;

namespace TripsAgent.IntegrationTests.Pricing;

/// <summary>
/// Who sees margin, checked on the bytes that actually leave the server.
/// </summary>
/// <remarks>
/// <para>
/// Asserting on a deserialised <see cref="PriceQuoteResponse"/> would prove nothing: a typed model
/// silently drops any property it does not declare, so a response leaking the net rate would
/// deserialise into a perfectly clean object and the test would pass. The raw JSON is what a
/// curious counter agent's browser shows them, so the raw JSON is what is checked.
/// </para>
/// <para>
/// The figures are chosen to be distinctive — ₦1,234.57 net — so that searching the body for them
/// cannot collide with anything else in the response.
/// </para>
/// </remarks>
[Collection(PostgresCollection.Name)]
public sealed class PricingEndpointTests : IClassFixture<RedisFixture>, IAsyncLifetime, IDisposable
{
    private const long Net = 123_457;
    private const long Markup = 12_346;   // 10% of 123,457 kobo, rounded half up
    private const long Gross = Net + Markup;

    private static readonly string[] MarginFields = ["netAmountMinor", "markupAmountMinor", "markupRuleId"];

    private readonly PostgresFixture _postgres;
    private readonly RedisFixture _redis;

    private (string Key, string Value)[] _overrides = [];
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _api = null!;

    private Guid _agencyId;
    private Guid _ruleId;
    private Guid _quoteId;

    public PricingEndpointTests(PostgresFixture postgres, RedisFixture redis)
    {
        _postgres = postgres;
        _redis = redis;
    }

    public async Task InitializeAsync()
    {
        var database = $"pricing_api_{Guid.NewGuid():N}";

        await using (var setup = await _postgres.CreateEmptyDatabaseAsync(database))
        {
            await setup.Database.MigrateAsync();

            var agency = Agency.RegisterPrincipal("Lagos Travel Limited", "lagos-travel", "NG", "NGN", "Africa/Lagos");
            setup.Agencies.Add(agency);
            await setup.SaveChangesAsync();
            _agencyId = agency.Id;
        }

        // Environment variables, because a configuration source added through the factory loses to
        // appsettings.Development.json. See RegistrationEndToEndTests.
        _overrides =
        [
            // The API runs as the role row-level security polices, and migrates as the owner — the
            // same split as production (ADR-0006). A flow that only works as a superuser fails here.
            ("ConnectionStrings__Postgres", _postgres.ConnectionStringFor(database, asApplicationRole: true)),
            ("ConnectionStrings__PostgresAdmin", _postgres.ConnectionStringFor(database, asApplicationRole: false)),
            ("ConnectionStrings__Redis", _redis.ConnectionString),
        ];

        foreach (var (key, value) in _overrides)
        {
            Environment.SetEnvironmentVariable(key, value);
        }

        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(host => host.UseEnvironment("Development"));

        _api = _factory.CreateClient();

        // A rule and a quote, made through the application's own services exactly as a request
        // acting for the agency would make them.
        await using var scope = _factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().SetTenant(_agencyId, _agencyId);

        var created = await scope.ServiceProvider.GetRequiredService<MarkupRuleService>().CreateAsync(new MarkupRuleTerms
        {
            Scope = MarkupScope.Global,
            Currency = "NGN",
            CalculationType = MarkupCalculationType.Percentage,
            PercentBasisPoints = 1_000,
            EffectiveFrom = DateTimeOffset.UtcNow,
        });
        _ruleId = created.Should().BeOfType<MarkupRuleChangeOutcome.Saved>().Subject.Rule.Id;

        var quote = await scope.ServiceProvider.GetRequiredService<PricingService>().QuoteAsync(
            new PricingSubject(PricedProductType.Flight, "NGN", supplierCode: "trips_africa"), new Money(Net));
        _quoteId = quote.Id;
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

    // ------------------------------------------------------------------ quotes

    [Fact]
    public async Task Without_margin_view_the_quote_json_carries_the_price_and_nothing_else()
    {
        using var response = await GetAsync($"/api/v1/pricing/quotes/{_quoteId}", PermissionCodes.BookingSearch);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var raw = await response.Content.ReadAsStringAsync();

        using var json = JsonDocument.Parse(raw);
        var properties = json.RootElement.EnumerateObject().Select(property => property.Name).ToList();

        // Exactly these, so a margin field added under a name nobody thought to forbid fails too.
        properties.Should().BeEquivalentTo("id", "productType", "currency", "grossAmountMinor", "createdAt");
        properties.Should().NotContain(MarginFields);

        json.RootElement.GetProperty("grossAmountMinor").GetInt64().Should().Be(Gross);

        // Not under any name, nested or otherwise: the net rate, the markup and the rule id must
        // not appear anywhere in the body.
        raw.Should().NotContain(Net.ToString(System.Globalization.CultureInfo.InvariantCulture));
        raw.Should().NotContain(Markup.ToString(System.Globalization.CultureInfo.InvariantCulture));
        raw.Should().NotContain(_ruleId.ToString());
    }

    [Fact]
    public async Task With_margin_view_the_quote_json_carries_the_net_rate_markup_and_rule()
    {
        using var response = await GetAsync(
            $"/api/v1/pricing/quotes/{_quoteId}", PermissionCodes.BookingSearch, PermissionCodes.MarginView);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;

        // The control for the test above: the same quote, and the fields really are there when
        // they are allowed to be — so their absence there is the projection, not a missing value.
        root.GetProperty("netAmountMinor").GetInt64().Should().Be(Net);
        root.GetProperty("markupAmountMinor").GetInt64().Should().Be(Markup);
        root.GetProperty("markupRuleId").GetGuid().Should().Be(_ruleId);
        root.GetProperty("grossAmountMinor").GetInt64().Should().Be(Gross);
    }

    [Fact]
    public async Task Another_agencys_quote_is_not_found()
    {
        using var response = await GetAsync(
            $"/api/v1/pricing/quotes/{_quoteId}",
            agencyId: Guid.CreateVersion7(),
            PermissionCodes.BookingSearch, PermissionCodes.MarginView);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ------------------------------------------------------------------ rules

    [Fact]
    public async Task Markup_rules_are_refused_to_anyone_without_margin_view()
    {
        using var response = await GetAsync("/api/v1/pricing/markup-rules", PermissionCodes.BookingSearch);

        // A rule plus a price gives away the net rate, so the rules are margin too.
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await response.Content.ReadAsStringAsync()).Should().NotContain(_ruleId.ToString());
    }

    [Fact]
    public async Task Markup_rules_are_listed_for_margin_view()
    {
        using var response = await GetAsync("/api/v1/pricing/markup-rules", PermissionCodes.MarginView);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var rules = await response.Content.ReadFromJsonAsync<List<MarkupRuleResponse>>();
        rules.Should().ContainSingle().Which.Id.Should().Be(_ruleId);
    }

    [Fact]
    public async Task Creating_a_rule_needs_margin_edit()
    {
        var request = new MarkupRuleRequest(
            "ProductType", "Flight", null, null, "NGN", "Fixed", null, 500_000, null, null, 0, true, null, null);

        using (var refused = await SendAsync(HttpMethod.Post, "/api/v1/pricing/markup-rules", request, PermissionCodes.MarginView))
        {
            refused.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }

        using var created = await SendAsync(HttpMethod.Post, "/api/v1/pricing/markup-rules", request, PermissionCodes.MarginEdit);
        created.StatusCode.Should().Be(HttpStatusCode.Created);

        var rule = await created.Content.ReadFromJsonAsync<MarkupRuleResponse>();
        rule!.Scope.Should().Be("ProductType");
        rule.ValueMinor.Should().Be(500_000);
    }

    [Fact]
    public async Task A_rule_that_does_not_add_up_is_refused_with_a_reason()
    {
        var request = new MarkupRuleRequest(
            "Global", null, null, null, "NGN", "Fixed", null, 500_000, 100, null, 0, true, null, null);

        using var response = await SendAsync(HttpMethod.Post, "/api/v1/pricing/markup-rules", request, PermissionCodes.MarginEdit);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("percentage rules");
    }

    [Fact]
    public async Task A_scope_given_as_a_number_is_refused()
    {
        var request = new MarkupRuleRequest(
            "4", "Flight", Guid.CreateVersion7(), null, "NGN", "Percentage", 1_000, null, null, null, 0, true, null, null);

        using var response = await SendAsync(HttpMethod.Post, "/api/v1/pricing/markup-rules", request, PermissionCodes.MarginEdit);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ------------------------------------------------------------------ helpers

    private Task<HttpResponseMessage> GetAsync(string path, params string[] permissions) =>
        GetAsync(path, _agencyId, permissions);

    private Task<HttpResponseMessage> GetAsync(string path, Guid agencyId, params string[] permissions)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TokenFor(agencyId, permissions));
        return SendAndDisposeRequestAsync(request);
    }

    private Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object body, params string[] permissions)
    {
        var request = new HttpRequestMessage(method, path) { Content = JsonContent.Create(body) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TokenFor(_agencyId, permissions));
        return SendAndDisposeRequestAsync(request);
    }

    private async Task<HttpResponseMessage> SendAndDisposeRequestAsync(HttpRequestMessage request)
    {
        using (request)
        {
            return await _api.SendAsync(request);
        }
    }

    /// <summary>
    /// A real token, signed by the API's own issuer, carrying exactly <paramref name="permissions"/>.
    /// The permission claims are what the policies and the projection read.
    /// </summary>
    private string TokenFor(Guid agencyId, IReadOnlyCollection<string> permissions)
    {
        var issuer = _factory.Services.GetRequiredService<IAccessTokenIssuer>();
        var user = User.ForAgency(agencyId, "counter@lagos-travel.test", "not-a-real-hash", "Ada", "Obi");

        return issuer.Issue(user, ["Agent"], permissions, agencyId).Value;
    }
}
