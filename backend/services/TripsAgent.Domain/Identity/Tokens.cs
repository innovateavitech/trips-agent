using TripsAgent.Domain.Common;

namespace TripsAgent.Domain.Identity;

/// <summary>
/// A long-lived token that buys a new access token. Single-use, and rotated on every use.
/// </summary>
/// <remarks>
/// <para>
/// Only the hash is stored. A refresh token is a bearer credential: anyone holding the plaintext
/// can impersonate the user for thirty days, so a leaked database backup must not hand them out.
/// The column is named <c>token_hash</c> so that is visible in psql as well as in the C#.
/// </para>
/// <para>
/// <see cref="ReplacedById"/> is what makes reuse detectable. Using a token mints a replacement
/// and points the old row at it; presenting an already-replaced token means someone kept a copy,
/// and the whole chain is revoked. That logic lands with #16 — this type just records the shape.
/// </para>
/// </remarks>
public sealed class RefreshToken : Entity, IAuditableEntity
{
    private RefreshToken() => TokenHash = string.Empty;

    public static RefreshToken Issue(Guid userId, string tokenHash, DateTimeOffset expiresAt, string? createdByIp = null) =>
        new()
        {
            UserId = userId,
            TokenHash = RequireHash(tokenHash),
            ExpiresAt = expiresAt,
            CreatedByIp = createdByIp,
        };

    public Guid UserId { get; private set; }

    /// <summary>A hash of the token. Never the token itself.</summary>
    public string TokenHash { get; private set; }

    public DateTimeOffset ExpiresAt { get; private set; }

    public DateTimeOffset? RevokedAt { get; private set; }

    /// <summary>The token issued when this one was used. Null until it is used.</summary>
    public Guid? ReplacedById { get; private set; }

    /// <summary>Where it was issued from, for the security log.</summary>
    public string? CreatedByIp { get; private set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>True when this token has already been exchanged for another.</summary>
    public bool IsUsed => ReplacedById.HasValue;

    /// <summary>True when it may still be exchanged at <paramref name="now"/>.</summary>
    public bool IsActive(DateTimeOffset now) =>
        RevokedAt is null && !IsUsed && ExpiresAt > now;

    /// <summary>Marks this token as exchanged for <paramref name="replacementId"/>.</summary>
    public void MarkReplacedBy(Guid replacementId, DateTimeOffset at)
    {
        ReplacedById = replacementId;
        RevokedAt ??= at;
    }

    /// <summary>Revokes the token — a sign-out, or reuse detected somewhere in the chain.</summary>
    public void Revoke(DateTimeOffset at) => RevokedAt ??= at;

    internal static string RequireHash(string hash) =>
        string.IsNullOrWhiteSpace(hash)
            ? throw new ArgumentException("A token hash is required.", nameof(hash))
            : hash;
}

/// <summary>
/// A single-use link that lets someone set a new password. Thirty minutes, per FRD §2.1 UC-1B RS-7.
/// </summary>
public sealed class PasswordResetToken : Entity, IAuditableEntity
{
    /// <summary>How long a reset link stays valid.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);

    private PasswordResetToken() => TokenHash = string.Empty;

    public static PasswordResetToken Issue(Guid userId, string tokenHash, DateTimeOffset now) =>
        new()
        {
            UserId = userId,
            TokenHash = RefreshToken.RequireHash(tokenHash),
            ExpiresAt = now.Add(Lifetime),
        };

    public Guid UserId { get; private set; }

    /// <summary>A hash of the token. Never the token itself.</summary>
    public string TokenHash { get; private set; }

    public DateTimeOffset ExpiresAt { get; private set; }

    /// <summary>Set the moment it is redeemed, so it cannot be redeemed twice.</summary>
    public DateTimeOffset? UsedAt { get; private set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public bool IsUsable(DateTimeOffset now) => UsedAt is null && ExpiresAt > now;

    public void MarkUsed(DateTimeOffset at) => UsedAt ??= at;
}

/// <summary>How a one-time code reaches its recipient.</summary>
public enum OtpChannel
{
    Email = 1,
    Sms = 2,
}

/// <summary>What a one-time code is for.</summary>
public enum OtpPurpose
{
    /// <summary>Proving a new account's email address really belongs to the person signing up.</summary>
    EmailVerification = 1,

    /// <summary>Proving a phone number.</summary>
    PhoneVerification = 2,

    /// <summary>A second factor at sign-in.</summary>
    TwoFactor = 3,
}

/// <summary>
/// A short-lived numeric code sent to an email address or phone number.
/// </summary>
/// <remarks>
/// <para>
/// Hashed like every other credential: a six-digit code is guessable enough on its own without
/// also being readable in a database dump.
/// </para>
/// <para>
/// <see cref="AttemptCount"/> is what stops someone trying all million codes. The verification
/// flow in #14 enforces the ceiling; this type records the count.
/// </para>
/// </remarks>
public sealed class OtpCode : Entity, IAuditableEntity
{
    /// <summary>How long a code stays valid (FRD §2.2).</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);

