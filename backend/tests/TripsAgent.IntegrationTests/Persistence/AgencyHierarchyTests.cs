using System.Runtime.CompilerServices;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using TripsAgent.Application.Tenancy;
using TripsAgent.Domain.Tenancy;
using TripsAgent.Infrastructure.Persistence;
using TripsAgent.Infrastructure.Tenancy;

namespace TripsAgent.IntegrationTests.Persistence;

/// <summary>
/// Covers the agency hierarchy: the materialised <c>ltree</c> path, the GIST index that makes
/// subtree queries cheap, and the constraints that keep the tree well-formed.
/// </summary>
[Collection(PostgresCollection.Name)]
public class AgencyHierarchyTests
{
    private readonly PostgresFixture _postgres;

    // These tests inspect several agencies at once, which is inherently a cross-tenant view —
    // so they hold the scope their context reads and enter it where they read back through EF.
    private readonly (TenantContext Tenant, PlatformScope Scope) _tenancy = TestTenancy.None();

    public AgencyHierarchyTests(PostgresFixture postgres) => _postgres = postgres;

    // -------------------------------------------------------------------- path maintenance

    [Fact]
    public async Task A_principal_gets_a_single_label_path()
    {
        await using var context = await MigratedDatabaseAsync();

        var principal = NewPrincipal("acme-travel");
        context.Agencies.Add(principal);
        await context.SaveChangesAsync();

        // The label is the id with hyphens swapped for underscores — ltree allows only letters,
        // digits and underscores in a label.
        principal.Path.Should().Be(principal.Id.ToString().Replace('-', '_'));
    }

    [Fact]
    public async Task A_sub_agent_path_is_the_parent_path_plus_its_own_label()
    {
        await using var context = await MigratedDatabaseAsync();

        var principal = NewPrincipal("acme-travel");
        context.Agencies.Add(principal);
        await context.SaveChangesAsync();

        var subAgent = Agency.RegisterSubAgent(principal, "Acme Ikeja Limited", "acme-ikeja");
        context.Agencies.Add(subAgent);
        await context.SaveChangesAsync();

        subAgent.Path.Should().Be($"{principal.Path}.{subAgent.Id.ToString().Replace('-', '_')}");
    }

