using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TripsAgent.Application.Notifications;
using TripsAgent.Application.Persistence;
using TripsAgent.Application.Tenancy;
using TripsAgent.Contracts.Identity;
using TripsAgent.Domain.Identity;

namespace TripsAgent.Application.Identity.Registration;

/// <summary>What happened to a reset attempt.</summary>
public abstract record ResetPasswordOutcome
{
    private ResetPasswordOutcome()
    {
    }

    public sealed record Reset : ResetPasswordOutcome;

    /// <summary>
    /// Unknown, expired or already used. One outcome for all three, and the message points at
    /// requesting a new link — which is the only useful thing to do in any of those cases.
    /// </summary>
    public sealed record LinkNotUsable : ResetPasswordOutcome;

    /// <summary>The new password does not meet the policy.</summary>
    public sealed record WeakPassword(IReadOnlyList<string> Problems) : ResetPasswordOutcome;
}

/// <summary>
/// Sends a password reset link. Always reports success.
/// </summary>
/// <remarks>
/// <para>
/// The response is identical whether or not the address has an account, for the same reason
/// registration's is: a forgot-password form that answers honestly is a way to enumerate
/// customers, one guess at a time. FRD §2.1 UC-1B words the confirmation to be true either way.
/// </para>
/// <para>
/// Limited to a few links per address per window, counted in the table so the limit holds across
/// API instances. That is the per-address half of the issue's rate-limiting requirement; the
/// per-IP half is HTTP-layer work and belongs to the security epic (S1), which names
/// forgot-password explicitly.
/// </para>
/// </remarks>
public sealed partial class ForgotPasswordHandler
{
    /// <summary>Links one address may be sent inside <see cref="ThrottleWindow"/>.</summary>
    public const int MaxLinksPerWindow = 3;

    public static readonly TimeSpan ThrottleWindow = TimeSpan.FromMinutes(15);

    private readonly IAppDbContext _db;
    private readonly ITokenHasher _tokenHasher;
    private readonly IEmailSender _email;
    private readonly IPlatformScope _platformScope;
    private readonly TimeProvider _clock;
    private readonly PasswordResetLinkBuilder _links;
    private readonly ILogger<ForgotPasswordHandler> _logger;

    public ForgotPasswordHandler(
        IAppDbContext db,
        ITokenHasher tokenHasher,
        IEmailSender email,
        IPlatformScope platformScope,
        TimeProvider clock,
        PasswordResetLinkBuilder links,
        ILogger<ForgotPasswordHandler> logger)
    {
        _db = db;
        _tokenHasher = tokenHasher;
        _email = email;
        _platformScope = platformScope;
        _clock = clock;
        _links = links;
        _logger = logger;
    }

    public async Task HandleAsync(ForgotPasswordRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Email))
        {
            return;
        }

        var email = request.Email.Trim().ToLowerInvariant();
        var now = _clock.GetUtcNow();

        using var scope = _platformScope.Enter(
            "password reset — the account is identified by address before any tenant is known");

        var user = await _db.Users.FirstOrDefaultAsync(u => u.Email == email, cancellationToken);

        if (user is null)
        {
            return;
        }

        var windowStart = now - ThrottleWindow;

        var recent = await _db.PasswordResetTokens
            .CountAsync(t => t.UserId == user.Id && t.CreatedAt > windowStart, cancellationToken);

        if (recent >= MaxLinksPerWindow)
        {
            LogThrottled(_logger, user.Id);
            return;
        }

        // Opaque and random — 256 bits. Only its hash is stored, so a leaked database cannot be
        // used to reset anybody's password.
        var plaintext = _tokenHasher.GenerateOpaqueToken();

        _db.PasswordResetTokens.Add(PasswordResetToken.Issue(user.Id, _tokenHasher.Hash(plaintext), now));

        await _db.SaveChangesAsync(cancellationToken);

        try
        {
            await _email.SendAsync(
                PasswordResetEmail.Create(
                    user.Email, user.FirstName, _links.Build(plaintext), PasswordResetToken.Lifetime),
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The caller sees the same response either way, and surfacing this would say which
            // addresses reached the send step.
            LogSendFailed(_logger, ex, user.Id);
        }
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Password reset link not sent to user {UserId}: the per-address limit for this window is reached.")]
    private static partial void LogThrottled(ILogger logger, Guid userId);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "Password reset email to user {UserId} could not be delivered. They can ask for another.")]
    private static partial void LogSendFailed(ILogger logger, Exception exception, Guid userId);
}

