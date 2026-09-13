using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TripsAgent.Api.Identity;
using TripsAgent.Api.Security;
using TripsAgent.Application.Identity;
using TripsAgent.Application.Storefront;
using TripsAgent.Application.Tenancy;
using TripsAgent.Contracts.Identity;
using TripsAgent.Domain.Identity;
using TripsAgent.Domain.Storefront;
using TripsAgent.Domain.Tenancy;
using TripsAgent.Infrastructure.Persistence;
using TripsAgent.Infrastructure.Storefront;
using TripsAgent.Infrastructure.Tenancy;
using TripsAgent.IntegrationTests.Persistence;
using TripsAgent.IntegrationTests.Pricing;

namespace TripsAgent.IntegrationTests.Security;

/// <summary>
/// The API's edge (issue 107): the security headers on every response, which browser origins may read
/// an answer, and the refresh token travelling only in an <c>HttpOnly</c> cookie.
/// </summary>
/// <remarks>
/// Through the real pipeline rather than the middleware alone, so that moving a middleware — the
/// thing that silently drops a header — fails here.
/// </remarks>
[Collection(PostgresCollection.Name)]
public sealed class EdgeHardeningTests : IClassFixture<RedisFixture>, IAsyncLifetime, IDisposable
{
    private const string Email = "ada@lagos-travel.test";
    private const string Password = "Correct-Horse-9";

    /// <summary>Listed in appsettings.Development.json, as the agent console's dev server.</summary>
    private const string ConsoleOrigin = "http://localhost:5173";

    private const string SiteHost = "lagos-travel.localhost";
    private const string PendingHost = "www.lagostravel.test";

    private static readonly DateTimeOffset Now = new(2026, 9, 13, 9, 0, 0, TimeSpan.Zero);

    private readonly PostgresFixture _postgres;
    private readonly RedisFixture _redis;

    private (string Key, string Value)[] _overrides = [];
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _api = null!;
    private string _database = string.Empty;
    private Guid _pendingDomainId;

    public EdgeHardeningTests(PostgresFixture postgres, RedisFixture redis)
    {
        _postgres = postgres;
        _redis = redis;
    }

    public async Task InitializeAsync()
    {
        _database = $"edge_{Guid.NewGuid():N}";
        var tenancy = TestTenancy.None();

        await using (var setup = await _postgres.CreateEmptyDatabaseAsync(_database, tenancy.Tenant, tenancy.Scope))
        {
            await setup.Database.MigrateAsync();
            await ReferenceDataSeeder.EnsureAsync(setup, tenancy.Scope);
        }

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

        // Every test rebuilds the same hostnames in a database of its own; retire the previous map.
        await _redis.Connection.GetDatabase().StringIncrementAsync(RedisStorefrontHostCache.GenerationKey);

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host => host.UseEnvironment("Development"));

        // Cookies are read and sent by hand: the client's own cookie jar will not send a Secure cookie
        // over the test server's plain http, and the flags are what these tests are about.
        _api = _factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

        await SeedAsync(tenancy);
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

    [Theory]
    [InlineData("/")]
    [InlineData("/api/v1/auth/me")]
    [InlineData("/api/v1/no-such-route")]
    public async Task Every_response_carries_the_security_headers(string path)
    {
        using var response = await _api.GetAsync(new Uri(path, UriKind.Relative));

        Header(response, "X-Content-Type-Options").Should().Be("nosniff");
        Header(response, "Referrer-Policy").Should().Be("no-referrer");
        Header(response, "X-Frame-Options").Should().Be("SAMEORIGIN");
        Header(response, "Content-Security-Policy").Should()
            .Be("frame-ancestors 'self' http://localhost:5173 http://localhost:5174");
        Header(response, "Permissions-Policy").Should().Contain("camera=()");

        // No banner, and no HSTS over plain http, where a browser would ignore it anyway.
        response.Headers.Contains("Server").Should().BeFalse();
        response.Headers.Contains("Strict-Transport-Security").Should().BeFalse();
    }

