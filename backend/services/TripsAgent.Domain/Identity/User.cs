using TripsAgent.Domain.Common;

namespace TripsAgent.Domain.Identity;

/// <summary>Where a user account is in its lifecycle.</summary>
public enum UserStatus
{
    /// <summary>Invited but has not accepted yet. Cannot sign in.</summary>
    Invited = 1,

    /// <summary>Normal, working account.</summary>
    Active = 2,

    /// <summary>Temporarily blocked by an administrator. Retains all their data.</summary>
    Suspended = 3,

    /// <summary>Switched off for good. Kept so their past actions still have an actor.</summary>
    Deactivated = 4,
}

/// <summary>
/// A person who can sign in — either staff at a travel agency, or Trips back-office staff.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="AgencyId"/> is nullable, and that nullability is the whole distinction: a user with
/// an agency works for that travel business, and a user without one is Trips platform staff. It
/// is why <see cref="User"/> does not implement <c>ITenantScoped</c>, which requires a
/// non-nullable agency — the query filter for users is written out explicitly instead.
/// </para>
/// <para>
/// The email column is <c>citext</c>, so <c>Ada@example.com</c> and <c>ada@example.com</c> are
/// the same account and cannot both be registered. Comparing case-insensitively in C# would not
/// help: the uniqueness has to be enforced by the index, or two concurrent signups race past it.
/// </para>
/// </remarks>
public sealed class User : Entity, IAuditableEntity
{
    /// <summary>Failed sign-ins before the account locks (FRD §2.1).</summary>
    public const int MaxFailedLoginAttempts = 5;

    /// <summary>How long the account stays locked once it does.</summary>
    public static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);

    private User()
    {
        Email = string.Empty;
        PasswordHash = string.Empty;
        FirstName = string.Empty;
        LastName = string.Empty;
    }

    /// <summary>
    /// Creates a user belonging to a travel agency.
    /// </summary>
    /// <param name="passwordHash">
    /// An already-hashed password. This type never sees a plaintext one — hashing is the caller's
    /// job precisely so there is no code path where a plaintext password could be stored or logged
    /// by accident.
    /// </param>
    public static User ForAgency(
        Guid agencyId,
        string email,
        string passwordHash,
        string firstName,
        string lastName,
        UserStatus status = UserStatus.Active)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(agencyId, Guid.Empty);

        return new User
        {
            AgencyId = agencyId,
            Email = NormaliseEmail(email),
            PasswordHash = Require(passwordHash, nameof(passwordHash)),
            FirstName = Require(firstName, nameof(firstName)),
            LastName = Require(lastName, nameof(lastName)),
            Status = status,
        };
    }

    /// <summary>Creates a Trips back-office user, who belongs to no agency.</summary>
    public static User ForPlatform(
        string email,
        string passwordHash,
        string firstName,
        string lastName,
        UserStatus status = UserStatus.Active) =>
        new()
        {
            AgencyId = null,
            Email = NormaliseEmail(email),
            PasswordHash = Require(passwordHash, nameof(passwordHash)),
            FirstName = Require(firstName, nameof(firstName)),
            LastName = Require(lastName, nameof(lastName)),
            Status = status,
        };

    /// <summary>The agency this user works for. Null for Trips platform staff.</summary>
    public Guid? AgencyId { get; private set; }

    /// <summary>Stored in a <c>citext</c> column with a unique index.</summary>
    public string Email { get; private set; }

    /// <summary>
    /// The Argon2id hash. Never a plaintext password, and never logged — the column is named
    /// with a <c>_hash</c> suffix so that is obvious in psql too.
    /// </summary>
    public string PasswordHash { get; private set; }

    public string FirstName { get; private set; }

    public string LastName { get; private set; }

    public string? PhoneNumber { get; private set; }

    public UserStatus Status { get; private set; }

    /// <summary>Set when the user confirms their email. Null means unverified.</summary>
    public DateTimeOffset? EmailVerifiedAt { get; private set; }

    /// <summary>Consecutive failed sign-ins. Reset to zero by a successful one.</summary>
    public int FailedLoginCount { get; private set; }

    /// <summary>When the lockout expires. Null when the account is not locked.</summary>
    public DateTimeOffset? LockedUntil { get; private set; }

    public DateTimeOffset? LastLoginAt { get; private set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>True for Trips back-office staff.</summary>
    public bool IsPlatformStaff => AgencyId is null;

    /// <summary>True once the user has confirmed their email address.</summary>
    public bool IsEmailVerified => EmailVerifiedAt.HasValue;

    /// <summary>True while the account is locked out at <paramref name="now"/>.</summary>
    public bool IsLockedOut(DateTimeOffset now) => LockedUntil.HasValue && LockedUntil.Value > now;

    /// <summary>Records the user confirming their email address. Idempotent.</summary>
    public void MarkEmailVerified(DateTimeOffset at)
    {
        EmailVerifiedAt ??= at;

        // An invited user becomes a working one the moment they prove the address is theirs.
        if (Status == UserStatus.Invited)
        {
            Status = UserStatus.Active;
        }
    }

    /// <summary>
    /// Records a failed sign-in, locking the account once the limit is reached.
    /// </summary>
    /// <remarks>
    /// Counting here rather than in a handler means every sign-in path shares one rule. The
    /// count is consecutive: a success clears it, so an occasional typo never accumulates into
    /// a lockout weeks later.
    /// </remarks>
    public void RecordFailedLogin(DateTimeOffset now)
    {
        FailedLoginCount++;

        if (FailedLoginCount >= MaxFailedLoginAttempts)
        {
            LockedUntil = now.Add(LockoutDuration);
        }
    }

    /// <summary>Records a successful sign-in and clears any lockout state.</summary>
    public void RecordSuccessfulLogin(DateTimeOffset now)
    {
        FailedLoginCount = 0;
        LockedUntil = null;
        LastLoginAt = now;
    }

    /// <summary>Lifts a lockout without waiting it out — an administrator unlocking an account.</summary>
    public void ClearLockout()
    {
        FailedLoginCount = 0;
        LockedUntil = null;
    }

    /// <summary>Replaces the stored hash. Takes a hash, never a password.</summary>
    public void SetPasswordHash(string passwordHash) =>
        PasswordHash = Require(passwordHash, nameof(passwordHash));

    public void SetPhoneNumber(string? phoneNumber) =>
        PhoneNumber = string.IsNullOrWhiteSpace(phoneNumber) ? null : phoneNumber.Trim();

    public void Suspend() => Status = UserStatus.Suspended;

    public void Reactivate() => Status = UserStatus.Active;

    public void Deactivate() => Status = UserStatus.Deactivated;

    private static string NormaliseEmail(string email)
    {
        var normalised = Require(email, nameof(email)).ToLowerInvariant();

        // A deliberately shallow check. Full RFC 5322 validation rejects addresses that work and
        // accepts ones that do not; the address is proved by sending a message to it, which is
        // what the OTP flow does.
        return normalised.Contains('@', StringComparison.Ordinal) && normalised.Length >= 3
            ? normalised
            : throw new ArgumentException($"'{email}' is not an email address.", nameof(email));
    }

    private static string Require(string value, string parameterName) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("Value is required.", parameterName)
            : value.Trim();
}
