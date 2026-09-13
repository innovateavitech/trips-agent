using System.Runtime.CompilerServices;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using TripsAgent.Application.Tenancy.SubAgents;
using TripsAgent.Domain.Common;
using TripsAgent.Domain.Payments;
using TripsAgent.Domain.Tenancy;
using TripsAgent.Infrastructure.Persistence;
using TripsAgent.Infrastructure.Tenancy;
using TripsAgent.IntegrationTests.Persistence;

namespace TripsAgent.IntegrationTests.Tenancy;

/// <summary>
/// A sub-agent's hard spending cap, against a real PostgreSQL. Feature F10, issue 63.
/// </summary>
/// <remarks>
/// <para>
/// The test that matters is <see cref="Twenty_bookings_at_once_never_go_past_the_cap"/>. A cap
/// checked in C# and written back would pass every other test here and fail that one, because the
/// bug it is about only exists when two bookings are in flight at the same instant — which, for a
/// busy agency, is most of the time.
/// </para>
/// <para>
/// The amounts are in kobo. ₦10,000.00 is 1,000,000 of them.
/// </para>
/// </remarks>
[Collection(PostgresCollection.Name)]
public class SubAgentAllowanceTests
{
    /// <summary>₦10,000.00, the cap in every test here.</summary>
    private const long Cap = 1_000_000;

    private readonly PostgresFixture _postgres;

    public SubAgentAllowanceTests(PostgresFixture postgres) => _postgres = postgres;

    [Fact]
    public async Task Twenty_bookings_at_once_never_go_past_the_cap()
    {
        await using var world = await WorldAsync();

        // Twenty bookings of ₦600.00 against a ₦10,000.00 cap. Sixteen fit exactly; the other four
        // must be refused. Chosen so the right answer is a single number rather than a range: if
        // reservations were lost or double-counted, this is 15, or 17, or 20.
        const long each = 60_000;
        const int attempts = 20;
        const int shouldFit = (int)(Cap / each);

        var outcomes = await Task.WhenAll(Enumerable.Range(0, attempts).Select(async _ =>
        {
            // A context each, so they are twenty separate connections racing exactly as twenty
            // concurrent requests would. One shared context would serialise them and prove nothing.
            await using var session = world.ActingAs(world.SubAgent, world.Principal);
            var reservations = new PostgresAllowanceReservations(session.Db);

            return await reservations.ReserveAsync("NGN", each);
        }));

        outcomes.Count(outcome => outcome == AllowanceReservation.Reserved)
            .Should().Be(shouldFit, "the cap is the cap however many bookings arrive at once");

        outcomes.Count(outcome => outcome == AllowanceReservation.Exceeded)
            .Should().Be(attempts - shouldFit);

        // And the row agrees: no update was lost under the others.
        var spent = await world.AdminCountAsync("SELECT spent_minor FROM payments.wallet_allowances");
        spent.Should().Be(shouldFit * each);
        spent.Should().BeLessThanOrEqualTo(Cap);
    }

    [Fact]
    public async Task A_booking_over_what_is_left_is_refused_and_changes_nothing()
    {
        await using var world = await WorldAsync();
        await using var session = world.ActingAs(world.SubAgent, world.Principal);
        var reservations = new PostgresAllowanceReservations(session.Db);

        (await reservations.ReserveAsync("NGN", 900_000)).Should().Be(AllowanceReservation.Reserved);
        (await reservations.ReserveAsync("NGN", 200_000)).Should().Be(AllowanceReservation.Exceeded);

        (await world.AdminCountAsync("SELECT spent_minor FROM payments.wallet_allowances")).Should().Be(900_000);
    }

    [Fact]
    public async Task Exactly_the_cap_fits()
    {
        await using var world = await WorldAsync();
        await using var session = world.ActingAs(world.SubAgent, world.Principal);
        var reservations = new PostgresAllowanceReservations(session.Db);

        // The boundary is inclusive: a cap of ₦10,000.00 buys a ₦10,000.00 ticket.
        (await reservations.ReserveAsync("NGN", Cap)).Should().Be(AllowanceReservation.Reserved);
        (await reservations.ReserveAsync("NGN", 1)).Should().Be(AllowanceReservation.Exceeded);
    }

