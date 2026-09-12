using System.Runtime.CompilerServices;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using TripsAgent.Domain.Common;
using TripsAgent.Domain.Identity;
using TripsAgent.Domain.Payments;
using TripsAgent.Domain.Tenancy;
using TripsAgent.Domain.Tenancy.SubAgents;
using TripsAgent.Infrastructure.Persistence;
using TripsAgent.IntegrationTests.Persistence;

namespace TripsAgent.IntegrationTests.Tenancy;

/// <summary>
/// Who can read and write the sub-agent network's three tables, proved against the database as the
/// role production runs as. Feature F10, issue 63.
/// </summary>
/// <remarks>
/// <para>
/// Each of these tables carries two agencies — the principal that owns the row, and the sub-agent
/// it is about — so its SELECT policy is wider than the usual <c>agency_id = me</c> and its write
/// policy is not. That asymmetry is the only thing in this feature that could leak across
/// agencies, so it is what these tests are about.
/// </para>
/// <para>
/// Every context here connects as <c>tripsagent_app</c>. A superuser skips every policy, so a test
/// run as one would pass whether the policies worked or not.
/// </para>
/// </remarks>
[Collection(PostgresCollection.Name)]
public class SubAgentNetworkRlsTests
{
    private readonly PostgresFixture _postgres;

    public SubAgentNetworkRlsTests(PostgresFixture postgres) => _postgres = postgres;

    [Fact]
    public async Task Every_table_in_the_feature_is_policed_and_forced()
    {
        await using var world = await WorldAsync();

        var policed = await world.AdminListAsync(
            """
            SELECT n.nspname || '.' || c.relname
              FROM pg_class c
              JOIN pg_namespace n ON n.oid = c.relnamespace
             WHERE c.relrowsecurity
               AND c.relforcerowsecurity
               AND EXISTS (SELECT 1 FROM pg_policies p
                            WHERE p.schemaname = n.nspname
                              AND p.tablename = c.relname
                              AND p.policyname = 'tenant_isolation')
            """);

        policed.Should().Contain(
            ["tenancy.sub_agent_scopes", "tenancy.permission_overrides", "payments.wallet_allowances"],
            "each carries one agency's commercial terms for another and must be policed like every other tenant table");
    }

    [Fact]
    public async Task The_widened_read_is_a_SELECT_policy_and_nothing_more()
    {
        await using var world = await WorldAsync();

        // The sub-agent's clause must live on a SELECT-only policy. On a FOR ALL policy it would
        // also decide DELETE — which has no WITH CHECK — and a sub-agent could then delete the
        // allowance capping it. The tests below prove the behaviour; this one names the mechanism,
        // so a later edit that merges the two policies fails here with the reason attached.
        var widened = await world.AdminListAsync(
            """
            SELECT tablename || ':' || cmd
              FROM pg_policies
             WHERE policyname = 'sub_agent_read'
            """);

        widened.Should().BeEquivalentTo(
            ["sub_agent_scopes:SELECT", "permission_overrides:SELECT", "wallet_allowances:SELECT"]);
    }

    // ------------------------------------------------------------------ reads

