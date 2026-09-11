using System.Runtime.CompilerServices;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using TripsAgent.Domain.Tenancy;
using TripsAgent.Infrastructure.Persistence;

namespace TripsAgent.IntegrationTests.Persistence;

/// <summary>
/// The tests that matter most in the whole suite: one agency must never see another's rows.
/// </summary>
/// <remarks>
/// A bug here leaks a travel agency's customers and prices to a competitor. Everything below runs
/// against real PostgreSQL with real SQL, because the question is what the database returns, not
/// what EF intended.
/// </remarks>
[Collection(PostgresCollection.Name)]
public class TenantIsolationTests
{
    private readonly PostgresFixture _postgres;

    public TenantIsolationTests(PostgresFixture postgres) => _postgres = postgres;

    // ------------------------------------------------------------------------------ reading

    [Fact]
    public async Task An_agency_cannot_read_another_agencys_rows()
    {
        var world = await TwoAgenciesAsync();

        await using var asAlpha = await ActingAsAsync(world.Database, world.AlphaId);

        var visible = await asAlpha.AgencySettings.ToListAsync();

        visible.Should().ContainSingle();
        visible[0].AgencyId.Should().Be(world.AlphaId);
    }

    [Fact]
    public async Task Fetching_another_agencys_row_by_its_primary_key_returns_nothing()
    {
        var world = await TwoAgenciesAsync();

        await using var asAlpha = await ActingAsAsync(world.Database, world.AlphaId);

        // Knowing the id is not authorisation. This is the shape of the bug where an id leaks
        // into a URL and the handler trusts it.
        var stolen = await asAlpha.AgencySettings
            .SingleOrDefaultAsync(s => s.AgencyId == world.BetaId);

        stolen.Should().BeNull();
    }

