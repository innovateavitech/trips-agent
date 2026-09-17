using System.Net;
using System.Net.Http.Headers;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TripsAgent.Application.Identity;
using TripsAgent.Domain.Identity;
using TripsAgent.Domain.Tenancy;
using TripsAgent.Infrastructure.Persistence;
using TripsAgent.IntegrationTests.Persistence;

namespace TripsAgent.IntegrationTests.Tenancy;

/// <summary>
/// Who at an agency may touch its KYB submission (issue 172).
/// </summary>
/// <remarks>
/// <para>
/// KYB is the agency's legal identity: the documents Trips verifies it on, and the button that sends
/// them for review. Every route under <c>/api/v1/kyb</c> used to ask only that the caller was signed
/// in, so a counter agent — who can change nothing else about the agency — could replace a document
/// or re-submit the lot. The permission is <c>kyb.submit</c>, and of the agency roles only the Owner
/// holds it: a Manager runs the day-to-day business and does not restructure it.
/// </para>
/// <para>
/// 403 is asserted, never 401: every token here is valid and signed, so a refusal means the
/// permission check fired rather than the sign-in. Where the Owner is let through, the assertion is
/// only that it is <em>not</em> refused — what comes back past authorisation is this agency's own KYB
/// state, which <c>KybSubmissionTests</c> already covers.
/// </para>
/// </remarks>
[Collection(PostgresCollection.Name)]
public sealed class KybAuthorizationTests : IAsyncLifetime, IDisposable
{
    private readonly PostgresFixture _postgres;

    private (string Key, string Value)[] _overrides = [];
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _api = null!;
    private string _database = string.Empty;
    private Guid _agencyId;
    private User _owner = null!;
    private User _agent = null!;

    public KybAuthorizationTests(PostgresFixture postgres) => _postgres = postgres;

    public async Task InitializeAsync()
    {
        _database = $"kyb_authz_{Guid.NewGuid():N}";

        var tenancy = TestTenancy.None();

        await using (var setup = await _postgres.CreateEmptyDatabaseAsync(_database, tenancy.Tenant, tenancy.Scope))
        {
            await setup.Database.MigrateAsync();

            // The same reference data a deploy loads, which is where the new permission and its grant
            // to the Owner role come from — no migration of its own.
            await ReferenceDataSeeder.EnsureAsync(setup, tenancy.Scope);

            using var seeding = tenancy.Scope.Enter("test setup — one agency, its owner and a counter agent");

            var agency = Agency.RegisterPrincipal("Lagos Travel Limited", "lagos-travel", "NG", "NGN", "Africa/Lagos");
            var owner = User.ForAgency(agency.Id, "owner@lagos-travel.test", "not-a-real-hash", "Ada", "Obi");
            var agent = User.ForAgency(agency.Id, "agent@lagos-travel.test", "not-a-real-hash", "Segun", "Oyelaran");

            setup.Agencies.Add(agency);
            setup.Users.AddRange(owner, agent);
            await setup.SaveChangesAsync();

            (_agencyId, _owner, _agent) = (agency.Id, owner, agent);
        }

        // The API runs as the role row-level security polices and migrates as the owner, the same
        // split as production (ADR-0006).
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

    [Theory]
    [InlineData("GET", "/api/v1/kyb/status")]
    [InlineData("GET", "/api/v1/kyb/limits")]
    [InlineData("POST", "/api/v1/kyb/submit")]
    public async Task An_agent_is_refused_the_kyb_routes(string method, string path)
    {
        using var response = await SendAsync(new HttpMethod(method), path, _agent, "Agent", IdentitySeedData.AgentPermissions);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task An_agent_cannot_upload_a_document_either()
    {
        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent("%PDF-1.7\nnot really a certificate\n"u8.ToArray());
        file.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");

        content.Add(file, "file", "certificate.pdf");
        content.Add(new StringContent("CertificateOfIncorporation"), "documentType");

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/kyb/documents") { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer", TokenFor(_agent, "Agent", IdentitySeedData.AgentPermissions));

        using var response = await _api.SendAsync(request);

        // Refused before a byte of the upload is read, let alone stored.
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_manager_is_refused_too_because_kyb_is_the_agencys_legal_identity()
    {
        using var response = await SendAsync(
            HttpMethod.Post, "/api/v1/kyb/submit", _agent, "Manager", IdentitySeedData.ManagerPermissions);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task The_owner_is_not_refused()
    {
        using var status = await SendAsync(
            HttpMethod.Get, "/api/v1/kyb/status", _owner, Role.SystemRoles.Owner, IdentitySeedData.OwnerPermissions);

        status.StatusCode.Should().Be(HttpStatusCode.OK);

        using var submit = await SendAsync(
            HttpMethod.Post, "/api/v1/kyb/submit", _owner, Role.SystemRoles.Owner, IdentitySeedData.OwnerPermissions);

        // Nothing has been uploaded, so this is a 400 about missing documents — past authorisation,
        // which is all this asserts.
        submit.StatusCode.Should().NotBe(HttpStatusCode.Forbidden);
    }

    /// <summary>
    /// The catalogue and the system roles agree on a database seeded the way a deploy seeds one, so an
    /// existing database gets the permission and the Owner's grant on its next <c>migrate</c>.
    /// </summary>
    [Fact]
    public async Task The_permission_is_seeded_and_only_the_owner_and_super_admin_hold_it()
    {
        var tenancy = TestTenancy.None();

        await using var db = _postgres.Connect(
            _database, tenancy.Tenant, tenancy.Scope, asApplicationRole: false);

        using var _ = tenancy.Scope.Enter("test — reading the permission catalogue and the system roles");

        var permission = await db.Permissions.SingleAsync(candidate => candidate.Code == PermissionCodes.KybSubmit);

        var roles = await db.RolePermissions
            .Where(grant => grant.PermissionId == permission.Id)
            .Join(db.Roles, grant => grant.RoleId, role => role.Id, (_, role) => role)
            .Where(role => role.AgencyId == null)
            .Select(role => role.Name)
            .ToListAsync();

        roles.Should().BeEquivalentTo([Role.SystemRoles.Owner, Role.SystemRoles.SuperAdmin]);
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string path,
        User user,
        string role,
        IReadOnlyCollection<string> permissions)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TokenFor(user, role, permissions));

        return await _api.SendAsync(request);
    }

    /// <summary>A real token from the API's own issuer, carrying exactly <paramref name="permissions"/>.</summary>
    private string TokenFor(User user, string role, IReadOnlyCollection<string> permissions)
    {
        var issuer = _factory.Services.GetRequiredService<IAccessTokenIssuer>();

        return issuer.Issue(user, [role], permissions, _agencyId).Value;
    }
}