/// <summary>Builds the link that goes in the email.</summary>
/// <remarks>
/// The console owns the page that receives the token, so the base URL is configuration rather
/// than something this assembly can know.
/// </remarks>
public sealed class PasswordResetLinkBuilder
{
    private readonly string _baseUrl;

    public PasswordResetLinkBuilder(string baseUrl) =>
        _baseUrl = string.IsNullOrWhiteSpace(baseUrl) ? "https://localhost:5173/reset-password" : baseUrl.TrimEnd('/');

    public string Build(string token) => $"{_baseUrl}?token={Uri.EscapeDataString(token)}";
}

/// <summary>
/// Redeems a reset link and sets a new password.
/// </summary>
/// <remarks>
/// A successful reset revokes every refresh token the user holds. Somebody resetting a password
/// may be doing it because they think they were compromised, and leaving live sessions running
/// would defeat the point — whoever else was signed in stays signed in.
/// </remarks>
public sealed class ResetPasswordHandler
{
    private readonly IAppDbContext _db;
    private readonly ITokenHasher _tokenHasher;
    private readonly IPasswordHasher _passwordHasher;
    private readonly IPlatformScope _platformScope;
    private readonly TimeProvider _clock;

    public ResetPasswordHandler(
        IAppDbContext db,
        ITokenHasher tokenHasher,
        IPasswordHasher passwordHasher,
        IPlatformScope platformScope,
        TimeProvider clock)
    {
        _db = db;
        _tokenHasher = tokenHasher;
        _passwordHasher = passwordHasher;
        _platformScope = platformScope;
        _clock = clock;
    }

    public async Task<ResetPasswordOutcome> HandleAsync(
        ResetPasswordRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Token))
        {
            return new ResetPasswordOutcome.LinkNotUsable();
        }

        // Checked before the token is looked up, so a weak password is reported without spending
        // the link — otherwise someone who mistypes a short password has to request a new email.
        var problems = PasswordPolicy.Validate(request.NewPassword);

        if (problems.Count > 0)
        {
            return new ResetPasswordOutcome.WeakPassword(problems);
        }

        var now = _clock.GetUtcNow();
        var hash = _tokenHasher.Hash(request.Token);

        using var scope = _platformScope.Enter(
            "password reset — the token identifies its user before any tenant exists");

        var token = await _db.PasswordResetTokens.FirstOrDefaultAsync(t => t.TokenHash == hash, cancellationToken);

        if (token is null || !token.IsUsable(now))
        {
            return new ResetPasswordOutcome.LinkNotUsable();
        }

        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == token.UserId, cancellationToken);

        if (user is null)
        {
            return new ResetPasswordOutcome.LinkNotUsable();
        }

        user.SetPasswordHash(_passwordHasher.Hash(request.NewPassword!));
        token.MarkUsed(now);

        // Every other outstanding link is burned too. Two links in an inbox after a suspected
        // compromise is one more than anybody needs.
        var otherLinks = await _db.PasswordResetTokens
            .Where(t => t.UserId == user.Id && t.UsedAt == null && t.Id != token.Id)
            .ToListAsync(cancellationToken);

        foreach (var other in otherLinks)
        {
            other.MarkUsed(now);
        }

        var sessions = await _db.RefreshTokens
            .Where(t => t.UserId == user.Id && t.RevokedAt == null)
            .ToListAsync(cancellationToken);

        foreach (var session in sessions)
        {
            session.Revoke(now);
        }

        // A reset also clears a lockout: someone who has just proved control of their inbox
        // should not still be locked out by the guessing that made them reset in the first place.
        user.ClearLockout();

        await _db.SaveChangesAsync(cancellationToken);

        return new ResetPasswordOutcome.Reset();
    }
}
