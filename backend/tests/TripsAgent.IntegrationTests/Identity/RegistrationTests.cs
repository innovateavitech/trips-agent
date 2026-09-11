using System.Runtime.CompilerServices;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using TripsAgent.Application.Identity;
using TripsAgent.Application.Identity.Registration;
using TripsAgent.Application.Notifications;
using TripsAgent.Application.Tenancy;
using TripsAgent.Contracts.Identity;
using TripsAgent.Domain.Identity;
using TripsAgent.Domain.Tenancy;
using TripsAgent.Infrastructure.Identity;
using TripsAgent.Infrastructure.Persistence;
using TripsAgent.Infrastructure.Tenancy;
using TripsAgent.IntegrationTests.Persistence;

namespace TripsAgent.IntegrationTests.Identity;

/// <summary>
/// Registration, verification and resend, driven through the real handlers against real
/// PostgreSQL. Email delivery is captured in memory here; the Mailpit test proves real delivery.
/// </summary>
[Collection(PostgresCollection.Name)]
public class RegistrationTests
{
    private static readonly byte[] HashKey = Enumerable.Range(0, 32).Select(i => (byte)(i + 7)).ToArray();

    private readonly PostgresFixture _postgres;

    public RegistrationTests(PostgresFixture postgres) => _postgres = postgres;

    // ---------------------------------------------------------------------------- happy path

    [Fact]
    public async Task Registering_creates_a_pending_agency_its_owner_and_sends_a_code()
    {
        await using var world = await WorldAsync();

        var outcome = await world.Register(NewRequest("ada@example.com"));

        outcome.Should().BeOfType<RegistrationOutcome.Accepted>();

        using var _ = world.Tenancy.Scope.Enter("test — inspecting what registration created");

        var user = await world.Db.Users.SingleAsync(u => u.Email == "ada@example.com");
        var agency = await world.Db.Agencies.SingleAsync(a => a.Id == user.AgencyId);

        agency.Status.Should().Be(AgencyStatus.PendingVerification);
        agency.Type.Should().Be(AgencyType.Principal);
        agency.LegalName.Should().Be("Ada's Travel Limited");
        user.IsEmailVerified.Should().BeFalse();

        // The owner holds the Owner role in their new agency.
        var owner = await world.Db.Roles.SingleAsync(r => r.Name == Role.SystemRoles.Owner && r.AgencyId == null);
        (await world.Db.UserRoles.AnyAsync(r => r.UserId == user.Id && r.RoleId == owner.Id && r.AgencyId == agency.Id))
            .Should().BeTrue();

        world.Sent.Should().ContainSingle().Which.To.Should().Be("ada@example.com");
    }

    [Fact]
    public async Task The_password_is_stored_as_an_Argon2id_hash_and_never_in_plaintext()
    {
        await using var world = await WorldAsync();

        await world.Register(NewRequest("ada@example.com", password: "CorrectHorse1"));

        using var _ = world.Tenancy.Scope.Enter("test — reading the stored hash");
        var user = await world.Db.Users.SingleAsync(u => u.Email == "ada@example.com");

        user.PasswordHash.Should().StartWith("argon2id$");
        user.PasswordHash.Should().NotContain("CorrectHorse1");
        new Argon2PasswordHasher().Verify("CorrectHorse1", user.PasswordHash).Verified.Should().BeTrue();
    }

    [Fact]
    public async Task The_emailed_code_verifies_the_address()
    {
        await using var world = await WorldAsync();

        await world.Register(NewRequest("ada@example.com"));

        var outcome = await world.Verify("ada@example.com", world.LastCode());

        outcome.Should().Be(VerifyEmailOutcome.Verified);

        using var _ = world.Tenancy.Scope.Enter("test — confirming verification");
        (await world.Db.Users.SingleAsync(u => u.Email == "ada@example.com")).IsEmailVerified.Should().BeTrue();
    }

    [Fact]
    public async Task The_address_is_matched_case_insensitively()
    {
        await using var world = await WorldAsync();

        await world.Register(NewRequest("Ada@Example.COM"));

        (await world.Verify("ADA@example.com", world.LastCode())).Should().Be(VerifyEmailOutcome.Verified);
    }

    // ------------------------------------------------------------------- no account enumeration

