using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TripsAgent.Api.Payments;
using TripsAgent.Infrastructure.Persistence;
using TripsAgent.IntegrationTests.Persistence;

namespace TripsAgent.IntegrationTests.Payments;

/// <summary>
/// The webhook endpoint over real HTTP.
/// </summary>
/// <remarks>
/// <para>
/// The handler is covered in <see cref="WebhookIdempotencyTests"/>. What only a real request can
/// show is the part in front of it: that the raw body reaches the signature check unmodified,
/// that the header is read under the name Paystack actually sends, and that the endpoint is
/// reachable without a token — it is mapped after <c>UseAuthentication</c>, so a missing
/// <c>AllowAnonymous</c> would turn every delivery into a 401 and nothing else here would notice.
/// </para>
/// <para>
/// Body handling is the specific risk. Read the body through model binding, or let anything else
/// consume the stream first, and the HMAC is computed over different bytes than were signed —
/// which looks exactly like a gateway sending bad signatures.
/// </para>
/// </remarks>
[Collection(PostgresCollection.Name)]
public sealed class PaystackWebhookEndpointTests : IAsyncLifetime, IDisposable
{
    private const string SecretKey = "paystack-endpoint-test-key";
    private const string Route = "/api/v1/webhooks/paystack";

    private readonly PostgresFixture _postgres;

    private (string Key, string Value)[] _overrides = [];
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _api = null!;

    public PaystackWebhookEndpointTests(PostgresFixture postgres) => _postgres = postgres;

    public async Task InitializeAsync()
    {
        // A database of its own. See the note in RegistrationEndToEndTests: a reused name gets
        // recreated between tests while Npgsql still caches the old citext and ltree OIDs.
        var database = $"paystack_hook_{Guid.NewGuid():N}";

        await using (var setup = await _postgres.CreateEmptyDatabaseAsync(database))
        {
            await setup.Database.MigrateAsync();
            await ReferenceDataSeeder.EnsureAsync(setup, TestTenancy.None().Scope);
        }

        var connectionString = new Npgsql.NpgsqlConnectionStringBuilder(_postgres.ConnectionString)
        {
            Database = database,
        }.ConnectionString;

        // Environment variables, because a configuration source added through the factory loses
        // to appsettings.Development.json.
        _overrides =
        [
            ("ConnectionStrings__Postgres", connectionString),
            ("Paystack__SecretKey", SecretKey),
        ];

        foreach (var (key, value) in _overrides)
        {
            Environment.SetEnvironmentVariable(key, value);
        }

        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(host => host.UseEnvironment("Development"));

        _api = _factory.CreateClient();
    }

    [Fact]
    public async Task A_correctly_signed_delivery_is_acknowledged()
    {
        var body = Delivery("charge.success", "TA-ENDPOINT-001");

        using var response = await PostAsync(body, Sign(body));

        // 200, and quickly. Paystack reads anything else as "try again" and redelivers for 72
        // hours, so an honest 500 here would multiply the traffic rather than reduce it.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_delivery_with_no_signature_is_refused()
    {
        var body = Delivery("charge.success", "TA-ENDPOINT-002");

        using var response = await PostAsync(body, signature: null);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        // And nothing was written. The endpoint is public, so a row per unsigned request would
        // let anyone who found the URL fill the table.
        (await CountEventsAsync()).Should().Be(0);
    }

    [Fact]
    public async Task A_delivery_signed_with_the_wrong_key_is_refused()
    {
        var body = Delivery("charge.success", "TA-ENDPOINT-003");

        var forged = Convert.ToHexString(
                HMACSHA512.HashData(Encoding.UTF8.GetBytes("not-the-real-key"), Encoding.UTF8.GetBytes(body)))
            .ToLower(CultureInfo.InvariantCulture);

        using var response = await PostAsync(body, forged);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await CountEventsAsync()).Should().Be(0);
    }