    [Fact]
    public async Task A_frozen_allowance_refuses_before_anything_else_happens()
    {
        await using var world = await WorldAsync();

        await using (var asPrincipal = world.ActingAs(world.Principal))
        {
            var allowance = await asPrincipal.Db.WalletAllowances.SingleAsync();
            allowance.Freeze();
            await asPrincipal.Db.SaveChangesAsync();
        }

        await using var session = world.ActingAs(world.SubAgent, world.Principal);
        var reservations = new PostgresAllowanceReservations(session.Db);

        (await reservations.ReserveAsync("NGN", 1)).Should().Be(AllowanceReservation.Frozen);
    }

    [Fact]
    public async Task A_sub_agent_with_no_allowance_cannot_spend_at_all()
    {
        await using var world = await WorldAsync();
        await using var session = world.ActingAs(world.Sibling, world.Principal);
        var reservations = new PostgresAllowanceReservations(session.Db);

        // Fails closed. A new sub-agent has no allowance row, and it sells nothing until it does.
        (await reservations.ReserveAsync("NGN", 1)).Should().Be(AllowanceReservation.NoAllowance);
    }

    [Fact]
    public async Task A_reservation_given_back_can_be_spent_again()
    {
        await using var world = await WorldAsync();
        await using var session = world.ActingAs(world.SubAgent, world.Principal);
        var reservations = new PostgresAllowanceReservations(session.Db);

        await reservations.ReserveAsync("NGN", Cap);
        (await reservations.ReleaseAsync(world.SubAgent, "NGN", 400_000)).Should().BeTrue();

        (await world.AdminCountAsync("SELECT spent_minor FROM payments.wallet_allowances")).Should().Be(600_000);
        (await reservations.ReserveAsync("NGN", 400_000)).Should().Be(AllowanceReservation.Reserved);
    }

    [Fact]
    public async Task A_second_release_of_the_same_hold_is_refused_and_leaves_other_bookings_alone()
    {
        await using var world = await WorldAsync();
        await using var session = world.ActingAs(world.SubAgent, world.Principal);
        var reservations = new PostgresAllowanceReservations(session.Db);

        // Two bookings counted against the one cap.
        await reservations.ReserveAsync("NGN", 100_000);
        await reservations.ReserveAsync("NGN", 50_000);

        // A reversal racing a lapse: the same hold given back twice. The first release is that
        // booking's own money; the second is nobody's, and must not come out of the other booking's
        // reservation — which is what clamping the subtraction at zero used to do (issue 175).
        (await reservations.ReleaseAsync(world.SubAgent, "NGN", 100_000)).Should().BeTrue();
        (await reservations.ReleaseAsync(world.SubAgent, "NGN", 100_000)).Should().BeFalse();

        (await world.AdminCountAsync("SELECT spent_minor FROM payments.wallet_allowances")).Should().Be(50_000);
    }

    [Fact]
    public async Task The_function_itself_refuses_to_release_more_than_the_allowance_holds()
    {
        await using var world = await WorldAsync();
        await using var session = world.ActingAs(world.SubAgent, world.Principal);
        var reservations = new PostgresAllowanceReservations(session.Db);

        await reservations.ReserveAsync("NGN", 300_000);

        // Straight at the SECURITY DEFINER function, as the policed role the API connects with. The
        // application is what usually decides the amount; this is the case where it does not, and
        // the bound has to be the database's own.
        (await session.Db.Database.SqlQueryRaw<string>("""SELECT current_user::text AS "Value" """).SingleAsync())
            .Should().Be("tripsagent_app", "the bound is only worth testing against the role that cannot bypass it");

        var released = await session.Db.Database
            .SqlQueryRaw<bool>(
                """SELECT payments.release_sub_agent_allowance({0}, {1}, {2}) AS "Value" """,
                world.SubAgent,
                "NGN",
                300_001L)
            .SingleAsync();

        released.Should().BeFalse("a release of more than was held is refused, not clamped");
        (await world.AdminCountAsync("SELECT spent_minor FROM payments.wallet_allowances")).Should().Be(300_000);

        // What was held still goes back, so the bound refuses nothing legitimate.
        (await reservations.ReleaseAsync(world.SubAgent, "NGN", 300_000)).Should().BeTrue();
        (await world.AdminCountAsync("SELECT spent_minor FROM payments.wallet_allowances")).Should().Be(0);
    }

