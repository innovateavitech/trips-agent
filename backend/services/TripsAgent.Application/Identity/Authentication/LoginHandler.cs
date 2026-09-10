using Microsoft.EntityFrameworkCore;
using TripsAgent.Application.Persistence;
using TripsAgent.Application.Tenancy;
using TripsAgent.Contracts.Identity;
using TripsAgent.Domain.Identity;

namespace TripsAgent.Application.Identity.Authentication;

/// <summary>What happened to a sign-in attempt.</summary>
public abstract record LoginOutcome
{
    private LoginOutcome()
    {
    }

    public sealed record Succeeded(IssuedTokenPair Tokens) : LoginOutcome;

    /// <summary>
    /// Wrong password, unknown address, or a locked account. One outcome for all three: which one
    /// it was is exactly what an attacker wants to know.
    /// </summary>
    public sealed record Failed : LoginOutcome;

    /// <summary>
    /// Correct credentials, but the address was never confirmed. Safe to say so — they proved
    /// they own the account, so this reveals nothing they did not already know.
    /// </summary>
    public sealed record EmailNotVerified : LoginOutcome;

    /// <summary>Correct credentials, but the account is suspended or closed.</summary>
    public sealed record AccountUnavailable : LoginOutcome;
}

/// <summary>Signs a user in and issues their first token pair.</summary>
public sealed class LoginHandler
{
    private readonly IAppDbContext _db;
    private readonly IPasswordHasher _passwordHasher;
    private readonly IPlatformScope _platformScope;
    private readonly TokenPairFactory _tokens;
    private readonly TimeProvider _clock;

    public LoginHandler(
        IAppDbContext db,
        IPasswordHasher passwordHasher,
        IPlatformScope platformScope,
        TokenPairFactory tokens,
        TimeProvider clock)
    {
        _db = db;
        _passwordHasher = passwordHasher;
        _platformScope = platformScope;
        _tokens = tokens;
        _clock = clock;
    }

    public async Task<LoginOutcome> HandleAsync(
        LoginRequest request,
        string? ipAddress = null,
        string? userAgent = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var email = (request.Email ?? string.Empty).Trim().ToLowerInvariant();
        var now = _clock.GetUtcNow();

        // Sign-in happens before there is a tenant — the token is what establishes one.
        using var scope = _platformScope.Enter(
            "sign-in — the account is identified by address before any tenant exists");

        var user = await _db.Users.FirstOrDefaultAsync(u => u.Email == email, cancellationToken);

        if (user is null)
        {
            // Hash anyway. Argon2id takes tens of milliseconds, and skipping it for unknown
            // addresses would make "no such account" measurably faster than "wrong password".
            _ = _passwordHasher.Hash(request.Password ?? string.Empty);

            await RecordAttemptAsync(email, succeeded: false, now, ipAddress, userAgent, null, cancellationToken);
            return new LoginOutcome.Failed();
        }

        if (user.IsLockedOut(now))
        {
            // Deliberately the same answer as a wrong password: saying "locked" confirms the
            // account exists, and tells an attacker their guessing is having an effect.
            await RecordAttemptAsync(email, succeeded: false, now, ipAddress, userAgent, user.Id, cancellationToken);
            return new LoginOutcome.Failed();
        }

        var (verified, needsRehash) = _passwordHasher.Verify(request.Password ?? string.Empty, user.PasswordHash);

        if (!verified)
        {
            user.RecordFailedLogin(now);
            await RecordAttemptAsync(email, succeeded: false, now, ipAddress, userAgent, user.Id, cancellationToken);
            return new LoginOutcome.Failed();
        }

        // Past this point the caller has proved the password, so specific answers leak nothing.
        if (!user.IsEmailVerified)
        {
            await RecordAttemptAsync(email, succeeded: false, now, ipAddress, userAgent, user.Id, cancellationToken);
            return new LoginOutcome.EmailNotVerified();
        }

        if (user.Status != UserStatus.Active)
        {
            await RecordAttemptAsync(email, succeeded: false, now, ipAddress, userAgent, user.Id, cancellationToken);
            return new LoginOutcome.AccountUnavailable();
        }

        if (needsRehash)
        {
            // The only moment the plaintext exists, so the only chance to upgrade a hash made
            // with weaker parameters without asking anyone to change their password.
            user.SetPasswordHash(_passwordHasher.Hash(request.Password!));
        }

        user.RecordSuccessfulLogin(now);

        var pair = await _tokens.CreateAsync(user, now, ipAddress, cancellationToken);

        await RecordAttemptAsync(email, succeeded: true, now, ipAddress, userAgent, user.Id, cancellationToken);

        return new LoginOutcome.Succeeded(pair);
    }

    /// <summary>
    /// Records the attempt and commits. Every path writes one — the failures are the interesting
    /// ones, and an attempt against an address that matches no account is the most interesting.
    /// </summary>
    private async Task RecordAttemptAsync(
        string email,
        bool succeeded,
        DateTimeOffset now,
        string? ipAddress,
        string? userAgent,
        Guid? userId,
        CancellationToken cancellationToken)
    {
        _db.LoginAttempts.Add(LoginAttempt.Record(email, succeeded, now, ipAddress, userAgent, userId));
        await _db.SaveChangesAsync(cancellationToken);
    }
}
