using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TripsAgent.Api.RateLimiting;
using TripsAgent.Application.Identity;
using TripsAgent.Application.RateLimiting;
using TripsAgent.Contracts.Identity;
using TripsAgent.Domain.Identity;
using TripsAgent.IntegrationTests.Persistence;
using TripsAgent.IntegrationTests.Pricing;

namespace TripsAgent.IntegrationTests.RateLimiting;

/// <summary>
/// The rate limiter, through the real pipeline, counting in a real Redis — and, the part an
/// in-memory limiter fails, two separate API instances sharing one count.
/// </summary>
/// <remarks>
/// <para>
/// Each <see cref="WebApplicationFactory{TEntryPoint}"/> is a separate host with its own container,
/// its own Redis connection and its own limiter — as close to a second API process as a test gets.
/// Anything a limiter kept in memory would not be shared between them.
/// </para>
/// <para>
/// TestServer requests arrive with no socket address, so a startup filter stands in for the TCP peer:
/// <see cref="PeerAddressFilter.Header"/> sets <c>RemoteIpAddress</c> before anything else runs. That is
/// the address the load balancer would have connected from, and it is what decides whether
/// <c>X-Forwarded-For</c> is believed. Every address is from the documentation ranges, and each test
/// uses its own, so tests sharing the Redis container never share a count.
/// </para>
/// </remarks>
[Collection(PostgresCollection.Name)]
public sealed class RateLimitingTests : IClassFixture<RedisFixture>, IAsyncLifetime, IDisposable
{
    private const int LoginLimit = 3;
    private const int UserLimit = 5;
    private const int AgencyLimit = 8;
    private const int SearchLimit = 2;

    /// <summary>The one proxy the API is told to trust.</summary>
    private const string TrustedProxy = "198.51.100.10";

    private readonly PostgresFixture _postgres;
    private readonly RedisFixture _redis;
    private readonly CapturingLoggerProvider _logs = new();
    private readonly List<WebApplicationFactory<Program>> _instances = [];

    private (string Key, string Value)[] _overrides = [];

    public RateLimitingTests(PostgresFixture postgres, RedisFixture redis)
    {
        _postgres = postgres;
        _redis = redis;
    }

    public async Task InitializeAsync()
    {
        var database = $"rate_limit_{Guid.NewGuid():N}";

        await using (var setup = await _postgres.CreateEmptyDatabaseAsync(database))
        {
            await setup.Database.MigrateAsync();
        }

        // Environment variables, because a configuration source added through the factory loses to
        // appsettings.Development.json — which is exactly where rate limiting is switched off.
        _overrides =
        [
            ("ConnectionStrings__Postgres", _postgres.ConnectionStringFor(database, asApplicationRole: true)),
            ("ConnectionStrings__PostgresAdmin", _postgres.ConnectionStringFor(database, asApplicationRole: false)),
            ("ConnectionStrings__Redis", _redis.ConnectionString),
            ("Paystack__SecretKey", "rate-limit-test-key"),
            ("RateLimiting__Enabled", "true"),
            ("RateLimiting__Policies__Login__PermitLimit", LoginLimit.ToString(CultureInfo.InvariantCulture)),
            ("RateLimiting__Policies__Login__Window", "00:05:00"),
            ("RateLimiting__Policies__Default__PermitLimit", UserLimit.ToString(CultureInfo.InvariantCulture)),
            ("RateLimiting__Policies__Default__Window", "00:05:00"),
            ("RateLimiting__Policies__Agency__PermitLimit", AgencyLimit.ToString(CultureInfo.InvariantCulture)),
            ("RateLimiting__Policies__Agency__Window", "00:05:00"),
            ("RateLimiting__Policies__Search__PermitLimit", SearchLimit.ToString(CultureInfo.InvariantCulture)),
            ("RateLimiting__Policies__Search__Window", "00:05:00"),
            ("ForwardedHeaders__KnownProxies", TrustedProxy),
        ];

        foreach (var (key, value) in _overrides)
        {
            Environment.SetEnvironmentVariable(key, value);
        }
    }

    public async Task DisposeAsync()
    {
        foreach (var instance in _instances)
        {
            await instance.DisposeAsync();
        }

        foreach (var (key, _) in _overrides)
        {
            Environment.SetEnvironmentVariable(key, null);
        }
    }

    public void Dispose() => _logs.Dispose();

    [Fact]
    public async Task The_request_after_the_limit_gets_429_with_Retry_After_and_the_RateLimit_headers()
    {
        var api = StartInstance().CreateClient();
        const string client = "203.0.113.11";

        for (var attempt = 1; attempt <= LoginLimit; attempt++)
        {
            using var allowed = await LoginAsync(api, client);

            allowed.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "a wrong password inside the limit is just a wrong password");
            Header(allowed, RateLimitHeaders.Limit).Should().Be("3");
            Header(allowed, RateLimitHeaders.Remaining).Should().Be((LoginLimit - attempt).ToString(CultureInfo.InvariantCulture));
        }

        using var refused = await LoginAsync(api, client);