    [Fact]
    public async Task The_application_never_has_to_set_the_path()
    {
        await using var context = await MigratedDatabaseAsync();

        var principal = NewPrincipal("acme-travel");

        // Nothing assigned it — the domain object goes to the database with an empty path.
        principal.Path.Should().BeEmpty();

        context.Agencies.Add(principal);
        await context.SaveChangesAsync();

        // ...and comes back with the one the trigger computed.
        principal.Path.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Reparenting_a_sub_agent_rewrites_its_path()
    {
        await using var context = await MigratedDatabaseAsync();

        var first = NewPrincipal("first-principal");
        var second = NewPrincipal("second-principal");
        context.Agencies.AddRange(first, second);
        await context.SaveChangesAsync();

        var subAgent = Agency.RegisterSubAgent(first, "Moving Branch Limited", "moving-branch");
        context.Agencies.Add(subAgent);
        await context.SaveChangesAsync();

        subAgent.Path.Should().StartWith(first.Path);

        // Reparent in SQL: the domain has no reparent operation yet, and the point of the
        // trigger is that it holds even when the change does not come through the model.
        await context.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE tenancy.agencies SET parent_agency_id = {second.Id} WHERE id = {subAgent.Id}");

        context.ChangeTracker.Clear();

        using var _ = _tenancy.Scope.Enter("test — reading an agency from outside its own tenant");
        var reloaded = await context.Agencies.SingleAsync(a => a.Id == subAgent.Id);

        reloaded.Path.Should().Be($"{second.Path}.{subAgent.Id.ToString().Replace('-', '_')}");
    }

    // ------------------------------------------------------------------------ subtree query

    [Fact]
    public async Task A_principal_reads_its_whole_subtree_in_one_query()
    {
        await using var context = await MigratedDatabaseAsync();

        var principal = NewPrincipal("network-principal");
        var unrelated = NewPrincipal("unrelated-principal");
        context.Agencies.AddRange(principal, unrelated);
        await context.SaveChangesAsync();

        var branchA = Agency.RegisterSubAgent(principal, "Branch A Limited", "branch-a");
        var branchB = Agency.RegisterSubAgent(principal, "Branch B Limited", "branch-b");
        var otherBranch = Agency.RegisterSubAgent(unrelated, "Other Branch Limited", "other-branch");
        context.Agencies.AddRange(branchA, branchB, otherBranch);
        await context.SaveChangesAsync();

        var subtree = await QuerySubtreeIdsAsync(context, principal.Path);

        // The principal itself plus its two branches — and nothing from the other tree.
        subtree.Should().BeEquivalentTo([principal.Id, branchA.Id, branchB.Id]);
        subtree.Should().NotContain(otherBranch.Id);
    }

    [Fact]
    public async Task The_subtree_query_uses_the_GIST_index()
    {
        await using var context = await MigratedDatabaseAsync();

        var principal = NewPrincipal("indexed-principal");
        context.Agencies.Add(principal);
        await context.SaveChangesAsync();

        // A planner will happily seq-scan a tiny table however good the index is, so give it
        // enough rows that the index is genuinely the cheaper plan, and disable seq scans so
        // the answer is about whether the index *can* serve this query at all.
        await SeedManyPrincipalsAsync(context, count: 500);

        var plan = await ExplainSubtreeQueryAsync(context, principal.Path);

        plan.Should().Contain("ix_agencies_path_gist",
            $"the subtree query must be index-assisted, but the planner chose:\n{plan}");
    }

    // -------------------------------------------------------------------------- constraints

    [Fact]
    public async Task The_hierarchy_is_capped_at_two_levels()
    {
        await using var context = await MigratedDatabaseAsync();

        var principal = NewPrincipal("deep-principal");
        context.Agencies.Add(principal);
        await context.SaveChangesAsync();

        var subAgent = Agency.RegisterSubAgent(principal, "Level Two Limited", "level-two");
        context.Agencies.Add(subAgent);
        await context.SaveChangesAsync();

        // Bypass the domain guard entirely and try to hang a third level off the sub-agent.
        var act = async () => await context.Database.ExecuteSqlInterpolatedAsync(
            $"""
             INSERT INTO tenancy.agencies
                 (id, parent_agency_id, type, status, legal_name, slug, country_code,
                  base_currency, timezone, vat_rate_basis_points, created_at, updated_at)
             VALUES
                 ({Guid.CreateVersion7()}, {subAgent.Id}, 'SubAgent', 'PendingVerification',
                  'Level Three Limited', 'level-three', 'NG', 'NGN', 'Africa/Lagos', 750,
                  now(), now())
             """);

        (await act.Should().ThrowAsync<PostgresException>())
            .Which.ConstraintName.Should().Be("ck_agencies_depth");
    }

    [Fact]
    public async Task The_domain_refuses_a_sub_agent_of_a_sub_agent()
    {
        await using var context = await MigratedDatabaseAsync();

        var principal = NewPrincipal("guarded-principal");
        context.Agencies.Add(principal);
        await context.SaveChangesAsync();

        var subAgent = Agency.RegisterSubAgent(principal, "Only Level Limited", "only-level");
        context.Agencies.Add(subAgent);
        await context.SaveChangesAsync();

        // The database catches it, but failing here gives a far better message than a
        // constraint violation would.
        var act = () => Agency.RegisterSubAgent(subAgent, "Too Deep Limited", "too-deep");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Only a principal can have sub-agents*");
    }

    [Fact]
    public async Task A_slug_is_unique_across_the_platform()
    {
        await using var context = await MigratedDatabaseAsync();

        context.Agencies.Add(NewPrincipal("duplicate-slug"));
        await context.SaveChangesAsync();

        context.Agencies.Add(NewPrincipal("duplicate-slug"));

        var act = async () => await context.SaveChangesAsync();

        var thrown = await act.Should().ThrowAsync<DbUpdateException>();

        thrown.Which.InnerException.Should().BeOfType<PostgresException>()
            .Which.ConstraintName.Should().Be("ix_agencies_slug");
    }

    [Theory]
    [InlineData("Lagos Travel")]     // spaces
    [InlineData("lagos_travel")]     // underscore
    [InlineData("-lagos")]           // leading hyphen
    [InlineData("lagos-")]           // trailing hyphen
    [InlineData("lagos--travel")]    // doubled hyphen
    [InlineData("")]                 // empty
    public void The_domain_rejects_a_slug_that_is_not_url_safe(string slug)
    {
        var act = () => NewPrincipal(slug);

        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("LagosTravel", "lagostravel")]
    [InlineData("Lagos-Travel", "lagos-travel")]
    [InlineData("  lagos-travel  ", "lagos-travel")]
    public void A_slug_differing_only_in_case_or_padding_is_normalised(string given, string expected)
    {
        // Case and stray whitespace are a typo, not a different agency — normalise rather than
        // reject, so nobody loses a signup to a capital letter. Anything that is genuinely not
        // URL-safe still throws.
        NewPrincipal(given).Slug.Should().Be(expected);
    }

    [Fact]
    public async Task The_database_rejects_a_slug_that_is_not_url_safe()
    {
        await using var context = await MigratedDatabaseAsync();

        // Inserted around the domain, the way a seed script or a hand-written migration would.
        var act = async () => await context.Database.ExecuteSqlInterpolatedAsync(
            $"""
             INSERT INTO tenancy.agencies
                 (id, parent_agency_id, type, status, legal_name, slug, country_code,
                  base_currency, timezone, vat_rate_basis_points, created_at, updated_at)
             VALUES
                 ({Guid.CreateVersion7()}, NULL, 'Principal', 'PendingVerification',
                  'Bad Slug Limited', 'Not A Slug', 'NG', 'NGN', 'Africa/Lagos', 750,
                  now(), now())
             """);

        (await act.Should().ThrowAsync<PostgresException>())
            .Which.ConstraintName.Should().Be("ck_agencies_slug_url_safe");
    }

    [Fact]
    public async Task A_principal_may_not_have_a_parent()
    {
        await using var context = await MigratedDatabaseAsync();

        var principal = NewPrincipal("parented-principal");
        context.Agencies.Add(principal);
        await context.SaveChangesAsync();

        var act = async () => await context.Database.ExecuteSqlInterpolatedAsync(
            $"""
             INSERT INTO tenancy.agencies
                 (id, parent_agency_id, type, status, legal_name, slug, country_code,
                  base_currency, timezone, vat_rate_basis_points, created_at, updated_at)
             VALUES
                 ({Guid.CreateVersion7()}, {principal.Id}, 'Principal', 'PendingVerification',
                  'Confused Limited', 'confused', 'NG', 'NGN', 'Africa/Lagos', 750,
                  now(), now())
             """);

        (await act.Should().ThrowAsync<PostgresException>())
            .Which.ConstraintName.Should().Be("ck_agencies_hierarchy");
    }

    [Fact]
    public async Task Deleting_a_principal_that_still_has_sub_agents_is_refused()
    {
        await using var context = await MigratedDatabaseAsync();

        var principal = NewPrincipal("protected-principal");
        context.Agencies.Add(principal);
        await context.SaveChangesAsync();

        context.Agencies.Add(Agency.RegisterSubAgent(principal, "Dependent Limited", "dependent"));
        await context.SaveChangesAsync();

        // Cascade here would take a live agency's orders and ledger with it. Restrict is correct.
        var act = async () => await context.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM tenancy.agencies WHERE id = {principal.Id}");

        await act.Should().ThrowAsync<PostgresException>();
    }

    // ------------------------------------------------------------------------------ helpers

    private static Agency NewPrincipal(string slug) =>
        Agency.RegisterPrincipal(
            legalName: $"{slug} Limited",
            slug: slug,
            countryCode: "NG",
            baseCurrency: "NGN",
            timezone: "Africa/Lagos");

    private async Task<AppDbContext> MigratedDatabaseAsync([CallerMemberName] string testName = "")
    {
        var name = testName.ToLowerInvariant();
        var context = await _postgres.CreateEmptyDatabaseAsync(
            name[..Math.Min(name.Length, 60)], _tenancy.Tenant, _tenancy.Scope);

        await context.Database.MigrateAsync();
        return context;
    }

    private static async Task<List<Guid>> QuerySubtreeIdsAsync(AppDbContext context, string rootPath)
    {
        await using var connection = new NpgsqlConnection(context.Database.GetConnectionString());
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id FROM tenancy.agencies WHERE path <@ $1::ltree";
        command.Parameters.AddWithValue(rootPath);

        var ids = new List<Guid>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            ids.Add(reader.GetGuid(0));
        }

        return ids;
    }