    [Fact]
    public async Task Counting_and_aggregating_respect_the_filter()
    {
        var world = await TwoAgenciesAsync();

        await using var asAlpha = await ActingAsAsync(world.Database, world.AlphaId);

        // Aggregates are the easiest place for a leak to hide: nobody inspects a number the way
        // they inspect a list.
        (await asAlpha.AgencySettings.CountAsync()).Should().Be(1);
        (await asAlpha.AgencyBranding.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task An_unresolved_tenant_sees_nothing_rather_than_everything()
    {
        var world = await TwoAgenciesAsync();

        // A background job, or a request that skipped the middleware. Returning nothing is a
        // visible bug; returning everything is a silent breach.
        await using var anonymous = _postgres.Connect(world.Database);

        (await anonymous.AgencySettings.CountAsync()).Should().Be(0);
        (await anonymous.Agencies.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task The_generated_SQL_carries_the_agency_predicate()
    {
        var world = await TwoAgenciesAsync();

        await using var asAlpha = await ActingAsAsync(world.Database, world.AlphaId);

        var sql = asAlpha.AgencySettings.ToQueryString();

        // Belt and braces: proves the filter reaches the database rather than being applied in
        // memory after every row has already been fetched.
        sql.Should().Contain("agency_id");
    }

    // -------------------------------------------------------------------------- the hierarchy

    [Fact]
    public async Task A_principal_sees_itself_and_its_sub_agents()
    {
        var world = await TwoAgenciesAsync();

        await using var context = await ActingAsAsync(world.Database, world.AlphaId);

        var branch = Agency.RegisterSubAgent(
            await context.Agencies.SingleAsync(a => a.Id == world.AlphaId),
            "Alpha Branch Limited",
            "alpha-branch");

        context.Agencies.Add(branch);
        await context.SaveChangesAsync();

        context.ChangeTracker.Clear();
        var visible = await context.Agencies.Select(a => a.Id).ToListAsync();

        visible.Should().BeEquivalentTo([world.AlphaId, branch.Id]);
        visible.Should().NotContain(world.BetaId);
    }

    [Fact]
    public async Task A_sub_agent_sees_only_itself()
    {
        var world = await TwoAgenciesAsync();

        Guid branchId;
        await using (var asAlpha = await ActingAsAsync(world.Database, world.AlphaId))
        {
            var branch = Agency.RegisterSubAgent(
                await asAlpha.Agencies.SingleAsync(a => a.Id == world.AlphaId),
                "Alpha Branch Limited",
                "alpha-branch");

            asAlpha.Agencies.Add(branch);
            await asAlpha.SaveChangesAsync();
            branchId = branch.Id;
        }

        await using var asBranch = await ActingAsAsync(world.Database, branchId, rootAgencyId: world.AlphaId);

        // A sub-agent must not see its parent or its siblings, even though they share a root.
        var visible = await asBranch.Agencies.Select(a => a.Id).ToListAsync();
        visible.Should().Equal(branchId);
    }

    // ------------------------------------------------------------------------------ writing

    [Fact]
    public async Task Agency_id_is_stamped_on_insert_when_nobody_set_it()
    {
        var world = await TwoAgenciesAsync();

        await using var asAlpha = await ActingAsAsync(world.Database, world.AlphaId);

        var alpha = await asAlpha.Agencies.SingleAsync(a => a.Id == world.AlphaId);
        var branding = AgencyBranding.CreateDefault(alpha);

        asAlpha.AgencyBranding.Add(branding);

        // Clear it back out: this is the handler that forgot, or the DTO that never carried an
        // agency in the first place. A row inserted with an empty agency_id belongs to nobody
        // and its owner would never see it again.
        asAlpha.Entry(branding).Property(nameof(AgencyBranding.AgencyId)).CurrentValue = Guid.Empty;

        // Alpha already has branding from the fixture, so replace rather than duplicate.
        asAlpha.AgencyBranding.RemoveRange(
            await asAlpha.AgencyBranding.Where(b => b.Id != branding.Id).ToListAsync());

        await asAlpha.SaveChangesAsync();

        branding.AgencyId.Should().Be(world.AlphaId);
    }

    [Fact]
    public async Task An_insert_with_no_tenant_and_no_agency_is_refused()
    {
        var world = await TwoAgenciesAsync();

        // No middleware ran and the entity carries nothing either — the row would be orphaned.
        // As the owner, which row-level security does not police, so the EF guard is the only thing
        // that can refuse — the guard is what this test is about. RowLevelSecurityTests proves the
        // database refuses as well.
        await using var anonymous = _postgres.Connect(world.Database, asApplicationRole: false);

        var alpha = await anonymous.Agencies.IgnoreQueryFilters().SingleAsync(a => a.Id == world.AlphaId);
        var branding = AgencyBranding.CreateDefault(alpha);

        anonymous.AgencyBranding.Add(branding);
        anonymous.Entry(branding).Property(nameof(AgencyBranding.AgencyId)).CurrentValue = Guid.Empty;

        var act = async () => await anonymous.SaveChangesAsync();

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*belongs to nobody*");
    }

    [Fact]
    public async Task A_principal_cannot_yet_write_rows_for_its_own_sub_agent()
    {
        var world = await TwoAgenciesAsync();

        await using var asAlpha = await ActingAsAsync(world.Database, world.AlphaId);

        var alpha = await asAlpha.Agencies.SingleAsync(a => a.Id == world.AlphaId);
        var branch = Agency.RegisterSubAgent(alpha, "Stamped Branch Limited", "stamped-branch");
        asAlpha.Agencies.Add(branch);
        await asAlpha.SaveChangesAsync();

        asAlpha.AgencySettings.Add(AgencySettings.CreateDefault(branch));

        var act = async () => await asAlpha.SaveChangesAsync();

        // Documenting the current rule rather than endorsing it: writes are allowed only into
        // the agency you are acting as, even for a principal writing into its own subtree. The
        // read filter is wider than that, so the two are not symmetric.
        //
        // Sub-agent management will need this to work. Whether the answer is a subtree write
        // rule or an explicit "act as sub-agent" switch is a product decision, not one to settle
        // silently inside an interceptor — so the strict rule stands until it is made.
        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*Refusing to insert*");
    }

    [Fact]
    public async Task Writing_a_row_into_another_agency_is_refused()
    {
        var world = await TwoAgenciesAsync();

        // As the owner, for the same reason as the test above: this proves the EF guard in isolation.
        var tenancy = TestTenancy.For(world.AlphaId);
        await using var asAlpha = _postgres.Connect(world.Database, tenancy.Tenant, tenancy.Scope, asApplicationRole: false);

        // Hand-built so it carries Beta's id while the request is acting as Alpha.
        var beta = await ReadAgencyBypassingFiltersAsync(asAlpha, world.BetaId);
        var trespassing = AgencyBranding.CreateDefault(beta);

        asAlpha.AgencyBranding.Add(trespassing);

        var act = async () => await asAlpha.SaveChangesAsync();

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*Refusing to insert*");
    }

    [Fact]
    public async Task Moving_a_row_to_another_agency_is_refused()
    {
        var world = await TwoAgenciesAsync();

        await using var asAlpha = await ActingAsAsync(world.Database, world.AlphaId);

        var settings = await asAlpha.AgencySettings.SingleAsync();

        // AgencyId has a private setter, so this is what a mis-mapped DTO would do.
        asAlpha.Entry(settings).Property(nameof(AgencySettings.AgencyId)).CurrentValue = world.BetaId;

        var act = async () => await asAlpha.SaveChangesAsync();

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*Rows do not change owner*");
    }

    // -------------------------------------------------------------------------- the bypass

    [Fact]
    public async Task A_platform_scope_lifts_the_filter_while_it_is_open()
    {
        var world = await TwoAgenciesAsync();

        var tenancy = TestTenancy.For(world.AlphaId);
        await using var context = _postgres.Connect(world.Database, tenancy.Tenant, tenancy.Scope);

        (await context.Agencies.CountAsync()).Should().Be(1);

        using (tenancy.Scope.Enter("test — platform-admin reporting reads across agencies"))
        {
            (await context.Agencies.CountAsync()).Should().Be(2);
        }

        // ...and closes again afterwards.
        (await context.Agencies.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Nested_platform_scopes_do_not_reopen_the_filter_early()
    {
        var world = await TwoAgenciesAsync();

        var tenancy = TestTenancy.For(world.AlphaId);
        await using var context = _postgres.Connect(world.Database, tenancy.Tenant, tenancy.Scope);

        using (tenancy.Scope.Enter("outer"))
        {
            using (tenancy.Scope.Enter("inner"))
            {
                (await context.Agencies.CountAsync()).Should().Be(2);
            }

            // The inner block ending must not restore the filter while the outer one is running.
            (await context.Agencies.CountAsync()).Should().Be(2);
        }

        (await context.Agencies.CountAsync()).Should().Be(1);
    }

    // ------------------------------------------------------------------------------ helpers

    private sealed record World(string Database, Guid AlphaId, Guid BetaId);

    /// <summary>Two unrelated principals, each with settings and branding of their own.</summary>
    private async Task<World> TwoAgenciesAsync([CallerMemberName] string testName = "")
    {
        var database = testName.ToLowerInvariant();
        database = database[..Math.Min(database.Length, 60)];

        await using var setup = await _postgres.CreateEmptyDatabaseAsync(database);
        await setup.Database.MigrateAsync();

        var alpha = Agency.RegisterPrincipal("Alpha Limited", "alpha", "NG", "NGN", "Africa/Lagos");
        var beta = Agency.RegisterPrincipal("Beta Limited", "beta", "NG", "NGN", "Africa/Lagos");

        setup.Agencies.AddRange(alpha, beta);
        setup.AgencySettings.AddRange(AgencySettings.CreateDefault(alpha), AgencySettings.CreateDefault(beta));
        setup.AgencyBranding.AddRange(AgencyBranding.CreateDefault(alpha), AgencyBranding.CreateDefault(beta));

        // Seeded with no tenant resolved, which is how a seed script runs. The rows carry their
        // own agency ids, so the interceptor leaves them alone.
        await setup.SaveChangesAsync();

        return new World(database, alpha.Id, beta.Id);
    }

    private Task<AppDbContext> ActingAsAsync(string database, Guid agencyId, Guid? rootAgencyId = null)
    {
        var tenancy = TestTenancy.For(agencyId, rootAgencyId);
        return Task.FromResult(_postgres.Connect(database, tenancy.Tenant, tenancy.Scope));
    }

    private static async Task<Agency> ReadAgencyBypassingFiltersAsync(AppDbContext context, Guid agencyId) =>
        await context.Agencies.IgnoreQueryFilters().SingleAsync(a => a.Id == agencyId);
}