    [Fact]
    public async Task Https_responses_carry_hsts_without_preload()
    {
        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://api.example.test"),
            HandleCookies = false,
        });

        using var response = await client.GetAsync(new Uri("/", UriKind.Relative));

        Header(response, "Strict-Transport-Security").Should().Be("max-age=31536000");
    }

    [Fact]
    public async Task A_console_origin_may_call_with_credentials()
    {
        using var preflight = Preflight("/api/v1/auth/refresh", ConsoleOrigin);
        using var response = await _api.SendAsync(preflight);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        Header(response, "Access-Control-Allow-Origin").Should().Be(ConsoleOrigin);
        Header(response, "Access-Control-Allow-Credentials").Should().Be("true");
    }

    [Fact]
    public async Task An_unknown_origin_gets_no_cors_headers_and_never_a_wildcard()
    {
        using var preflight = Preflight("/api/v1/auth/refresh", "https://evil.example");
        using var response = await _api.SendAsync(preflight);

        response.Headers.Contains("Access-Control-Allow-Origin").Should().BeFalse();
        response.Headers.Contains("Access-Control-Allow-Credentials").Should().BeFalse();
    }

    [Fact]
    public async Task A_verified_storefront_origin_may_read_the_public_routes_but_never_with_credentials()
    {
        var origin = $"http://{SiteHost}:3000";

        using var publicCall = Preflight("/api/v1/public/storefront/site", origin);
        using var allowed = await _api.SendAsync(publicCall);

        Header(allowed, "Access-Control-Allow-Origin").Should().Be(origin);
        allowed.Headers.Contains("Access-Control-Allow-Credentials").Should().BeFalse();

        // The session routes are for the consoles alone, whoever the storefront belongs to.
        using var sessionCall = Preflight("/api/v1/auth/refresh", origin);
        using var refused = await _api.SendAsync(sessionCall);

        refused.Headers.Contains("Access-Control-Allow-Origin").Should().BeFalse();
    }

    [Fact]
    public async Task A_storefront_origin_is_trusted_once_its_domain_is_verified_and_the_cache_is_dropped()
    {
        var origin = $"http://{PendingHost}:3000";

        (await PublicPreflightAllowsAsync(origin)).Should().BeFalse("a domain whose DNS is unproved serves nobody");

        var tenancy = TestTenancy.None();

        await using (var db = _postgres.Connect(_database, tenancy.Tenant, tenancy.Scope, asApplicationRole: false))
        using (tenancy.Scope.Enter("test — proving the agency's custom domain"))
        {
            var domain = await db.SiteDomains.SingleAsync(d => d.Id == _pendingDomainId);
            domain.RecordVerificationAttempt(bothRecordsFound: true, Now);
            await db.SaveChangesAsync();
        }

        (await PublicPreflightAllowsAsync(origin)).Should().BeFalse("the earlier answer is still cached");

        // What the storefront cache consumer does when SiteDomainsChanged arrives.
        await _factory.Services.GetRequiredService<IStorefrontHostCache>().InvalidateAllAsync();

        (await PublicPreflightAllowsAsync(origin)).Should().BeTrue();
    }

    [Fact]
    public async Task Sign_in_sets_the_refresh_token_as_an_httponly_cookie_and_leaves_it_out_of_the_body()
    {
        using var response = await _api.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(Email, Password));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.TryGetProperty("accessToken", out _).Should().BeTrue();
        body.RootElement.TryGetProperty("refreshToken", out _).Should().BeFalse();

        var cookie = SetCookie(response, RefreshTokenCookie.AgentCookieName);
        cookie.Should().NotBeNull();
        cookie!.ToLowerInvariant().Should()
            .Contain("httponly").And.Contain("secure").And.Contain("samesite=strict").And.Contain("path=/api/v1/auth");
    }

    [Fact]
    public async Task The_cookie_refreshes_rotates_and_signs_out()
    {
        var first = await SignInAsync();

        using var refreshed = await PostWithCookieAsync("/api/v1/auth/refresh", first);
        refreshed.StatusCode.Should().Be(HttpStatusCode.OK);

        var second = CookieValue(refreshed, RefreshTokenCookie.AgentCookieName);
        second.Should().NotBeNullOrEmpty().And.NotBe(first);

        using var signedOut = await PostWithCookieAsync("/api/v1/auth/logout", second!);
        signedOut.StatusCode.Should().Be(HttpStatusCode.NoContent);
        SetCookie(signedOut, RefreshTokenCookie.AgentCookieName).Should().Contain("expires=Thu, 01 Jan 1970");

        using var afterSignOut = await PostWithCookieAsync("/api/v1/auth/refresh", second!);
        afterSignOut.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_refresh_with_no_cookie_is_refused()
    {
        using var response = await _api.PostAsync(new Uri("/api/v1/auth/refresh", UriKind.Relative), content: null);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Each_console_gets_a_cookie_of_its_own()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/login")
        {
            Content = JsonContent.Create(new LoginRequest(Email, Password)),
        };
        request.Headers.Add(RefreshTokenCookie.ClientHeader, "admin");

        using var response = await _api.SendAsync(request);

        SetCookie(response, RefreshTokenCookie.AdminCookieName).Should().NotBeNull();
        SetCookie(response, RefreshTokenCookie.AgentCookieName).Should().BeNull();
    }

    [Theory]
    [InlineData("*")]
    [InlineData("https://*.example.com")]
    [InlineData("https://app.example.com/console")]
    [InlineData("app.example.com")]
    public void A_wildcard_or_malformed_console_origin_is_refused(string origin)
    {
        var act = () => CorsSetup.NormaliseConsoleOrigin(origin);

        act.Should().Throw<InvalidOperationException>();
    }

    private async Task SeedAsync((TenantContext Tenant, PlatformScope Scope) tenancy)
    {
        var hash = _factory.Services.GetRequiredService<IPasswordHasher>().Hash(Password);

        await using var db = _postgres.Connect(_database, tenancy.Tenant, tenancy.Scope, asApplicationRole: false);
        using var _ = tenancy.Scope.Enter("test setup — one agency with a site and a signed-in owner");

        var agency = Agency.RegisterPrincipal("Lagos Travel Limited", "lagos-travel", "NG", "NGN", "Africa/Lagos");
        agency.MarkVerified(Now);
        db.Agencies.Add(agency);

        var user = User.ForAgency(agency.Id, Email, hash, "Ada", "Okonkwo");
        user.MarkEmailVerified(Now);
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var template = await db.SiteTemplates.FirstAsync();
        var site = Site.Create(agency.Id, template.Id, agency.LegalName);
        var draft = SiteVersion.CreateDraft(site);
        site.AttachDraft(draft);

        var subdomain = SiteDomain.ForSubdomain(site, SiteHost, Now);
        site.SetPrimaryDomain(subdomain);

        var pending = SiteDomain.ForCustom(site, PendingHost, new string('a', 40), Now);
        _pendingDomainId = pending.Id;

        db.Sites.Add(site);
        db.SiteVersions.Add(draft);
        db.SiteDomains.AddRange(subdomain, pending);
        await db.SaveChangesAsync();
    }

    private async Task<bool> PublicPreflightAllowsAsync(string origin)
    {
        using var preflight = Preflight("/api/v1/public/storefront/site", origin);
        using var response = await _api.SendAsync(preflight);

        return response.Headers.Contains("Access-Control-Allow-Origin");
    }

    private async Task<string> SignInAsync()
    {
        using var response = await _api.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(Email, Password));

        response.StatusCode.Should().Be(HttpStatusCode.OK, "signing in is the precondition for this test");

        return CookieValue(response, RefreshTokenCookie.AgentCookieName)!;
    }

    private async Task<HttpResponseMessage> PostWithCookieAsync(string path, string refreshToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Headers.Add("Cookie", $"{RefreshTokenCookie.AgentCookieName}={refreshToken}");

        return await _api.SendAsync(request);
    }

    private static HttpRequestMessage Preflight(string path, string origin)
    {
        var request = new HttpRequestMessage(HttpMethod.Options, path);
        request.Headers.Add("Origin", origin);
        request.Headers.Add("Access-Control-Request-Method", "POST");
        request.Headers.Add("Access-Control-Request-Headers", "content-type");
        return request;
    }

    private static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? string.Join(", ", values) : null;

    private static string? SetCookie(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues("Set-Cookie", out var values)
            ? values.FirstOrDefault(value => value.StartsWith(name + "=", StringComparison.Ordinal))
            : null;

    private static string? CookieValue(HttpResponseMessage response, string name) =>
        SetCookie(response, name)?.Split(';')[0][(name.Length + 1)..];
}
