using FluentAssertions;
using TripsAgent.Domain.Identity;

namespace TripsAgent.UnitTests.Identity;

public class UserTests
{
    private static readonly DateTimeOffset Now = new(2026, 3, 1, 9, 0, 0, TimeSpan.Zero);

    private static User NewAgencyUser() =>
        User.ForAgency(Guid.CreateVersion7(), "Ada@Example.com", "argon2id$hash", "Ada", "Okonkwo");

    [Fact]
    public void Email_is_stored_lower_case()
    {
        // The citext column makes lookups case-insensitive regardless, but storing one canonical
        // form means what the user sees back is predictable.
        NewAgencyUser().Email.Should().Be("ada@example.com");
    }

    [Theory]
    [InlineData("not-an-email")]
    [InlineData("")]
    [InlineData("   ")]
    public void An_address_without_an_at_sign_is_rejected(string email)
    {
        var act = () => User.ForAgency(Guid.CreateVersion7(), email, "hash", "Ada", "O");

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void A_user_with_no_agency_is_platform_staff()
    {
        User.ForPlatform("admin@example.com", "hash", "Ada", "O").IsPlatformStaff.Should().BeTrue();
        NewAgencyUser().IsPlatformStaff.Should().BeFalse();
    }

    [Fact]
    public void An_agency_user_must_have_a_real_agency()
    {
        var act = () => User.ForAgency(Guid.Empty, "ada@example.com", "hash", "Ada", "O");

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void A_new_user_is_unverified()
    {
        NewAgencyUser().IsEmailVerified.Should().BeFalse();
    }

    [Fact]
    public void Verifying_an_invited_user_activates_them()
    {
        var user = User.ForAgency(Guid.CreateVersion7(), "ada@example.com", "hash", "Ada", "O", UserStatus.Invited);

        user.MarkEmailVerified(Now);

        user.Status.Should().Be(UserStatus.Active);
        user.EmailVerifiedAt.Should().Be(Now);
    }

    [Fact]
    public void Verifying_twice_keeps_the_first_timestamp()
    {
        var user = NewAgencyUser();

        user.MarkEmailVerified(Now);
        user.MarkEmailVerified(Now.AddDays(1));

        user.EmailVerifiedAt.Should().Be(Now);
    }

    // ------------------------------------------------------------------------------ lockout

    [Fact]
    public void Four_failures_do_not_lock_the_account()
    {
        var user = NewAgencyUser();

        for (var i = 0; i < User.MaxFailedLoginAttempts - 1; i++)
        {
            user.RecordFailedLogin(Now);
        }

        user.IsLockedOut(Now).Should().BeFalse();
        user.FailedLoginCount.Should().Be(4);
    }

    [Fact]
    public void The_fifth_failure_locks_the_account_for_fifteen_minutes()
    {
        var user = NewAgencyUser();

        for (var i = 0; i < User.MaxFailedLoginAttempts; i++)
        {
            user.RecordFailedLogin(Now);
        }

        // FRD §2.1: lock after 5 failures for 15 minutes.
        user.IsLockedOut(Now).Should().BeTrue();
        user.LockedUntil.Should().Be(Now.AddMinutes(15));
    }

    [Fact]
    public void A_lockout_expires_on_its_own()
    {
        var user = NewAgencyUser();

        for (var i = 0; i < User.MaxFailedLoginAttempts; i++)
        {
            user.RecordFailedLogin(Now);
        }

        user.IsLockedOut(Now.AddMinutes(14)).Should().BeTrue();
        user.IsLockedOut(Now.AddMinutes(15)).Should().BeFalse("the lock runs until, not through, LockedUntil");
    }

    [Fact]
    public void A_successful_login_clears_the_failure_count()
    {
        var user = NewAgencyUser();

        user.RecordFailedLogin(Now);
        user.RecordFailedLogin(Now);
        user.RecordSuccessfulLogin(Now);

        // Consecutive failures, not lifetime ones: an occasional typo must never accumulate into
        // a lockout weeks later.
        user.FailedLoginCount.Should().Be(0);
        user.LastLoginAt.Should().Be(Now);
    }

    [Fact]
    public void An_administrator_can_lift_a_lockout_early()
    {
        var user = NewAgencyUser();

        for (var i = 0; i < User.MaxFailedLoginAttempts; i++)
        {
            user.RecordFailedLogin(Now);
        }

        user.ClearLockout();

        user.IsLockedOut(Now).Should().BeFalse();
        user.FailedLoginCount.Should().Be(0);
    }

    [Fact]
    public void A_password_hash_is_required()
    {
        var act = () => User.ForAgency(Guid.CreateVersion7(), "ada@example.com", "  ", "Ada", "O");

        act.Should().Throw<ArgumentException>();
    }
}

public class CredentialTokenTests
{
    private static readonly DateTimeOffset Now = new(2026, 3, 1, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_refresh_token_is_active_until_it_expires()
    {
        var token = RefreshToken.Issue(Guid.CreateVersion7(), "hash", Now.AddDays(30));

        token.IsActive(Now).Should().BeTrue();
        token.IsActive(Now.AddDays(30)).Should().BeFalse();
    }

    [Fact]
    public void A_replaced_refresh_token_is_no_longer_active()
    {
        var token = RefreshToken.Issue(Guid.CreateVersion7(), "hash", Now.AddDays(30));

        token.MarkReplacedBy(Guid.CreateVersion7(), Now);

        // Single-use. Presenting this again is the signal that somebody kept a copy.
        token.IsUsed.Should().BeTrue();
        token.IsActive(Now).Should().BeFalse();
    }

    [Fact]
    public void A_revoked_refresh_token_is_no_longer_active()
    {
        var token = RefreshToken.Issue(Guid.CreateVersion7(), "hash", Now.AddDays(30));

        token.Revoke(Now);

        token.IsActive(Now).Should().BeFalse();
    }

    [Fact]
    public void A_token_hash_is_required()
    {
        var act = () => RefreshToken.Issue(Guid.CreateVersion7(), "", Now.AddDays(30));

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void A_password_reset_link_lasts_thirty_minutes()
    {
        var token = PasswordResetToken.Issue(Guid.CreateVersion7(), "hash", Now);

        // FRD §2.1 UC-1B RS-7.
        token.ExpiresAt.Should().Be(Now.AddMinutes(30));
        token.IsUsable(Now.AddMinutes(29)).Should().BeTrue();
        token.IsUsable(Now.AddMinutes(30)).Should().BeFalse();
    }

    [Fact]
    public void A_password_reset_link_works_once()
    {
        var token = PasswordResetToken.Issue(Guid.CreateVersion7(), "hash", Now);

        token.MarkUsed(Now);

        token.IsUsable(Now).Should().BeFalse();
    }

    [Fact]
    public void An_otp_expires_after_fifteen_minutes()
    {
        var code = OtpCode.Issue(null, OtpChannel.Email, OtpPurpose.EmailVerification, "ada@example.com", "hash", Now);

        code.IsUsable(Now.AddMinutes(14)).Should().BeTrue();
        code.IsUsable(Now.AddMinutes(15)).Should().BeFalse();
    }

    [Fact]
    public void An_otp_is_burned_after_five_wrong_guesses()
    {
        var code = OtpCode.Issue(null, OtpChannel.Email, OtpPurpose.EmailVerification, "ada@example.com", "hash", Now);

        for (var i = 0; i < OtpCode.MaxAttempts; i++)
        {
            code.RecordFailedAttempt();
        }

        // A six-digit code has a million possibilities. Without a ceiling on attempts it can be
        // brute-forced inside its fifteen-minute window.
        code.IsUsable(Now).Should().BeFalse();
    }

    [Fact]
    public void An_otp_is_single_use()
    {
        var code = OtpCode.Issue(null, OtpChannel.Email, OtpPurpose.EmailVerification, "ada@example.com", "hash", Now);

        code.MarkConsumed(Now);

        code.IsUsable(Now).Should().BeFalse();
    }

    [Fact]
    public void A_login_attempt_records_the_email_normalised()
    {
        var attempt = LoginAttempt.Record("  Ada@Example.COM ", succeeded: false, Now);

        attempt.EmailNormalized.Should().Be("ada@example.com");
    }
}
