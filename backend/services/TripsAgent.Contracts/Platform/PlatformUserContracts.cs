namespace TripsAgent.Contracts.Platform;

/// <summary>One Trips back-office account.</summary>
/// <param name="Roles">The platform roles held. Usually one; the model allows more.</param>
public sealed record PlatformUserResponse(
    Guid Id,
    string Email,
    string FirstName,
    string LastName,
    string Status,
    IReadOnlyList<string> Roles,
    IReadOnlyList<string> Permissions,
    DateTimeOffset? LastLoginAt,
    DateTimeOffset CreatedAt);

/// <summary>One of the four back-office roles, and what it lets somebody do.</summary>
public sealed record PlatformRoleResponse(
    Guid Id,
    string Name,
    string Description,
    IReadOnlyList<string> Permissions);

/// <summary>Creates a back-office account and grants it one role.</summary>
/// <remarks>
/// No password: the account is created invited, and the person sets their own through the
/// ordinary reset flow. A password chosen by an administrator is a password an administrator
/// knows.
/// </remarks>
public sealed record CreatePlatformUserRequest(
    string Email,
    string FirstName,
    string LastName,
    string RoleName,
    string Reason);

/// <summary>Moves a back-office account onto a different role.</summary>
public sealed record ChangePlatformUserRoleRequest(string RoleName, string Reason);

/// <summary>Suspends or reactivates a back-office account.</summary>
public sealed record ChangePlatformUserStatusRequest(string Status, string Reason);
