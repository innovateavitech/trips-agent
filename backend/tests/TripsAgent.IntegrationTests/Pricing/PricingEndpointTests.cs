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
    private const long Tax = 926;         // 7.5% VAT on the markup: 925.95, rounded half up
    private const long Gross = Net + Markup + Tax;

    // The VAT is margin too: charged on the markup alone, it gives the markup away at a known rate.
    private static readonly string[] MarginFields =
        ["netAmountMinor", "markupAmountMinor", "markupRuleId", "taxAmountMinor", "platformFeeMinor"];

    private readonly PostgresFixture _postgres;
    private readonly RedisFixture _redis;

    private (string Key, string Value)[] _overrides = [];
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _api = null!;

    private string _database = string.Empty;
    private Guid _agencyId;
    private Guid _subAgentId;
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
        _database = database;

        await using (var setup = await _postgres.CreateEmptyDatabaseAsync(database))
        {
            await setup.Database.MigrateAsync();

            var agency = Agency.RegisterPrincipal("Lagos Travel Limited", "lagos-travel", "NG", "NGN", "Africa/Lagos");
            setup.Agencies.Add(agency);
            await setup.SaveChangesAsync();
            _agencyId = agency.Id;

            // With no rules of its own, so it inherits the principal's — for the preview's "inherited".
            var subAgent = Agency.RegisterSubAgent(agency, "Lagos Travel Ikeja Limited", "lagos-travel-ikeja");
            setup.Agencies.Add(subAgent);
            await setup.SaveChangesAsync();
            _subAgentId = subAgent.Id;
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
        properties.Should().BeEquivalentTo("id", "productType", "currency", "grossAmountMinor", "createdAt", "expiresAt");
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
        root.GetProperty("taxAmountMinor").GetInt64().Should().Be(Tax);
        root.GetProperty("platformFeeMinor").GetInt64().Should().Be(0);
        root.GetProperty("fxRate").GetDecimal().Should().Be(1m);
        root.GetProperty("grossAmountMinor").GetInt64().Should().Be(Gross);
    }

    // ------------------------------------------------------------------ the preview

    [Fact]
    public async Task The_preview_names_the_winning_rule_and_stores_nothing()
    {
        var before = await QuoteCountAsync();

        using var response = await SendAsync(
            HttpMethod.Post, "/api/v1/pricing/preview", new PricePreviewRequest("Flight", null, null, null, Net), PermissionCodes.MarginView);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var preview = await response.Content.ReadFromJsonAsync<PricePreviewResponse>();

        preview!.Currency.Should().Be("NGN", "an omitted currency means the agency's base currency");
        preview.NetAmountMinor.Should().Be(Net);
        preview.MarkupAmountMinor.Should().Be(Markup);
        preview.VatRateBasisPoints.Should().Be(750);
        preview.TaxAmountMinor.Should().Be(Tax);
        preview.PlatformFeeMinor.Should().Be(0);
        preview.AgentMarginMinor.Should().Be(Markup);
        preview.GrossAmountMinor.Should().Be(Gross);
        preview.WinningRule!.Id.Should().Be(_ruleId);
        preview.WinningRule.Scope.Should().Be("Global");
        preview.WinningRule.Inherited.Should().BeFalse();
        preview.WinningRule.Summary.Should().Be("10% of the net rate");

        (await QuoteCountAsync()).Should().Be(before, "a preview is not a quote");
    }

    [Fact]
    public async Task A_sub_agents_preview_says_the_winning_rule_is_its_principals()
    {
        using var response = await SendAsync(
            HttpMethod.Post,
            "/api/v1/pricing/preview",
            new PricePreviewRequest("Tour", null, null, "NGN", 100_000),
            _subAgentId,
            PermissionCodes.MarginView);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var preview = await response.Content.ReadFromJsonAsync<PricePreviewResponse>();

        preview!.WinningRule!.Id.Should().Be(_ruleId);
        preview.WinningRule.Inherited.Should().BeTrue();
        preview.MarkupAmountMinor.Should().Be(10_000);
    }

    [Fact]
    public async Task The_preview_is_margin_and_is_refused_without_margin_view()
    {
        using var response = await SendAsync(
            HttpMethod.Post, "/api/v1/pricing/preview", new PricePreviewRequest("Flight", null, null, null, Net), PermissionCodes.BookingSearch);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Theory]
    [InlineData("Hotel", 100_000, null, "productType")]
    [InlineData("Flight", -1, null, "between zero")]
    [InlineData("Flight", 100_000, "Not A Supplier!", "supplier code")]
    public async Task A_preview_that_makes_no_sense_is_refused_with_a_reason(
        string productType, long net, string? supplierCode, string reason)
    {
        using var response = await SendAsync(
            HttpMethod.Post,
            "/api/v1/pricing/preview",
            new PricePreviewRequest(productType, null, supplierCode, null, net),
            PermissionCodes.MarginView);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain(reason);
    }

    [Fact]
    public async Task A_rule_can_target_packages_and_a_package_can_be_quoted()
    {
        using var created = await SendAsync(
            HttpMethod.Post,
            "/api/v1/pricing/markup-rules",
            new MarkupRuleRequest("ProductType", "Package", null, null, "NGN", "Percentage", 2_000, null, null, null, 0, true, null, null),
            PermissionCodes.MarginEdit);

        created.StatusCode.Should().Be(HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        var rule = await created.Content.ReadFromJsonAsync<MarkupRuleResponse>();
        rule!.ProductType.Should().Be("Package");

        using (var package = await SendAsync(
                   HttpMethod.Post, "/api/v1/pricing/preview", new PricePreviewRequest("Package", null, null, null, 100_000), PermissionCodes.MarginView))
        {
            package.StatusCode.Should().Be(HttpStatusCode.OK);
            var price = await package.Content.ReadFromJsonAsync<PricePreviewResponse>();
            price!.WinningRule!.Id.Should().Be(rule.Id, "a product-type rule beats the global one");
            price.MarkupAmountMinor.Should().Be(20_000);
        }

        using (var tour = await SendAsync(
                   HttpMethod.Post, "/api/v1/pricing/preview", new PricePreviewRequest("Tour", null, null, null, 100_000), PermissionCodes.MarginView))
        {
            var price = await tour.Content.ReadFromJsonAsync<PricePreviewResponse>();
            price!.WinningRule!.Id.Should().Be(_ruleId, "a package rule says nothing about tours");
        }

        // A stored quote carries the type by name; the product-type CHECK has to accept "Package".
        await using var scope = _factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().SetTenant(_agencyId, _agencyId);

        var quote = await scope.ServiceProvider.GetRequiredService<PricingService>().QuoteAsync(
            new PricingSubject(PricedProductType.Package, "NGN", Guid.CreateVersion7()), new Money(100_000));

        quote.ProductType.Should().Be(PricedProductType.Package);
        quote.MarkupRuleId.Should().Be(rule.Id);
    }

    [Fact]
    public async Task The_settings_give_the_currency_and_the_rates_on_every_price()
    {
        using var response = await GetAsync("/api/v1/pricing/settings", PermissionCodes.MarginView);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var settings = await response.Content.ReadFromJsonAsync<PricingSettingsResponse>();

        // The fixture's principal has one sub-agent, and no principal of its own.
        settings.Should().Be(new PricingSettingsResponse("NGN", 750, 0, 30, HasPrincipal: false, HasSubAgents: true));
    }

    [Fact]
    public async Task A_sub_agents_settings_say_it_has_a_principal()
    {
        using var response = await GetAsync("/api/v1/pricing/settings", _subAgentId, PermissionCodes.MarginView);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var settings = await response.Content.ReadFromJsonAsync<PricingSettingsResponse>();

        settings!.HasPrincipal.Should().BeTrue();
        settings.HasSubAgents.Should().BeFalse();
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
        rules![0].Status.Should().Be("InForce");
    }

    [Fact]
    public async Task Every_rule_carries_its_status_by_the_servers_clock()
    {
        // The console used to decide "in force" by comparing these timestamps with the browser's
        // clock, and a browser a second behind showed the rule it had just replaced as current.
        // The server stamps the windows, so the server says where each one stands.
        var replacement = new MarkupRuleRequest(
            "Global", null, null, null, "NGN", "Percentage", 1_500, null, null, null, 0, true, null, null);

        using (var replaced = await SendAsync(HttpMethod.Put, $"/api/v1/pricing/markup-rules/{_ruleId}", replacement, PermissionCodes.MarginEdit))
        {
            replaced.StatusCode.Should().Be(HttpStatusCode.OK);
            (await replaced.Content.ReadFromJsonAsync<MarkupRuleResponse>())!.Status.Should().Be("InForce");
        }

        var flights = new MarkupRuleRequest(
            "ProductType", "Flight", null, null, "NGN", "Fixed", null, 50_000, null, null, 0, true, null, null);
        using var created = await SendAsync(HttpMethod.Post, "/api/v1/pricing/markup-rules", flights, PermissionCodes.MarginEdit);
        var flightsRule = await created.Content.ReadFromJsonAsync<MarkupRuleResponse>();

        using (var retired = await SendAsync(
                   HttpMethod.Post, $"/api/v1/pricing/markup-rules/{flightsRule!.Id}/retire", new { }, PermissionCodes.MarginEdit))
        {
            retired.StatusCode.Should().Be(HttpStatusCode.OK);
            (await retired.Content.ReadFromJsonAsync<MarkupRuleResponse>())!.Status.Should().Be("Ended");
        }

        using var listed = await GetAsync("/api/v1/pricing/markup-rules", PermissionCodes.MarginView);
        var rules = await listed.Content.ReadFromJsonAsync<List<MarkupRuleResponse>>();

        rules!.Single(rule => rule.Id == _ruleId).Status.Should().Be("Ended", "it was replaced");
        rules!.Single(rule => rule.Id == flightsRule.Id).Status.Should().Be("Ended", "it was removed");
        rules!.Single(rule => rule.PercentBasisPoints == 1_500).Status.Should().Be("InForce");
    }

    [Fact]
    public async Task A_sub_agent_is_shown_the_principal_rules_it_inherits()
    {
        using var response = await GetAsync("/api/v1/pricing/markup-rules/inherited", _subAgentId, PermissionCodes.MarginView);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var inherited = await response.Content.ReadFromJsonAsync<List<InheritedMarkupRuleResponse>>();

        var rule = inherited.Should().ContainSingle().Subject;
        rule.Id.Should().Be(_ruleId);
        rule.Scope.Should().Be("Global");
        rule.Summary.Should().Be("10% of the net rate");
    }

    [Fact]
    public async Task A_principal_inherits_nothing()
    {
        using var response = await GetAsync("/api/v1/pricing/markup-rules/inherited", PermissionCodes.MarginView);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<List<InheritedMarkupRuleResponse>>()).Should().BeEmpty();
    }

    [Fact]
    public async Task Inherited_rules_are_refused_without_margin_view()
    {
        using var response = await GetAsync("/api/v1/pricing/markup-rules/inherited", _subAgentId, PermissionCodes.BookingSearch);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
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

    [Theory]
    [InlineData("Fixed", null, 9_223_372_036_854_775_807L, null)]
    [InlineData("Percentage", 1_000, null, 9_223_372_036_854_775_807L)]
    public async Task A_markup_too_large_to_price_with_is_refused_before_it_is_saved(
        string calculation, int? percent, long? value, long? minimum)
    {
        // Saved, either would have made every price this agency and its sub-agents asked for
        // overflow — a 500 on every search, from one typo.
        var request = new MarkupRuleRequest(
            "Global", null, null, null, "NGN", calculation, percent, value, minimum, null, 0, true, null, null);

        using var response = await SendAsync(HttpMethod.Post, "/api/v1/pricing/markup-rules", request, PermissionCodes.MarginEdit);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("at most 100 billion");

        using var preview = await SendAsync(
            HttpMethod.Post, "/api/v1/pricing/preview", new PricePreviewRequest("Flight", null, null, null, Net), PermissionCodes.MarginView);
        preview.StatusCode.Should().Be(HttpStatusCode.OK, "pricing still works");
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

    private Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object body, params string[] permissions) =>
        SendAsync(method, path, body, _agencyId, permissions);

    private Task<HttpResponseMessage> SendAsync(
        HttpMethod method, string path, object body, Guid agencyId, params string[] permissions)
    {
        var request = new HttpRequestMessage(method, path) { Content = JsonContent.Create(body) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TokenFor(agencyId, permissions));
        return SendAndDisposeRequestAsync(request);
    }

    private async Task<int> QuoteCountAsync()
    {
        await using var owner = _postgres.Connect(_database, asApplicationRole: false);
        return await owner.PriceQuotes.CountAsync();
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
