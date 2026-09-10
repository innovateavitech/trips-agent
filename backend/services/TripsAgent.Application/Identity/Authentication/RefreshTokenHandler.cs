using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TripsAgent.Application.Persistence;
using TripsAgent.Application.Tenancy;
using TripsAgent.Contracts.Identity;
using TripsAgent.Domain.Identity;

namespace TripsAgent.Application.Identity.Authentication;

/// <summary>What happened to a refresh attempt.</summary>
public abstract record RefreshOutcome
{
    private RefreshOutcome()
    {
    }

    public sealed record Succeeded(IssuedTokenPair Tokens) : RefreshOutcome;

    /// <summary>Unknown, expired or revoked. The client must sign in again.</summary>
    public sealed record Rejected : RefreshOutcome;

    /// <summary>
    /// A token that had already been used was presented again. Every token for that user is
    /// revoked and they must sign in again.
    /// </summary>
    public sealed record ReuseDetected : RefreshOutcome;
}

/// <summary>
/// Exchanges a refresh token for a new pair, rotating it and watching for reuse.
/// </summary>
/// <remarks>
/// <para>
/// Refresh tokens are single use. Exchanging one mints a replacement and points the old row at it
/// through <c>replaced_by_id</c>, so the tokens form a chain.
/// </para>
/// <para>
/// That chain is what makes theft detectable. A stolen token is a copy — both the thief and the
/// real user hold the same value, and whoever uses it second presents one that has already been
/// exchanged. There is no way to tell which of them is the legitimate holder, so the only safe
/// response is to revoke everything for that user and make both sign in again. The real user
/// notices an unexpected sign-out; the attacker loses their access.
/// </para>
/// </remarks>
public sealed partial class RefreshTokenHandler
{
    private readonly IAppDbContext _db;
    private readonly ITokenHasher _tokenHasher;
    private readonly IPlatformScope _platformScope;
    private readonly TokenPairFactory _tokens;
    private readonly TimeProvider _clock;
    private readonly ILogger<RefreshTokenHandler> _logger;

    public RefreshTokenHandler(
        IAppDbContext db,
        ITokenHasher tokenHasher,
        IPlatformScope platformScope,
        TokenPairFactory tokens,
        TimeProvider clock,
        ILogger<RefreshTokenHandler> logger)
    {
        _db = db;
        _tokenHasher = tokenHasher;
        _platformScope = platformScope;
        _tokens = tokens;
        _clock = clock;
        _logger = logger;
    }

    public async Task<RefreshOutcome> HandleAsync(
        RefreshTokenRequest request,
        string? ipAddress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.RefreshToken))
        {
            return new RefreshOutcome.Rejected();
        }

        var now = _clock.GetUtcNow();
        var hash = _tokenHasher.Hash(request.RefreshToken);

        // The token identifies the user; nothing has established a tenant yet.
        using var scope = _platformScope.Enter(
            "token refresh — the refresh token identifies its user before any tenant exists");

        var presented = await _db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == hash, cancellationToken);

        if (presented is null)
        {
            return new RefreshOutcome.Rejected();
        }

        // Only an *already exchanged* token means reuse. A token that was merely revoked — by a
        // sign-out, or by an earlier chain revocation — is a client holding something stale, which
        // is ordinary. Treating that as reuse would raise a false alarm on every normal sign-out
        // and bury the real ones.
        if (presented.IsUsed)
        {
            await RevokeEverythingForAsync(presented.UserId, now, cancellationToken);
            LogReuseDetected(_logger, presented.UserId, presented.Id);
            return new RefreshOutcome.ReuseDetected();
        }

        if (!presented.IsActive(now))
        {
            return new RefreshOutcome.Rejected();
        }

        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == presented.UserId, cancellationToken);

        // An account suspended since the token was issued must not be able to refresh its way
        // back in — the access token is short-lived precisely so this check gets a chance to run.
        if (user is null || user.Status != UserStatus.Active || !user.IsEmailVerified)
        {
            presented.Revoke(now);
            await _db.SaveChangesAsync(cancellationToken);
            return new RefreshOutcome.Rejected();
        }

        var pair = await _tokens.CreateAsync(user, now, ipAddress, cancellationToken);

        presented.MarkReplacedBy(pair.Stored.Id, now);

        await _db.SaveChangesAsync(cancellationToken);

        return new RefreshOutcome.Succeeded(pair);
    }

    /// <summary>
    /// Revokes every refresh token the user holds.
    /// </summary>
    /// <remarks>
    /// Broader than walking the one chain, and deliberately so: an attacker who got one token may
    /// have got others, and there is no way to tell which side of the chain is legitimate. This is
    /// the standard response to detected reuse (RFC 6819 §5.2.2.3).
    /// </remarks>
    private async Task RevokeEverythingForAsync(Guid userId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var live = await _db.RefreshTokens
            .Where(token => token.UserId == userId && token.RevokedAt == null)
            .ToListAsync(cancellationToken);

        foreach (var token in live)
        {
            token.Revoke(now);
        }

        await _db.SaveChangesAsync(cancellationToken);
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Refresh token reuse detected for user {UserId} (token {TokenId}). Every token for "
                  + "that user has been revoked and they must sign in again.")]
    private static partial void LogReuseDetected(ILogger logger, Guid userId, Guid tokenId);
}
