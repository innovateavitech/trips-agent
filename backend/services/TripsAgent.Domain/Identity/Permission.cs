using TripsAgent.Domain.Common;

namespace TripsAgent.Domain.Identity;

/// <summary>
/// One thing a user may do, identified by a stable dotted code.
/// </summary>
/// <remarks>
/// Permissions are platform-wide and seeded, never created at runtime: code checks
/// <c>booking.issue</c> by name, so an agency inventing its own permission would have nothing
/// checking it. Roles are the per-agency part.
/// </remarks>
public sealed class Permission : Entity
{
    private Permission()
    {
        Code = string.Empty;
        Category = string.Empty;
        Description = string.Empty;
    }

    public static Permission Create(string code, string category, string description) =>
        new()
        {
            Code = code,
            Category = category,
            Description = description,
        };

    /// <summary>Dotted, lower-case, stable — <c>booking.issue</c>. Unique platform-wide.</summary>
    public string Code { get; private set; }

    /// <summary>Groups permissions for display on the role editor.</summary>
    public string Category { get; private set; }

    /// <summary>Plain-English explanation, shown next to the checkbox.</summary>
    public string Description { get; private set; }
}

/// <summary>
/// Every permission code in the system, in one place.
/// </summary>
/// <remarks>
/// Constants rather than loose strings, so a typo is a compile error rather than a permission
/// check that silently never passes. <c>PermissionCatalogTests</c> keeps this list and the seeded
/// rows in step.
/// </remarks>
public static class PermissionCodes
{
    public const string BookingSearch = "booking.search";
    public const string BookingCreate = "booking.create";
    public const string BookingIssue = "booking.issue";
    public const string BookingCancel = "booking.cancel";
    public const string BookingRefund = "booking.refund";

    public const string WalletView = "wallet.view";
    public const string WalletFund = "wallet.fund";
    public const string MarginView = "margin.view";
    public const string MarginEdit = "margin.edit";

    public const string CatalogView = "catalog.view";
    public const string CatalogEdit = "catalog.edit";
    public const string CatalogPublish = "catalog.publish";

    public const string StorefrontEdit = "storefront.edit";
    public const string StorefrontPublish = "storefront.publish";

    public const string CustomerView = "customer.view";
    public const string CustomerEdit = "customer.edit";

    public const string TeamInvite = "team.invite";
    public const string TeamManage = "team.manage";
    public const string SubAgentManage = "subagent.manage";

    public const string ReportView = "report.view";
    public const string ReportExport = "report.export";

    // ------------------------------------------------------------------ platform-only

    /// <summary>Approve or reject an agency's KYB submission. Trips staff only.</summary>
    public const string KybReview = "kyb.review";

    /// <summary>Read the agency directory and one agency's profile. Every back-office role holds it.</summary>
    public const string AgencyView = "agency.view";

    public const string AgencyManage = "agency.manage";
    public const string AgencySuspend = "agency.suspend";

    /// <summary>End an agency's relationship with Trips for good. Deliberately not bundled with suspend.</summary>
    public const string AgencyTerminate = "agency.terminate";

    /// <summary>Export everything one agency owns. The most concentrated read in the system.</summary>
    public const string AgencyExport = "agency.export";

    /// <summary>Read the platform audit trail — who did what to whom, and why.</summary>
    public const string AuditView = "audit.view";

    public const string PlatformReportView = "platform.report.view";
    public const string SubscriptionManage = "subscription.manage";
    public const string PlatformUserManage = "platform.user.manage";

    /// <summary>Categories used to group permissions on the role editor.</summary>
    public static class Categories
    {
        public const string Bookings = "Bookings";
        public const string Money = "Money";
        public const string Catalog = "Catalog";
        public const string Storefront = "Storefront";
        public const string Customers = "Customers";
        public const string Team = "Team";
        public const string Reports = "Reports";
        public const string Platform = "Platform";
    }

    /// <summary>
    /// Every permission with its category and description, in seed order.
    /// </summary>
    /// <remarks>
    /// The single source of truth. The seeder writes exactly this list, so adding a permission is
    /// a one-line change here rather than an edit in three places that drift apart.
    /// </remarks>
    public static IReadOnlyList<(string Code, string Category, string Description)> All { get; } =
    [
        (BookingSearch, Categories.Bookings, "Search flights and buses"),
        (BookingCreate, Categories.Bookings, "Create a booking and hold it"),
        (BookingIssue, Categories.Bookings, "Issue tickets — this spends real money"),
        (BookingCancel, Categories.Bookings, "Cancel a booking"),
        (BookingRefund, Categories.Bookings, "Request a refund"),

        (WalletView, Categories.Money, "See the wallet balance and its ledger"),
        (WalletFund, Categories.Money, "Top the wallet up"),
        (MarginView, Categories.Money, "See net rates and the markup applied"),
        (MarginEdit, Categories.Money, "Change markup rules"),

        (CatalogView, Categories.Catalog, "See tours, visas and group departures"),
        (CatalogEdit, Categories.Catalog, "Create and edit catalog products"),
        (CatalogPublish, Categories.Catalog, "Publish a catalog product to the storefront"),

        (StorefrontEdit, Categories.Storefront, "Edit the storefront's pages and branding"),
        (StorefrontPublish, Categories.Storefront, "Publish storefront changes"),

        (CustomerView, Categories.Customers, "See traveller records and booking history"),
        (CustomerEdit, Categories.Customers, "Edit traveller records"),

        (TeamInvite, Categories.Team, "Invite a colleague"),
        (TeamManage, Categories.Team, "Change colleagues' roles and access"),
        (SubAgentManage, Categories.Team, "Create and manage sub-agents"),

        (ReportView, Categories.Reports, "See this agency's reports"),
        (ReportExport, Categories.Reports, "Export a report"),

        (KybReview, Categories.Platform, "Approve or reject an agency's KYB submission"),
        (AgencyView, Categories.Platform, "See the agency directory and an agency's profile"),
        (AgencyManage, Categories.Platform, "Create and edit agencies"),
        (AgencySuspend, Categories.Platform, "Suspend an agency, or put a suspended one back"),
        (AgencyTerminate, Categories.Platform, "End an agency's relationship with Trips for good"),
        (AgencyExport, Categories.Platform, "Export everything one agency owns"),
        (AuditView, Categories.Platform, "Read the platform audit trail"),
        (PlatformReportView, Categories.Platform, "See platform-wide reporting across agencies"),
        (SubscriptionManage, Categories.Platform, "Manage subscription tiers and pricing"),
        (PlatformUserManage, Categories.Platform, "Manage Trips back-office users"),
    ];

    /// <summary>Codes that only Trips staff may ever hold.</summary>
    public static IReadOnlyList<string> PlatformOnly { get; } =
    [
        KybReview,
        AgencyView,
        AgencyManage,
        AgencySuspend,
        AgencyTerminate,
        AgencyExport,
        AuditView,
        PlatformReportView,
        SubscriptionManage,
        PlatformUserManage,
    ];
}
