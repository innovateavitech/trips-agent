using FluentAssertions;
using TripsAgent.Domain.Identity;
using TripsAgent.Domain.Tenancy.SubAgents;

namespace TripsAgent.UnitTests.Tenancy.SubAgents;

/// <summary>
/// Effective permissions: what a sub-agent's roles give, minus what its principal has taken away.
/// Feature F10, issue 63.
/// </summary>
public class SubAgentPermissionsTests
{
    [Fact]
    public void With_nothing_denied_the_roles_decide()
    {
        string[] granted = [PermissionCodes.BookingCreate, PermissionCodes.MarginView];

        SubAgentPermissions.Effective(granted, []).Should().Equal(granted);
    }

    [Fact]
    public void A_denied_permission_is_subtracted()
    {
        var effective = SubAgentPermissions.Effective(
            [PermissionCodes.BookingCreate, PermissionCodes.MarginView],
            [PermissionCodes.MarginView]);

        effective.Should().Equal(PermissionCodes.BookingCreate);
    }

    [Fact]
    public void Denying_something_the_roles_never_granted_changes_nothing()
    {
        var effective = SubAgentPermissions.Effective(
            [PermissionCodes.BookingCreate],
            [PermissionCodes.CatalogPublish]);

        effective.Should().Equal(PermissionCodes.BookingCreate);
    }

    [Fact]
    public void The_result_is_a_subtraction_and_never_an_addition()
    {
        // The property that matters: whatever is denied, the result is a subset of what the roles
        // granted. There is no deny list that can add a permission.
        string[] granted = [PermissionCodes.BookingCreate, PermissionCodes.WalletView];

        var effective = SubAgentPermissions.Effective(
            granted,
            [PermissionCodes.AgencyTerminate, PermissionCodes.PlatformUserManage]);

        effective.Should().BeSubsetOf(granted);
    }

    [Fact]
    public void Duplicates_and_casing_do_not_change_the_answer()
    {
        var effective = SubAgentPermissions.Effective(
            [PermissionCodes.BookingCreate, "BOOKING.CREATE", "  booking.create  "],
            ["  MARGIN.VIEW "]);

        effective.Should().Equal(PermissionCodes.BookingCreate);
    }

    [Fact]
    public void Margin_view_is_the_permission_margin_visibility_is_made_of()
    {
        // There is deliberately no second switch for margin visibility: hiding net rates from a
        // sub-agent is denying this one code, so the two can never disagree.
        SubAgentPermissions.CanBeOverridden(PermissionCodes.MarginView).Should().BeTrue();
        SubAgentPermissions.Overridable.Should().Contain(PermissionCodes.MarginView);
    }

    [Fact]
    public void A_platform_permission_cannot_be_overridden_because_no_agency_ever_holds_one()
    {
        foreach (var code in PermissionCodes.PlatformOnly)
        {
            SubAgentPermissions.CanBeOverridden(code).Should().BeFalse();
        }

        SubAgentPermissions.Overridable.Should().NotIntersectWith(PermissionCodes.PlatformOnly);
    }

    [Fact]
    public void A_code_nobody_ships_is_refused_rather_than_stored()
    {
        SubAgentPermissions.CanBeOverridden("booking.crate").Should().BeFalse("that is a typo, not a permission");
        SubAgentPermissions.CanBeOverridden("").Should().BeFalse();
        SubAgentPermissions.CanBeOverridden(null!).Should().BeFalse();
    }

    [Fact]
    public void Every_overridable_code_is_one_the_catalogue_ships()
    {
        SubAgentPermissions.Overridable.Should().OnlyContain(
            code => PermissionCodes.All.Any(entry => entry.Code == code));
    }

    [Fact]
    public void An_override_needs_a_reason_somebody_can_read_later()
    {
        var principal = Guid.CreateVersion7();
        var subAgent = Guid.CreateVersion7();

        var blank = () => PermissionOverride.Deny(principal, subAgent, PermissionCodes.MarginView, "   ");

        blank.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void An_agency_cannot_override_its_own_permissions()
    {
        var agency = Guid.CreateVersion7();

        var itself = () => PermissionOverride.Deny(agency, agency, PermissionCodes.MarginView, "no");

        itself.Should().Throw<InvalidOperationException>("that is what its roles are for");
    }
}
