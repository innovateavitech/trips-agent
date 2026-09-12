namespace TripsAgent.Contracts.Platform;

/// <summary>How agencies are distributed across the lifecycle.</summary>
public sealed record AgencyCountsResponse(
    int Total,
    int PendingVerification,
    int Verified,
    int Rejected,
    int Suspended,
    int Terminated);

/// <summary>What sold in one window, in one currency.</summary>
/// <param name="GrossMinor">What travellers paid, in minor units. Net + markup + tax.</param>
/// <param name="PlatformFeeMinor">Trips' own take, which comes out of the agency's margin.</param>
public sealed record SalesWindowResponse(
    string Label,
    DateTimeOffset From,
    DateTimeOffset To,
    string Currency,
    int OrderCount,
    long GrossMinor,
    long NetMinor,
    long MarkupMinor,
    long PlatformFeeMinor);

/// <summary>One thing waiting for a person, from <c>platform.admin_alerts</c>.</summary>
public sealed record AdminAlertResponse(
    Guid Id,
    string Type,
    string Severity,
    string Status,
    Guid? AgencyId,
    string? AgencyName,
    string EntityType,
    Guid? EntityId,
    string Message,
    DateTimeOffset CreatedAt);

/// <summary>
/// The operations dashboard, as of <paramref name="GeneratedAt"/>.
/// </summary>
/// <param name="GeneratedAt">
/// When these numbers were counted. The console shows it, because a figure with no age is a
/// figure somebody will quote in a meeting three hours after it stopped being true.
/// </param>
/// <param name="StaleAfter">
/// When they will be recounted. The acceptance criterion is ten minutes; the cache is shorter, so
/// what the screen shows is always inside it.
/// </param>
public sealed record OperationsDashboardResponse(
    DateTimeOffset GeneratedAt,
    DateTimeOffset StaleAfter,
    AgencyCountsResponse Agencies,
    int PendingKybCount,
    int OpenAlertCount,
    int CriticalAlertCount,
    int BookingsNeedingResolution,
    IReadOnlyList<SalesWindowResponse> Sales,
    IReadOnlyList<AdminAlertResponse> Alerts);
