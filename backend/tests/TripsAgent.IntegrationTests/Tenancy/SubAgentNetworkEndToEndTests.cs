using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TripsAgent.Application.Billing;
using TripsAgent.Application.Tenancy;
using TripsAgent.Contracts.Identity;
using TripsAgent.Contracts.Tenancy;
using TripsAgent.Domain.Billing;
using TripsAgent.Infrastructure.Persistence;
using TripsAgent.IntegrationTests.Billing;
using TripsAgent.IntegrationTests.Persistence;

namespace TripsAgent.IntegrationTests.Tenancy;

/// <summary>
/// A principal invites an agent beneath it, and the agent joins — through the real API, against a
/// real database and a real SMTP server. Feature F10, issue 63.
/// </summary>
/// <remarks>
/// <para>
/// Two things here cannot be checked any other way. The invitation email must carry the
/// <i>principal's</i> name and never ours (build-plan decision 6), which needs a rendered message
/// to look at. And the epic's key test — that a sub-agent without <c>margin.view</c> receives JSON
/// with no net or markup keys in it at all — has to read the bytes on the wire, because a DTO with
/// a null property and a DTO without the property are the same object graph in C# and different
/// JSON.
/// </para>
/// <para>
/// It also proves the permission bites immediately: the sub-agent's token is never re-issued
/// between the two reads, and the margin still disappears.
/// </para>
/// </remarks>
[Collection(PostgresCollection.Name)]
public sealed class SubAgentNetworkEndToEndTests : IAsyncLifetime, IDisposable
{
    private const int SmtpPort = 1025;
    private const int ApiPort = 8025;

    private const string PrincipalEmail = "owner@lagostravel.example.com";
    private const string SubAgentEmail = "owner@ikejabranch.example.com";
    private const string Password = "Password123";

    /// <summary>What every verification email's subject says, whoever's brand it is in.</summary>
    private const string VerificationSubject = "verification code";

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

    public SubAgentNetworkEndToEndTests(PostgresFixture postgres) => _postgres = postgres;

