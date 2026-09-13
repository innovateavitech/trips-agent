using TripsAgent.Domain.Identity;

namespace TripsAgent.Domain.Analytics;

/// <summary>
/// Every report this build can produce, in one place.
/// </summary>
/// <remarks>
/// The single source of truth: the migration seeds <c>analytics.report_definitions</c> from this
/// list, the API validates a requested code against it, and the generator dispatches on the same
/// code. Adding a report is a member here, a generator, and a seed row — and a test fails if the
/// table and this list drift apart, the way the permission catalogue already works.
/// </remarks>
public static class ReportCatalog
{
    /// <summary>An agency's own sales, revenue and margin by Lagos day.</summary>
    public const string AgencySalesDaily = "agency.sales.daily";

    /// <summary>An agency's own bookings, one row per order line. The drill-down target.</summary>
    public const string AgencyBookings = "agency.bookings";

    /// <summary>Platform GMV, growth and fee revenue by day, across every agency.</summary>
    public const string PlatformGmvDaily = "platform.gmv.daily";

    /// <summary>Supplier conversion, error rate and latency by day.</summary>
    public const string PlatformSupplierPerformance = "platform.supplier.performance";

    /// <summary>Every agency's totals over the window, one row per agency.</summary>
    public const string PlatformAgencySales = "platform.agency.sales";

    /// <summary>The definitions, exactly as they are seeded.</summary>
    /// <remarks>
    /// Ids are fixed rather than generated so the seed is idempotent: re-running the migration
    /// updates the same five rows instead of making five more.
    /// </remarks>
    public static readonly IReadOnlyList<ReportDefinition> All =
    [
        ReportDefinition.Create(
            new Guid("0199a1d0-0001-7000-8000-000000000001"),
            AgencySalesDaily,
            "Daily sales",
            "One row per day: bookings, gross sales, cost, markup and margin.",
            ReportScope.Agency,
            PermissionCodes.ReportView),

        ReportDefinition.Create(
            new Guid("0199a1d0-0002-7000-8000-000000000002"),
            AgencyBookings,
            "Bookings",
            "One row per booking, with the order, traveller count, status and what was charged.",
            ReportScope.Agency,
            PermissionCodes.ReportView),

        ReportDefinition.Create(
            new Guid("0199a1d0-0003-7000-8000-000000000003"),
            PlatformGmvDaily,
            "Platform GMV",
            "One row per day across every agency: GMV, selling agencies, new agencies and fee revenue.",
            ReportScope.Platform,
            PermissionCodes.PlatformReportView),

        ReportDefinition.Create(
            new Guid("0199a1d0-0004-7000-8000-000000000004"),
            PlatformSupplierPerformance,
            "Supplier performance",
            "One row per supplier per day: calls, search-to-book conversion, error rate and latency.",
            ReportScope.Platform,
            PermissionCodes.PlatformReportView),

        ReportDefinition.Create(
            new Guid("0199a1d0-0005-7000-8000-000000000005"),
            PlatformAgencySales,
            "Sales by agency",
            "One row per agency over the window: bookings, GMV and the fee Trips earned.",
            ReportScope.Platform,
            PermissionCodes.PlatformReportView),
    ];

    /// <summary>The definition with that code, or null if there is none.</summary>
    public static ReportDefinition? Find(string code) =>
        All.FirstOrDefault(definition =>
            string.Equals(definition.Code, code, StringComparison.Ordinal));
}