    /// <summary>Wrong guesses allowed before the code is burned.</summary>
    public const int MaxAttempts = 5;

    private OtpCode()
    {
        CodeHash = string.Empty;
        Destination = string.Empty;
    }

    public static OtpCode Issue(
        Guid? userId,
        OtpChannel channel,
        OtpPurpose purpose,
        string destination,
        string codeHash,
        DateTimeOffset now) =>
        new()
        {
            UserId = userId,
            Channel = channel,
            Purpose = purpose,
            Destination = destination,
            CodeHash = RefreshToken.RequireHash(codeHash),
            ExpiresAt = now.Add(Lifetime),
        };

    /// <summary>Null when the code was sent before an account existed.</summary>
    public Guid? UserId { get; private set; }

    public OtpChannel Channel { get; private set; }

    public OtpPurpose Purpose { get; private set; }

    /// <summary>The address or number the code went to.</summary>
    public string Destination { get; private set; }

    /// <summary>A hash of the code. Never the code itself.</summary>
    public string CodeHash { get; private set; }

    public DateTimeOffset ExpiresAt { get; private set; }

    public int AttemptCount { get; private set; }

    /// <summary>Set when the code is accepted, so it is single-use.</summary>
    public DateTimeOffset? ConsumedAt { get; private set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>True when it can still be presented: unused, unexpired and not out of attempts.</summary>
    public bool IsUsable(DateTimeOffset now) =>
        ConsumedAt is null && AttemptCount < MaxAttempts && ExpiresAt > now;

    public void RecordFailedAttempt() => AttemptCount++;

    public void MarkConsumed(DateTimeOffset at) => ConsumedAt ??= at;
}

/// <summary>
/// One sign-in attempt, successful or not.
/// </summary>
/// <remarks>
/// <para>
/// The audit trail behind the lockout rule, and the first thing anyone looks at when an account
/// is compromised. It records the <b>normalised email as typed</b>, not a user id, because the
/// interesting attempts are the ones against addresses that do not exist.
/// </para>
/// <para>
/// Indexed on <c>(email_normalized, attempted_at DESC)</c> — the lockout check reads the most
/// recent attempts for one address, and that index answers it without touching the table.
/// </para>
/// </remarks>
public sealed class LoginAttempt : Entity
{
    private LoginAttempt()
    {
        EmailNormalized = string.Empty;
    }

    public static LoginAttempt Record(
        string email,
        bool succeeded,
        DateTimeOffset attemptedAt,
        string? ipAddress = null,
        string? userAgent = null,
        Guid? userId = null) =>
        new()
        {
            EmailNormalized = (email ?? string.Empty).Trim().ToLowerInvariant(),
            Succeeded = succeeded,
            AttemptedAt = attemptedAt,
            IpAddress = ipAddress,
            UserAgent = userAgent,
            UserId = userId,
        };

    public string EmailNormalized { get; private set; }

    /// <summary>Null when the address matched no account — which is itself worth recording.</summary>
    public Guid? UserId { get; private set; }

    public bool Succeeded { get; private set; }

    public DateTimeOffset AttemptedAt { get; private set; }

    public string? IpAddress { get; private set; }

    public string? UserAgent { get; private set; }
}

/// <summary>
/// An outstanding invitation to join an agency, or to join Trips staff.
/// </summary>
/// <remarks>
/// Serves both sub-agent invites and internal Trips users, which is why
/// <see cref="AgencyId"/> is nullable — a null agency is a back-office invitation.
/// </remarks>
public sealed class UserInvitation : Entity, IAuditableEntity
{
    /// <summary>How long an invitation stays open.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(7);

    private UserInvitation()
    {
        Email = string.Empty;
        TokenHash = string.Empty;
    }

    public static UserInvitation Create(
        Guid? agencyId,
        string email,
        Guid roleId,
        string tokenHash,
        Guid invitedByUserId,
        DateTimeOffset now) =>
        new()
        {
            AgencyId = agencyId,
            Email = (email ?? string.Empty).Trim().ToLowerInvariant(),
            RoleId = roleId,
            TokenHash = RefreshToken.RequireHash(tokenHash),
            InvitedByUserId = invitedByUserId,
            ExpiresAt = now.Add(Lifetime),
        };

    /// <summary>Null for an invitation to the Trips back office.</summary>
    public Guid? AgencyId { get; private set; }

    public string Email { get; private set; }

    /// <summary>The role the invitee gets when they accept.</summary>
    public Guid RoleId { get; private set; }

    /// <summary>A hash of the invitation token. Never the token itself.</summary>
    public string TokenHash { get; private set; }

    public Guid InvitedByUserId { get; private set; }

    public DateTimeOffset ExpiresAt { get; private set; }

    public DateTimeOffset? AcceptedAt { get; private set; }

    public DateTimeOffset? RevokedAt { get; private set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public bool IsOpen(DateTimeOffset now) =>
        AcceptedAt is null && RevokedAt is null && ExpiresAt > now;

    public void MarkAccepted(DateTimeOffset at) => AcceptedAt ??= at;

    public void Revoke(DateTimeOffset at) => RevokedAt ??= at;
}