    [Fact]
    public async Task Registering_an_existing_address_looks_exactly_like_registering_a_new_one()
    {
        await using var world = await WorldAsync();

        var first = await world.Register(NewRequest("ada@example.com"));
        var second = await world.Register(NewRequest("ada@example.com", businessName: "Somebody Else Ltd"));

        // Same outcome type, and no second agency quietly created behind it.
        first.Should().BeOfType<RegistrationOutcome.Accepted>();
        second.Should().BeOfType<RegistrationOutcome.Accepted>();

        using var _ = world.Tenancy.Scope.Enter("test — counting agencies");
        (await world.Db.Agencies.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task The_password_is_hashed_on_the_existing_address_path_too()
    {
        await using var world = await WorldAsync();

        await world.Register(NewRequest("ada@example.com"));
        world.HashCalls = 0;

        await world.Register(NewRequest("ada@example.com"));

        // Argon2id takes tens of milliseconds. If only new registrations paid for it, response
        // time alone would reveal which addresses already have accounts.
        world.HashCalls.Should().Be(1);
    }

    [Fact]
    public async Task An_unverified_existing_account_gets_a_fresh_code()
    {
        await using var world = await WorldAsync();

        await world.Register(NewRequest("ada@example.com"));
        await world.Register(NewRequest("ada@example.com"));

        // The person who lost the first email is the most likely one to try again.
        world.Sent.Should().HaveCount(2);
    }

    [Fact]
    public async Task A_verified_existing_account_is_sent_nothing()
    {
        await using var world = await WorldAsync();

        await world.Register(NewRequest("ada@example.com"));
        await world.Verify("ada@example.com", world.LastCode());
        world.Sent.Clear();

        await world.Register(NewRequest("ada@example.com"));

        // Otherwise the signup form becomes a way to fill a stranger's inbox.
        world.Sent.Should().BeEmpty();
    }

    // ------------------------------------------------------------------------------- the OTP

    [Fact]
    public async Task A_code_expires_after_fifteen_minutes()
    {
        await using var world = await WorldAsync();

        await world.Register(NewRequest("ada@example.com"));
        var code = world.LastCode();

        world.Clock.Advance(TimeSpan.FromMinutes(15));

        (await world.Verify("ada@example.com", code)).Should().Be(VerifyEmailOutcome.Rejected);
    }

    [Fact]
    public async Task A_code_works_once()
    {
        await using var world = await WorldAsync();

        await world.Register(NewRequest("ada@example.com"));
        var code = world.LastCode();

        (await world.Verify("ada@example.com", code)).Should().Be(VerifyEmailOutcome.Verified);
        (await world.Verify("ada@example.com", code)).Should().Be(VerifyEmailOutcome.Rejected);
    }

    [Fact]
    public async Task Five_wrong_guesses_burn_the_code()
    {
        await using var world = await WorldAsync();

        await world.Register(NewRequest("ada@example.com"));
        var real = world.LastCode();
        var wrong = real == "000000" ? "111111" : "000000";

        for (var i = 0; i < OtpCode.MaxAttempts; i++)
        {
            (await world.Verify("ada@example.com", wrong)).Should().Be(VerifyEmailOutcome.Rejected);
        }

        // The right code is no good now. Without a ceiling, a million six-digit codes can be
        // tried inside the fifteen-minute window.
        (await world.Verify("ada@example.com", real)).Should().Be(VerifyEmailOutcome.Rejected);
    }

    [Fact]
    public async Task Asking_for_a_new_code_retires_the_old_one()
    {
        await using var world = await WorldAsync();

        await world.Register(NewRequest("ada@example.com"));
        var first = world.LastCode();

        world.Clock.Advance(TimeSpan.FromSeconds(30));
        await world.Resend("ada@example.com");
        var second = world.LastCode();

        (await world.Verify("ada@example.com", first)).Should().Be(VerifyEmailOutcome.Rejected);
        (await world.Verify("ada@example.com", second)).Should().Be(VerifyEmailOutcome.Verified);
    }

    [Fact]
    public async Task Codes_are_rate_limited_per_address()
    {
        await using var world = await WorldAsync();

        await world.Register(NewRequest("ada@example.com"));

        for (var i = 0; i < 5; i++)
        {
            world.Clock.Advance(TimeSpan.FromSeconds(10));
            await world.Resend("ada@example.com");
        }

        // Three codes per address per fifteen minutes, however many times someone presses resend.
        world.Sent.Should().HaveCount(VerificationCodeIssuer.MaxCodesPerWindow);
    }

    [Fact]
    public async Task The_rate_limit_lifts_once_the_window_has_passed()
    {
        await using var world = await WorldAsync();

        await world.Register(NewRequest("ada@example.com"));
        await world.Resend("ada@example.com");
        await world.Resend("ada@example.com");
        await world.Resend("ada@example.com");

        world.Sent.Should().HaveCount(3);

        world.Clock.Advance(VerificationCodeIssuer.ThrottleWindow + TimeSpan.FromSeconds(1));
        await world.Resend("ada@example.com");

        world.Sent.Should().HaveCount(4);
    }

    [Fact]
    public async Task Codes_are_stored_hashed()
    {
        await using var world = await WorldAsync();

        await world.Register(NewRequest("ada@example.com"));
        var code = world.LastCode();

        using var _ = world.Tenancy.Scope.Enter("test — reading the stored code");
        var stored = await world.Db.OtpCodes.SingleAsync();

        stored.CodeHash.Should().NotBe(code);
        stored.CodeHash.Should().NotContain(code);
    }

    [Theory]
    [InlineData("12345")]      // too short
    [InlineData("1234567")]    // too long
    [InlineData("12a456")]     // not numeric
    [InlineData("")]
    public async Task A_malformed_code_is_rejected_without_spending_an_attempt(string code)
    {
        await using var world = await WorldAsync();

        await world.Register(NewRequest("ada@example.com"));

        (await world.Verify("ada@example.com", code)).Should().Be(VerifyEmailOutcome.Rejected);

        using var _ = world.Tenancy.Scope.Enter("test — reading the attempt count");
        (await world.Db.OtpCodes.SingleAsync()).AttemptCount.Should().Be(0);
    }

    [Fact]
    public async Task Verifying_an_address_that_never_registered_is_just_rejected()
    {
        await using var world = await WorldAsync();

        (await world.Verify("nobody@example.com", "123456")).Should().Be(VerifyEmailOutcome.Rejected);
    }

    [Fact]
    public async Task Resending_to_an_unknown_address_sends_nothing_and_does_not_throw()
    {
        await using var world = await WorldAsync();

        await world.Resend("nobody@example.com");

        world.Sent.Should().BeEmpty();
    }

    // ---------------------------------------------------------------------------- validation

    [Theory]
    [InlineData("short1", "Password")]          // 6 characters
    [InlineData("nonumbers", "Password")]       // no digit
    public async Task A_weak_password_is_reported(string password, string field)
    {
        await using var world = await WorldAsync();

        var outcome = await world.Register(NewRequest("ada@example.com", password: password));

        outcome.Should().BeOfType<RegistrationOutcome.Invalid>()
            .Which.Errors.Should().ContainKey(field);
    }

    [Fact]
    public async Task Missing_fields_are_all_reported_at_once()
    {
        await using var world = await WorldAsync();

        var outcome = await world.Register(new RegisterAgentRequest("", "", "", "", null, "", ""));

        outcome.Should().BeOfType<RegistrationOutcome.Invalid>()
            .Which.Errors.Keys.Should().Contain(["BusinessName", "FirstName", "LastName", "Email", "CountryCode", "Password"]);
    }

    [Fact]
    public async Task An_unsupported_country_is_refused_rather_than_guessed()
    {
        await using var world = await WorldAsync();

        // Country feeds straight into tax and currency. A wrong default is worse than a refusal.
        var outcome = await world.Register(NewRequest("ada@example.com", countryCode: "XX"));

        outcome.Should().BeOfType<RegistrationOutcome.Invalid>()
            .Which.Errors.Should().ContainKey("CountryCode");
    }

    [Fact]
    public async Task An_invalid_request_creates_nothing_and_sends_nothing()
    {
        await using var world = await WorldAsync();

        await world.Register(NewRequest("ada@example.com", password: "weak"));

        world.Sent.Should().BeEmpty();

        using var _ = world.Tenancy.Scope.Enter("test — counting");
        (await world.Db.Users.CountAsync()).Should().Be(0);
    }

    // ------------------------------------------------------------------------------ slugs

    [Fact]
    public async Task Two_businesses_with_the_same_name_get_different_slugs()
    {
        await using var world = await WorldAsync();

        await world.Register(NewRequest("one@example.com", businessName: "Lagos Travel"));
        await world.Register(NewRequest("two@example.com", businessName: "Lagos Travel"));

        using var _ = world.Tenancy.Scope.Enter("test — reading slugs");
        var slugs = await world.Db.Agencies.Select(a => a.Slug).OrderBy(s => s).ToListAsync();

        slugs.Should().Equal("lagos-travel", "lagos-travel-2");
    }

    [Theory]
    [InlineData("Ada's Travel & Tours Ltd.", "ada-s-travel-tours-ltd")]
    [InlineData("  ---  ", "agency")]
    public async Task A_slug_is_derived_from_the_business_name(string businessName, string expected)
    {
        await using var world = await WorldAsync();

        await world.Register(NewRequest("ada@example.com", businessName: businessName));

        using var _ = world.Tenancy.Scope.Enter("test — reading the slug");
        (await world.Db.Agencies.SingleAsync()).Slug.Should().Be(expected);
    }

    // ------------------------------------------------------------------------------ helpers

    private static RegisterAgentRequest NewRequest(
        string email,
        string password = "Password123",
        string businessName = "Ada's Travel Limited",
        string countryCode = "NG") =>
        new(businessName, "Ada", "Okonkwo", email, "+2348000000000", countryCode, password);

    private async Task<World> WorldAsync([CallerMemberName] string testName = "")
    {
        var name = testName.ToLowerInvariant();
        var tenancy = TestTenancy.None();
        var clock = new ManualClock(new DateTimeOffset(2026, 3, 1, 9, 0, 0, TimeSpan.Zero));

        var db = await _postgres.CreateEmptyDatabaseAsync(name[..Math.Min(name.Length, 60)], tenancy.Tenant, tenancy.Scope, clock);
        await db.Database.MigrateAsync();
        await ReferenceDataSeeder.EnsureAsync(db, tenancy.Scope);

        return new World(db, tenancy, clock, new HmacTokenHasher(HashKey));
    }

    /// <summary>Everything one test needs, wired the way the DI container would wire it.</summary>
    private sealed class World : IAsyncDisposable
    {
        private readonly RegisterAgentHandler _register;
        private readonly VerifyEmailHandler _verify;
        private readonly ResendVerificationHandler _resend;

        public World(AppDbContext db, (TenantContext Tenant, PlatformScope Scope) tenancy, ManualClock clock, HmacTokenHasher tokenHasher)
        {
            Db = db;
            Tenancy = tenancy;
            Clock = clock;

            var sender = new CapturingEmailSender(Sent);
            var passwordHasher = new CountingPasswordHasher(this);
            var issuer = new VerificationCodeIssuer(db, tokenHasher, sender, NullLogger<VerificationCodeIssuer>.Instance);

            _register = new RegisterAgentHandler(db, passwordHasher, tenancy.Scope, issuer, clock);
            _verify = new VerifyEmailHandler(db, tokenHasher, tenancy.Scope, clock);
            _resend = new ResendVerificationHandler(db, tenancy.Scope, issuer, clock);
        }

        public AppDbContext Db { get; }

        public (TenantContext Tenant, PlatformScope Scope) Tenancy { get; }

        public ManualClock Clock { get; }

        public List<EmailMessage> Sent { get; } = [];

        public int HashCalls { get; set; }

        public Task<RegistrationOutcome> Register(RegisterAgentRequest request) => _register.HandleAsync(request);

        public Task<VerifyEmailOutcome> Verify(string email, string code) => _verify.HandleAsync(new VerifyEmailRequest(email, code));

        public Task Resend(string email) => _resend.HandleAsync(new ResendVerificationRequest(email));

        /// <summary>The six-digit code in the most recent email, read the way a person would.</summary>
        public string LastCode() =>
            System.Text.RegularExpressions.Regex.Match(Sent[^1].TextBody, @"\b\d{6}\b").Value;

        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    private sealed class CapturingEmailSender(List<EmailMessage> sent) : IEmailSender
    {
        public Task<EmailReceipt> SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
        {
            sent.Add(message);
            return Task.FromResult(new EmailReceipt($"<{Guid.NewGuid():N}@test>"));
        }
    }

    /// <summary>The real Argon2id hasher, counting calls so the timing-parity rule can be asserted.</summary>
    private sealed class CountingPasswordHasher(World world) : IPasswordHasher
    {
        private readonly Argon2PasswordHasher _inner = new();

        public string Hash(string password)
        {
            world.HashCalls++;
            return _inner.Hash(password);
        }

        public (bool Verified, bool NeedsRehash) Verify(string password, string hash) => _inner.Verify(password, hash);
    }
}

/// <summary>A clock a test moves by hand, so expiry is asserted exactly rather than by sleeping.</summary>
public sealed class ManualClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now = _now.Add(by);
}
