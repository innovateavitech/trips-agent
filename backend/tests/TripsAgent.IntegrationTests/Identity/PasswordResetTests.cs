using System.Runtime.CompilerServices;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using TripsAgent.Application.Identity;
using TripsAgent.Application.Identity.Authentication;
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

/// <summary>Forgot-password and reset, against real PostgreSQL.</summary>
[Collection(PostgresCollection.Name)]
public class PasswordResetTests
{
    private const string OldPassword = "OldPassword1";
    private const string NewPassword = "NewPass2word";

    private static readonly byte[] HashKey = Enumerable.Range(0, 32).Select(i => (byte)(i + 23)).ToArray();

    private readonly PostgresFixture _postgres;

    public PasswordResetTests(PostgresFixture postgres) => _postgres = postgres;

    // -------------------------------------------------------------------- no enumeration

    [Fact]
    public async Task An_unknown_address_is_treated_exactly_like_a_known_one()
    {
        await using var world = await WorldAsync();

        // No throw, no distinguishing outcome — the endpoint returns the same 202 either way.
        var act = async () => await world.Forgot("nobody@example.com");

        await act.Should().NotThrowAsync();
        world.Sent.Should().BeEmpty("there is nobody to send to, but the caller cannot tell");
    }

    [Fact]
    public async Task A_known_address_is_emailed_a_link()
    {
        await using var world = await WorldAsync();

        await world.Forgot(world.Email);

        var email = world.Sent.Should().ContainSingle().Which;
        email.To.Should().Be(world.Email);
        email.TextBody.Should().Contain("token=");
    }

    // --------------------------------------------------------------------------- the token

    [Fact]
    public async Task The_token_is_stored_only_as_a_hash()
    {
        await using var world = await WorldAsync();

        await world.Forgot(world.Email);
        var token = world.LastToken();

        using var _ = world.Tenancy.Scope.Enter("test — reading the stored token");
        var stored = await world.Db.PasswordResetTokens.SingleAsync();

        stored.TokenHash.Should().NotBe(token);
        stored.TokenHash.Should().NotContain(token);
    }

    [Fact]
    public async Task A_link_expires_after_thirty_minutes()
    {
        await using var world = await WorldAsync();
        await world.Forgot(world.Email);
        var token = world.LastToken();

        world.Clock.Advance(TimeSpan.FromMinutes(30));

        // FRD §2.1 UC-1B RS-7.
        (await world.Reset(token, NewPassword)).Should().BeOfType<ResetPasswordOutcome.LinkNotUsable>();
    }

    [Fact]
    public async Task A_link_works_once()
    {
        await using var world = await WorldAsync();
        await world.Forgot(world.Email);
        var token = world.LastToken();

        (await world.Reset(token, NewPassword)).Should().BeOfType<ResetPasswordOutcome.Reset>();
        (await world.Reset(token, "ThirdPass3")).Should().BeOfType<ResetPasswordOutcome.LinkNotUsable>();
    }

    [Fact]
    public async Task An_unknown_token_is_refused()
    {
        await using var world = await WorldAsync();

        (await world.Reset("not-a-real-token", NewPassword))
            .Should().BeOfType<ResetPasswordOutcome.LinkNotUsable>();
    }

    [Fact]
    public async Task Requesting_a_second_link_does_not_invalidate_the_first_until_one_is_used()
    {
        await using var world = await WorldAsync();

        await world.Forgot(world.Email);
        var first = world.LastToken();

        world.Clock.Advance(TimeSpan.FromMinutes(1));
        await world.Forgot(world.Email);
        var second = world.LastToken();

        // Someone who clicks the older email should not be stranded. Using either one burns both.
        (await world.Reset(first, NewPassword)).Should().BeOfType<ResetPasswordOutcome.Reset>();
        (await world.Reset(second, "ThirdPass3")).Should().BeOfType<ResetPasswordOutcome.LinkNotUsable>();
    }

    // -------------------------------------------------------------------------- the reset

    [Fact]
    public async Task Resetting_changes_the_password()
    {
        await using var world = await WorldAsync();
        await world.Forgot(world.Email);

        await world.Reset(world.LastToken(), NewPassword);

        (await world.Login(OldPassword)).Should().BeOfType<LoginOutcome.Failed>();
        (await world.Login(NewPassword)).Should().BeOfType<LoginOutcome.Succeeded>();
    }

    [Fact]
    public async Task Resetting_signs_every_other_session_out()
    {
        await using var world = await WorldAsync();

        // Signed in on another device before the reset.
        var existing = (await world.Login(OldPassword) as LoginOutcome.Succeeded)!.Tokens;

        await world.Forgot(world.Email);
        await world.Reset(world.LastToken(), NewPassword);

        // Somebody resetting a password may believe they were compromised. Leaving live sessions
        // running would defeat the point.
        (await world.Refresh(existing.RefreshTokenValue)).Should().BeOfType<RefreshOutcome.Rejected>();
    }

    [Fact]
    public async Task Resetting_clears_a_lockout()
    {
        await using var world = await WorldAsync();

        for (var i = 0; i < User.MaxFailedLoginAttempts; i++)
        {
            await world.Login("wrong");
        }

        await world.Forgot(world.Email);
        await world.Reset(world.LastToken(), NewPassword);

        // They have just proved control of their inbox; staying locked out by the guessing that
        // caused the reset would be perverse.
        (await world.Login(NewPassword)).Should().BeOfType<LoginOutcome.Succeeded>();
    }

