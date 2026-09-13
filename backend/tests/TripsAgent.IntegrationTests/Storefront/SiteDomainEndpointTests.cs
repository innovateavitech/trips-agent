using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TripsAgent.Application.Identity;
using TripsAgent.Application.Storefront;
using TripsAgent.Contracts.Storefront;
using TripsAgent.Domain.Identity;
using TripsAgent.Domain.Platform;
using TripsAgent.Domain.Storefront;
using TripsAgent.Domain.Tenancy;
using TripsAgent.Infrastructure.Persistence;
using TripsAgent.IntegrationTests.Persistence;

namespace TripsAgent.IntegrationTests.Storefront;

/// <summary>
/// An agency's own website addresses through the API (issue 59): connecting one and the records it needs,
/// the checks that verify it, its certificate, making it the main address, and the review brand-like
/// addresses wait for. Development's DNS answers <c>.test</c> names as if their records were in place, and
/// knows nothing of any other name.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class SiteDomainEndpointTests : IAsyncLifetime, IDisposable
{
    private const string Domains = "/api/v1/storefront/domains";
    private const string Reviews = "/api/v1/admin/hostname-reviews";

    private static readonly string[] Edit = [PermissionCodes.StorefrontEdit];
    private static readonly string[] Publish = [PermissionCodes.StorefrontEdit, PermissionCodes.StorefrontPublish];

    private readonly PostgresFixture _postgres;

    private (string Key, string Value)[] _overrides = [];
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _api = null!;
    private string _database = string.Empty;

    private Guid _lagosId;
    private Guid _abujaId;

    public SiteDomainEndpointTests(PostgresFixture postgres) => _postgres = postgres;

    public async Task InitializeAsync()
    {
        _database = $"storefront_domains_{Guid.NewGuid():N}";
        var tenancy = TestTenancy.None();

        await using (var setup = await _postgres.CreateEmptyDatabaseAsync(_database, tenancy.Tenant, tenancy.Scope))
        {
            await setup.Database.MigrateAsync();
            await ReferenceDataSeeder.EnsureAsync(setup, tenancy.Scope);

            var lagos = Agency.RegisterPrincipal("Lagos Travel Limited", "lagos-travel", "NG", "NGN", "Africa/Lagos", tradingName: "Lagos Travel");
            var abuja = Agency.RegisterPrincipal("Abuja Tours Limited", "abuja-tours", "NG", "NGN", "Africa/Lagos");
            lagos.MarkVerified(DateTimeOffset.UtcNow);
            abuja.MarkVerified(DateTimeOffset.UtcNow);
            setup.Agencies.AddRange(lagos, abuja);
            await setup.SaveChangesAsync();

            (_lagosId, _abujaId) = (lagos.Id, abuja.Id);
        }

        // Environment variables, because a configuration source added through the factory loses to
        // appsettings.Development.json. See RegistrationEndToEndTests.
        _overrides =
        [
            ("ConnectionStrings__Postgres", _postgres.ConnectionStringFor(_database, asApplicationRole: true)),
            ("ConnectionStrings__PostgresAdmin", _postgres.ConnectionStringFor(_database, asApplicationRole: false)),
        ];

        foreach (var (key, value) in _overrides)
        {
            Environment.SetEnvironmentVariable(key, value);
        }

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host => host.UseEnvironment("Development"));
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

    [Fact]
    public async Task A_custom_hostname_is_connected_with_the_two_records_to_create()
    {
        await CreateSiteAsync(_lagosId);

        var domain = await AddAsync(_lagosId, "https://WWW.LagosTravel.test/about-us");

        domain.Hostname.Should().Be("www.lagostravel.test", "a pasted address keeps only its hostname");
        domain.Type.Should().Be("Custom");
        domain.VerificationStatus.Should().Be("Pending");
        domain.CanMakePrimary.Should().BeFalse();
        domain.CanCheckNow.Should().BeTrue();

        var txt = domain.DnsRecords.Single(record => record.RecordType == "TXT");
        txt.Name.Should().Be("_storefront-verify.www.lagostravel.test");
        txt.HostLabel.Should().Be("_storefront-verify.www");
        txt.Value.Should().MatchRegex("^[0-9a-f]{40}$");

        var cname = domain.DnsRecords.Single(record => record.RecordType == "CNAME");
        cname.HostLabel.Should().Be("www");
        cname.Value.Should().Be("sites.localhost");

        var all = await GetAsync<List<SiteDomainResponse>>(Domains, _lagosId, Edit);
        all.Select(entry => entry.Hostname).Should().Equal("lagos-travel.localhost", "www.lagostravel.test");
        all[0].IsPrimary.Should().BeTrue();
        all[0].CanRemove.Should().BeFalse();
    }

    [Fact]
    public async Task An_address_that_cannot_be_connected_is_refused_with_the_reason()
    {
        await CreateSiteAsync(_lagosId);

        (string Input, string Reason)[] refused =
        [
            ("lagostravel.test", "www.lagostravel.test"),
            ("shop.localhost", "free addresses"),
            ("book.sites.localhost", "free addresses"),
            ("not a hostname", "at least two parts"),
        ];

        foreach (var (input, reason) in refused)
        {
            using var response = await SendAsync(HttpMethod.Post, Domains, new AddSiteDomainRequest(input), _lagosId, Publish);

            response.StatusCode.Should().Be(HttpStatusCode.BadRequest, input);
            (await response.Content.ReadAsStringAsync()).Should().Contain(reason);
        }
    }

    [Fact]
    public async Task A_website_connects_each_address_once_and_at_most_five_of_its_own()
    {
        await CreateSiteAsync(_lagosId);

        for (var number = 1; number <= SiteDomain.MaxCustomDomainsPerSite; number++)
        {
            await AddAsync(_lagosId, $"www{number}.lagostravel.test");
        }

        using (var again = await SendAsync(HttpMethod.Post, Domains, new AddSiteDomainRequest("WWW1.lagostravel.test"), _lagosId, Publish))
        {
            again.StatusCode.Should().Be(HttpStatusCode.Conflict);
        }

        using var sixth = await SendAsync(HttpMethod.Post, Domains, new AddSiteDomainRequest("www6.lagostravel.test"), _lagosId, Publish);
        sixth.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Checking_now_verifies_a_hostname_the_certificate_job_secures_it_and_it_can_become_the_main_address()
    {
        await CreateSiteAsync(_lagosId);
        var added = await AddAsync(_lagosId, "www.lagostravel.test");

        var verified = await CheckAsync(added.Id, _lagosId);

        verified.VerificationStatus.Should().Be("Verified");
        verified.SslStatus.Should().Be("Pending");
        verified.CanMakePrimary.Should().BeFalse("it has no certificate yet");
        verified.RecentChecks.Select(check => check.Outcome).Should().Equal("Match", "Match");

        (await RunAsync<CertificateSweep>(sweep => sweep.RunAsync())).Should().Be(1);

        var secured = await GetDomainAsync(added.Id, _lagosId);
        secured.SslStatus.Should().Be("Issued");
        secured.SslExpiresAt.Should().BeCloseTo(DateTimeOffset.UtcNow.AddDays(90), TimeSpan.FromMinutes(5));
        secured.CanMakePrimary.Should().BeTrue();

        var primary = await PostAsync<SiteDomainResponse>($"{Domains}/{added.Id}/primary", _lagosId, Publish);
        primary.IsPrimary.Should().BeTrue();
        (await GetAsync<SiteResponse>("/api/v1/storefront/site", _lagosId, Edit)).PrimaryHostname.Should().Be("www.lagostravel.test");

        using (var removed = await SendAsync(HttpMethod.Delete, $"{Domains}/{added.Id}", null, _lagosId, Publish))
        {
            removed.StatusCode.Should().Be(HttpStatusCode.NoContent);
        }

        (await GetAsync<SiteResponse>("/api/v1/storefront/site", _lagosId, Edit)).PrimaryHostname
            .Should().Be("lagos-travel.localhost", "the free address takes over when the main one goes");

        await using var owner = _postgres.Connect(_database, asApplicationRole: false);
        (await owner.OutboxMessages.Select(message => message.MessageType).ToListAsync())
            .Should().Contain(type => type.Contains(nameof(SiteDomainsChanged)), "the storefront's host cache must hear of it");
    }

    [Fact]
    public async Task A_hostname_whose_records_are_missing_stays_pending_and_the_check_says_what_was_found()
    {
        await CreateSiteAsync(_lagosId);
        var added = await AddAsync(_lagosId, "www.lagostravel.com");

        var result = await CheckAsync(added.Id, _lagosId);

        result.VerificationStatus.Should().Be("Pending");
        result.NextCheckAt.Should().NotBeNull();
        result.RecentChecks.Should().HaveCount(2)
            .And.OnlyContain(check => check.Outcome == "NotFound" && check.Resolver == "development");

        using var tooSoon = await SendAsync(HttpMethod.Post, $"{Domains}/{added.Id}/check", null, _lagosId, Edit);
        tooSoon.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task The_verification_job_checks_every_agencys_hostnames_that_are_due_and_no_others()
    {
        await CreateSiteAsync(_lagosId);
        await CreateSiteAsync(_abujaId);
        var lagos = await AddAsync(_lagosId, "www.lagostravel.test");
        var abuja = await AddAsync(_abujaId, "www.abujatours.test");
        var notYet = await AddAsync(_abujaId, "book.abujatours.test");

        await MakeDueAsync(lagos.Id, abuja.Id);

        (await RunAsync<DomainVerificationSweep>(sweep => sweep.RunAsync())).Should().Be(2);

        (await GetDomainAsync(lagos.Id, _lagosId)).VerificationStatus.Should().Be("Verified");
        (await GetDomainAsync(abuja.Id, _abujaId)).VerificationStatus.Should().Be("Verified");
        (await GetDomainAsync(notYet.Id, _abujaId)).VerificationStatus.Should().Be("Pending", "its first check is a couple of minutes after it was added");
    }

    [Fact]
    public async Task Two_agencies_may_claim_a_hostname_but_only_the_first_to_verify_holds_it()
    {
        await CreateSiteAsync(_lagosId);
        await CreateSiteAsync(_abujaId);

        // Getting there first blocks nobody: an unproven claim may overlap another.
        var abujaClaim = await AddAsync(_abujaId, "www.shared-name.test");
        var lagosClaim = await AddAsync(_lagosId, "www.shared-name.test");

        (await CheckAsync(lagosClaim.Id, _lagosId)).VerificationStatus.Should().Be("Verified");

        var lost = await CheckAsync(abujaClaim.Id, _abujaId);
        lost.VerificationStatus.Should().Be("Pending");
        lost.RecentChecks.Should().Contain(check => check.Detail == DomainVerifier.HeldElsewhere);

        using (var removed = await SendAsync(HttpMethod.Delete, $"{Domains}/{abujaClaim.Id}", null, _abujaId, Publish))
        {
            removed.StatusCode.Should().Be(HttpStatusCode.NoContent);
        }

        using var taken = await SendAsync(HttpMethod.Post, Domains, new AddSiteDomainRequest("www.shared-name.test"), _abujaId, Publish);
        taken.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await taken.Content.ReadAsStringAsync()).Should().Contain("another website");
    }

    [Fact]
    public async Task A_brand_like_hostname_waits_for_a_platform_review_and_can_become_the_main_address_once_cleared()
    {
        await CreateSiteAsync(_lagosId);
        var added = await AddAsync(_lagosId, "www.emirates-deals.test");
        added.NeedsReview.Should().BeTrue();

        (await CheckAsync(added.Id, _lagosId)).VerificationStatus.Should().Be("Verified");
        await RunAsync<CertificateSweep>(sweep => sweep.RunAsync());
        (await GetDomainAsync(added.Id, _lagosId)).CanMakePrimary.Should().BeFalse("it is waiting for a review");

        using (var agency = await SendAsync(HttpMethod.Get, Reviews, null, _lagosId, Publish))
        {
            agency.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }

        var queue = await PlatformAsync<List<HostnameReviewResponse>>(HttpMethod.Get, Reviews);
        queue.Should().ContainSingle(item => item.DomainId == added.Id).Which.AgencyName.Should().Be("Lagos Travel");

        var approved = await PlatformAsync<HostnameReviewResponse>(HttpMethod.Post, $"{Reviews}/{added.Id}/approve");
        approved.NeedsReview.Should().BeFalse();

        (await GetDomainAsync(added.Id, _lagosId)).CanMakePrimary.Should().BeTrue();
        (await PlatformAsync<List<HostnameReviewResponse>>(HttpMethod.Get, Reviews)).Should().BeEmpty();

        await using var owner = _postgres.Connect(_database, asApplicationRole: false);
        (await owner.AdminAlerts.Where(alert => alert.EntityId == added.Id).Select(alert => alert.Status).ToListAsync())
            .Should().Equal(AdminAlertStatus.Resolved);
    }

    [Fact]
    public async Task Connecting_needs_the_publish_permission_the_free_address_stays_and_another_agencys_address_is_not_there()
    {
        await CreateSiteAsync(_lagosId);
        await CreateSiteAsync(_abujaId);

        using (var editor = await SendAsync(HttpMethod.Post, Domains, new AddSiteDomainRequest("www.lagostravel.test"), _lagosId, Edit))
        {
            editor.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }

        var free = (await GetAsync<List<SiteDomainResponse>>(Domains, _lagosId, Edit)).Single();

        using (var remove = await SendAsync(HttpMethod.Delete, $"{Domains}/{free.Id}", null, _lagosId, Publish))
        {
            remove.StatusCode.Should().Be(HttpStatusCode.Conflict);
        }

        using var foreign = await SendAsync(HttpMethod.Delete, $"{Domains}/{free.Id}", null, _abujaId, Publish);
        foreign.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ------------------------------------------------------------------ helpers

    private async Task CreateSiteAsync(Guid agencyId)
    {
        using var response = await SendAsync(HttpMethod.Post, "/api/v1/storefront/site", new CreateSiteRequest("horizon"), agencyId, Edit);
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
    }

    private async Task<SiteDomainResponse> AddAsync(Guid agencyId, string hostname)
    {
        using var response = await SendAsync(HttpMethod.Post, Domains, new AddSiteDomainRequest(hostname), agencyId, Publish);
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());

        return (await response.Content.ReadFromJsonAsync<SiteDomainResponse>())!;
    }

    private Task<SiteDomainResponse> CheckAsync(Guid domainId, Guid agencyId) =>
        PostAsync<SiteDomainResponse>($"{Domains}/{domainId}/check", agencyId, Edit);

    private async Task<SiteDomainResponse> GetDomainAsync(Guid domainId, Guid agencyId) =>
        (await GetAsync<List<SiteDomainResponse>>(Domains, agencyId, Edit)).Single(domain => domain.Id == domainId);

    /// <summary>Moves the next scheduled look into the past, as the clock would.</summary>
    private async Task MakeDueAsync(params Guid[] domainIds)
    {
        await using var owner = _postgres.Connect(_database, asApplicationRole: false);

        foreach (var id in domainIds)
        {
            await owner.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE storefront.site_domains SET next_check_at = now() - interval '1 minute' WHERE id = {id}");
        }
    }

    /// <summary>Runs a sweep the way the Worker's job does: in its own scope, with no agency.</summary>
    private async Task<int> RunAsync<TSweep>(Func<TSweep, Task<int>> run)
        where TSweep : notnull
    {
        using var scope = _factory.Services.CreateScope();

        return await run(scope.ServiceProvider.GetRequiredService<TSweep>());
    }

    private async Task<T> GetAsync<T>(string path, Guid agencyId, IReadOnlyCollection<string> permissions)
    {
        using var response = await SendAsync(HttpMethod.Get, path, null, agencyId, permissions);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private async Task<T> PostAsync<T>(string path, Guid agencyId, IReadOnlyCollection<string> permissions)
    {
        using var response = await SendAsync(HttpMethod.Post, path, null, agencyId, permissions);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private async Task<T> PlatformAsync<T>(HttpMethod method, string path)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", PlatformToken());

        using var response = await _api.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string path,
        object? body,
        Guid agencyId,
        IReadOnlyCollection<string> permissions)
    {
        using var request = new HttpRequestMessage(method, path);

        if (body is not null)
        {
            request.Content = JsonContent.Create(body, body.GetType());
        }

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TokenFor(agencyId, permissions));

        return await _api.SendAsync(request);
    }

    private string TokenFor(Guid agencyId, IReadOnlyCollection<string> permissions)
    {
        var issuer = _factory.Services.GetRequiredService<IAccessTokenIssuer>();
        var user = User.ForAgency(agencyId, "owner@agency.test", "not-a-real-hash", "Ngozi", "Adeyemi");

        return issuer.Issue(user, ["Owner"], permissions, agencyId).Value;
    }

    private string PlatformToken()
    {
        var issuer = _factory.Services.GetRequiredService<IAccessTokenIssuer>();
        var user = User.ForPlatform("reviewer@platform.test", "not-a-real-hash", "Ada", "Obi");

        return issuer.Issue(user, ["PlatformAdmin"], [PermissionCodes.AgencyManage], rootAgencyId: null).Value;
    }
}
