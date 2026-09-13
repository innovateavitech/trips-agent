using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TripsAgent.Application.Notifications;
using TripsAgent.Application.Persistence;
using TripsAgent.Application.Tenancy;
using TripsAgent.Domain.Identity;

namespace TripsAgent.Application.Identity.Registration;

/// <summary>
/// Issues email verification codes, throttled per address, and sends them.
/// </summary>
/// <remarks>
/// <para>
/// Shared by registration, "resend code" and accepting a sub-agent invitation (issue 170), so all
/// three obey one throttle rather than several that drift apart.
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
    private readonly IPlatformScope _platformScope;
    private readonly ILogger<VerificationCodeIssuer> _logger;

    public VerificationCodeIssuer(
        IAppDbContext db,
        ITokenHasher tokenHasher,
        IEmailSender emailSender,
        IPlatformScope platformScope,
        ILogger<VerificationCodeIssuer> logger)
    {
        _db = db;
        _tokenHasher = tokenHasher;
        _emailSender = emailSender;
        _platformScope = platformScope;
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

        try
        {
            var brand = await PrincipalBrandAsync(user, cancellationToken);
            var message = VerificationEmail.Create(user.Email, user.FirstName, code, OtpCode.Lifetime, brand);

            await _emailSender.SendAsync(message, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogSendFailed(_logger, ex, user.Id);
        }
    }

    /// <summary>
    /// The principal's brand when <paramref name="user"/> works for a sub-agent, and null — ours — otherwise.
    /// </summary>
    /// <remarks>
    /// Build-plan decision 6: a sub-agent works under its principal's name, and the invitation its first
    /// user joined through carried that name. The code that user is sent next (issue 170) arrives seconds
    /// later and must not be the first thing to name the platform. The principal is another agency's row,
    /// so it is read in the platform scope, whichever scope the caller was in.
    /// </remarks>
    private async Task<NotificationBrand?> PrincipalBrandAsync(User user, CancellationToken cancellationToken)
    {
        if (user.AgencyId is not { } agencyId)
        {
            return null;
        }

        using var scope = _platformScope.Enter(
            "verification email — a sub-agent's code carries its principal's brand, which is another agency's row");

        var principalId = await _db.Agencies.AsNoTracking()
            .Where(agency => agency.Id == agencyId)
            .Select(agency => agency.ParentAgencyId)
            .FirstOrDefaultAsync(cancellationToken);

        if (principalId is not { } id)
        {
            return null;
        }

        // A foreign key guarantees the row. Should it ever be unreadable, sending nothing is the safe
        // failure: the fallback, our own brand, is exactly what this method exists to avoid.
        var principal = await _db.Agencies.AsNoTracking()
            .FirstOrDefaultAsync(agency => agency.Id == id, cancellationToken)
            ?? throw new InvalidOperationException(
                $"Sub-agent {agencyId} names principal {id}, which cannot be read, so its brand is unknown.");

        var branding = await _db.AgencyBranding.AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.AgencyId == id, cancellationToken);

        return NotificationBrand.OfPrincipal(principal, branding);
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Verification code not sent to user {UserId}: the per-address limit for this window is reached.")]
    private static partial void LogThrottled(ILogger logger, Guid userId);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "Verification email to user {UserId} could not be delivered. The code is saved; the user can request another.")]
    private static partial void LogSendFailed(ILogger logger, Exception exception, Guid userId);
}
