namespace TripsAgent.Contracts.Platform;

/// <summary>One row of the agency directory.</summary>
/// <param name="Name">Trading name where the agency gave one, legal name otherwise.</param>
/// <param name="Status">AgencyStatus as its name — <c>Verified</c>, <c>Suspended</c>, and so on.</param>
/// <param name="Type">Principal or SubAgent.</param>
/// <param name="WalletBalanceMinor">
/// The agency's wallet balance in minor units of <paramref name="BaseCurrency"/>, or null if no
/// wallet has been opened — which is every agency that has not yet passed KYB.
/// </param>
/// <param name="OrderCount">Orders placed, ever. The cheapest signal of whether an account is alive.</param>
public sealed record AgencySummaryResponse(
    Guid Id,
    string Name,
    string LegalName,
    string Slug,
    string Status,
    string Type,
    string CountryCode,
    string BaseCurrency,
    Guid? ParentAgencyId,
    string? ParentAgencyName,
    DateTimeOffset CreatedAt,
    DateTimeOffset? VerifiedAt,
    long? WalletBalanceMinor,
    int OrderCount);

/// <summary>A page of the directory, with the total so the console can show "31 of 214".</summary>
public sealed record AgencyDirectoryResponse(
    IReadOnlyList<AgencySummaryResponse> Items,
    int TotalCount,
    int Page,
    int PageSize);

/// <summary>The person the console shows on an agency's profile.</summary>
public sealed record AgencyUserResponse(
    Guid Id,
    string Email,
    string FullName,
    string Status,
    IReadOnlyList<string> Roles,
    DateTimeOffset? LastLoginAt);

/// <summary>Everything one agency's profile screen reads.</summary>
/// <param name="StatusReason">Why it is suspended or terminated, in the admin's words. Internal only.</param>
/// <param name="CanTakeNewBookings">
/// Whether this agency may sell right now. The console shows it rather than re-deriving it, so
/// one rule (<c>AgencyAccess</c>) governs the screen and the checkout alike.
/// </param>
/// <param name="GrossSalesMinor">Everything travellers have paid this agency, ever, in minor units.</param>
public sealed record AgencyProfileResponse(
    Guid Id,
    string Name,
    string LegalName,
    string? TradingName,
    string Slug,
    string Status,
    string Type,
    string CountryCode,
    string BaseCurrency,
    string Timezone,
    string? TaxId,
    int VatRateBasisPoints,
    Guid? ParentAgencyId,
    string? ParentAgencyName,
    DateTimeOffset CreatedAt,
    DateTimeOffset? VerifiedAt,
    DateTimeOffset? OnboardingCompletedAt,
    DateTimeOffset? StatusChangedAt,
    string? StatusReason,
    bool CanTakeNewBookings,
    bool StorefrontIsLive,
    long? WalletBalanceMinor,
    long? WalletReservedMinor,
    int OrderCount,
    long GrossSalesMinor,
    IReadOnlyList<AgencyUserResponse> Users,
    IReadOnlyList<AgencySummaryResponse> SubAgents);

/// <summary>An edit to an agency's profile. The reason is not optional.</summary>
/// <param name="Reason">
/// Why this change is being made. Written to <c>platform.audit_logs</c> next to the before and
/// after state, so the trail answers "why" and not only "what".
/// </param>
public sealed record UpdateAgencyRequest(
    string LegalName,
    string? TradingName,
    string? TaxId,
    string Timezone,
    int VatRateBasisPoints,
    string Reason);

/// <summary>Suspend, reinstate or terminate. All three need a reason and nothing else.</summary>
public sealed record AgencyStatusChangeRequest(string Reason);

/// <summary>What the status now is, and what the agency may still do.</summary>
public sealed record AgencyStatusResponse(
    Guid AgencyId,
    string Status,
    DateTimeOffset ChangedAt,
    string Reason,
    bool CanTakeNewBookings,
    bool StorefrontIsLive);

/// <summary>One row of the audit trail, as the viewer shows it.</summary>
/// <param name="ActorName">The person's name, or "System" for a background job.</param>
/// <param name="BeforeState">The recorded columns before the change, as JSON text. Null on an insert.</param>
public sealed record AuditLogEntryResponse(
    Guid Id,
    DateTimeOffset OccurredAt,
    Guid? AgencyId,
    string? AgencyName,
    Guid? ActorUserId,
    string ActorName,
    string ActorType,
    string Action,
    string EntityType,
    string EntityId,
    string? Reason,
    string? BeforeState,
    string? AfterState,
    string? ActorIpAddress);

/// <summary>A page of the audit trail, newest first.</summary>
public sealed record AuditLogPageResponse(
    IReadOnlyList<AuditLogEntryResponse> Items,
    int TotalCount,
    int Page,
    int PageSize);
