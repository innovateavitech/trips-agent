using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using TripsAgent.Application.Platform;
using TripsAgent.Contracts.Platform;
using TripsAgent.Domain.Identity;
using TripsAgent.Domain.Platform;
using TripsAgent.Domain.Tenancy;
using TripsAgent.IntegrationTests.Persistence;

namespace TripsAgent.IntegrationTests.Platform;

/// <summary>
/// The operations dashboard, the audit viewer, and Trips' own back-office accounts.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class BackOfficeTests
{
    private readonly PostgresFixture _postgres;

    public BackOfficeTests(PostgresFixture postgres) => _postgres = postgres;

    // ------------------------------------------------------------------------------ dashboard

    [Fact]
    public async Task The_dashboard_counts_agencies_by_where_they_are_in_the_lifecycle()
    {
        await using var world = await AdminWorld.CreateAsync(_postgres);
        await world.AddAgencyAsync("Alpha Travel Limited", "alpha-travel");
        await world.AddAgencyAsync("Beta Travel Limited", "beta-travel");
        await world.AddAgencyAsync("Gamma Travel Limited", "gamma-travel", AgencyStatus.PendingVerification);
        await world.AddAgencyAsync("Delta Travel Limited", "delta-travel", AgencyStatus.Suspended);

        var dashboard = await world.Dashboard.BuildAsync(TimeSpan.FromMinutes(5));

        dashboard.Agencies.Total.Should().Be(4);
        dashboard.Agencies.Verified.Should().Be(2);
        dashboard.Agencies.PendingVerification.Should().Be(1);
        dashboard.Agencies.Suspended.Should().Be(1);
        dashboard.Agencies.Terminated.Should().Be(0);
    }

    [Fact]
    public async Task The_dashboard_says_how_old_its_numbers_are_and_when_they_expire()
    {
        await using var world = await AdminWorld.CreateAsync(_postgres);

        var dashboard = await world.Dashboard.BuildAsync(TimeSpan.FromMinutes(5));

        // A figure with no age is a figure somebody quotes three hours after it stopped being true.
        dashboard.GeneratedAt.Should().Be(world.Clock.GetUtcNow());
        dashboard.StaleAfter.Should().Be(world.Clock.GetUtcNow().AddMinutes(5));

        // The criterion is ten minutes; the cache is five, so the screen is always inside it.
        (dashboard.StaleAfter - dashboard.GeneratedAt).Should().BeLessThan(TimeSpan.FromMinutes(10));
    }

    [Fact]
    public async Task The_dashboard_carries_the_open_alerts_most_urgent_first()
    {
        await using var world = await AdminWorld.CreateAsync(_postgres);
        var agencyId = await world.AddAgencyAsync("Lagos Travel Limited", "lagos-travel");

        using (var _ = world.Tenancy.Scope.Enter("test setup — raises alerts"))
        {
            world.Db.AdminAlerts.AddRange(
                AdminAlert.ForPlatform(
                    AdminAlertType.GatewayError, AdminAlertSeverity.Info, "A webhook was late.", "test"),
                AdminAlert.ForPlatform(
                    AdminAlertType.LedgerIntegrity, AdminAlertSeverity.Critical,
                    "The nightly audit found an unbalanced transaction group.", "test", agencyId));

            await world.Db.SaveChangesAsync();
        }

        var dashboard = await world.Dashboard.BuildAsync(TimeSpan.FromMinutes(5));

        dashboard.OpenAlertCount.Should().Be(2);
        dashboard.CriticalAlertCount.Should().Be(1);
        dashboard.Alerts[0].Severity.Should().Be(nameof(AdminAlertSeverity.Critical));
        dashboard.Alerts[0].AgencyName.Should().Be("Lagos Travel Limited");
    }

    [Fact]
    public async Task Every_alert_type_the_code_can_raise_is_one_the_table_accepts()
    {
        // ck_admin_alerts_type listed five types while AdminAlertType had grown to seven, so the
        // nightly ledger audit and the supplier status poller could not record what they found.
        // This is what stops the two drifting apart again.
        await using var world = await AdminWorld.CreateAsync(_postgres);

        using var _ = world.Tenancy.Scope.Enter("test — raises one alert of every type there is");

        foreach (var type in Enum.GetValues<AdminAlertType>())
        {
            world.Db.AdminAlerts.Add(AdminAlert.ForPlatform(
                type, AdminAlertSeverity.Warning, $"An alert of type {type}.", "test"));
        }

        var save = async () => await world.Db.SaveChangesAsync();
        await save.Should().NotThrowAsync();
    }

    [Fact]
    public async Task A_resolved_alert_is_not_waiting_for_anybody()
    {
        await using var world = await AdminWorld.CreateAsync(_postgres);

        using (var _ = world.Tenancy.Scope.Enter("test setup — raises and resolves an alert"))
        {
            var alert = AdminAlert.ForPlatform(
                AdminAlertType.GatewayError, AdminAlertSeverity.Warning, "A webhook failed.", "test");

            alert.Resolve(world.Clock.GetUtcNow());
            world.Db.AdminAlerts.Add(alert);
            await world.Db.SaveChangesAsync();
        }

        (await world.Dashboard.BuildAsync(TimeSpan.FromMinutes(5))).OpenAlertCount.Should().Be(0);
    }

    [Fact]
    public async Task Sales_are_reported_over_three_windows_in_minor_units()
    {
        await using var world = await AdminWorld.CreateAsync(_postgres);

        var dashboard = await world.Dashboard.BuildAsync(TimeSpan.FromMinutes(5));

        dashboard.Sales.Select(window => window.Label)
            .Should().Equal("Today", "Last 7 days", "Last 30 days");

        // Nothing sold yet, but the shape has to be right — and the amounts are bigint kobo, never
        // a decimal (CLAUDE.md rule 2).
        dashboard.Sales.Should().OnlyContain(window => window.GrossMinor == 0 && window.OrderCount == 0);
    }

    // --------------------------------------------------------------------------- audit viewer

    [Fact]
    public async Task The_audit_viewer_finds_an_action_by_the_agency_it_was_about()
    {
        await using var world = await AdminWorld.CreateAsync(_postgres);
        var agencyId = await world.AddAgencyAsync("Lagos Travel Limited", "lagos-travel");

        await world.Lifecycle.SuspendAsync(agencyId, "Chargeback rate above 3% for two months.");

        var page = await world.AuditLog.SearchAsync(new AuditLogQuery(AgencyId: agencyId));

        page.TotalCount.Should().BeGreaterThan(0);
        page.Items.Should().Contain(entry =>
            entry.Reason != null && entry.Reason.Contains("Chargeback rate", StringComparison.Ordinal));

        // Either the agency's own staff wrote it, or it is an action about that agency. Nothing
        // about anybody else is in this page.
        page.Items.Should().OnlyContain(entry =>
            entry.AgencyId == agencyId
            || (entry.EntityType == "Agency" && entry.EntityId == agencyId.ToString()));
    }

    [Fact]
    public async Task The_audit_viewer_filters_by_who_acted()
    {
        await using var world = await AdminWorld.CreateAsync(_postgres);
        var agencyId = await world.AddAgencyAsync("Lagos Travel Limited", "lagos-travel");
        await world.Lifecycle.SuspendAsync(agencyId, "Chargeback rate above 3% for two months.");

        var mine = await world.AuditLog.SearchAsync(new AuditLogQuery(ActorUserId: world.AdminUserId));
        var nobodys = await world.AuditLog.SearchAsync(new AuditLogQuery(ActorUserId: Guid.CreateVersion7()));

        mine.TotalCount.Should().BeGreaterThan(0);
        nobodys.TotalCount.Should().Be(0);
    }

    [Fact]
    public async Task The_viewers_action_filter_is_read_from_the_trail_itself()
    {
        await using var world = await AdminWorld.CreateAsync(_postgres);
        var agencyId = await world.AddAgencyAsync("Lagos Travel Limited", "lagos-travel");
        await world.Lifecycle.SuspendAsync(agencyId, "Chargeback rate above 3% for two months.");

        // Not a list in code: a job that starts writing a new action name should appear in the
        // filter without anybody remembering to add it.
        (await world.AuditLog.ActionsAsync()).Should().NotBeEmpty();
    }

    [Fact]
    public async Task A_page_of_the_trail_is_capped_however_the_url_was_edited()
    {
        new AuditLogQuery(PageSize: 100_000).SafePageSize.Should().Be(AuditLogQuery.MaxPageSize);
        new AuditLogQuery(Page: -4).SafePage.Should().Be(1);

        await Task.CompletedTask;
    }

    // ----------------------------------------------------------------------- back-office users

    [Fact]
    public async Task A_back_office_account_is_created_invited_with_one_role()
    {
        await using var world = await AdminWorld.CreateAsync(_postgres);

        var outcome = await world.PlatformUsers.CreateAsync(new CreatePlatformUserRequest(
            "ada@tripsagent.example.com", "Ada", "Okonkwo", Role.SystemRoles.OperationsAdmin,
            "New starter on the operations team, joined 12 September."));

        var created = outcome.Should().BeOfType<PlatformUserOutcome.Done>().Subject.User;

        created.Email.Should().Be("ada@tripsagent.example.com");
        created.Roles.Should().Equal(Role.SystemRoles.OperationsAdmin);
        created.Permissions.Should().Contain(PermissionCodes.KybReview);

        // Invited, not Active: an administrator never chooses somebody else's password.
        created.Status.Should().Be(nameof(UserStatus.Invited));
    }

    [Fact]
    public async Task A_back_office_account_belongs_to_no_agency()
    {
        await using var world = await AdminWorld.CreateAsync(_postgres);

        await world.PlatformUsers.CreateAsync(new CreatePlatformUserRequest(
            "ada@tripsagent.example.com", "Ada", "Okonkwo", Role.SystemRoles.SuperAdmin,
            "Second super admin, so the first is not a single point of failure."));

        using var _ = world.Tenancy.Scope.Enter("test — reads the account and its grant back");

        var user = await world.Db.Users.AsNoTracking()
            .SingleAsync(candidate => candidate.Email == "ada@tripsagent.example.com");

        user.AgencyId.Should().BeNull();

        var grant = await world.Db.UserRoles.AsNoTracking().SingleAsync(g => g.UserId == user.Id);
        grant.AgencyId.Should().BeNull("a platform grant belongs to no agency, which is what hides it from every one of them");
    }

    [Fact]
    public async Task An_agency_cannot_see_a_back_office_account_or_its_role()
    {
        await using var world = await AdminWorld.CreateAsync(_postgres);
        var agencyId = await world.AddAgencyAsync("Lagos Travel Limited", "lagos-travel");

        await world.PlatformUsers.CreateAsync(new CreatePlatformUserRequest(
            "ada@tripsagent.example.com", "Ada", "Okonkwo", Role.SystemRoles.SuperAdmin,
            "Second super admin, so the first is not a single point of failure."));

        await using var asAgency = world.ActingAs(agencyId);

        (await asAgency.Users.AsNoTracking().CountAsync(user => user.AgencyId == null)).Should().Be(0);
        (await asAgency.UserRoles.AsNoTracking().CountAsync(grant => grant.AgencyId == null)).Should().Be(0);
    }

    [Fact]
    public async Task Creating_an_account_demands_a_reason_and_a_role_that_exists()
    {
        await using var world = await AdminWorld.CreateAsync(_postgres);

        (await world.PlatformUsers.CreateAsync(new CreatePlatformUserRequest(
                "ada@tripsagent.example.com", "Ada", "Okonkwo", Role.SystemRoles.SuperAdmin, "no")))
            .Should().BeOfType<PlatformUserOutcome.ReasonRequired>();

        // Owner is an agency role. Granting it to a Trips account would put somebody inside an
        // agency's console with nothing recording that they are staff.
        (await world.PlatformUsers.CreateAsync(new CreatePlatformUserRequest(
                "ada@tripsagent.example.com", "Ada", "Okonkwo", Role.SystemRoles.Owner,
                "Trying to grant an agency role to back-office staff.")))
            .Should().BeOfType<PlatformUserOutcome.Invalid>();
    }

    [Fact]
    public async Task One_email_address_is_one_account()
    {
        await using var world = await AdminWorld.CreateAsync(_postgres);
        var request = new CreatePlatformUserRequest(
            "ada@tripsagent.example.com", "Ada", "Okonkwo", Role.SystemRoles.SupportAdmin,
            "New starter on the support desk, joined 12 September.");

        await world.PlatformUsers.CreateAsync(request);

        (await world.PlatformUsers.CreateAsync(request)).Should().BeOfType<PlatformUserOutcome.EmailTaken>();
    }

    [Fact]
    public async Task Changing_a_role_replaces_what_was_held_rather_than_adding_to_it()
    {
        await using var world = await AdminWorld.CreateAsync(_postgres);

        var created = (await world.PlatformUsers.CreateAsync(new CreatePlatformUserRequest(
                "zainab@tripsagent.example.com", "Zainab", "Suleiman", Role.SystemRoles.SupportAdmin,
                "New starter on the support desk, joined 12 September.")))
            .Should().BeOfType<PlatformUserOutcome.Done>().Subject.User;

        var moved = (await world.PlatformUsers.ChangeRoleAsync(created.Id, new ChangePlatformUserRoleRequest(
                Role.SystemRoles.OperationsAdmin, "Moved onto the operations rota this quarter.")))
            .Should().BeOfType<PlatformUserOutcome.Done>().Subject.User;

        moved.Roles.Should().Equal(Role.SystemRoles.OperationsAdmin);

        // And the permissions moved with it: support could not review KYB, operations can.
        moved.Permissions.Should().Contain(PermissionCodes.KybReview);
    }

    [Fact]
    public async Task Support_holds_no_permission_that_changes_an_agency()
    {
        await using var world = await AdminWorld.CreateAsync(_postgres);

        var support = (await world.PlatformUsers.CreateAsync(new CreatePlatformUserRequest(
                "zainab@tripsagent.example.com", "Zainab", "Suleiman", Role.SystemRoles.SupportAdmin,
                "New starter on the support desk, joined 12 September.")))
            .Should().BeOfType<PlatformUserOutcome.Done>().Subject.User;

        // The half of "a support user must not see more than their role allows" that the database
        // can answer: the token they will be issued carries none of these, and every endpoint
        // behind them checks the claim.
        support.Permissions.Should().Contain(PermissionCodes.AgencyView);
        support.Permissions.Should().NotContain([
            PermissionCodes.AgencyManage,
            PermissionCodes.AgencySuspend,
            PermissionCodes.AgencyTerminate,
            PermissionCodes.AgencyExport,
            PermissionCodes.AuditView,
            PermissionCodes.PlatformUserManage,
            PermissionCodes.KybReview,
        ]);
    }

    [Fact]
    public async Task Suspending_your_own_account_is_refused()
    {
        await using var world = await AdminWorld.CreateAsync(_postgres);

        var created = (await world.PlatformUsers.CreateAsync(new CreatePlatformUserRequest(
                "ada@tripsagent.example.com", "Ada", "Okonkwo", Role.SystemRoles.SuperAdmin,
                "Second super admin, so the first is not a single point of failure.")))
            .Should().BeOfType<PlatformUserOutcome.Done>().Subject.User;

        // Somebody else's: allowed.
        (await world.PlatformUsers.ChangeStatusAsync(created.Id, new ChangePlatformUserStatusRequest(
                nameof(UserStatus.Suspended), "Left the company on 12 September.")))
            .Should().BeOfType<PlatformUserOutcome.Done>();

        // Their own: refused, because the person who did it is the one who can no longer undo it.
        using (var _ = world.Tenancy.Scope.Enter("test setup — the acting admin's own account"))
        {
            world.Db.Users.Add(User.ForPlatform(
                "self@tripsagent.example.com", "argon2id$hash", "Self", "Admin"));
            await world.Db.SaveChangesAsync();
        }

        var self = await SelfAsync(world);

        (await world.PlatformUsers.ChangeStatusAsync(self, new ChangePlatformUserStatusRequest(
                nameof(UserStatus.Suspended), "Trying to suspend the account I am signed in as.")))
            .Should().BeOfType<PlatformUserOutcome.Invalid>();
    }

    [Fact]
    public async Task The_role_list_is_the_four_back_office_roles_with_their_permissions()
    {
        await using var world = await AdminWorld.CreateAsync(_postgres);

        var roles = await world.PlatformUsers.RolesAsync();

        roles.Select(role => role.Name).Should().BeEquivalentTo(Role.SystemRoles.Platform);
        roles.Should().OnlyContain(role => role.Permissions.Count > 0);

        roles.Single(role => role.Name == Role.SystemRoles.SuperAdmin)
            .Permissions.Should().HaveCount(PermissionCodes.All.Count);
    }

    /// <summary>
    /// Makes the acting admin a real row, so the "cannot suspend yourself" rule has something to
    /// find. The world's admin id is invented rather than seeded, because almost nothing needs it
    /// to exist.
    /// </summary>
    private static async Task<Guid> SelfAsync(AdminWorld world)
    {
        using var _ = world.Tenancy.Scope.Enter("test setup — finds the acting admin's own account");

        var self = await world.Db.Users
            .SingleAsync(user => user.Email == "self@tripsagent.example.com");

        world.Audit.ActorUserId = self.Id;
        return self.Id;
    }
}