        refused.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        refused.Headers.RetryAfter.Should().NotBeNull();
        refused.Headers.RetryAfter!.Delta.Should().BeGreaterThan(TimeSpan.Zero).And.BeLessThanOrEqualTo(TimeSpan.FromMinutes(5));
        Header(refused, RateLimitHeaders.Limit).Should().Be("3");
        Header(refused, RateLimitHeaders.Remaining).Should().Be("0");
        int.Parse(Header(refused, RateLimitHeaders.Reset)!, CultureInfo.InvariantCulture).Should().BeInRange(1, 300);
        Header(refused, RateLimitHeaders.Policy).Should().Be("3;w=300");
        refused.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");

        // Abuse has to be visible: the log line names the policy and the partition that was refused.
        _logs.Messages.Should().Contain(message =>
            message.Contains(RateLimitPolicyNames.Login, StringComparison.Ordinal)
            && message.Contains($"ip:{client}", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_second_API_instance_sharing_the_same_Redis_agrees()
    {
        var first = StartInstance().CreateClient();
        var second = StartInstance().CreateClient();
        const string client = "203.0.113.21";

        for (var attempt = 0; attempt < LoginLimit; attempt++)
        {
            using var allowed = await LoginAsync(first, client);
            allowed.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        // The second instance has served this client nothing. An in-memory limiter would let it
        // through — that is how two instances quietly double every limit.
        using var refused = await LoginAsync(second, client);
        refused.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);

        // And it is this client that is over the limit, not the second instance as a whole.
        using var someoneElse = await LoginAsync(second, "203.0.113.22");
        someoneElse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var stored = await _redis.Connection.GetDatabase()
            .StringGetAsync(RateLimitEvaluator.KeyFor(RateLimitPolicyNames.Login, $"ip:{client}"));
        ((long)stored).Should().Be(LoginLimit + 1, "both instances counted into the one Redis key");
    }

    [Fact]
    public async Task A_forged_X_Forwarded_For_from_a_caller_that_is_not_our_proxy_is_ignored()
    {
        var api = StartInstance().CreateClient();
        const string attacker = "203.0.113.31";

        for (var attempt = 1; attempt <= LoginLimit; attempt++)
        {
            using var allowed = await LoginAsync(api, attacker, forwardedFor: $"192.0.2.{attempt}");
            allowed.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        // A made-up address on every request. If the header were believed, each would have had a
        // fresh bucket and this would get through.
        using var refused = await LoginAsync(api, attacker, forwardedFor: "192.0.2.99");
        refused.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task Through_the_trusted_proxy_each_client_is_counted_by_its_own_address()
    {
        var api = StartInstance().CreateClient();
        const string client = "203.0.113.41";

        for (var attempt = 0; attempt < LoginLimit; attempt++)
        {
            using var allowed = await LoginAsync(api, TrustedProxy, forwardedFor: client);
            allowed.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        using var refused = await LoginAsync(api, TrustedProxy, forwardedFor: client);
        refused.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);

        // Another client behind the same load balancer has a count of its own. Without the forwarded
        // address every traveller would share the balancer's one address, and one would lock out all.
        using var neighbour = await LoginAsync(api, TrustedProxy, forwardedFor: "203.0.113.42");
        neighbour.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Signed_in_callers_are_counted_as_themselves_and_their_agency_has_a_ceiling()
    {
        var instance = StartInstance();
        var api = instance.CreateClient();

        // Everyone at the same office address, so an address-based limit would treat them as one.
        const string office = "203.0.113.71";
        var lagos = Guid.CreateVersion7();
        var abuja = Guid.CreateVersion7();

        var ada = Token(instance, lagos);
        var bayo = Token(instance, lagos);
        var chioma = Token(instance, abuja);

        for (var request = 0; request < UserLimit; request++)
        {
            using var allowed = await MeAsync(api, ada, office);
            allowed.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        // Bayo is at the same address but is not Ada: he is counted as himself...
        using (var own = await MeAsync(api, bayo, office))
        {
            own.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        using (var own = await MeAsync(api, bayo, office))
        {
            own.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        using (var own = await MeAsync(api, bayo, office))
        {
            own.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        // ...but Lagos Travel has now made eight requests between them, its ceiling. Bayo's fourth
        // is refused by the agency's bucket, though he is nowhere near his own limit.
        using var overCeiling = await MeAsync(api, bayo, office);
        overCeiling.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        Header(overCeiling, RateLimitHeaders.Limit).Should().Be(AgencyLimit.ToString(CultureInfo.InvariantCulture));

        // Another agency is untouched: one agency's traffic must never use up another's.
        using var otherAgency = await MeAsync(api, chioma, office);
        otherAgency.StatusCode.Should().Be(HttpStatusCode.OK);

        _logs.Messages.Should().Contain(message =>
            message.Contains(RateLimitPolicyNames.Agency, StringComparison.Ordinal)
            && message.Contains($"agency:{lagos:N}", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Search_is_counted_under_its_own_tighter_policy()
    {
        var instance = StartInstance();
        var api = instance.CreateClient();

        // No booking.search permission, so authorisation refuses each request with a 403 — after the
        // limiter has counted it. That proves the endpoint's policy without calling a supplier.
        var token = Token(instance, Guid.CreateVersion7());

        for (var request = 1; request <= SearchLimit; request++)
        {
            using var counted = await SearchAsync(api, token, "203.0.113.81");

            counted.StatusCode.Should().Be(HttpStatusCode.Forbidden);
            Header(counted, RateLimitHeaders.Limit).Should().Be(SearchLimit.ToString(CultureInfo.InvariantCulture));
            Header(counted, RateLimitHeaders.Policy).Should().Be($"{SearchLimit};w=300");
        }

        using var refused = await SearchAsync(api, token, "203.0.113.81");
        refused.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);

        // The same user's other requests are counted separately, under the default policy.
        using var elsewhere = await MeAsync(api, token, "203.0.113.81");
        elsewhere.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task The_Paystack_webhook_is_never_throttled()
    {
        var api = StartInstance().CreateClient();

        // Far past every limit configured here, from one of Paystack's real sending addresses.
        for (var delivery = 0; delivery < UserLimit * 3; delivery++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/webhooks/paystack")
            {
                Content = new StringContent("""{"event":"charge.success","data":{}}""", Encoding.UTF8, "application/json"),
            };
            request.Headers.Add(PeerAddressFilter.Header, "52.31.139.75");
            request.Headers.Add("x-paystack-signature", "not-a-valid-signature");

            using var response = await api.SendAsync(request);

            // 401 — the signature is wrong — but never 429. A throttled webhook is a lost payment notification.
            response.StatusCode.Should().NotBe(HttpStatusCode.TooManyRequests);
            response.Headers.Contains(RateLimitHeaders.Limit).Should().BeFalse("the webhook is exempt, not merely under its limit");
        }
    }

    [Fact]
    public async Task Health_checks_are_never_throttled()
    {
        var api = StartInstance().CreateClient();

        for (var probe = 0; probe < UserLimit * 2; probe++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/health");
            request.Headers.Add(PeerAddressFilter.Header, "198.51.100.200");

            using var response = await api.SendAsync(request);

            response.StatusCode.Should().NotBe(HttpStatusCode.TooManyRequests, "a 429 here reads as unhealthy to a load balancer");
            response.Headers.Contains(RateLimitHeaders.Limit).Should().BeFalse();
        }
    }

    private WebApplicationFactory<Program> StartInstance()
    {
        var instance = new WebApplicationFactory<Program>().WithWebHostBuilder(host =>
        {
            host.UseEnvironment("Development");
            host.ConfigureLogging(logging => logging.AddProvider(_logs));
            host.ConfigureServices(services => services.AddSingleton<IStartupFilter>(new PeerAddressFilter()));
        });

        _instances.Add(instance);
        return instance;
    }

    private static async Task<HttpResponseMessage> LoginAsync(HttpClient api, string peer, string? forwardedFor = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/login")
        {
            // A different unknown account every time, so account lockout never gets involved: every
            // refusal here is the rate limiter's.
            Content = JsonContent.Create(new LoginRequest($"nobody-{Guid.NewGuid():N}@example.test", "not-the-password")),
        };

        request.Headers.Add(PeerAddressFilter.Header, peer);

        if (forwardedFor is not null)
        {
            request.Headers.Add("X-Forwarded-For", forwardedFor);
        }

        return await api.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> SearchAsync(HttpClient api, string token, string peer)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/search/flights")
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add(PeerAddressFilter.Header, peer);

        return await api.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> MeAsync(HttpClient api, string token, string peer)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/auth/me");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add(PeerAddressFilter.Header, peer);

        return await api.SendAsync(request);
    }

    /// <summary>A real token, signed by the API's own issuer, for a new user of <paramref name="agencyId"/>.</summary>
    private static string Token(WebApplicationFactory<Program> instance, Guid agencyId)
    {
        var issuer = instance.Services.GetRequiredService<IAccessTokenIssuer>();
        var user = User.ForAgency(agencyId, $"{Guid.NewGuid():N}@agency.test", "not-a-real-hash", "Ada", "Obi");

        return issuer.Issue(user, ["Agent"], [], agencyId).Value;
    }

    private static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? values.Single() : null;

    /// <summary>Plays the part of the TCP connection: sets the socket address a real server would see.</summary>
    private sealed class PeerAddressFilter : IStartupFilter
    {
        public const string Header = "X-Test-Peer-Address";

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) =>
            app =>
            {
                app.Use(async (context, nextMiddleware) =>
                {
                    if (IPAddress.TryParse(context.Request.Headers[Header].ToString(), out var peer))
                    {
                        context.Connection.RemoteIpAddress = peer;
                    }

                    await nextMiddleware(context);
                });

                next(app);
            };
    }

    /// <summary>Keeps what the rate limiter logs, so a test can check a refusal was visible.</summary>
    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public ConcurrentQueue<string> Messages { get; } = new();

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, Messages);

        public void Dispose()
        {
        }

        private sealed class CapturingLogger(string category, ConcurrentQueue<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (category == RateLimitingSetup.LogCategory)
                {
                    messages.Enqueue(formatter(state, exception));
                }
            }
        }
    }
}
