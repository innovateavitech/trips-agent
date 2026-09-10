using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using TripsAgent.Application.Persistence;
using TripsAgent.Application.Tenancy;
using TripsAgent.Contracts.Identity;
using TripsAgent.Domain.Identity;

namespace TripsAgent.Application.Identity.Registration;

/// <summary>What happened to a verification attempt.</summary>
public enum VerifyEmailOutcome
{
    Verified = 1,

    /// <summary>
    /// Wrong, expired, already used, burned through its attempts, or no such account. One outcome
    /// for all of them, so the response never says which — each distinction is something an
    /// attacker could use.
    /// </summary>
    Rejected = 2,
}

/// <summary>Confirms an email address with the code sent to it. FRD §2.2 UC-1A RS-3.</summary>
public sealed class VerifyEmailHandler
{
    private readonly IAppDbContext _db;
    private readonly ITokenHasher _tokenHasher;
    private readonly IPlatformScope _platformScope;
    private readonly TimeProvider _clock;

    public VerifyEmailHandler(IAppDbContext db, ITokenHasher tokenHasher, IPlatformScope platformScope, TimeProvider clock)
    {
        _db = db;
        _tokenHasher = tokenHasher;
        _platformScope = platformScope;
        _clock = clock;
    }

    public async Task<VerifyEmailOutcome> HandleAsync(VerifyEmailRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var code = request.Code?.Trim() ?? string.Empty;

        if (code.Length != 6 || !code.All(char.IsAsciiDigit) || string.IsNullOrWhiteSpace(request.Email))
        {
            return VerifyEmailOutcome.Rejected;
        }

        var email = request.Email.Trim().ToLowerInvariant();
        var now = _clock.GetUtcNow();

        // The address arrives before anyone has signed in, so there is no tenant yet.
        using var scope = _platformScope.Enter(
            "email verification — the account is identified by address before any tenant is known");

        var user = await _db.Users.FirstOrDefaultAsync(u => u.Email == email, cancellationToken);

        // Only the newest code counts. Asking for a new one retires the old, so a code that has
        // been sitting in an old email cannot be used once a fresher one exists.
        var latest = await _db.OtpCodes
            .Where(c => c.Destination == email && c.Purpose == OtpPurpose.EmailVerification)
            .OrderByDescending(c => c.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        if (user is null || latest is null || !latest.IsUsable(now))
        {
            return VerifyEmailOutcome.Rejected;
        }

        // Fixed-time comparison, so response timing never reveals how many characters matched.
        var presented = Encoding.UTF8.GetBytes(_tokenHasher.Hash(code));
        var stored = Encoding.UTF8.GetBytes(latest.CodeHash);

        if (!CryptographicOperations.FixedTimeEquals(presented, stored))
        {
            // Counted and saved, so five wrong guesses burn the code — a million possibilities is
            // brute-forceable inside fifteen minutes without a ceiling.
            latest.RecordFailedAttempt();
            await _db.SaveChangesAsync(cancellationToken);
            return VerifyEmailOutcome.Rejected;
        }

        latest.MarkConsumed(now);
        user.MarkEmailVerified(now);
        await _db.SaveChangesAsync(cancellationToken);

        return VerifyEmailOutcome.Verified;
    }
}
