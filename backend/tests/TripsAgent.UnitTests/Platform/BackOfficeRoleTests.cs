using FluentAssertions;
using TripsAgent.Domain.Identity;
using TripsAgent.Infrastructure.Persistence;

namespace TripsAgent.UnitTests.Platform;

/// <summary>
/// The four back-office roles and what each of them holds.
/// </summary>
/// <remarks>
/// These assertions are the written-down version of "a support user must not see more than their
/// role allows". They are cheap, they are read by anyone wondering what a role means, and they
/// fail loudly if a permission is quietly added to a role it does not belong on.
/// </remarks>
public class BackOfficeRoleTests
{
    private static IReadOnlyList<string> PermissionsFor(string roleName) =>
        IdentitySeedData.SystemRoles.Single(role => role.Name == roleName).Permissions;

    [Fact]
    public void There_are_exactly_four_back_office_roles()
    {
        var platformRoles = IdentitySeedData.SystemRoles
            .Where(role => role.Scope == RoleScope.Platform)
            .Select(role => role.Name);

        platformRoles.Should().BeEquivalentTo(Role.SystemRoles.Platform);
        Role.SystemRoles.Platform.Should().HaveCount(4);
    }

    [Fact]
    public void A_super_admin_holds_every_permission_there_is()
    {
        PermissionsFor(Role.SystemRoles.SuperAdmin)
            .Should().BeEquivalentTo(PermissionCodes.All.Select(permission => permission.Code));
    }

    [Theory]
    [InlineData(PermissionCodes.AgencyManage)]
    [InlineData(PermissionCodes.AgencySuspend)]
    [InlineData(PermissionCodes.AgencyTerminate)]
    [InlineData(PermissionCodes.AgencyExport)]
    [InlineData(PermissionCodes.AuditView)]
    [InlineData(PermissionCodes.PlatformUserManage)]
    [InlineData(PermissionCodes.KybReview)]
    public void Support_can_read_an_agency_but_change_nothing(string forbidden)
    {
        var support = PermissionsFor(Role.SystemRoles.SupportAdmin);

        support.Should().Contain(PermissionCodes.AgencyView);
        support.Should().NotContain(forbidden);
    }

    [Theory]
    [InlineData(PermissionCodes.AgencySuspend)]
    [InlineData(PermissionCodes.AgencyTerminate)]
    [InlineData(PermissionCodes.AgencyExport)]
    [InlineData(PermissionCodes.PlatformUserManage)]
    public void Ending_or_pausing_a_relationship_is_super_admin_only(string permission)
    {
        foreach (var role in Role.SystemRoles.Platform.Where(name => name != Role.SystemRoles.SuperAdmin))
        {
            PermissionsFor(role).Should().NotContain(
                permission,
                $"{permission} is a commercial decision, and {role} does not make it");
        }
    }

    [Fact]
    public void Operations_reviews_kyb_and_edits_agencies()
    {
        PermissionsFor(Role.SystemRoles.OperationsAdmin)
            .Should().Contain([PermissionCodes.KybReview, PermissionCodes.AgencyView, PermissionCodes.AgencyManage]);
    }

    [Fact]
    public void Finance_sees_the_money_but_does_not_touch_the_agency()
    {
        var finance = PermissionsFor(Role.SystemRoles.FinanceAdmin);

        finance.Should().Contain([PermissionCodes.PlatformReportView, PermissionCodes.SubscriptionManage]);
        finance.Should().NotContain(PermissionCodes.AgencyManage);
    }

    [Fact]
    public void No_agency_role_ever_holds_a_platform_permission()
    {
        var agencyRoles = IdentitySeedData.SystemRoles.Where(role => role.Scope == RoleScope.Agency);

        foreach (var role in agencyRoles)
        {
            role.Permissions.Should().NotIntersectWith(
                PermissionCodes.PlatformOnly,
                $"{role.Name} belongs to a travel agency, and a platform permission reads every agency's data");
        }
    }

    [Fact]
    public void Every_permission_a_role_grants_is_in_the_catalogue()
    {
        var catalogue = PermissionCodes.All.Select(permission => permission.Code).ToHashSet(StringComparer.Ordinal);

        foreach (var role in IdentitySeedData.SystemRoles)
        {
            role.Permissions.Should().BeSubsetOf(catalogue, $"{role.Name} grants only codes that exist");
        }
    }

    [Fact]
    public void A_platform_grant_carries_no_agency()
    {
        var grant = UserRole.GrantPlatform(Guid.CreateVersion7(), Guid.CreateVersion7());

        // Null is what makes it invisible to every agency: row-level security only ever matches
        // agency_id = current_agency_id(), and NULL equals nothing.
        grant.AgencyId.Should().BeNull();
    }
}