    public async Task InitializeAsync()
    {
        await _mailpit.StartAsync();

        // A unique database per test: xUnit builds a new instance for each, and Npgsql caches its
        // type catalogue per host and database — a recreated database hands out new OIDs for
        // citext and ltree while the cached catalogue still holds the old ones.
        var database = $"subagent_e2e_{Guid.NewGuid():N}";

        await using (var setup = await _postgres.CreateEmptyDatabaseAsync(database))
        {
            await setup.Database.MigrateAsync();
            await ReferenceDataSeeder.EnsureAsync(setup, TestTenancy.None().Scope);
        }

        _overrides =
        [
            ("ConnectionStrings__Postgres", _postgres.ConnectionStringFor(database, asApplicationRole: true)),
            ("ConnectionStrings__PostgresAdmin", _postgres.ConnectionStringFor(database, asApplicationRole: false)),
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

    public void Dispose()
    {
        _api?.Dispose();
        _mailpitApi?.Dispose();
        _api = null!;
        _mailpitApi = null!;
    }

    [Fact]
    public async Task A_principal_invites_an_agent_and_the_invitation_never_mentions_Trips()
    {
        await SignInAsPrincipalAsync();

        var invited = await InviteAsync();
        var email = await WaitForEmailToAsync(SubAgentEmail);

        // Build-plan decision 6: a sub-agent sells under its principal's brand, and this is the
        // first thing it ever receives from the platform.
        email.Subject.Should().Contain("Lagos Travel Limited");
        email.Html.Should().Contain("Lagos Travel Limited");
        email.Html.Should().NotContain("Trips", "nothing a sub-agent sees names the platform");
        email.Text.Should().NotContain("Trips");
        email.Html.Should().Contain(Uri.EscapeDataString(invited.InvitationToken));
    }

    [Fact]
    public async Task An_invitation_is_previewed_accepted_once_and_then_dead()
    {
        await SignInAsPrincipalAsync();
        var invited = await InviteAsync();

        // Anonymous: whoever holds the link has no account yet.
        using var anonymous = _factory.CreateClient();

        var preview = await anonymous.GetFromJsonAsync<InvitationPreviewResponse>(
            $"/api/v1/invitations?token={Uri.EscapeDataString(invited.InvitationToken)}");

        preview!.Email.Should().Be(SubAgentEmail);
        preview.InvitedBy.Should().Be("Lagos Travel Limited");

        var accepted = await anonymous.PostAsJsonAsync("/api/v1/invitations/accept", Accept(invited.InvitationToken));
        accepted.StatusCode.Should().Be(HttpStatusCode.OK);

        // Single use. The second attempt is refused without saying which of the reasons it was.
        var again = await anonymous.PostAsJsonAsync("/api/v1/invitations/accept", Accept(invited.InvitationToken));
        again.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    /// <summary>
    /// Issue 170. The principal's console shows it the link, so accepting proves only that somebody had
    /// the link. The account must not count as verified until its address has answered — the same code,
    /// and the same rule, as a self-registered account.
    /// </summary>
    [Fact]
    public async Task An_accepted_invitation_is_not_a_verified_address_until_the_code_comes_back()
    {
        await SignInAsPrincipalAsync();
        var invited = await InviteAsync();

        // Whoever holds the link — here it could as well be the principal itself — sets a password.
        using var anonymous = _factory.CreateClient();

        using (var accepted = await anonymous.PostAsJsonAsync("/api/v1/invitations/accept", Accept(invited.InvitationToken)))
        {
            accepted.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        (await VerifiedAtAsync(SubAgentEmail)).Should().BeNull("nobody at the address has done anything yet");

        // The right password is not enough while the address is unproved, exactly as for a self-registered account.
        using (var early = await anonymous.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(SubAgentEmail, Password)))
        {
            early.StatusCode.Should().Be(HttpStatusCode.Forbidden);
            (await early.Content.ReadAsStringAsync()).Should().Contain("not been verified");
        }

        // The code goes to the address, and in the principal's name, like the invitation before it.
        var email = await WaitForEmailToAsync(SubAgentEmail, VerificationSubject);

        email.Subject.Should().Contain("Lagos Travel Limited");
        email.Html.Should().NotContain("Trips", "a sub-agent works under its principal's brand");
        email.Text.Should().NotContain("Trips");

        using (var verified = await anonymous.PostAsJsonAsync(
            "/api/v1/auth/verify-email", new VerifyEmailRequest(SubAgentEmail, CodeIn(email))))
        {
            verified.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        (await VerifiedAtAsync(SubAgentEmail)).Should().NotBeNull();
        (await TokenAsync(SubAgentEmail)).Should().NotBeNullOrWhiteSpace("the address answered, so the account signs in");
    }

    [Fact]
    public async Task A_sub_agent_cannot_have_sub_agents_of_its_own()
    {
        await SignInAsPrincipalAsync();
        var invited = await InviteAsync();
        await AcceptAsync(invited.InvitationToken);

        using var asSubAgent = await SignedInClientAsync(SubAgentEmail);

        // Build-plan decision 7: two levels, and the third is refused at the API as well as by the
        // domain and by a CHECK constraint on the table.
        var response = await asSubAgent.PostAsJsonAsync(
            "/api/v1/sub-agents",
            new InviteSubAgentRequest("Third Level Ltd", null, "third@example.com"));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    /// <summary>
    /// The epic's key test: with margin visibility off, net and markup are <b>absent</b> from the
    /// JSON, not null in it.
    /// </summary>
    [Fact]
    public async Task With_margin_visibility_off_the_JSON_has_no_margin_keys_at_all()
    {
        await SignInAsPrincipalAsync();
        var invited = await InviteAsync();
        await AcceptAsync(invited.InvitationToken);

        using var asSubAgent = await SignedInClientAsync(SubAgentEmail);

        // An Owner holds margin.view, so the sub-agent starts able to see it.
        var before = await asSubAgent.GetStringAsync("/api/v1/network-performance");
        Keys(before).Should().Contain("marginMinor");

        // The principal takes it away. No new token is issued to the sub-agent after this.
        var denied = await _api.PostAsJsonAsync(
            $"/api/v1/sub-agents/{invited.Id}/permissions/deny",
            new DenyPermissionRequest("margin.view", "Resellers do not see what we pay."));

        denied.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var after = await asSubAgent.GetStringAsync("/api/v1/network-performance");

        // The assertion the epic asks for, on the bytes rather than on a deserialised object.
        after.Should().NotContain("marginMinor");
        after.Should().NotContain("netMinor");
        after.Should().NotContain("markupMinor");

        // Structurally absent, not null: no key anywhere in the document names a margin, at any
        // depth. `"marginMinor": null` would pass the three checks above and fail this one.
        Keys(after).Should().NotContain(
            key => key.Contains("margin", StringComparison.OrdinalIgnoreCase)
                   || key.Contains("net", StringComparison.OrdinalIgnoreCase)
                   || key.Contains("markup", StringComparison.OrdinalIgnoreCase));

        // …and the same request from the principal still carries it, so the test is about the
        // permission and not about the endpoint having quietly stopped returning margins.
        Keys(await _api.GetStringAsync("/api/v1/network-performance")).Should().Contain("marginMinor");
    }

    [Fact]
    public async Task A_denied_permission_stops_working_on_the_next_request()
    {
        await SignInAsPrincipalAsync();
        var invited = await InviteAsync();
        await AcceptAsync(invited.InvitationToken);

        using var asSubAgent = await SignedInClientAsync(SubAgentEmail);

        using var first = await asSubAgent.GetAsync("/api/v1/network-performance");
        first.StatusCode.Should().Be(
            HttpStatusCode.OK, "the report failed with: {0}", await first.Content.ReadAsStringAsync());

        await _api.PostAsJsonAsync(
            $"/api/v1/sub-agents/{invited.Id}/permissions/deny",
            new DenyPermissionRequest("report.view", "Branches do not run reports."));

        // The same token as before. Waiting for it to expire would be fifteen minutes of a
        // permission the principal believes it has already removed.
        (await asSubAgent.GetAsync("/api/v1/network-performance")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Freezing_a_sub_agent_stops_it_selling_and_leaves_it_able_to_sign_in()
    {
        await SignInAsPrincipalAsync();
        var invited = await InviteAsync();
        await AcceptAsync(invited.InvitationToken);

        var frozen = await _api.PostAsJsonAsync(
            $"/api/v1/sub-agents/{invited.Id}/freeze",
            new SubAgentStandingRequest("Unpaid balance with us."));

        frozen.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // It can still sign in: it has travellers mid-journey to look after. What it cannot do is
        // sell, which AgencyAccess decides from the status this set.
        using var asSubAgent = await SignedInClientAsync(SubAgentEmail);
        (await asSubAgent.GetAsync("/api/v1/network-performance")).StatusCode.Should().Be(HttpStatusCode.OK);

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        using var _ = scope.ServiceProvider.GetRequiredService<IPlatformScope>().Enter("test — reading the frozen sub-agent");

        var agency = await db.Agencies.SingleAsync(candidate => candidate.Id == invited.Id);
        agency.Status.Should().Be(Domain.Tenancy.AgencyStatus.Suspended);
        agency.CanTransact.Should().BeFalse();
        agency.StatusReason.Should().Be("Unpaid balance with us.");
    }

    [Fact]
    public async Task A_principal_whose_plan_allows_no_sub_agents_is_refused_before_anything_is_created()
    {
        await SignInAsPrincipalAsync(onAPlanWithSubAgents: false);

        var response = await _api.PostAsJsonAsync(
            "/api/v1/sub-agents",
            new InviteSubAgentRequest("Ikeja Branch Limited", null, SubAgentEmail));

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        using var _ = scope.ServiceProvider.GetRequiredService<IPlatformScope>().Enter("test — looking for an agency that must not exist");

        (await db.Agencies.CountAsync(candidate => candidate.ParentAgencyId != null)).Should().Be(0);
    }

    [Fact]
    public async Task Revoking_a_sub_agent_ends_it_and_kills_its_sessions()
    {
        await SignInAsPrincipalAsync();
        var invited = await InviteAsync();
        await AcceptAsync(invited.InvitationToken);

        var revoked = await _api.PostAsJsonAsync(
            $"/api/v1/sub-agents/{invited.Id}/revoke",
            new SubAgentStandingRequest("The relationship has ended."));

        revoked.StatusCode.Should().Be(HttpStatusCode.NoContent);

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        using var _ = scope.ServiceProvider.GetRequiredService<IPlatformScope>().Enter("test — reading the revoked sub-agent");

        (await db.Agencies.SingleAsync(candidate => candidate.Id == invited.Id))
            .Status.Should().Be(Domain.Tenancy.AgencyStatus.Terminated);

        // Its allowance goes to zero and freezes, so a booking already in flight cannot draw on
        // the principal's money after the relationship has ended.
        var allowance = await db.WalletAllowances.SingleOrDefaultAsync(a => a.SubAgencyId == invited.Id);
        if (allowance is not null)
        {
            allowance.LimitMinor.AmountMinor.Should().Be(0);
        }
    }

    [Fact]
    public async Task An_allowance_is_set_by_the_principal_and_read_by_the_sub_agent()
    {
        await SignInAsPrincipalAsync();
        var invited = await InviteAsync();
        await AcceptAsync(invited.InvitationToken);

        var set = await _api.PutAsJsonAsync(
            $"/api/v1/sub-agents/{invited.Id}/allowance",
            new SetAllowanceRequest(1_000_000, "Monthly"));

        set.StatusCode.Should().Be(HttpStatusCode.OK);

        using var asSubAgent = await SignedInClientAsync(SubAgentEmail);
        var own = await asSubAgent.GetFromJsonAsync<AllowanceResponse>("/api/v1/my-allowance");

        own!.LimitMinor.Should().Be(1_000_000);
        own.RemainingMinor.Should().Be(1_000_000);

        // It reads its own row and can change nothing: every write policy is still the principal's.
        // A 404 rather than a 403, deliberately — the answer is the same whether the sub-agency
        // exists or not, so the endpoint is not a way to find out whose network somebody is in.
        var attempt = await asSubAgent.PutAsJsonAsync(
            $"/api/v1/sub-agents/{invited.Id}/allowance",
            new SetAllowanceRequest(9_999_999, "Monthly"));

        attempt.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>Every property name in a JSON document, however deeply nested.</summary>
    private static List<string> Keys(string json)
    {
        var names = new List<string>();
        Walk(JsonDocument.Parse(json).RootElement);
        return names;

        void Walk(JsonElement element)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (var property in element.EnumerateObject())
                    {
                        names.Add(property.Name);
                        Walk(property.Value);
                    }

                    break;

                case JsonValueKind.Array:
                    foreach (var item in element.EnumerateArray())
                    {
                        Walk(item);
                    }

                    break;

                default:
                    break;
            }
        }
    }

    private static AcceptInvitationRequest Accept(string token) =>
        new(token, "Bola", "Adeyemi", Password, null);

    private async Task SignInAsPrincipalAsync(bool onAPlanWithSubAgents = true)
    {
        await _api.PostAsJsonAsync(
            "/api/v1/auth/register",
            new RegisterAgentRequest("Lagos Travel Limited", "Ada", "Okonkwo", PrincipalEmail, null, "NG", Password));

        var email = await WaitForEmailToAsync(PrincipalEmail);
        var code = System.Text.RegularExpressions.Regex.Match(email.Text, @"\b\d{6}\b").Value;

        await _api.PostAsJsonAsync("/api/v1/auth/verify-email", new VerifyEmailRequest(PrincipalEmail, code));

        _api.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await TokenAsync(PrincipalEmail));

        if (onAPlanWithSubAgents)
        {
            await PutPrincipalOnAPlanWithSubAgentsAsync();
        }
    }

    /// <summary>Subscribes the principal to a plan that allows sub-agents. See <see cref="TestPlans"/>.</summary>
    private async Task PutPrincipalOnAPlanWithSubAgentsAsync()
    {
        Guid principalId;

        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            using var _ = scope.ServiceProvider.GetRequiredService<IPlatformScope>().Enter("test setup — finds the principal");
            principalId = (await db.Agencies.SingleAsync(candidate => candidate.ParentAgencyId == null)).Id;
        }

        await TestPlans.SubscribeAsync(_factory.Services, principalId, new EntitlementGrant(EntitlementCodes.MaxSubAgents, "5"));
    }

    private async Task<InviteSubAgentResponse> InviteAsync()
    {
        var response = await _api.PostAsJsonAsync(
            "/api/v1/sub-agents",
            new InviteSubAgentRequest("Ikeja Branch Limited", null, SubAgentEmail));

        // The body is in the assertion because a 500 here is otherwise a bare status code, and
        // every test in this file starts by inviting somebody.
        response.StatusCode.Should().Be(
            HttpStatusCode.Created, "inviting failed with: {0}", await response.Content.ReadAsStringAsync());

        return (await response.Content.ReadFromJsonAsync<InviteSubAgentResponse>())!;
    }

    /// <summary>
    /// Joins as the invitee would: accepts the invitation, then answers the code sent to the address.
    /// Since issue 170 the account signs in only after the second step.
    /// </summary>
    private async Task AcceptAsync(string token)
    {
        using var anonymous = _factory.CreateClient();
        var accepted = await anonymous.PostAsJsonAsync("/api/v1/invitations/accept", Accept(token));

        accepted.StatusCode.Should().Be(HttpStatusCode.OK);

        var code = CodeIn(await WaitForEmailToAsync(SubAgentEmail, VerificationSubject));
        var verified = await anonymous.PostAsJsonAsync("/api/v1/auth/verify-email", new VerifyEmailRequest(SubAgentEmail, code));

        verified.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    /// <summary>When the account at <paramref name="email"/> proved its address, or null while it has not.</summary>
    private async Task<DateTimeOffset?> VerifiedAtAsync(string email)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        using var _ = scope.ServiceProvider.GetRequiredService<IPlatformScope>().Enter("test — reading whether an address is verified");

        return await db.Users
            .Where(user => user.Email == email)
            .Select(user => user.EmailVerifiedAt)
            .SingleAsync();
    }

    /// <summary>The six-digit code in a verification email.</summary>
    private static string CodeIn(ReceivedEmail email) =>
        System.Text.RegularExpressions.Regex.Match(email.Text, @"\b\d{6}\b").Value;

    private async Task<HttpClient> SignedInClientAsync(string email)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await TokenAsync(email));

        return client;
    }

    private async Task<string> TokenAsync(string email)
    {
        using var anonymous = _factory.CreateClient();
        var response = await anonymous.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(email, Password));

        response.StatusCode.Should().Be(HttpStatusCode.OK, "signing in is the precondition for every test here");

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        return document.RootElement.GetProperty("accessToken").GetString()!;
    }

    private sealed record ReceivedEmail(string Subject, string Html, string Text);

    /// <summary>
    /// The newest email to <paramref name="to"/> — or, given <paramref name="subjectContains"/>, the newest
    /// whose subject says so, because an invitee receives the invitation first and the code after it.
    /// </summary>
    private async Task<ReceivedEmail> WaitForEmailToAsync(string to, string? subjectContains = null)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            using var search = await _mailpitApi.GetAsync(
                new Uri($"/api/v1/search?query=to:{Uri.EscapeDataString(to)}", UriKind.Relative));
            using var list = JsonDocument.Parse(await search.Content.ReadAsStringAsync());

            if (list.RootElement.TryGetProperty("messages", out var messages))
            {
                foreach (var message in messages.EnumerateArray())
                {
                    var subject = message.GetProperty("Subject").GetString() ?? string.Empty;

                    if (subjectContains is not null && !subject.Contains(subjectContains, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var id = message.GetProperty("ID").GetString();

                    using var detail = JsonDocument.Parse(
                        await _mailpitApi.GetStringAsync(new Uri($"/api/v1/message/{id}", UriKind.Relative)));
                    var root = detail.RootElement;

                    return new ReceivedEmail(
                        root.GetProperty("Subject").GetString() ?? string.Empty,
                        root.GetProperty("HTML").GetString() ?? string.Empty,
                        root.GetProperty("Text").GetString() ?? string.Empty);
                }
            }

            await Task.Delay(100);
        }

        throw new TimeoutException($"No email to {to} reached Mailpit within five seconds.");
    }
}
