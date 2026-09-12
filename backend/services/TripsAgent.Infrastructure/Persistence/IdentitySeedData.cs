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
    public const string SupportAdminEmail = "support@tripsagent.example.com";
    public const string FinanceAdminEmail = "finance@tripsagent.example.com";
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
    /// Operations staff review KYB, edit agencies and read the audit trail.
    /// </summary>
    /// <remarks>
    /// Deliberately without <c>agency.suspend</c>, <c>agency.terminate</c>, <c>agency.export</c>
    /// or <c>subscription.manage</c> — ending or pausing a customer relationship is a commercial
    /// decision, and an export is the most concentrated read of one agency's data there is. All
    /// four are Super Admin only.
    /// </remarks>
    public static IReadOnlyList<string> OperationsAdminPermissions { get; } =
    [
        PermissionCodes.KybReview,
        PermissionCodes.AgencyView,
        PermissionCodes.AgencyManage,
        PermissionCodes.AuditView,
        PermissionCodes.PlatformReportView,
        PermissionCodes.CustomerView,
        PermissionCodes.ReportView,
    ];

    /// <summary>
    /// Support staff answer agencies' questions. They read, and that is all.
    /// </summary>
    /// <remarks>
    /// No <c>agency.manage</c>: a support conversation ends in an operations ticket, not in
    /// somebody editing a legal name on the phone. No <c>audit.view</c> either — the audit trail
    /// records what colleagues did, and reading it is an oversight job, not a support one.
    /// This is the role the "a support user must not see more than their role allows" test pins.
    /// </remarks>
    public static IReadOnlyList<string> SupportAdminPermissions { get; } =
    [
        PermissionCodes.AgencyView,
        PermissionCodes.CustomerView,
        PermissionCodes.ReportView,
    ];

    /// <summary>
    /// Finance staff look after the money: platform reporting, subscriptions and the audit trail.
    /// </summary>
    /// <remarks>
    /// They can see every agency's standing, because an unpaid subscription is their business,
    /// but they cannot change one — suspending a late payer is still a decision somebody signs.
    /// </remarks>
    public static IReadOnlyList<string> FinanceAdminPermissions { get; } =
    [
        PermissionCodes.AgencyView,
        PermissionCodes.AuditView,
        PermissionCodes.PlatformReportView,
        PermissionCodes.SubscriptionManage,
        PermissionCodes.ReportView,
        PermissionCodes.ReportExport,
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

        (Role.SystemRoles.SupportAdmin, RoleScope.Platform,
            "Trips staff who answer agencies' questions. Read-only.", SupportAdminPermissions),

        (Role.SystemRoles.FinanceAdmin, RoleScope.Platform,
            "Trips staff who look after billing, reporting and the audit trail.", FinanceAdminPermissions),
    ];
}
