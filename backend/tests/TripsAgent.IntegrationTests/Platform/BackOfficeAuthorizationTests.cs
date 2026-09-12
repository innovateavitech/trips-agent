using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TripsAgent.Application.Identity;
using TripsAgent.Contracts.Platform;
using TripsAgent.Domain.Identity;
using TripsAgent.Infrastructure.Persistence;
using TripsAgent.IntegrationTests.Persistence;

namespace TripsAgent.IntegrationTests.Platform;

/// <summary>
/// The back-office routes through the real host: what each role reaches, and what it is refused.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists beside <c>BackOfficeTests</c>.</b> That suite proves a Support account is
/// granted none of the permissions that change an agency — which is the half the database can
/// answer. This is the other half, and the one the acceptance criterion is really about: with a
/// real signed token in a real request, the endpoints themselves refuse. A permission list nobody
/// enforces is a comment.
/// </para>
/// <para>
/// It also pins the tenant boundary at the edge: an agency's own token, however many agency
/// permissions it carries, cannot reach a route that reads across agencies at all. Behind these
/// endpoints the EF filter and row-level security hold the same line (ADR-0006); this is the
/// outermost of the three.
/// </para>
/// <para>
/// 403 is asserted, never 401: every token here is valid and signed, so a refusal means the
/// permission check fired rather than the sign-in. Where a role <em>is</em> allowed through, the
/// assertion is only that it is not refused — the agency ids are invented, so the answer past
/// authorisation is a 404 or a 400, and asserting on that would be testing the wrong thing.
/// </para>
/// </remarks>
[Collection(PostgresCollection.Name)]
public sealed class BackOfficeAuthorizationTests : IAsyncLifetime, IDisposable
{
    private const string Reason = "Documented in the ticket raised by the operations desk.";

    private readonly PostgresFixture _postgres;

    private (string Key, string Value)[] _overrides = [];
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _api = null!;

    public BackOfficeAuthorizationTests(PostgresFixture postgres) => _postgres = postgres;

    public async Task InitializeAsync()
    {
        var database = $"admin_authz_{Guid.NewGuid():N}";

        await using (var setup = await _postgres.CreateEmptyDatabaseAsync(database))
        {
            await setup.Database.MigrateAsync();
        }

        // The API runs as the role row-level security polices and migrates as the owner, the same
        // split as production (ADR-0006).
        _overrides =
        [
            ("ConnectionStrings__Postgres", _postgres.ConnectionStringFor(database, asApplicationRole: true)),
            ("ConnectionStrings__PostgresAdmin", _postgres.ConnectionStringFor(database, asApplicationRole: false)),
        ];

        foreach (var (key, value) in _overrides)
        {
            Environment.SetEnvironmentVariable(key, value);
        }

        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(host => host.UseEnvironment("Development"));

        _api = _factory.CreateClient();
    }

    // ------------------------------------------------------------------ what Support is refused

    [Fact]
    public async Task Support_reads_the_agency_directory_because_answering_questions_needs_it()
    {
        var response = await GetAsync("/api/v1/admin/agencies", Role.SystemRoles.SupportAdmin);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("/api/v1/admin/audit-logs")]
    [InlineData("/api/v1/admin/users")]
    [InlineData("/api/v1/admin/users/roles")]
    [InlineData("/api/v1/admin/dashboard")]
    public async Task Support_is_refused_every_route_its_role_does_not_carry(string path)
    {
        var response = await GetAsync(path, Role.SystemRoles.SupportAdmin);

        response.StatusCode.Should().Be(
            HttpStatusCode.Forbidden,
            "a support account holds agency.view, customer.view and report.view, and nothing else");
    }

    [Fact]
    public async Task Support_cannot_suspend_terminate_or_edit_an_agency()
    {
        var agencyId = Guid.NewGuid();

        foreach (var path in new[]
                 {
                     $"/api/v1/admin/agencies/{agencyId}/suspend",
                     $"/api/v1/admin/agencies/{agencyId}/terminate",
                     $"/api/v1/admin/agencies/{agencyId}/reinstate",
                     $"/api/v1/admin/agencies/{agencyId}/verify",
                 })
        {
            var response = await PostAsync(
                path, new AgencyStatusChangeRequest(Reason), Role.SystemRoles.SupportAdmin);

            response.StatusCode.Should().Be(
                HttpStatusCode.Forbidden, "support answers questions; it does not decide them ({0})", path);
        }
    }

