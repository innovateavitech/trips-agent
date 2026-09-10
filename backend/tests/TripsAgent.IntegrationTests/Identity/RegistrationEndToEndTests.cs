using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TripsAgent.Api.Identity;
using TripsAgent.Application.Tenancy;
using TripsAgent.Contracts.Identity;
using TripsAgent.Infrastructure.Persistence;
using TripsAgent.IntegrationTests.Persistence;

namespace TripsAgent.IntegrationTests.Identity;

/// <summary>
/// The whole flow through HTTP: register, receive the email in a real Mailpit, read the code out
/// of it, verify. Proves the endpoints, the DI wiring and the SMTP adapter together — and that
/// the verification email genuinely renders in Mailpit, which is the acceptance criterion.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class RegistrationEndToEndTests : IAsyncLifetime, IDisposable
{
    private const int SmtpPort = 1025;
    private const int ApiPort = 8025;

    private readonly PostgresFixture _postgres;

    private readonly IContainer _mailpit = new ContainerBuilder("axllent/mailpit:latest")
        .WithPortBinding(SmtpPort, assignRandomHostPort: true)
        .WithPortBinding(ApiPort, assignRandomHostPort: true)
        .WithWaitStrategy(Wait.ForUnixContainer()
            .UntilHttpRequestIsSucceeded(request => request.ForPort(ApiPort).ForPath("/api/v1/messages")))
        .Build();

    private (string Key, string Value)[] _overrides = [];
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _api = null!;
    private HttpClient _mailpitApi = null!;

    public RegistrationEndToEndTests(PostgresFixture postgres) => _postgres = postgres;

    public async Task InitializeAsync()
    {
        await _mailpit.StartAsync();

        // A unique database per test, not a shared name. xUnit builds a new instance for every
        // test, so a fixed name would be dropped and recreated between them — and Npgsql caches
        // its type catalogue per host/port/database. The recreated database hands out new OIDs
        // for citext and ltree while the cached catalogue still holds the old ones, and every
        // read of those columns then fails with DataTypeName '-.-'.
        var database = $"reg_e2e_{Guid.NewGuid():N}";
        await using (var setup = await _postgres.CreateEmptyDatabaseAsync(database))
        {
            await setup.Database.MigrateAsync();
            await ReferenceDataSeeder.EnsureAsync(setup, TestTenancy.None().Scope);
        }

        var connectionString = new Npgsql.NpgsqlConnectionStringBuilder(_postgres.ConnectionString)
        {
            Database = database,
        }.ConnectionString;

        // Environment variables rather than ConfigureAppConfiguration: the host adds them after
        // appsettings.Development.json, so they win. A configuration source added through the
        // factory does not — the API kept using the localhost connection string from that file,
        // which is a database this test never started.
        _overrides =
        [
            ("ConnectionStrings__Postgres", connectionString),
            ("Smtp__Host", _mailpit.Hostname),
            ("Smtp__Port", _mailpit.GetMappedPublicPort(SmtpPort).ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ("Smtp__SecureSocket", "None"),
        ];

        foreach (var (key, value) in _overrides)
        {
            Environment.SetEnvironmentVariable(key, value);
        }

        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(host => host.UseEnvironment("Development"));

        _api = _factory.CreateClient();
        _mailpitApi = new HttpClient
        {
            BaseAddress = new Uri($"http://{_mailpit.Hostname}:{_mailpit.GetMappedPublicPort(ApiPort)}"),
        };
    }

    public async Task DisposeAsync()
    {
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }

        await _mailpit.DisposeAsync();

        foreach (var (key, _) in _overrides)
        {
            Environment.SetEnvironmentVariable(key, null);
        }
    }

    /// <summary>
    /// The HttpClients are disposed here rather than in <see cref="DisposeAsync"/> because they
    /// only offer synchronous disposal, and a type owning them has to be IDisposable (CA1001).
    /// xUnit calls both.
    /// </summary>
    public void Dispose()
    {
        _api?.Dispose();
        _mailpitApi?.Dispose();
        _api = null!;
        _mailpitApi = null!;
    }

    [Fact]
    public async Task A_business_registers_receives_the_code_in_Mailpit_and_verifies()
    {
        var register = await _api.PostAsJsonAsync("/api/v1/auth/register", Request("ada@example.com"));

        register.StatusCode.Should().Be(HttpStatusCode.Accepted);

        var email = await WaitForEmailToAsync("ada@example.com");

        email.Subject.Should().Contain("verification code");
        email.Html.Should().Contain("Hello Ada");

        var code = Regex.Match(email.Text, @"\b\d{6}\b").Value;
        code.Should().HaveLength(6);

        var verify = await _api.PostAsJsonAsync("/api/v1/auth/verify-email", new VerifyEmailRequest("ada@example.com", code));

        verify.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_new_and_an_existing_address_get_byte_for_byte_the_same_response()
    {
        var first = await _api.PostAsJsonAsync("/api/v1/auth/register", Request("same@example.com"));
        var second = await _api.PostAsJsonAsync("/api/v1/auth/register", Request("same@example.com"));

        // Status code and body both — any difference is enough to tell the two cases apart.
        second.StatusCode.Should().Be(first.StatusCode);
        (await second.Content.ReadAsStringAsync()).Should().Be(await first.Content.ReadAsStringAsync());

        (await first.Content.ReadFromJsonAsync<RegistrationAcceptedResponse>())!
            .Message.Should().Be(RegistrationEndpoints.AcceptedMessage);
    }

    [Fact]
    public async Task A_weak_password_comes_back_as_a_validation_problem()
    {
        var response = await _api.PostAsJsonAsync("/api/v1/auth/register", Request("weak@example.com", password: "short"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("Password");
    }

    [Fact]
    public async Task A_wrong_code_gets_a_generic_rejection()
    {
        await _api.PostAsJsonAsync("/api/v1/auth/register", Request("wrong@example.com"));
        await WaitForEmailToAsync("wrong@example.com");

        var response = await _api.PostAsJsonAsync("/api/v1/auth/verify-email", new VerifyEmailRequest("wrong@example.com", "000000"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("invalid or has expired");
    }

    [Fact]
    public async Task The_agency_is_created_pending_verification()
    {
        await _api.PostAsJsonAsync("/api/v1/auth/register", Request("pending@example.com", businessName: "Pending Check Ltd"));

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        using var _ = scope.ServiceProvider.GetRequiredService<IPlatformScope>().Enter("test — reading the new agency");

        (await db.Agencies.SingleAsync(a => a.Slug == "pending-check-ltd"))
            .Status.Should().Be(Domain.Tenancy.AgencyStatus.PendingVerification);
    }

    private static RegisterAgentRequest Request(string email, string password = "Password123", string businessName = "Ada Travel Ltd") =>
        new(businessName, "Ada", "Okonkwo", email, null, "NG", password);

    private sealed record ReceivedEmail(string Subject, string Html, string Text);

    /// <summary>Polls Mailpit's API until a message to <paramref name="to"/> arrives.</summary>
    private async Task<ReceivedEmail> WaitForEmailToAsync(string to)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            using var search = await _mailpitApi.GetAsync(new Uri($"/api/v1/search?query=to:{Uri.EscapeDataString(to)}", UriKind.Relative));
            using var list = JsonDocument.Parse(await search.Content.ReadAsStringAsync());

            if (list.RootElement.TryGetProperty("messages", out var messages) && messages.GetArrayLength() > 0)
            {
                var id = messages[0].GetProperty("ID").GetString();

                using var detail = JsonDocument.Parse(await _mailpitApi.GetStringAsync(new Uri($"/api/v1/message/{id}", UriKind.Relative)));
                var root = detail.RootElement;

                return new ReceivedEmail(
                    root.GetProperty("Subject").GetString() ?? string.Empty,
                    root.GetProperty("HTML").GetString() ?? string.Empty,
                    root.GetProperty("Text").GetString() ?? string.Empty);
            }

            await Task.Delay(100);
        }

        throw new TimeoutException($"No email to {to} reached Mailpit within five seconds.");
    }
}

