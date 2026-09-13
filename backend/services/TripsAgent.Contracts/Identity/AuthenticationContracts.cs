namespace TripsAgent.Contracts.Identity;

/// <summary>Credentials presented at sign-in.</summary>
public sealed record LoginRequest(string Email, string Password);

/// <summary>
/// A refresh token being exchanged, or revoked at sign-out. The API builds it from the refresh
/// cookie; no client sends it as a body.
/// </summary>
public sealed record RefreshTokenRequest(string RefreshToken);

/// <summary>
/// A freshly issued session. The refresh token is not in it: the same response sets it as an
/// <c>HttpOnly</c> cookie, single use, which the browser sends back to <c>/refresh</c> and
/// <c>/logout</c> by itself (issue 107).
/// </summary>
/// <param name="AccessToken">Send as <c>Authorization: Bearer …</c>. Short-lived; keep it in memory,
/// never in storage.</param>
/// <param name="ExpiresInSeconds">How long the access token has left, so the client can refresh
/// before a request fails rather than after.</param>
public sealed record TokenPairResponse(string AccessToken, int ExpiresInSeconds);

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
