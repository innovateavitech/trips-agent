using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using TripsAgent.Application.Platform;
using TripsAgent.Contracts.Platform;
using TripsAgent.Domain.Auditing;
using TripsAgent.Domain.Tenancy;
using TripsAgent.IntegrationTests.Persistence;

namespace TripsAgent.IntegrationTests.Platform;

/// <summary>
/// The agency directory, the profile, and the three lifecycle actions — against a real database,
/// with real row-level security in front of it.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class AgencyAdministrationTests
{
    private readonly PostgresFixture _postgres;

    public AgencyAdministrationTests(PostgresFixture postgres) => _postgres = postgres;

    // ----------------------------------------------------------------------------- directory

    [Fact]
    public async Task The_directory_lists_every_agency_and_says_how_many_there_are()
    {
        await using var world = await AdminWorld.CreateAsync(_postgres);
        await world.AddAgencyAsync("Alpha Travel Limited", "alpha-travel");
        await world.AddAgencyAsync("Beta Travel Limited", "beta-travel", AgencyStatus.PendingVerification);

        var page = await world.Directory.SearchAsync(new AgencyDirectoryQuery());

        page.TotalCount.Should().Be(2);
        page.Items.Select(item => item.Slug).Should().BeEquivalentTo(["alpha-travel", "beta-travel"]);
    }

    [Fact]
    public async Task Searching_matches_the_legal_name_the_trading_name_and_the_slug()
    {
        await using var world = await AdminWorld.CreateAsync(_postgres);
        await world.AddAgencyAsync("Kano Journeys Limited", "kano-journeys", tradingName: "Sahara Trips");
        await world.AddAgencyAsync("Lagos Travel Limited", "lagos-travel");

        // Case-insensitively, and on any of the three: whoever is searching has one of the names
        // in front of them and no idea which kind it is.
        foreach (var term in new[] { "kano", "SAHARA", "kano-jour" })
        {
            var found = await world.Directory.SearchAsync(new AgencyDirectoryQuery(Search: term));
            found.Items.Should().ContainSingle().Which.Slug.Should().Be("kano-journeys");
        }
    }

    [Fact]
    public async Task A_search_term_with_a_wildcard_in_it_is_not_a_wildcard()
    {
        await using var world = await AdminWorld.CreateAsync(_postgres);
        await world.AddAgencyAsync("Alpha Travel Limited", "alpha-travel");

        (await world.Directory.SearchAsync(new AgencyDirectoryQuery(Search: "%")))
            .TotalCount.Should().Be(0);
    }

    [Fact]
    public async Task The_directory_filters_by_status()
    {
        await using var world = await AdminWorld.CreateAsync(_postgres);
        await world.AddAgencyAsync("Alpha Travel Limited", "alpha-travel");
        await world.AddAgencyAsync("Beta Travel Limited", "beta-travel", AgencyStatus.Suspended);

        var suspended = await world.Directory.SearchAsync(
            new AgencyDirectoryQuery(Status: AgencyStatus.Suspended));

        suspended.Items.Should().ContainSingle().Which.Slug.Should().Be("beta-travel");
    }

    [Fact]
    public async Task A_page_size_beyond_the_ceiling_is_clamped()
    {
        // The page size comes from a query string, so it comes from whoever edits the URL.
        new AgencyDirectoryQuery(PageSize: 10_000).SafePageSize
            .Should().Be(AgencyDirectoryQuery.MaxPageSize);

        new AgencyDirectoryQuery(Page: 0).SafePage.Should().Be(1);

        await Task.CompletedTask;
    }

    // ------------------------------------------------------------------------------- profile

    [Fact]
    public async Task A_profile_carries_the_agencys_staff_and_its_standing()
    {
        await using var world = await AdminWorld.CreateAsync(_postgres);
        var agencyId = await world.AddAgencyAsync("Lagos Travel Limited", "lagos-travel", tradingName: "Lagos Travel");

        var profile = await world.Directory.ProfileAsync(agencyId);

        profile.Should().NotBeNull();
        profile!.Name.Should().Be("Lagos Travel");
        profile.LegalName.Should().Be("Lagos Travel Limited");
        profile.Status.Should().Be(nameof(AgencyStatus.Verified));
        profile.CanTakeNewBookings.Should().BeTrue();
        profile.StorefrontIsLive.Should().BeTrue();
        profile.Users.Should().ContainSingle().Which.Email.Should().Be("owner@lagos-travel.test");
        profile.WalletBalanceMinor.Should().Be(0);
    }

    [Fact]
    public async Task An_unknown_agency_has_no_profile()
    {
        await using var world = await AdminWorld.CreateAsync(_postgres);

        (await world.Directory.ProfileAsync(Guid.CreateVersion7())).Should().BeNull();
    }

    // ----------------------------------------------------------------------------- lifecycle

    [Fact]
    public async Task Suspending_stops_new_bookings_and_takes_the_storefront_offline()
    {
        await using var world = await AdminWorld.CreateAsync(_postgres);
        var agencyId = await world.AddAgencyAsync("Lagos Travel Limited", "lagos-travel");

        var outcome = await world.Lifecycle.SuspendAsync(
            agencyId, "Chargeback rate above 3% for two consecutive months.");

        var done = outcome.Should().BeOfType<AgencyActionOutcome.Done>().Subject;
        done.Status.Status.Should().Be(nameof(AgencyStatus.Suspended));
        done.Status.CanTakeNewBookings.Should().BeFalse();
        done.Status.StorefrontIsLive.Should().BeFalse();

        // Decision 14: what the agency already sold is untouched, so nothing here cancels or
        // refunds anything — only the two switches above move.
        var storefront = await world.Storefront.ForAsync(agencyId);
        storefront!.IsLive.Should().BeFalse();
    }

    [Fact]
    public async Task Every_lifecycle_action_refuses_a_reason_too_thin_to_defend()
    {
        await using var world = await AdminWorld.CreateAsync(_postgres);
        var agencyId = await world.AddAgencyAsync("Lagos Travel Limited", "lagos-travel");

        foreach (var reason in new[] { "", "   ", "bad" })
        {
            (await world.Lifecycle.SuspendAsync(agencyId, reason))
                .Should().BeOfType<AgencyActionOutcome.ReasonRequired>();

            (await world.Lifecycle.TerminateAsync(agencyId, reason))
                .Should().BeOfType<AgencyActionOutcome.ReasonRequired>();
        }

        // Nothing was written on the way past.
        using var _ = world.Tenancy.Scope.Enter("test — reads the agency back");
        var agency = await world.Db.Agencies.AsNoTracking().SingleAsync(a => a.Id == agencyId);
        agency.Status.Should().Be(AgencyStatus.Verified);
    }

    [Fact]
    public async Task Suspending_twice_is_refused_rather_than_silently_repeated()
    {
        await using var world = await AdminWorld.CreateAsync(_postgres);
        var agencyId = await world.AddAgencyAsync("Lagos Travel Limited", "lagos-travel");

        await world.Lifecycle.SuspendAsync(agencyId, "Under investigation for chargebacks.");

        (await world.Lifecycle.SuspendAsync(agencyId, "Under investigation for chargebacks."))
            .Should().BeOfType<AgencyActionOutcome.NotAllowed>();
    }

    [Fact]
    public async Task Reinstating_puts_a_verified_agency_back_to_selling()
    {
        await using var world = await AdminWorld.CreateAsync(_postgres);
        var agencyId = await world.AddAgencyAsync("Lagos Travel Limited", "lagos-travel");

        await world.Lifecycle.SuspendAsync(agencyId, "Disputed transactions being investigated.");
        var outcome = await world.Lifecycle.ReinstateAsync(agencyId, "Investigation closed, no case to answer.");

        var done = outcome.Should().BeOfType<AgencyActionOutcome.Done>().Subject;
        done.Status.Status.Should().Be(nameof(AgencyStatus.Verified));
        done.Status.CanTakeNewBookings.Should().BeTrue();
    }

    [Fact]
    public async Task Terminating_keeps_the_row_because_orders_still_point_at_it()
    {
        await using var world = await AdminWorld.CreateAsync(_postgres);
        var agencyId = await world.AddAgencyAsync("Lagos Travel Limited", "lagos-travel");

        await world.Lifecycle.TerminateAsync(agencyId, "The agency asked us to close the account.");

        using var _ = world.Tenancy.Scope.Enter("test — reads the terminated agency back");
        var agency = await world.Db.Agencies.AsNoTracking().SingleAsync(a => a.Id == agencyId);

        agency.Status.Should().Be(AgencyStatus.Terminated);
        agency.StatusReason.Should().Be("The agency asked us to close the account.");
        agency.StatusChangedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Editing_a_profile_records_the_reason_with_the_before_and_after_state()
    {
        await using var world = await AdminWorld.CreateAsync(_postgres);
        var agencyId = await world.AddAgencyAsync("Lagos Travel Limited", "lagos-travel");

        await world.Lifecycle.UpdateAsync(agencyId, new UpdateAgencyRequest(
            LegalName: "Lagos Travel Services PLC",
            TradingName: "Lagos Travel",
            TaxId: "12345678-0001",
            Timezone: "Africa/Lagos",
            VatRateBasisPoints: 750,
            Reason: "Companies house filing shows the business converted to a PLC."));

        using var _ = world.Tenancy.Scope.Enter("test — reads the audit trail");

        // By action rather than by time: creating the agency wrote its own row, and the test clock
        // does not move between the two, so "the newest" is ambiguous.
        var entry = await world.Db.AuditLogs.AsNoTracking()
            .Where(log => log.EntityId == agencyId.ToString() && log.Action == AuditActions.Updated)
            .SingleAsync();

        entry.ActorUserId.Should().Be(world.AdminUserId);
        entry.Reason.Should().Contain("converted to a PLC");
        entry.BeforeState.Should().Contain("Lagos Travel Limited");
        entry.AfterState.Should().Contain("Lagos Travel Services PLC");
    }

    // -------------------------------------------------------------------------------- export

    [Fact]
    public async Task An_export_carries_the_agency_its_people_and_its_orders_and_no_secrets()
    {
        await using var world = await AdminWorld.CreateAsync(_postgres);
        var agencyId = await world.AddAgencyAsync("Lagos Travel Limited", "lagos-travel");

        var export = await world.Exports.BuildAsync(agencyId);

        export.Should().NotBeNull();
        export!.FileName.Should().StartWith("trips-export-lagos-travel-").And.EndWith(".json");

        using var document = JsonDocument.Parse(export.Json);
        var root = document.RootElement;

        root.GetProperty("agency").GetProperty("legalName").GetString().Should().Be("Lagos Travel Limited");
        root.GetProperty("users").GetArrayLength().Should().Be(1);
        root.GetProperty("exportedByUserId").GetGuid().Should().Be(world.AdminUserId);

        // An export is a record of the business, not a way to walk off with the keys to it.
        export.Json.Should().NotContain("passwordHash").And.NotContain("argon2id");
    }

    [Fact]
    public async Task An_export_of_an_agency_that_does_not_exist_is_nothing()
    {
        await using var world = await AdminWorld.CreateAsync(_postgres);

        (await world.Exports.BuildAsync(Guid.CreateVersion7())).Should().BeNull();
    }

    // ------------------------------------------------------------------------------- tenancy

    [Fact]
    public async Task One_agency_never_sees_another_through_the_admin_queries()
    {
        await using var world = await AdminWorld.CreateAsync(_postgres);
        var mine = await world.AddAgencyAsync("Mine Travel Limited", "mine-travel");
        var theirs = await world.AddAgencyAsync("Theirs Travel Limited", "theirs-travel");

        // The directory service is only ever reachable behind a platform permission, but the
        // filter underneath it has to hold on its own: this is the leak CLAUDE.md rule 3 is about.
        await using var asAgency = world.ActingAs(mine);

        (await asAgency.Agencies.AsNoTracking().Select(agency => agency.Id).ToListAsync())
            .Should().BeEquivalentTo([mine]);

        (await asAgency.Agencies.AsNoTracking().CountAsync(agency => agency.Id == theirs))
            .Should().Be(0);
    }

    [Fact]
    public async Task An_agency_reads_its_own_audit_trail_and_nobody_elses()
    {
        await using var world = await AdminWorld.CreateAsync(_postgres);
        var mine = await world.AddAgencyAsync("Mine Travel Limited", "mine-travel");
        var theirs = await world.AddAgencyAsync("Theirs Travel Limited", "theirs-travel");

        await world.Lifecycle.SuspendAsync(theirs, "Chargebacks under investigation at this agency.");

        await using var asAgency = world.ActingAs(mine);

        // Their suspension, and the reason for it, is none of this agency's business.
        (await asAgency.AuditLogs.AsNoTracking().CountAsync(log => log.EntityId == theirs.ToString()))
            .Should().Be(0);
    }
}