    [Fact]
    public async Task A_body_altered_in_transit_is_refused()
    {
        var original = Delivery("charge.success", "TA-ENDPOINT-004");
        var signature = Sign(original);

        var tampered = original.Replace("\"amount\":500000", "\"amount\":5000000", StringComparison.Ordinal);

        using var response = await PostAsync(tampered, signature);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await CountEventsAsync()).Should().Be(0);
    }

    [Fact]
    public async Task The_signature_is_read_from_the_header_paystack_actually_sends()
    {
        var body = Delivery("charge.success", "TA-ENDPOINT-005");

        // Paystack sends x-paystack-signature. HTTP header names are case-insensitive, so a
        // delivery using different casing must verify just the same.
        using var request = new HttpRequestMessage(HttpMethod.Post, Route)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

        request.Headers.TryAddWithoutValidation("X-Paystack-Signature", Sign(body));

        using var response = await _api.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_redelivery_is_acknowledged_rather_than_treated_as_an_error()
    {
        var body = Delivery("charge.success", "TA-ENDPOINT-006");
        var signature = Sign(body);

        using var first = await PostAsync(body, signature);
        using var second = await PostAsync(body, signature);

        // Both 200. A duplicate is the gateway behaving correctly, and answering it with an
        // error would only make it redeliver.
        first.StatusCode.Should().Be(HttpStatusCode.OK);
        second.StatusCode.Should().Be(HttpStatusCode.OK);

        // One row, though.
        (await CountEventsAsync("charge.success:4099260516")).Should().Be(1);
    }

    [Fact]
    public async Task An_unknown_event_type_is_accepted_and_not_an_error()
    {
        var body =
            """
            {"event":"subscription.create","data":{"id":7,"reference":"TA-ENDPOINT-007"}}
            """;

        using var response = await PostAsync(body, Sign(body));

        // Recorded and ignored. A gateway sends many event types, and a new one appearing must
        // not start failing deliveries.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task An_oversized_body_is_refused_without_being_buffered()
    {
        // Well past the ceiling. The signature cannot be checked until the whole body is in
        // memory, so without a cap this is a way to make a public endpoint buffer anything.
        var huge = new string('x', PaymentWebhookEndpoints.MaxBodyBytes + 1_024);

        using var response = await PostAsync(huge, Sign(huge));

        response.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
        (await CountEventsAsync()).Should().Be(0);
    }

    [Fact]
    public async Task A_signed_but_unparseable_body_is_refused()
    {
        const string Nonsense = "this is not json";

        using var response = await PostAsync(Nonsense, Sign(Nonsense));

        // The signature is genuine, so this is our own key signing something that is not a
        // Paystack event — worth refusing loudly rather than recording.
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await CountEventsAsync()).Should().Be(0);
    }

    [Fact]
    public async Task The_endpoint_is_not_advertised_in_the_public_schema()
    {
        using var response = await _api.GetAsync(new Uri("/openapi/v1.json", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var schema = await response.Content.ReadAsStringAsync();

        // Paystack's contract, not ours. Publishing it only advertises an anonymous endpoint.
        schema.Should().NotContain("webhooks/paystack");
    }

    // ------------------------------------------------------------------- helpers

    private Task<HttpResponseMessage> PostAsync(string body, string? signature)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, Route)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

        if (signature is not null)
        {
            request.Headers.TryAddWithoutValidation("x-paystack-signature", signature);
        }

        return _api.SendAsync(request);
    }

    private static string Delivery(string eventType, string reference) =>
        $$$"""
           {"event":"{{{eventType}}}","data":{"id":4099260516,"status":"success","reference":"{{{reference}}}","amount":500000,"fees":7500,"currency":"NGN","gateway_response":"Successful"}}
           """;

    private static string Sign(string body) =>
        Convert.ToHexString(
                HMACSHA512.HashData(Encoding.UTF8.GetBytes(SecretKey), Encoding.UTF8.GetBytes(body)))
            .ToLower(CultureInfo.InvariantCulture);

    private async Task<int> CountEventsAsync(string? eventId = null)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var query = db.PaymentWebhookEvents.AsNoTracking();

        if (eventId is not null)
        {
            query = query.Where(e => e.EventId == eventId);
        }

        return await query.CountAsync();
    }

    public async Task DisposeAsync()
    {
        _api?.Dispose();

        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }

        // Put the environment back, or the next test class inherits this one's connection string.
        foreach (var (key, _) in _overrides)
        {
            Environment.SetEnvironmentVariable(key, null);
        }
    }

    public void Dispose() => _api?.Dispose();
}
