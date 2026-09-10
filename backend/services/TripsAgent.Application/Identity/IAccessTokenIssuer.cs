using TripsAgent.Domain.Identity;

namespace TripsAgent.Application.Identity;

/// <summary>A signed access token and the moment it stops being accepted.</summary>
public sealed record AccessToken(string Value, DateTimeOffset ExpiresAt);

/// <summary>
/// Mints the short-lived token the console sends on every request.
/// </summary>
/// <remarks>
/// A port, so the signing scheme is an infrastructure detail. It is deliberately the only way to
/// create one: a token assembled ad hoc somewhere else would be the thing that quietly omits
/// <c>agency_id</c> and leaves every query returning nothing.
/// </remarks>
public interface IAccessTokenIssuer
{
    /// <summary>
    /// Issues a token for <paramref name="user"/>.
    /// </summary>
    /// <param name="roles">Role names the user holds in the agency they are acting as.</param>
    /// <param name="rootAgencyId">
    /// The principal at the top of that agency's tree. Null for platform staff.
    /// </param>
    public AccessToken Issue(User user, IReadOnlyCollection<string> roles, Guid? rootAgencyId);
}