    [Fact]
    public async Task Support_cannot_export_an_agencys_data()
    {
        // The most concentrated read of one agency's data there is — travellers' names and
        // contact details included. Super Admin only.
        var response = await GetAsync(
            $"/api/v1/admin/agencies/{Guid.NewGuid()}/export", Role.SystemRoles.SupportAdmin);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Support_cannot_create_a_back_office_account_and_grant_itself_more()
    {
        var response = await PostAsync(
            "/api/v1/admin/users",
            new CreatePlatformUserRequest(
                "new@tripsagent.example.com", "New", "Starter", Role.SystemRoles.SuperAdmin, Reason),
            Role.SystemRoles.SupportAdmin);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ------------------------------------------------------- what the other three roles do hold

    [Theory]
    [InlineData("/api/v1/admin/agencies")]
    [InlineData("/api/v1/admin/audit-logs")]
    [InlineData("/api/v1/admin/users")]
    [InlineData("/api/v1/admin/dashboard")]
    public async Task A_super_admin_is_refused_nothing(string path)
    {
        var response = await GetAsync(path, Role.SystemRoles.SuperAdmin);

        response.StatusCode.Should().NotBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Operations_reads_the_audit_trail_but_cannot_end_a_customer_relationship()
    {
        (await GetAsync("/api/v1/admin/audit-logs", Role.SystemRoles.OperationsAdmin))
            .StatusCode.Should().NotBe(HttpStatusCode.Forbidden);

        // Ending or pausing a customer relationship is a commercial decision, not an operations one.
        (await PostAsync(
                $"/api/v1/admin/agencies/{Guid.NewGuid()}/terminate",
                new AgencyStatusChangeRequest(Reason),
                Role.SystemRoles.OperationsAdmin))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Finance_reads_the_numbers_and_the_trail_and_manages_nobodys_account()
    {
        (await GetAsync("/api/v1/admin/dashboard", Role.SystemRoles.FinanceAdmin))
            .StatusCode.Should().NotBe(HttpStatusCode.Forbidden);

        (await GetAsync("/api/v1/admin/users", Role.SystemRoles.FinanceAdmin))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // --------------------------------------------------------------------- the tenant boundary

    [Theory]
    [InlineData("/api/v1/admin/agencies")]
    [InlineData("/api/v1/admin/audit-logs")]
    [InlineData("/api/v1/admin/users")]
    [InlineData("/api/v1/admin/dashboard")]
    public async Task An_agencys_own_account_cannot_reach_a_route_that_reads_across_agencies(string path)
    {
        // Every permission an agency's Owner can hold. None of them is a platform permission, and
        // no amount of them adds up to one — which is the point: the back office is not the top of
        // an agency's ladder, it is a different ladder.
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer", AgencyToken(Guid.NewGuid()));

        var response = await _api.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ------------------------------------------------------------------------------- helpers

    private Task<HttpResponseMessage> GetAsync(string path, string roleName) =>
        SendAsync(new HttpRequestMessage(HttpMethod.Get, path), roleName);

    private Task<HttpResponseMessage> PostAsync(string path, object body, string roleName) =>
        SendAsync(
            new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) },
            roleName);

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, string roleName)
    {
        using (request)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", PlatformToken(roleName));
            return await _api.SendAsync(request);
        }
    }

    /// <summary>
    /// A real token for a back-office account: no agency, and exactly the permissions the seeder
    /// grants that role. Taken from <see cref="IdentitySeedData"/> rather than retyped, so a role
    /// that gains a permission gains it here too and this suite keeps testing the real thing.
    /// </summary>
    private string PlatformToken(string roleName)
    {
        var issuer = _factory.Services.GetRequiredService<IAccessTokenIssuer>();
        var user = User.ForPlatform($"{roleName}@tripsagent.example.com", "not-a-real-hash", "Ada", "Obi");

        return issuer.Issue(user, [roleName], PermissionsFor(roleName), rootAgencyId: null).Value;
    }

    /// <summary>A travel agency's own Owner, with everything their side of the line allows.</summary>
    private string AgencyToken(Guid agencyId)
    {
        var issuer = _factory.Services.GetRequiredService<IAccessTokenIssuer>();
        var user = User.ForAgency(agencyId, "owner@lagostravel.example.com", "not-a-real-hash", "Ada", "Obi");

        return issuer.Issue(user, [Role.SystemRoles.Owner], IdentitySeedData.OwnerPermissions, agencyId).Value;
    }

    private static IReadOnlyList<string> PermissionsFor(string roleName) => roleName switch
    {
        Role.SystemRoles.SuperAdmin => IdentitySeedData.SuperAdminPermissions,
        Role.SystemRoles.OperationsAdmin => IdentitySeedData.OperationsAdminPermissions,
        Role.SystemRoles.SupportAdmin => IdentitySeedData.SupportAdminPermissions,
        Role.SystemRoles.FinanceAdmin => IdentitySeedData.FinanceAdminPermissions,
        _ => throw new ArgumentOutOfRangeException(nameof(roleName), roleName, "Not a back-office role."),
    };

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

    /// <summary>See the note on <c>RegistrationEndToEndTests.Dispose</c>: CA1001 needs this.</summary>
    public void Dispose()
    {
        _api?.Dispose();
        _api = null!;
    }
}
