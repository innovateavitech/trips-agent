using System.Runtime.CompilerServices;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using TripsAgent.Application.Identity;
using TripsAgent.Application.Identity.Authentication;
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
/// Sign-in, refresh rotation, reuse detection and sign-out, against real PostgreSQL.
/// </summary>
[Collection(PostgresCollection.Name)]
public class AuthenticationTests
{
    private const string Password = "Password123";

    private static readonly byte[] HashKey = Enumerable.Range(0, 32).Select(i => (byte)(i + 3)).ToArray();

    private static readonly string SigningKey =
        Convert.ToBase64String(Enumerable.Range(0, 32).Select(i => (byte)(i + 11)).ToArray());

    private readonly PostgresFixture _postgres;

    public AuthenticationTests(PostgresFixture postgres) => _postgres = postgres;

    // -------------------------------------------------------------------------------- sign-in

    [Fact]
    public async Task Correct_credentials_return_a_token_pair()
    {
        await using var world = await WorldAsync();

        var outcome = await world.Login(world.OwnerEmail, Password);

        var tokens = outcome.Should().BeOfType<LoginOutcome.Succeeded>().Which.Tokens;
        tokens.Access.Value.Should().NotBeNullOrWhiteSpace();
        tokens.RefreshTokenValue.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task The_refresh_token_is_stored_only_as_a_hash()
    {
        await using var world = await WorldAsync();

        var tokens = (await world.Login(world.OwnerEmail, Password) as LoginOutcome.Succeeded)!.Tokens;

        using var _ = world.Tenancy.Scope.Enter("test — reading the stored token");
        var stored = await world.Db.RefreshTokens.SingleAsync();

        stored.TokenHash.Should().NotBe(tokens.RefreshTokenValue);
        stored.TokenHash.Should().NotContain(tokens.RefreshTokenValue);
    }

    [Fact]
    public async Task A_refresh_token_lasts_thirty_days()
    {
        await using var world = await WorldAsync();

        await world.Login(world.OwnerEmail, Password);

        using var _ = world.Tenancy.Scope.Enter("test — reading the stored token");
        (await world.Db.RefreshTokens.SingleAsync()).ExpiresAt
            .Should().Be(world.Clock.GetUtcNow().AddDays(30));
    }

    [Theory]
    [InlineData("wrong-password")]
    [InlineData("")]
    public async Task A_wrong_password_fails(string password)
    {
        await using var world = await WorldAsync();

        (await world.Login(world.OwnerEmail, password)).Should().BeOfType<LoginOutcome.Failed>();
    }

    [Fact]
    public async Task An_unknown_address_fails_the_same_way_as_a_wrong_password()
    {
        await using var world = await WorldAsync();

        // Identical outcome type: which one it was is exactly what an attacker wants to learn.
        (await world.Login("nobody@example.com", Password)).Should().BeOfType<LoginOutcome.Failed>();
    }

    [Fact]
    public async Task The_password_is_hashed_even_when_the_address_is_unknown()
    {
        await using var world = await WorldAsync();

        world.HashCalls = 0;
        await world.Login("nobody@example.com", Password);

        // Otherwise "no such account" returns measurably faster than "wrong password".
        world.HashCalls.Should().Be(1);
    }

    [Fact]
    public async Task Every_attempt_is_recorded_including_ones_against_unknown_addresses()
    {
        await using var world = await WorldAsync();

        await world.Login(world.OwnerEmail, Password);
        await world.Login(world.OwnerEmail, "wrong");
        await world.Login("nobody@example.com", "wrong");

        using var _ = world.Tenancy.Scope.Enter("test — reading the login audit trail");
        var attempts = await world.Db.LoginAttempts.OrderBy(a => a.AttemptedAt).ToListAsync();

        attempts.Should().HaveCount(3);
        attempts.Should().Contain(a => a.EmailNormalized == "nobody@example.com" && !a.Succeeded);
        attempts.Count(a => a.Succeeded).Should().Be(1);
    }

    // -------------------------------------------------------------------------------- lockout

    [Fact]
    public async Task Five_wrong_passwords_lock_the_account_for_fifteen_minutes()
    {
        await using var world = await WorldAsync();

        for (var i = 0; i < User.MaxFailedLoginAttempts; i++)
        {
            await world.Login(world.OwnerEmail, "wrong");
        }

        // Even the right password now fails — and fails identically, so the lockout is not
        // announced to whoever is guessing.
        (await world.Login(world.OwnerEmail, Password)).Should().BeOfType<LoginOutcome.Failed>();

        world.Clock.Advance(TimeSpan.FromMinutes(15));

        (await world.Login(world.OwnerEmail, Password)).Should().BeOfType<LoginOutcome.Succeeded>();
    }

    [Fact]
    public async Task A_successful_sign_in_resets_the_failure_count()
    {
        await using var world = await WorldAsync();

        await world.Login(world.OwnerEmail, "wrong");
        await world.Login(world.OwnerEmail, "wrong");
        await world.Login(world.OwnerEmail, Password);

        // Four more would lock the account if the count had not reset.
        for (var i = 0; i < 4; i++)
        {
            await world.Login(world.OwnerEmail, "wrong");
        }

        (await world.Login(world.OwnerEmail, Password)).Should().BeOfType<LoginOutcome.Succeeded>();
    }

    // ------------------------------------------------------------------------ account state

    [Fact]
    public async Task An_unverified_address_is_told_so_rather_than_given_a_generic_failure()
    {
        await using var world = await WorldAsync(verifyOwner: false);

        // They proved the password, so this reveals nothing they did not already know — and the
        // console needs to know to send them to the verification screen.
        (await world.Login(world.OwnerEmail, Password)).Should().BeOfType<LoginOutcome.EmailNotVerified>();
    }

    [Fact]
    public async Task A_suspended_account_cannot_sign_in()
    {
        await using var world = await WorldAsync();

        using (var _ = world.Tenancy.Scope.Enter("test — suspending the account"))
        {
            (await world.Db.Users.SingleAsync(u => u.Email == world.OwnerEmail)).Suspend();
            await world.Db.SaveChangesAsync();
        }

        (await world.Login(world.OwnerEmail, Password)).Should().BeOfType<LoginOutcome.AccountUnavailable>();
    }

    // --------------------------------------------------------------------------- rotation

    [Fact]
    public async Task Refreshing_returns_a_new_pair_and_retires_the_old_token()
    {
        await using var world = await WorldAsync();

        var first = (await world.Login(world.OwnerEmail, Password) as LoginOutcome.Succeeded)!.Tokens;

        var refreshed = await world.Refresh(first.RefreshTokenValue);

        var second = refreshed.Should().BeOfType<RefreshOutcome.Succeeded>().Which.Tokens;
        second.RefreshTokenValue.Should().NotBe(first.RefreshTokenValue);

        using var _ = world.Tenancy.Scope.Enter("test — inspecting the rotation chain");
        var old = await world.Db.RefreshTokens.SingleAsync(t => t.Id == first.Stored.Id);

        old.IsUsed.Should().BeTrue();
        old.ReplacedById.Should().Be(second.Stored.Id);
    }

    [Fact]
    public async Task A_refresh_token_works_only_once()
    {
        await using var world = await WorldAsync();

        var first = (await world.Login(world.OwnerEmail, Password) as LoginOutcome.Succeeded)!.Tokens;
        await world.Refresh(first.RefreshTokenValue);

        (await world.Refresh(first.RefreshTokenValue)).Should().BeOfType<RefreshOutcome.ReuseDetected>();
    }

    [Fact]
    public async Task Reusing_a_token_revokes_every_token_the_user_holds()
    {
        await using var world = await WorldAsync();

        var first = (await world.Login(world.OwnerEmail, Password) as LoginOutcome.Succeeded)!.Tokens;
        var second = (await world.Refresh(first.RefreshTokenValue) as RefreshOutcome.Succeeded)!.Tokens;

        // The thief and the real user hold the same value; whoever goes second presents a token
        // that has already been exchanged. There is no way to tell which is which, so both lose.
        (await world.Refresh(first.RefreshTokenValue)).Should().BeOfType<RefreshOutcome.ReuseDetected>();

        (await world.Refresh(second.RefreshTokenValue)).Should().BeOfType<RefreshOutcome.Rejected>();

        using var _ = world.Tenancy.Scope.Enter("test — confirming every token is revoked");
        (await world.Db.RefreshTokens.AllAsync(t => t.RevokedAt != null)).Should().BeTrue();
    }

    [Fact]
    public async Task An_expired_refresh_token_is_rejected()
    {
        await using var world = await WorldAsync();

        var tokens = (await world.Login(world.OwnerEmail, Password) as LoginOutcome.Succeeded)!.Tokens;

        world.Clock.Advance(TimeSpan.FromDays(30));

        (await world.Refresh(tokens.RefreshTokenValue)).Should().BeOfType<RefreshOutcome.Rejected>();
    }

    [Fact]
    public async Task An_unknown_refresh_token_is_rejected()
    {
        await using var world = await WorldAsync();

        (await world.Refresh("not-a-real-token")).Should().BeOfType<RefreshOutcome.Rejected>();
    }

    [Fact]
    public async Task An_account_suspended_after_sign_in_cannot_refresh_its_way_back()
    {
        await using var world = await WorldAsync();

        var tokens = (await world.Login(world.OwnerEmail, Password) as LoginOutcome.Succeeded)!.Tokens;

        using (var _ = world.Tenancy.Scope.Enter("test — suspending after sign-in"))
        {
            (await world.Db.Users.SingleAsync(u => u.Email == world.OwnerEmail)).Suspend();
            await world.Db.SaveChangesAsync();
        }

        // The short access-token lifetime exists so this check gets a chance to run.
        (await world.Refresh(tokens.RefreshTokenValue)).Should().BeOfType<RefreshOutcome.Rejected>();
    }

    [Fact]
    public async Task A_refreshed_access_token_picks_up_a_role_granted_since_sign_in()
    {
        await using var world = await WorldAsync();

        var first = (await world.Login(world.OwnerEmail, Password) as LoginOutcome.Succeeded)!.Tokens;

        using (var _ = world.Tenancy.Scope.Enter("test — granting another role"))
        {
            var user = await world.Db.Users.SingleAsync(u => u.Email == world.OwnerEmail);
            var agent = await world.Db.Roles.SingleAsync(r => r.Name == Role.SystemRoles.Agent && r.AgencyId == null);
            world.Db.UserRoles.Add(UserRole.Grant(user.Id, agent.Id, user.AgencyId!.Value));
            await world.Db.SaveChangesAsync();
        }

        var second = (await world.Refresh(first.RefreshTokenValue) as RefreshOutcome.Succeeded)!.Tokens;

        RolesIn(second.Access.Value).Should().Contain(Role.SystemRoles.Agent);
    }

    // ---------------------------------------------------------------------------- sign-out

    [Fact]
    public async Task Signing_out_revokes_the_refresh_token()
    {
        await using var world = await WorldAsync();

        var tokens = (await world.Login(world.OwnerEmail, Password) as LoginOutcome.Succeeded)!.Tokens;

        await world.Logout(tokens.RefreshTokenValue);

        // Rejected, not "reuse detected": the token was revoked, never exchanged. A signed-out
        // client presenting its old token is ordinary, and must not raise a security alarm.
        (await world.Refresh(tokens.RefreshTokenValue)).Should().BeOfType<RefreshOutcome.Rejected>();
    }

    [Fact]
    public async Task Signing_out_with_a_token_we_never_issued_does_nothing_and_does_not_throw()
    {
        await using var world = await WorldAsync();

        var act = async () => await world.Logout("not-a-real-token");

        await act.Should().NotThrowAsync();
    }

    // ------------------------------------------------------------------------------- claims

    [Fact]
    public async Task The_access_token_carries_the_agency_the_tenant_context_needs()
    {
        await using var world = await WorldAsync();

        var tokens = (await world.Login(world.OwnerEmail, Password) as LoginOutcome.Succeeded)!.Tokens;

        var claims = ClaimsIn(tokens.Access.Value);

        claims.Should().ContainKey(TripsClaimTypes.AgencyId)
            .WhoseValue.Should().Contain(world.AgencyId.ToString());

        // A principal is its own root.
        claims.Should().ContainKey(TripsClaimTypes.RootAgencyId)
            .WhoseValue.Should().Contain(world.AgencyId.ToString());
    }

    [Fact]
    public async Task A_sub_agents_token_names_its_principal_as_the_root()
    {
        await using var world = await WorldAsync();

        Guid branchId;
        string branchEmail = "branch-owner@example.com";

        using (var _ = world.Tenancy.Scope.Enter("test — creating a sub-agent and its owner"))
        {
            var principal = await world.Db.Agencies.SingleAsync(a => a.Id == world.AgencyId);
            var branch = Agency.RegisterSubAgent(principal, "Branch Limited", "branch-limited");
            branch.MarkVerified(world.Clock.GetUtcNow());
            world.Db.Agencies.Add(branch);

            var owner = User.ForAgency(branch.Id, branchEmail, world.PasswordHash, "Branch", "Owner");
            owner.MarkEmailVerified(world.Clock.GetUtcNow());
            world.Db.Users.Add(owner);

            var ownerRole = await world.Db.Roles.SingleAsync(r => r.Name == Role.SystemRoles.Owner && r.AgencyId == null);
            world.Db.UserRoles.Add(UserRole.Grant(owner.Id, ownerRole.Id, branch.Id));

            await world.Db.SaveChangesAsync();
            branchId = branch.Id;
        }

        var tokens = (await world.Login(branchEmail, Password) as LoginOutcome.Succeeded)!.Tokens;
        var claims = ClaimsIn(tokens.Access.Value);

        claims[TripsClaimTypes.AgencyId].Should().Contain(branchId.ToString());
        claims[TripsClaimTypes.RootAgencyId].Should().Contain(world.AgencyId.ToString());
    }

    [Fact]
    public async Task A_users_roles_in_another_agency_do_not_reach_their_token()
    {
        await using var world = await WorldAsync();

        using (var _ = world.Tenancy.Scope.Enter("test — granting a role in an unrelated agency"))
        {
            var other = Agency.RegisterPrincipal("Other Limited", "other-limited", "NG", "NGN", "Africa/Lagos");
            world.Db.Agencies.Add(other);

            var user = await world.Db.Users.SingleAsync(u => u.Email == world.OwnerEmail);
            var agent = await world.Db.Roles.SingleAsync(r => r.Name == Role.SystemRoles.Agent && r.AgencyId == null);
            world.Db.UserRoles.Add(UserRole.Grant(user.Id, agent.Id, other.Id));

            await world.Db.SaveChangesAsync();
        }

        var tokens = (await world.Login(world.OwnerEmail, Password) as LoginOutcome.Succeeded)!.Tokens;

        // One person can hold different roles in different agencies. The token is issued for the
        // agency they belong to, so only those roles travel with it.
        RolesIn(tokens.Access.Value).Should().Equal(Role.SystemRoles.Owner);
    }

    // ------------------------------------------------------------------------------ helpers

    private static Dictionary<string, List<string>> ClaimsIn(string jwt) =>
        new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler()
            .ReadJwtToken(jwt).Claims
            .GroupBy(c => c.Type)
            .ToDictionary(g => g.Key, g => g.Select(c => c.Value).ToList(), StringComparer.Ordinal);

    private static List<string> RolesIn(string jwt) =>
        ClaimsIn(jwt).TryGetValue(TripsClaimTypes.Role, out var roles) ? roles : [];

    private async Task<World> WorldAsync(bool verifyOwner = true, [CallerMemberName] string testName = "")
    {
        var name = testName.ToLowerInvariant();
        var tenancy = TestTenancy.None();
        var clock = new ManualClock(new DateTimeOffset(2026, 3, 1, 9, 0, 0, TimeSpan.Zero));

        var db = await _postgres.CreateEmptyDatabaseAsync(name[..Math.Min(name.Length, 60)], tenancy.Tenant, tenancy.Scope, clock);
        await db.Database.MigrateAsync();
        var (_, roles) = await ReferenceDataSeeder.EnsureAsync(db, tenancy.Scope);

        using (var _ = tenancy.Scope.Enter("test setup — creating an agency and its owner"))
        {
            var agency = Agency.RegisterPrincipal("Lagos Travel Limited", "lagos-travel", "NG", "NGN", "Africa/Lagos");
            agency.MarkVerified(clock.GetUtcNow());
            db.Agencies.Add(agency);

            var passwordHash = new Argon2PasswordHasher().Hash(Password);
            var owner = User.ForAgency(agency.Id, "owner@lagostravel.test", passwordHash, "Ada", "Okonkwo");

            if (verifyOwner)
            {
                owner.MarkEmailVerified(clock.GetUtcNow());
            }

            db.Users.Add(owner);
            db.UserRoles.Add(UserRole.Grant(owner.Id, roles[Role.SystemRoles.Owner], agency.Id));

            await db.SaveChangesAsync();

            return new World(db, tenancy, clock, agency.Id, owner.Email, passwordHash, SigningKey);
        }
    }

    private sealed class World : IAsyncDisposable
    {
        private readonly LoginHandler _login;
        private readonly RefreshTokenHandler _refresh;
        private readonly LogoutHandler _logout;

        public World(
            AppDbContext db,
            (TenantContext Tenant, PlatformScope Scope) tenancy,
            ManualClock clock,
            Guid agencyId,
            string ownerEmail,
            string passwordHash,
            string signingKey)
        {
            Db = db;
            Tenancy = tenancy;
            Clock = clock;
            AgencyId = agencyId;
            OwnerEmail = ownerEmail;
            PasswordHash = passwordHash;

            var tokenHasher = new HmacTokenHasher(HashKey);
            var issuer = new JwtAccessTokenIssuer(
                new JwtOptions { Issuer = "https://tripsagent.test", Audience = "trips-agent-api", SigningKey = signingKey },
                clock);

            var factory = new TokenPairFactory(db, issuer, tokenHasher);
            var passwordHasher = new CountingPasswordHasher(this);

            _login = new LoginHandler(db, passwordHasher, tenancy.Scope, factory, clock);
            _refresh = new RefreshTokenHandler(db, tokenHasher, tenancy.Scope, factory, clock, NullLogger<RefreshTokenHandler>.Instance);
            _logout = new LogoutHandler(db, tokenHasher, tenancy.Scope, clock);
        }

        public AppDbContext Db { get; }

        public (TenantContext Tenant, PlatformScope Scope) Tenancy { get; }

        public ManualClock Clock { get; }

        public Guid AgencyId { get; }

        public string OwnerEmail { get; }

        public string PasswordHash { get; }

        public int HashCalls { get; set; }

        public Task<LoginOutcome> Login(string email, string password) =>
            _login.HandleAsync(new LoginRequest(email, password));

        public Task<RefreshOutcome> Refresh(string refreshToken) =>
            _refresh.HandleAsync(new RefreshTokenRequest(refreshToken));

        public Task Logout(string refreshToken) =>
            _logout.HandleAsync(new RefreshTokenRequest(refreshToken));

        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

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