    [Theory]
    [InlineData("short1")]
    [InlineData("nodigitshere")]
    public async Task A_weak_new_password_is_refused_without_spending_the_link(string weak)
    {
        await using var world = await WorldAsync();
        await world.Forgot(world.Email);
        var token = world.LastToken();

        (await world.Reset(token, weak)).Should().BeOfType<ResetPasswordOutcome.WeakPassword>();

        // Otherwise a mistyped password costs a fresh email.
        (await world.Reset(token, NewPassword)).Should().BeOfType<ResetPasswordOutcome.Reset>();
    }

    // ------------------------------------------------------------------------ rate limiting

    [Fact]
    public async Task Links_are_rate_limited_per_address()
    {
        await using var world = await WorldAsync();

        for (var i = 0; i < 6; i++)
        {
            world.Clock.Advance(TimeSpan.FromSeconds(10));
            await world.Forgot(world.Email);
        }

        world.Sent.Should().HaveCount(ForgotPasswordHandler.MaxLinksPerWindow);
    }

    [Fact]
    public async Task The_limit_lifts_once_the_window_passes()
    {
        await using var world = await WorldAsync();

        for (var i = 0; i < ForgotPasswordHandler.MaxLinksPerWindow; i++)
        {
            await world.Forgot(world.Email);
        }

        world.Clock.Advance(ForgotPasswordHandler.ThrottleWindow + TimeSpan.FromSeconds(1));
        await world.Forgot(world.Email);

        world.Sent.Should().HaveCount(ForgotPasswordHandler.MaxLinksPerWindow + 1);
    }

    // ------------------------------------------------------------------------------ helpers

    private async Task<World> WorldAsync([CallerMemberName] string testName = "")
    {
        var name = testName.ToLowerInvariant();
        name = name[..Math.Min(name.Length, 55)];

        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var tenancy = TestTenancy.None();

        var db = await _postgres.CreateEmptyDatabaseAsync(name, tenancy.Tenant, tenancy.Scope, clock);
        await db.Database.MigrateAsync();
        var (_, roles) = await ReferenceDataSeeder.EnsureAsync(db, tenancy.Scope);

        string email;
        using (var _ = tenancy.Scope.Enter("test setup — creating an agency and its owner"))
        {
            var agency = Agency.RegisterPrincipal("Lagos Travel Limited", "lagos-travel", "NG", "NGN", "Africa/Lagos");
            agency.MarkVerified(clock.GetUtcNow());
            db.Agencies.Add(agency);

            var owner = User.ForAgency(
                agency.Id, "owner@lagostravel.test", new Argon2PasswordHasher().Hash(OldPassword), "Ada", "Okonkwo");

            owner.MarkEmailVerified(clock.GetUtcNow());
            db.Users.Add(owner);
            db.UserRoles.Add(UserRole.Grant(owner.Id, roles[Role.SystemRoles.Owner], agency.Id));

            await db.SaveChangesAsync();
            email = owner.Email;
        }

        return new World(db, tenancy, clock, email);
    }

    private sealed class World : IAsyncDisposable
    {
        private readonly ForgotPasswordHandler _forgot;
        private readonly ResetPasswordHandler _reset;
        private readonly LoginHandler _login;
        private readonly RefreshTokenHandler _refresh;

        public World(AppDbContext db, (TenantContext Tenant, PlatformScope Scope) tenancy, ManualClock clock, string email)
        {
            Db = db;
            Tenancy = tenancy;
            Clock = clock;
            Email = email;

            var tokenHasher = new HmacTokenHasher(HashKey);
            var passwordHasher = new Argon2PasswordHasher();
            var sender = new CapturingEmailSender(Sent);

            _forgot = new ForgotPasswordHandler(
                db, tokenHasher, sender, tenancy.Scope, clock,
                new PasswordResetLinkBuilder("https://console.test/reset-password"),
                NullLogger<ForgotPasswordHandler>.Instance);

            _reset = new ResetPasswordHandler(db, tokenHasher, passwordHasher, tenancy.Scope, clock);

            var issuer = new JwtAccessTokenIssuer(
                new JwtOptions
                {
                    Issuer = "https://tripsagent.test",
                    Audience = "trips-agent-api",
                    SigningKey = Convert.ToBase64String(Enumerable.Range(0, 32).Select(i => (byte)i).ToArray()),
                },
                clock);

            var factory = new TokenPairFactory(db, issuer, tokenHasher);

            _login = new LoginHandler(db, passwordHasher, tenancy.Scope, factory, clock);
            _refresh = new RefreshTokenHandler(
                db, tokenHasher, tenancy.Scope, factory, clock, NullLogger<RefreshTokenHandler>.Instance);
        }

        public AppDbContext Db { get; }

        public (TenantContext Tenant, PlatformScope Scope) Tenancy { get; }

        public ManualClock Clock { get; }

        public string Email { get; }

        public List<EmailMessage> Sent { get; } = [];

        public Task Forgot(string email) => _forgot.HandleAsync(new ForgotPasswordRequest(email));

        public Task<ResetPasswordOutcome> Reset(string token, string password) =>
            _reset.HandleAsync(new ResetPasswordRequest(token, password));

        public Task<LoginOutcome> Login(string password) =>
            _login.HandleAsync(new LoginRequest(Email, password));

        public Task<RefreshOutcome> Refresh(string token) =>
            _refresh.HandleAsync(new RefreshTokenRequest(token));

        /// <summary>The token out of the most recent email, read the way a person clicking would.</summary>
        public string LastToken()
        {
            var body = Sent[^1].TextBody;
            var marker = body.IndexOf("token=", StringComparison.Ordinal) + "token=".Length;
            var end = body.IndexOfAny([' ', '\n', '\r'], marker);

            return Uri.UnescapeDataString(end < 0 ? body[marker..] : body[marker..end]);
        }

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
}
