using Microsoft.EntityFrameworkCore;
using TripsAgent.Application.Persistence;
using TripsAgent.Domain.Identity;
using TripsAgent.Domain.Tenancy;

namespace TripsAgent.Application.Identity.Authentication;

/// <summary>An access token plus the refresh token that will replace it.</summary>
/// <param name="RefreshTokenValue">
/// The plaintext, which exists only here and in the response. Only its hash is stored.
/// </param>
public sealed record IssuedTokenPair(AccessToken Access, string RefreshTokenValue, RefreshToken Stored);

/// <summary>
/// Builds a token pair for a user: gathers their roles, mints the access token, and records the
/// refresh token.
/// </summary>
/// <remarks>
/// Shared by sign-in and refresh so both produce identical tokens. If refresh built its own, a
/// role added today would silently not appear until the next full sign-in — or worse, a role
/// removed today would survive in refreshed tokens indefinitely.
/// </remarks>
public sealed class TokenPairFactory
{
    private readonly IAppDbContext _db;
    private readonly IAccessTokenIssuer _accessTokens;
    private readonly ITokenHasher _tokenHasher;

    public TokenPairFactory(IAppDbContext db, IAccessTokenIssuer accessTokens, ITokenHasher tokenHasher)
    {
        _db = db;
        _accessTokens = accessTokens;
        _tokenHasher = tokenHasher;
    }

    /// <summary>Creates a pair and adds the refresh token to the context. Not yet saved.</summary>
    public async Task<IssuedTokenPair> CreateAsync(
        User user,
        DateTimeOffset now,
        string? ipAddress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);

        var roles = await RolesForAsync(user, cancellationToken);
        var rootAgencyId = await RootAgencyIdForAsync(user, cancellationToken);

        var access = _accessTokens.Issue(user, roles, rootAgencyId);

        // Opaque and random, not a JWT: it carries no information, so there is nothing to read
        // out of it, and it is only ever meaningful next to the row that records it.
        var refreshValue = _tokenHasher.GenerateOpaqueToken();

        var stored = RefreshToken.Issue(
            user.Id,
            _tokenHasher.Hash(refreshValue),
            now.Add(RefreshToken.Lifetime),
            ipAddress);

        _db.RefreshTokens.Add(stored);

        return new IssuedTokenPair(access, refreshValue, stored);
    }

    /// <summary>The role names this user holds in the agency they are acting as.</summary>
    private async Task<List<string>> RolesForAsync(User user, CancellationToken cancellationToken)
    {
        var grants =
            from userRole in _db.UserRoles
            join role in _db.Roles on userRole.RoleId equals role.Id
            where userRole.UserId == user.Id
            select new { userRole.AgencyId, role.Name };

        // Platform staff have no agency of their own, so every grant they hold counts. Agency
        // users get only the roles granted in the agency they belong to — one person can hold
        // different roles in a principal and in one of its branches.
        if (user.AgencyId is { } agencyId)
        {
            grants = grants.Where(grant => grant.AgencyId == agencyId);
        }

        return await grants.Select(grant => grant.Name).Distinct().ToListAsync(cancellationToken);
    }

    private async Task<Guid?> RootAgencyIdForAsync(User user, CancellationToken cancellationToken)
    {
        if (user.AgencyId is not { } agencyId)
        {
            return null;
        }

        var agency = await _db.Agencies
            .Where(a => a.Id == agencyId)
            .Select(a => new { a.Id, a.ParentAgencyId })
            .FirstOrDefaultAsync(cancellationToken);

        // A principal is its own root; a sub-agent's root is the principal above it.
        return agency?.ParentAgencyId ?? agencyId;
    }
}