    [Fact]
    public async Task The_release_function_keeps_its_definer_rights_its_search_path_and_its_grant()
    {
        await using var world = await WorldAsync();

        // Replacing a function rewrites the whole definition. Lose SECURITY DEFINER and a sub-agent's
        // own release stops working; lose the search path and it runs with one its caller chose; lose
        // the grant and nothing can call it at all.
        var installed = await world.AdminListAsync(
            """
            SELECT p.prosecdef::text
                   || '|' || coalesce(array_to_string(p.proconfig, ','), '')
                   || '|' || has_function_privilege('tripsagent_app', p.oid, 'EXECUTE')::text
              FROM pg_proc p
              JOIN pg_namespace n ON n.oid = p.pronamespace
             WHERE n.nspname = 'payments' AND p.proname = 'release_sub_agent_allowance'
            """);

        var settings = installed.Should().ContainSingle().Subject;

        settings.Should().StartWith("true|search_path=");
        settings.Should().Contain("payments").And.Contain("tenancy");
        settings.Should().EndWith("|true");
    }

    [Fact]
    public async Task An_unrelated_agency_cannot_free_up_somebody_elses_allowance()
    {
        await using var world = await WorldAsync();
        await using var asOutsider = world.ActingAs(world.Outsider);

        await using (var session = world.ActingAs(world.SubAgent, world.Principal))
        {
            await new PostgresAllowanceReservations(session.Db).ReserveAsync("NGN", 500_000);
        }

        var released = await new PostgresAllowanceReservations(asOutsider.Db)
            .ReleaseAsync(world.SubAgent, "NGN", 500_000);

        released.Should().BeFalse("only the sub-agent, its own principal or a platform job may release it");
        (await world.AdminCountAsync("SELECT spent_minor FROM payments.wallet_allowances")).Should().Be(500_000);
    }

    [Fact]
    public async Task The_principal_can_give_its_sub_agents_allowance_back()
    {
        await using var world = await WorldAsync();

        await using (var session = world.ActingAs(world.SubAgent, world.Principal))
        {
            await new PostgresAllowanceReservations(session.Db).ReserveAsync("NGN", 500_000);
        }

        await using var asPrincipal = world.ActingAs(world.Principal);

        (await new PostgresAllowanceReservations(asPrincipal.Db)
            .ReleaseAsync(world.SubAgent, "NGN", 500_000)).Should().BeTrue();
    }

    [Fact]
    public async Task Lowering_the_cap_below_what_is_spent_blocks_the_next_booking_and_claws_nothing_back()
    {
        await using var world = await WorldAsync();

        await using (var session = world.ActingAs(world.SubAgent, world.Principal))
        {
            await new PostgresAllowanceReservations(session.Db).ReserveAsync("NGN", 800_000);
        }

        await using (var asPrincipal = world.ActingAs(world.Principal))
        {
            var allowance = await asPrincipal.Db.WalletAllowances.SingleAsync();
            allowance.ChangeLimit(new Money(500_000));
            await asPrincipal.Db.SaveChangesAsync();
        }

        // The money is spent; lowering the cap only stops the next one.
        (await world.AdminCountAsync("SELECT spent_minor FROM payments.wallet_allowances")).Should().Be(800_000);

        await using var session2 = world.ActingAs(world.SubAgent, world.Principal);
        (await new PostgresAllowanceReservations(session2.Db).ReserveAsync("NGN", 1))
            .Should().Be(AllowanceReservation.Exceeded);
    }

    // ------------------------------------------------------------------ the world

    private async Task<SubAgentWorld> WorldAsync([CallerMemberName] string testName = "")
    {
        var name = $"subagent_allow_{testName.ToLowerInvariant()}";
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

            setup.WalletAllowances.Add(WalletAllowance.Open(
                principal.Id, subAgent.Id, "NGN", new Money(Cap), AllowancePeriod.Monthly, DateTimeOffset.UtcNow));

            await setup.SaveChangesAsync();

            return new SubAgentWorld(_postgres, name, principal.Id, subAgent.Id, sibling.Id, outsider.Id);
        }
    }
}