    [Fact]
    public async Task A_principal_reads_the_terms_it_set_for_its_own_sub_agent()
    {
        await using var world = await WorldAsync();
        await using var asPrincipal = world.ActingAs(world.Principal);

        (await asPrincipal.Db.SubAgentScopes.CountAsync()).Should().Be(1);
        (await asPrincipal.Db.PermissionOverrides.CountAsync()).Should().Be(1);
        (await asPrincipal.Db.WalletAllowances.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task A_sub_agent_reads_the_terms_that_apply_to_it()
    {
        await using var world = await WorldAsync();
        await using var asSubAgent = world.ActingAs(world.SubAgent, world.Principal);

        // It has to: the console shows a sub-agent what it may sell and what it may spend.
        (await asSubAgent.Db.SubAgentScopes.SingleAsync()).SubAgencyId.Should().Be(world.SubAgent);
        (await asSubAgent.Db.PermissionOverrides.SingleAsync()).PermissionCode.Should().Be(PermissionCodes.MarginView);
        (await asSubAgent.Db.WalletAllowances.SingleAsync()).LimitMinor.Should().Be(new Money(1_000_000));
    }

    [Fact]
    public async Task A_sibling_sub_agent_reads_none_of_it()
    {
        await using var world = await WorldAsync();
        await using var asSibling = world.ActingAs(world.Sibling, world.Principal);

        (await asSibling.Db.SubAgentScopes.CountAsync()).Should().Be(0);
        (await asSibling.Db.PermissionOverrides.CountAsync()).Should().Be(0);
        (await asSibling.Db.WalletAllowances.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Another_principal_reads_none_of_another_networks_terms()
    {
        await using var world = await WorldAsync();
        await using var asOutsider = world.ActingAs(world.Outsider);

        (await asOutsider.Db.SubAgentScopes.CountAsync()).Should().Be(0);
        (await asOutsider.Db.WalletAllowances.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Removing_the_EF_filter_does_not_widen_what_a_sibling_sees()
    {
        await using var world = await WorldAsync();
        await using var asSibling = world.ActingAs(world.Sibling, world.Principal);

        // The backstop's whole claim: with the filter gone, PostgreSQL still refuses.
        var visible = await asSibling.Db.WalletAllowances.IgnoreQueryFilters().CountAsync();

        visible.Should().Be(0);
    }

    // ------------------------------------------------------------------ writes

    [Fact]
    public async Task A_sub_agent_cannot_raise_its_own_allowance()
    {
        await using var world = await WorldAsync();

        // Straight past EF, as the policed role, acting as the sub-agent: the WITH CHECK clause is
        // still agency_id = the principal, so this changes nothing at all.
        var changed = await world.ExecuteAsAgencyAsync(
            world.SubAgent,
            "UPDATE payments.wallet_allowances SET limit_minor = 99999999");

        changed.Should().Be(0, "only the principal that owns the allowance may write it");


        await using var asPrincipal = world.ActingAs(world.Principal);
        (await asPrincipal.Db.WalletAllowances.SingleAsync()).LimitMinor.Should().Be(new Money(1_000_000));
    }

    [Fact]
    public async Task A_sub_agent_cannot_give_itself_a_scope()
    {
        await using var world = await WorldAsync();

        // The tempting shape for an attacker: a row the tenant rule accepts, because agency_id
        // really is the caller. What refuses it is the composite foreign key — the pair has to be
        // a real principal and its real sub-agent, and this one is the hierarchy upside down.
        var upsideDown = () => world.ExecuteAsAgencyAsync(
            world.SubAgent,
            $"""
             INSERT INTO tenancy.sub_agent_scopes (id, agency_id, sub_agency_id, product_type, created_at, updated_at)
             VALUES ('{Guid.CreateVersion7()}', '{world.SubAgent}', '{world.Principal}', 'Bus', now(), now())
             """);

        (await upsideDown.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be(PostgresErrorCodes.ForeignKeyViolation);

        // And the straightforward shape: a row it would own on behalf of its own principal. The
        // policy's WITH CHECK refuses this one, because the row does not belong to the caller.
        var notMine = () => world.ExecuteAsAgencyAsync(
            world.SubAgent,
            $"""
             INSERT INTO tenancy.sub_agent_scopes (id, agency_id, sub_agency_id, product_type, created_at, updated_at)
             VALUES ('{Guid.CreateVersion7()}', '{world.Principal}', '{world.SubAgent}', 'Bus', now(), now())
             """);

        (await notMine.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
    }

    [Fact]
    public async Task A_sub_agent_cannot_deny_a_permission_to_an_agency_outside_its_network()
    {
        await using var world = await WorldAsync();

        // The reason the composite foreign key exists. Effective permissions are read by
        // sub_agency_id, so a row claiming this sub-agent is the outsider's principal would take a
        // permission off an unrelated agency's staff — a cross-tenant denial of service written
        // with nothing but a row the tenant rule would have accepted.
        var insert = () => world.ExecuteAsAgencyAsync(
            world.SubAgent,
            $"""
             INSERT INTO tenancy.permission_overrides
                 (id, agency_id, sub_agency_id, permission_code, reason, created_at, updated_at)
             VALUES ('{Guid.CreateVersion7()}', '{world.SubAgent}', '{world.Outsider}',
                     'booking.create', 'not mine to take', now(), now())
             """);

        (await insert.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be(PostgresErrorCodes.ForeignKeyViolation);
    }

    [Fact]
    public async Task A_sub_agent_cannot_lift_the_permission_its_principal_took_away()
    {
        await using var world = await WorldAsync();

        var deleted = await world.ExecuteAsAgencyAsync(
            world.SubAgent,
            "DELETE FROM tenancy.permission_overrides");

        deleted.Should().Be(0, "a sub-agent reads its overrides and can change none of them");
    }

    // ------------------------------------------------------------------ the world

    private async Task<SubAgentWorld> WorldAsync([CallerMemberName] string testName = "")
    {
        var name = $"subagent_rls_{testName.ToLowerInvariant()}";
        name = name[..Math.Min(name.Length, 60)];

        var tenancy = TestTenancy.None();

        await using (var setup = await _postgres.CreateEmptyDatabaseAsync(name, tenancy.Tenant, tenancy.Scope))
        {
            await setup.Database.MigrateAsync();

            using var _ = tenancy.Scope.Enter("test setup — a principal, two sub-agents and an unrelated principal");

            var principal = Agency.RegisterPrincipal("Lagos Travel Limited", "lagos-travel", "NG", "NGN", "Africa/Lagos");
            var outsider = Agency.RegisterPrincipal("Abuja Tours Limited", "abuja-tours", "NG", "NGN", "Africa/Lagos");
            principal.MarkVerified(DateTimeOffset.UtcNow);
            outsider.MarkVerified(DateTimeOffset.UtcNow);

            setup.Agencies.AddRange(principal, outsider);
            await setup.SaveChangesAsync();

            var subAgent = Agency.RegisterSubAgent(principal, "Ikeja Travel Limited", "ikeja-travel");
            var sibling = Agency.RegisterSubAgent(principal, "Yaba Travel Limited", "yaba-travel");
            subAgent.MarkVerified(DateTimeOffset.UtcNow);
            sibling.MarkVerified(DateTimeOffset.UtcNow);

            setup.Agencies.AddRange(subAgent, sibling);
            await setup.SaveChangesAsync();

            setup.SubAgentScopes.Add(
                SubAgentScope.Grant(principal.Id, subAgent.Id, SellableProductType.Flight));

            setup.PermissionOverrides.Add(
                PermissionOverride.Deny(principal.Id, subAgent.Id, PermissionCodes.MarginView, "Resellers do not see our net rates."));

            setup.WalletAllowances.Add(WalletAllowance.Open(
                principal.Id, subAgent.Id, "NGN", new Money(1_000_000), AllowancePeriod.Monthly, DateTimeOffset.UtcNow));

            await setup.SaveChangesAsync();

            return new SubAgentWorld(_postgres, name, principal.Id, subAgent.Id, sibling.Id, outsider.Id);
        }
    }
}
