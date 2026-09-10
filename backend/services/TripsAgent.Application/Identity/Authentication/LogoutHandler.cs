using Microsoft.EntityFrameworkCore;
using TripsAgent.Application.Persistence;
using TripsAgent.Application.Tenancy;
using TripsAgent.Contracts.Identity;

namespace TripsAgent.Application.Identity.Authentication;

/// <summary>
/// Revokes a refresh token at sign-out.
/// </summary>
/// <remarks>
/// <para>
/// Returns nothing. Whether the token existed, was already revoked, or was never ours, the caller
/// gets the same answer — a sign-out endpoint that reported "no such token" would be a way to test
/// whether a stolen value is live.
/// </para>
/// <para>
/// The access token stays valid until it expires. That is the trade being made by having one at
/// all: checking a revocation list on every request would mean a database round trip per request,
/// which is exactly what stateless tokens exist to avoid. Fifteen minutes is the cap on how long
/// a signed-out session can linger.
/// </para>
/// </remarks>
public sealed class LogoutHandler
{
    private readonly IAppDbContext _db;
    private readonly ITokenHasher _tokenHasher;
    private readonly IPlatformScope _platformScope;
    private readonly TimeProvider _clock;

    public LogoutHandler(IAppDbContext db, ITokenHasher tokenHasher, IPlatformScope platformScope, TimeProvider clock)
    {
        _db = db;
        _tokenHasher = tokenHasher;
        _platformScope = platformScope;
        _clock = clock;
    }

    public async Task HandleAsync(RefreshTokenRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.RefreshToken))
        {
            return;
        }

        var hash = _tokenHasher.Hash(request.RefreshToken);

        using var scope = _platformScope.Enter("sign-out — a refresh token is revoked by its own value");

        var token = await _db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == hash, cancellationToken);

        if (token is null)
        {
            return;
        }

        token.Revoke(_clock.GetUtcNow());
        await _db.SaveChangesAsync(cancellationToken);
    }
}
