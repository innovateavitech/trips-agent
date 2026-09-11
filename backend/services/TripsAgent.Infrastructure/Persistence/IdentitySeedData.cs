using TripsAgent.Domain.Identity;

namespace TripsAgent.Infrastructure.Persistence;

/// <summary>
/// Which permissions each system role gets, and the development accounts.
/// </summary>
/// <remarks>
/// Kept apart from <see cref="DatabaseSeeder"/> so the "what" is readable without wading through
/// the "how". Changing what an Owner can do is a one-line edit here.
/// </remarks>
public static class IdentitySeedData
{
    /// <summary>
    /// The password every seeded development account shares.
    /// </summary>
    /// <remarks>
    /// Not a secret, and deliberately obvious: it exists so a developer who has just cloned the
    /// repository can sign in. Seeding only ever runs against a local database — the command
    /// refuses to run if migrations are outstanding, and it is never part of a deploy.
    /// </remarks>
    public const string DevelopmentPassword = "Password123";

    public const string SuperAdminEmail = "admin@tripsagent.example.com";
    public const string OperationsAdminEmail = "ops@tripsagent.example.com";
    public const string VerifiedAgentEmail = "owner@lagostravel.example.com";
    public const string SubAgentEmail = "owner@ikejabranch.example.com";
    public const string PendingAgentEmail = "owner@pendingtravel.example.com";

    /// <summary>Slug of the extra agency seeded so a pending-verification account exists.</summary>
    public const string PendingAgencySlug = "pending-travel";

    /// <summary>Everything an agency Owner may do — every agency permission, no platform ones.</summary>
    public static IReadOnlyList<string> OwnerPermissions { get; } =
        PermissionCodes.All
            .Select(permission => permission.Code)
            .Where(code => !PermissionCodes.PlatformOnly.Contains(code))
            .ToList();

    /// <summary>
    /// A Manager runs the day-to-day business but does not restructure it: no sub-agents, no
    /// team changes beyond inviting, and no wallet top-ups.
    /// </summary>
    public static IReadOnlyList<string> ManagerPermissions { get; } =
    [
        PermissionCodes.BookingSearch,
        PermissionCodes.BookingCreate,
        PermissionCodes.BookingIssue,
        PermissionCodes.BookingCancel,
        PermissionCodes.BookingRefund,
        PermissionCodes.WalletView,
        PermissionCodes.MarginView,
        PermissionCodes.MarginEdit,
        PermissionCodes.CatalogView,
        PermissionCodes.CatalogEdit,
        PermissionCodes.CatalogPublish,
        PermissionCodes.StorefrontEdit,
        PermissionCodes.StorefrontPublish,
        PermissionCodes.CustomerView,
        PermissionCodes.CustomerEdit,
        PermissionCodes.TeamInvite,
        PermissionCodes.ReportView,
        PermissionCodes.ReportExport,
    ];

    /// <summary>
    /// An Agent sells. Notably no <c>margin.view</c>: the net rate is what Trips charges the
    /// agency, and a counter agent has no reason to see their employer's cost price.
    /// </summary>
    public static IReadOnlyList<string> AgentPermissions { get; } =
    [
        PermissionCodes.BookingSearch,
        PermissionCodes.BookingCreate,
        PermissionCodes.BookingIssue,
        PermissionCodes.CatalogView,
        PermissionCodes.CustomerView,
        PermissionCodes.CustomerEdit,
    ];

    /// <summary>A Super Admin holds everything, including every platform permission.</summary>
    public static IReadOnlyList<string> SuperAdminPermissions { get; } =
        PermissionCodes.All.Select(permission => permission.Code).ToList();

    /// <summary>
    /// Operations staff review KYB and support agencies. Deliberately without
    /// <c>agency.suspend</c> or <c>subscription.manage</c> — those are commercial decisions.
    /// </summary>
    public static IReadOnlyList<string> OperationsAdminPermissions { get; } =
    [
        PermissionCodes.KybReview,
        PermissionCodes.AgencyManage,
        PermissionCodes.PlatformReportView,
        PermissionCodes.CustomerView,
        PermissionCodes.ReportView,
    ];

    /// <summary>The system roles, with their scope and description.</summary>
    public static IReadOnlyList<(string Name, RoleScope Scope, string Description, IReadOnlyList<string> Permissions)> SystemRoles { get; } =
    [
        (Role.SystemRoles.Owner, RoleScope.Agency,
            "Full control of the agency, including money and team.", OwnerPermissions),

        (Role.SystemRoles.Manager, RoleScope.Agency,
            "Runs the day-to-day business. No sub-agents and no wallet top-ups.", ManagerPermissions),

        (Role.SystemRoles.Agent, RoleScope.Agency,
            "Sells to travellers. Cannot see net rates or margin.", AgentPermissions),

        (Role.SystemRoles.SuperAdmin, RoleScope.Platform,
            "Trips staff with unrestricted access.", SuperAdminPermissions),

        (Role.SystemRoles.OperationsAdmin, RoleScope.Platform,
            "Trips staff who review KYB and support agencies.", OperationsAdminPermissions),
    ];
}
