namespace TripsAgent.Contracts.Tenancy;

// ---------------------------------------------------------------------------- the network

/// <summary>One sub-agent on the principal's list.</summary>
/// <param name="Status"><c>PendingVerification</c>, <c>Verified</c>, <c>Suspended</c>, <c>Terminated</c>.</param>
/// <param name="StatusReason">Why it was frozen or ended, in the principal's own words.</param>
/// <param name="HasOpenInvitation">True while its first user has not accepted yet.</param>
/// <param name="CanSeeMargin">False when the principal has taken <c>margin.view</c> away.</param>
/// <param name="AllowanceSpentMinor">Reserved this period, in kobo. Null when it has no allowance.</param>
/// <param name="AllowanceLimitMinor">The cap for this period, in kobo. Null when it has no allowance.</param>
public sealed record SubAgentResponse(
    Guid Id,
    string LegalName,
    string? TradingName,
    string Slug,
    string Status,
    string? StatusReason,
    DateTimeOffset CreatedAt,
    bool HasOpenInvitation,
    int ScopeCount,
    int DeniedPermissionCount,
    bool CanSeeMargin,
    long? AllowanceSpentMinor,
    long? AllowanceLimitMinor,
    string? AllowanceCurrency);

/// <summary>The principal's whole network, and how much room its plan leaves.</summary>
/// <param name="MaxSubAgents">The plan's cap, or null when the plan does not cap it.</param>
/// <param name="CannotAddReason">One sentence for the agent when another is not allowed.</param>
public sealed record SubAgentListResponse(
    IReadOnlyList<SubAgentResponse> SubAgents,
    int? MaxSubAgents,
    bool CanAddAnother,
    string? CannotAddReason);

/// <summary>Create a sub-agency and invite the person who will run it.</summary>
public sealed record InviteSubAgentRequest(string LegalName, string? TradingName, string Email);

/// <summary>
/// The sub-agent that was created, and the one-time invitation link.
/// </summary>
/// <param name="InvitationToken">
/// Returned once and never again — only a keyed hash of it is stored. It is here so the console can
/// show a copyable link when the email does not arrive; it is not a second way in.
/// </param>
public sealed record InviteSubAgentResponse(
    Guid Id,
    string Email,
    string InvitationToken,
    DateTimeOffset ExpiresAt);

/// <summary>Freeze, unfreeze or end a sub-agent. The reason is required and is audited.</summary>
public sealed record SubAgentStandingRequest(string Reason);

// ---------------------------------------------------------------------------- invitations

/// <summary>What the accept-invitation page shows before anything is typed.</summary>
/// <param name="InvitedBy">The principal's name. Never the Trips name — build-plan decision 6.</param>
public sealed record InvitationPreviewResponse(string Email, string BusinessName, string InvitedBy);

/// <summary>Accept an invitation and create the sub-agency's first user.</summary>
public sealed record AcceptInvitationRequest(
    string Token,
    string FirstName,
    string LastName,
    string Password,
    string? PhoneNumber);

/// <summary>
/// The account now exists. It can sign in once the six-digit code just sent to its address has been
/// entered at <c>/api/v1/auth/verify-email</c>, exactly like a self-registered account (issue 170).
/// </summary>
public sealed record AcceptInvitationResponse(Guid UserId, Guid AgencyId, string Email);

// ---------------------------------------------------------------------------- scopes

/// <summary>One thing a sub-agent may sell.</summary>
/// <param name="ProductType"><c>Flight</c>, <c>Bus</c>, <c>Tour</c>, <c>Visa</c> or <c>Package</c>.</param>
/// <param name="SupplierId">Null means every supplier of that product type.</param>
public sealed record SubAgentScopeResponse(
    Guid Id,
    string ProductType,
    Guid? SupplierId,
    string? SupplierName);

/// <summary>Let a sub-agent sell a product type, optionally through one supplier only.</summary>
public sealed record GrantSubAgentScopeRequest(string ProductType, Guid? SupplierId);

// ---------------------------------------------------------------------------- permissions

/// <summary>One row of the permissions matrix.</summary>
public sealed record SubAgentPermissionResponse(
    string Code,
    string Category,
    string Description,
    bool IsDenied,
    string? Reason);

/// <summary>Take a permission away from a sub-agent, with a reason that is audited.</summary>
public sealed record DenyPermissionRequest(string PermissionCode, string Reason);

// ---------------------------------------------------------------------------- allowances

/// <summary>A sub-agent's hard spending cap, and what is left of it.</summary>
/// <param name="Period"><c>Lifetime</c>, <c>Daily</c>, <c>Weekly</c> or <c>Monthly</c>.</param>
/// <param name="Status"><c>Active</c> or <c>Frozen</c>.</param>
public sealed record AllowanceResponse(
    Guid SubAgencyId,
    string Currency,
    long SpentMinor,
    long LimitMinor,
    long RemainingMinor,
    string Period,
    string Status,
    DateTimeOffset? ResetsAt);

/// <summary>Set a sub-agent's cap and how often it starts again.</summary>
public sealed record SetAllowanceRequest(long LimitMinor, string Period);

// ---------------------------------------------------------------------------- network reporting

/// <summary>
/// One agency's part of the network's figures, for a caller who may <b>not</b> see margin.
/// </summary>
/// <remarks>
/// A separate type from <see cref="NetworkMemberWithMarginResponse"/> rather than the same type
/// with the margin left null — the same rule, and the same reason, as
/// <c>PriceQuoteResponse</c>: a null field is still a field in the JSON, and a type that has no
/// such property cannot leak it.
/// </remarks>
public sealed record NetworkMemberResponse(
    Guid AgencyId,
    string Name,
    string Status,
    bool IsPrincipal,
    int Orders,
    long SalesMinor,
    long? AllowanceSpentMinor,
    long? AllowanceLimitMinor);

/// <summary>One agency's figures for a caller holding <c>margin.view</c>, margin included.</summary>
/// <param name="MarginMinor">Markup less the platform's fee, in kobo.</param>
public sealed record NetworkMemberWithMarginResponse(
    Guid AgencyId,
    string Name,
    string Status,
    bool IsPrincipal,
    int Orders,
    long SalesMinor,
    long MarginMinor,
    long? AllowanceSpentMinor,
    long? AllowanceLimitMinor);

/// <summary>The network's consolidated figures, without margin.</summary>
public sealed record NetworkPerformanceResponse(
    DateTimeOffset From,
    DateTimeOffset To,
    string Currency,
    int Orders,
    long SalesMinor,
    IReadOnlyList<NetworkMemberResponse> Members);

/// <summary>The network's consolidated figures for a caller holding <c>margin.view</c>.</summary>
public sealed record NetworkPerformanceWithMarginResponse(
    DateTimeOffset From,
    DateTimeOffset To,
    string Currency,
    int Orders,
    long SalesMinor,
    long MarginMinor,
    IReadOnlyList<NetworkMemberWithMarginResponse> Members);