    private static async Task SeedManyPrincipalsAsync(AppDbContext context, int count)
    {
        for (var i = 0; i < count; i++)
        {
            context.Agencies.Add(NewPrincipal($"bulk-{i:D4}"));
        }

        await context.SaveChangesAsync();
        await context.Database.ExecuteSqlRawAsync("ANALYZE tenancy.agencies;");
    }

    private static async Task<string> ExplainSubtreeQueryAsync(AppDbContext context, string rootPath)
    {
        await using var connection = new NpgsqlConnection(context.Database.GetConnectionString());
        await connection.OpenAsync();

        await using (var settings = connection.CreateCommand())
        {
            // Not a claim that the planner *would* pick the index at this size — a claim that
            // the index can serve the query. Without this a 500-row table is often seq-scanned
            // and the test would say nothing about the index at all.
            settings.CommandText = "SET enable_seqscan = off;";
            await settings.ExecuteNonQueryAsync();
        }

        await using var command = connection.CreateCommand();
        command.CommandText = "EXPLAIN (FORMAT TEXT) SELECT id FROM tenancy.agencies WHERE path <@ $1::ltree";
        command.Parameters.AddWithValue(rootPath);

        var plan = new System.Text.StringBuilder();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            plan.AppendLine(reader.GetString(0));
        }

        return plan.ToString();
    }
}
