namespace TripsAgent.Contracts.Identity;

/// <summary>Credentials presented at sign-in.</summary>
public sealed record LoginRequest(string Email, string Password);

/// <summary>A refresh token being exchanged, or revoked at sign-out.</summary>
public sealed record RefreshTokenRequest(string RefreshToken);

/// <summary>
/// A freshly issued pair.
/// </summary>
/// <param name="AccessToken">Send as <c>Authorization: Bearer …</c>. Short-lived.</param>
/// <param name="ExpiresInSeconds">How long the access token has left, so the client can refresh
/// before a request fails rather than after.</param>
/// <param name="RefreshToken">
/// Exchange for a new pair when the access token expires. Single use — the exchange returns a new
/// refresh token, and the old one stops working the moment it is used.
/// </param>
public sealed record TokenPairResponse(string AccessToken, int ExpiresInSeconds, string RefreshToken);

/// <summary>
/// Who the caller is, as the API sees them. The agency here is resolved from the token's claims,
/// so it is also a live check that tenant scoping is working.
/// </summary>
public sealed record CurrentUserResponse(
    Guid UserId,
    string Email,
    Guid? AgencyId,
    string? AgencyName,
    IReadOnlyList<string> Roles);
