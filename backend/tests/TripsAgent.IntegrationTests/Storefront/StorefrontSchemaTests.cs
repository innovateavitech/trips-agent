using System.Runtime.CompilerServices;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using TripsAgent.Application.Storefront;
using TripsAgent.Contracts.Storefront;
using TripsAgent.Domain.Platform;
using TripsAgent.Domain.Storefront;
using TripsAgent.Domain.Tenancy;
using TripsAgent.Infrastructure.Persistence;
using TripsAgent.IntegrationTests.Persistence;

namespace TripsAgent.IntegrationTests.Storefront;

/// <summary>
/// The website tables, proven against PostgreSQL: one agency cannot reach another's site even with the
/// EF filter switched off, a version that has been staged can never change, and every child row belongs
/// to a parent of its own agency.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class StorefrontSchemaTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 11, 9, 0, 0, TimeSpan.Zero);

    /// <summary>Long enough for the verification rules; plainly not a real verification value.</summary>
    private static readonly string VerificationValue = new('v', 32);

    private readonly PostgresFixture _postgres;

    public StorefrontSchemaTests(PostgresFixture postgres) => _postgres = postgres;

    [Fact]
    public async Task Agency_A_cannot_see_agency_Bs_site_even_with_the_EF_filter_removed()
    {
        var world = await WorldAsync();
        var tenancy = TestTenancy.For(world.AgencyA);
        await using var asA = _postgres.Connect(world.Database, tenancy.Tenant, tenancy.Scope);

        (await asA.Sites.IgnoreQueryFilters().Select(site => site.AgencyId).ToListAsync()).Should().Equal(world.AgencyA);
        (await asA.SiteVersions.IgnoreQueryFilters().Select(version => version.AgencyId).Distinct().ToListAsync()).Should().Equal(world.AgencyA);
        (await asA.SitePages.IgnoreQueryFilters().Select(page => page.AgencyId).Distinct().ToListAsync()).Should().Equal(world.AgencyA);
        (await asA.SiteBlocks.IgnoreQueryFilters().Select(block => block.AgencyId).Distinct().ToListAsync()).Should().Equal(world.AgencyA);
        (await asA.SiteDomains.IgnoreQueryFilters().Select(domain => domain.AgencyId).Distinct().ToListAsync()).Should().Equal(world.AgencyA);
    }

    [Fact]
    public async Task A_raw_update_cannot_reach_another_agencys_page()
    {
        var world = await WorldAsync();
        var tenancy = TestTenancy.For(world.AgencyA);
        await using var asA = _postgres.Connect(world.Database, tenancy.Tenant, tenancy.Scope);

        var updated = await asA.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE storefront.site_pages SET title = 'Taken over' WHERE agency_id = {world.AgencyB}");

        updated.Should().Be(0);
    }

    [Fact]
    public async Task A_live_versions_snapshot_cannot_be_rewritten_even_by_the_owner()
    {
        var world = await WorldAsync();
        await using var owner = _postgres.Connect(world.Database, asApplicationRole: false);

        var act = () => owner.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE storefront.site_versions SET content_snapshot = '{{}}'::jsonb WHERE id = {world.LiveA}");

        (await act.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.RestrictViolation);
    }

    [Fact]
    public async Task A_page_cannot_be_added_to_a_frozen_version()
    {
        var world = await WorldAsync();
        await using var owner = _postgres.Connect(world.Database, asApplicationRole: false);

        var act = () => InsertPageAsync(owner, world.AgencyA, world.LiveA, "late");

        (await act.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.RestrictViolation);
    }

    [Fact]
    public async Task A_page_cannot_belong_to_another_agencys_version()
    {
        var world = await WorldAsync();
        await using var owner = _postgres.Connect(world.Database, asApplicationRole: false);

        var act = () => InsertPageAsync(owner, world.AgencyA, world.DraftB, "sneaky");

        (await act.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.ForeignKeyViolation);
    }

    [Fact]
    public async Task Two_live_versions_for_one_site_are_refused_when_the_transaction_commits()
    {
        var world = await WorldAsync();
        await using var owner = _postgres.Connect(world.Database, asApplicationRole: false);
        await using var transaction = await owner.Database.BeginTransactionAsync();

        // Deferred: inside the transaction the second live version is allowed to exist for a moment…
        await owner.Database.ExecuteSqlInterpolatedAsync(
            $$"""
             INSERT INTO storefront.site_versions
                 (id, agency_id, site_id, version_no, status, content_snapshot, theme_snapshot,
                  staged_at, published_at, created_at, updated_at)
             VALUES ({{Guid.CreateVersion7()}}, {{world.AgencyA}}, {{world.SiteA}}, 2, 'Published',
                     '{}'::jsonb, '{}'::jsonb, now(), now(), now(), now())
             """);

        // …but it can never be committed.
        var commit = () => transaction.CommitAsync();

        (await commit.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.ExclusionViolation);
    }

    [Fact]
    public async Task The_application_role_reads_templates_but_cannot_write_them_or_delete_a_version()
    {
        var world = await WorldAsync();
        var tenancy = TestTenancy.For(world.AgencyA);
        await using var asA = _postgres.Connect(world.Database, tenancy.Tenant, tenancy.Scope);

        (await asA.SiteTemplates.CountAsync()).Should().Be(SiteTemplateCatalog.All.Count);

        var write = () => asA.Database.ExecuteSqlRawAsync("UPDATE storefront.site_templates SET name = 'Mine'");
        (await write.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);

        var delete = () => asA.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM storefront.site_versions WHERE agency_id = {world.AgencyA}");
        (await delete.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
    }

    [Fact]
    public async Task A_hostname_belongs_to_one_agency_and_is_stored_in_lower_case()
    {
        var world = await WorldAsync();
        await using var owner = _postgres.Connect(world.Database, asApplicationRole: false);

        var taken = () => InsertCustomDomainAsync(owner, world.AgencyB, world.SiteB, "lagos-travel.localhost");
        (await taken.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.UniqueViolation);

        var shouting = () => InsertCustomDomainAsync(owner, world.AgencyB, world.SiteB, "WWW.ABUJA-TOURS.COM");
        (await shouting.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.CheckViolation);
    }

    [Fact]
    public async Task The_admin_queue_accepts_hostname_reviews_and_ledger_alerts()
    {
        var world = await WorldAsync();
        await using var owner = _postgres.Connect(world.Database, asApplicationRole: false);

        owner.AdminAlerts.AddRange(
            AdminAlert.ForPlatform(AdminAlertType.HostnameReview, AdminAlertSeverity.Warning, "Looks like a brand.", "test", world.AgencyA),
            AdminAlert.ForPlatform(AdminAlertType.LedgerIntegrity, AdminAlertSeverity.Critical, "The books do not balance.", "test"));

        var save = () => owner.SaveChangesAsync();

        await save.Should().NotThrowAsync();
    }

    private static Task<int> InsertPageAsync(AppDbContext db, Guid agencyId, Guid? versionId, string slug) =>
        db.Database.ExecuteSqlInterpolatedAsync(
            $"""
             INSERT INTO storefront.site_pages
                 (id, agency_id, version_id, slug, page_type, title, is_system, show_in_nav, position,
                  revision, created_at, updated_at)
             VALUES ({Guid.CreateVersion7()}, {agencyId}, {versionId}, {slug}, 'Custom', 'Page', false, true, 9,
                     0, now(), now())
             """);

    private static Task<int> InsertCustomDomainAsync(AppDbContext db, Guid agencyId, Guid siteId, string hostname) =>
        db.Database.ExecuteSqlInterpolatedAsync(
            $"""
             INSERT INTO storefront.site_domains
                 (id, agency_id, site_id, hostname, type, verification_status, verification_token,
                  check_count, ssl_status, ssl_attempt_count, needs_review, created_at, updated_at)
             VALUES ({Guid.CreateVersion7()}, {agencyId}, {siteId}, {hostname}, 'Custom', 'Pending', {VerificationValue},
                     0, 'None', 0, false, now(), now())
             """);

    private async Task<World> WorldAsync([CallerMemberName] string testName = "")
    {
        var name = $"sf_{testName.ToLowerInvariant()}";
        name = name[..Math.Min(name.Length, 60)];

        var tenancy = TestTenancy.None();
        await using var setup = await _postgres.CreateEmptyDatabaseAsync(name, tenancy.Tenant, tenancy.Scope);
        await setup.Database.MigrateAsync();
        await ReferenceDataSeeder.EnsureAsync(setup, tenancy.Scope);

        using var _ = tenancy.Scope.Enter("test setup — two agencies, each with a website");

        var a = Agency.RegisterPrincipal("Lagos Travel Limited", "lagos-travel", "NG", "NGN", "Africa/Lagos");
        var b = Agency.RegisterPrincipal("Abuja Tours Limited", "abuja-tours", "NG", "NGN", "Africa/Lagos");
        setup.Agencies.AddRange(a, b);
        await setup.SaveChangesAsync();

        var template = await setup.SiteTemplates.FirstAsync();
        var siteA = AddSite(setup, a, template.Id, "lagos-travel.localhost", publish: true);
        var siteB = AddSite(setup, b, template.Id, "abuja-tours.localhost", publish: false);
        await setup.SaveChangesAsync();

        return new World(name, a.Id, b.Id, siteA.Site.Id, siteB.Site.Id, siteA.Live!.Id, siteB.Draft.Id);
    }

    private static (Site Site, SiteVersion Draft, SiteVersion? Live) AddSite(
        AppDbContext db,
        Agency agency,
        Guid templateId,
        string hostname,
        bool publish)
    {
        var site = Site.Create(agency.Id, templateId, agency.LegalName);
        var draft = SiteVersion.CreateDraft(site);
        site.AttachDraft(draft);

        var home = SitePage.Create(draft, SitePageType.Home, "home", "Home", true, 0);
        home.ReplaceBlocks(
        [
            new SiteBlockContent(null, SiteBlockType.Text, SiteBlocks.ToStored(SiteBlocks.Text(new TextBlockConfig(null, "Welcome.")))),
        ]);

        var domain = SiteDomain.ForSubdomain(site, hostname, Now);
        site.SetPrimaryDomain(domain);

        db.Sites.Add(site);
        db.SiteVersions.Add(draft);
        db.SitePages.Add(home);
        db.SiteDomains.Add(domain);

        if (!publish)
        {
            return (site, draft, null);
        }

        var live = SiteVersion.Stage(site, 1, """{"schemaVersion":1}""", """{"schemaVersion":1}""", null, Now);
        db.SiteVersions.Add(live);
        site.Publish(live, null, null, Now);

        return (site, draft, live);
    }

    private sealed record World(
        string Database,
        Guid AgencyA,
        Guid AgencyB,
        Guid SiteA,
        Guid SiteB,
        Guid LiveA,
        Guid DraftB);
}
