using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TripsAgent.Application.Notifications;
using TripsAgent.Application.Persistence;
using TripsAgent.Domain.Identity;

namespace TripsAgent.Application.Identity.Registration;

/// <summary>
/// Issues email verification codes, throttled per address, and sends them.
/// </summary>
/// <remarks>
/// <para>
/// Shared by registration and "resend code", so both obey one throttle rather than two that
/// drift apart.
/// </para>
/// <para>
/// The throttle is per email address and is enforced by counting rows in <c>otp_codes</c>, which
/// means it holds across every API instance without needing Redis. It is not the general request
/// rate limiter — that is per IP, user and agency, returns 429s, and arrives with the security
/// epic (S1). This rule is narrower and exists so one address cannot be mail-bombed with codes.
/// </para>
/// </remarks>
public sealed partial class VerificationCodeIssuer
{
    /// <summary>Codes one address may be sent inside <see cref="ThrottleWindow"/>.</summary>
    public const int MaxCodesPerWindow = 3;

    public static readonly TimeSpan ThrottleWindow = TimeSpan.FromMinutes(15);

    private const int CodeDigits = 6;

    private readonly IAppDbContext _db;
    private readonly ITokenHasher _tokenHasher;
    private readonly IEmailSender _emailSender;
    private readonly ILogger<VerificationCodeIssuer> _logger;

    public VerificationCodeIssuer(
        IAppDbContext db,
        ITokenHasher tokenHasher,
        IEmailSender emailSender,
        ILogger<VerificationCodeIssuer> logger)
    {
        _db = db;
        _tokenHasher = tokenHasher;
        _emailSender = emailSender;
        _logger = logger;
    }

    /// <summary>
    /// Adds a new code for <paramref name="user"/> to the context, unless the address is throttled.
    /// </summary>
    /// <returns>The plaintext code to email, or null when throttled. Not yet saved.</returns>
    public async Task<string?> PrepareAsync(User user, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);

        var windowStart = now - ThrottleWindow;

        var recent = await _db.OtpCodes.CountAsync(
            code => code.Destination == user.Email
                    && code.Purpose == OtpPurpose.EmailVerification
                    && code.CreatedAt > windowStart,
            cancellationToken);

        if (recent >= MaxCodesPerWindow)
        {
            LogThrottled(_logger, user.Id);
            return null;
        }

        var plaintext = _tokenHasher.GenerateNumericCode(CodeDigits);

        _db.OtpCodes.Add(OtpCode.Issue(
            user.Id,
            OtpChannel.Email,
            OtpPurpose.EmailVerification,
            user.Email,
            _tokenHasher.Hash(plaintext),
            now));

        return plaintext;
    }

    /// <summary>
    /// Emails a code. Called only after the code has been saved, so a code that arrives in an
    /// inbox always exists in the database.
    /// </summary>
    /// <remarks>
    /// A delivery failure is logged, not thrown. The account and the code are already committed,
    /// and the caller is about to return the same generic response either way — surfacing an SMTP
    /// error here would tell an attacker which addresses reached the send step. The user can ask
    /// for a new code.
    /// </remarks>
    public async Task SendAsync(User user, string code, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);

        var message = VerificationEmail.Create(user.Email, user.FirstName, code, OtpCode.Lifetime);

        try
        {
            await _emailSender.SendAsync(message, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogSendFailed(_logger, ex, user.Id);
        }
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Verification code not sent to user {UserId}: the per-address limit for this window is reached.")]
    private static partial void LogThrottled(ILogger logger, Guid userId);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "Verification email to user {UserId} could not be delivered. The code is saved; the user can request another.")]
    private static partial void LogSendFailed(ILogger logger, Exception exception, Guid userId);
}
