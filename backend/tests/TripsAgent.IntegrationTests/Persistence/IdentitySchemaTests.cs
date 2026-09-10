using System.Runtime.CompilerServices;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using TripsAgent.Application.Tenancy;
using TripsAgent.Domain.Identity;
using TripsAgent.Domain.Tenancy;
using TripsAgent.Infrastructure.Identity;
using TripsAgent.Infrastructure.Persistence;
using TripsAgent.Infrastructure.Tenancy;

namespace TripsAgent.IntegrationTests.Persistence;

/// <summary>
/// Covers the identity schema against real PostgreSQL — the parts that are only true if the
/// database says so: citext, the lockout index, and the constraints.
/// </summary>
[Collection(PostgresCollection.Name)]
public class IdentitySchemaTests
{
    private readonly PostgresFixture _postgres;
    private readonly (TenantContext Tenant, PlatformScope Scope) _tenancy = TestTenancy.None();

    public IdentitySchemaTests(PostgresFixture postgres) => _postgres = postgres;

    // ------------------------------------------------------------------- case-insensitive email

    [Fact]
    public async Task Two_addresses_differing_only_in_case_cannot_both_register()
    {
        await using var context = await MigratedDatabaseAsync();
        var agency = await AgencyAsync(context);

        context.Users.Add(User.ForAgency(agency.Id, "ada@example.com", "hash", "Ada", "O"));
        await context.SaveChangesAsync();

        // Inserted around the domain, which lower-cases: the guarantee has to come from the
        // citext column, or two concurrent signups both pass a C# check and both insert.
        var act = async () => await context.Database.ExecuteSqlInterpolatedAsync(
            $"""
             INSERT INTO identity.users
                 (id, agency_id, email, password_hash, first_name, last_name, status,
                  failed_login_count, created_at, updated_at)
             VALUES
                 ({Guid.CreateVersion7()}, {agency.Id}, 'Ada@Example.com', 'hash', 'Ada', 'O',
                  'Active', 0, now(), now())
             """);

        (await act.Should().ThrowAsync<PostgresException>())
            .Which.ConstraintName.Should().Be("ix_users_email");
    }

    [Fact]
    public async Task An_address_can_be_found_regardless_of_how_it_was_typed()
    {
        await using var context = await MigratedDatabaseAsync();
        var agency = await AgencyAsync(context);

        context.Users.Add(User.ForAgency(agency.Id, "ada@example.com", "hash", "Ada", "O"));
        await context.SaveChangesAsync();

        await using var connection = new NpgsqlConnection(context.Database.GetConnectionString());
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM identity.users WHERE email = 'ADA@EXAMPLE.COM'";

        // citext makes the comparison itself case-insensitive — no LOWER() at the call site,
        // which is what would quietly stop using the index.
        (await command.ExecuteScalarAsync()).Should().Be(1L);
    }

    [Fact]
    public async Task The_email_column_really_is_citext()
    {
        await using var context = await MigratedDatabaseAsync();

        var type = await ScalarAsync(context,
            """
            SELECT data_type FROM information_schema.columns
            WHERE table_schema = 'identity' AND table_name = 'users' AND column_name = 'email'
            """);

        type.Should().Be("USER-DEFINED", "citext is an extension type, not a built-in");

        var underlying = await ScalarAsync(context,
            """
            SELECT udt_name FROM information_schema.columns
            WHERE table_schema = 'identity' AND table_name = 'users' AND column_name = 'email'
            """);

        underlying.Should().Be("citext");
    }

    // ---------------------------------------------------------------------------- credentials

    [Theory]
    [InlineData("users", "password_hash")]
    [InlineData("refresh_tokens", "token_hash")]
    [InlineData("password_reset_tokens", "token_hash")]
    [InlineData("otp_codes", "code_hash")]
    [InlineData("user_invitations", "token_hash")]
    public async Task Credentials_are_stored_in_a_column_named_hash(string table, string column)
    {
        await using var context = await MigratedDatabaseAsync();

        var exists = await ScalarAsync(context,
            $"""
             SELECT count(*)::text FROM information_schema.columns
             WHERE table_schema = 'identity' AND table_name = '{table}' AND column_name = '{column}'
             """);

        exists.Should().Be("1");
    }

    [Fact]
    public async Task No_identity_column_looks_like_it_holds_a_plaintext_credential()
    {
        await using var context = await MigratedDatabaseAsync();

        // A column called `password`, `token` or `code` with no `_hash` suffix is the shape of
        // the mistake: someone adds one, it is never reviewed, and a database dump hands out
        // live credentials. Catch it at the schema level rather than in a review.
        var suspicious = await QueryAsync(context,
            """
            SELECT table_name || '.' || column_name
            FROM information_schema.columns
            WHERE table_schema = 'identity'
              -- permissions.code is a permission identifier like 'booking.issue', not a
              -- credential. It is the one legitimate column this name heuristic would catch.
              AND table_name <> 'permissions'
              AND (column_name IN ('password', 'token', 'code', 'secret', 'otp')
                   OR column_name LIKE '%_plaintext')
            """);

        suspicious.Should().BeEmpty(
            $"credentials must be stored hashed: {string.Join(", ", suspicious)}");
    }

    [Fact]
    public async Task A_refresh_token_cannot_replace_itself()
    {
        await using var context = await MigratedDatabaseAsync();
        var user = await UserAsync(context);

        var token = RefreshToken.Issue(user.Id, "hash-1", DateTimeOffset.UtcNow.AddDays(30));
        context.RefreshTokens.Add(token);
        await context.SaveChangesAsync();

        // A self-replacing token makes the reuse-detection walk loop forever.
        var act = async () => await context.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE identity.refresh_tokens SET replaced_by_id = id WHERE id = {token.Id}");

        (await act.Should().ThrowAsync<PostgresException>())
            .Which.ConstraintName.Should().Be("ck_refresh_tokens_not_self_replacing");
    }

    [Fact]
    public async Task Two_refresh_tokens_cannot_share_a_hash()
    {
        await using var context = await MigratedDatabaseAsync();
        var user = await UserAsync(context);

        context.RefreshTokens.Add(RefreshToken.Issue(user.Id, "same-hash", DateTimeOffset.UtcNow.AddDays(30)));
        await context.SaveChangesAsync();

        context.RefreshTokens.Add(RefreshToken.Issue(user.Id, "same-hash", DateTimeOffset.UtcNow.AddDays(30)));

        // Presenting a token means looking it up by hash. Two rows sharing one would make reuse
        // detection ambiguous at exactly the moment it matters.
        var act = async () => await context.SaveChangesAsync();

        var thrown = await act.Should().ThrowAsync<DbUpdateException>();
        thrown.Which.InnerException.Should().BeOfType<PostgresException>()
            .Which.ConstraintName.Should().Be("ix_refresh_tokens_token_hash");
    }

    // ------------------------------------------------------------------------------- lockout

    [Fact]
    public async Task Login_attempts_are_indexed_by_email_and_most_recent_first()
    {
        await using var context = await MigratedDatabaseAsync();

        var definition = await ScalarAsync(context,
            """
            SELECT indexdef FROM pg_indexes
            WHERE schemaname = 'identity'
              AND indexname = 'ix_login_attempts_email_normalized_attempted_at'
            """);

        // The lockout check reads the newest attempts for one address. Descending on the
        // timestamp turns that into reading the first few index entries rather than a sort.
        definition.Should().Contain("email_normalized");
        definition.Should().Contain("attempted_at DESC");
    }

    [Fact]
    public async Task The_lockout_query_uses_that_index()
    {
        await using var context = await MigratedDatabaseAsync();

        for (var i = 0; i < 500; i++)
        {
            context.LoginAttempts.Add(LoginAttempt.Record(
                $"user{i % 50}@example.com", i % 3 == 0, DateTimeOffset.UtcNow.AddSeconds(-i)));
        }

        await context.SaveChangesAsync();
        await context.Database.ExecuteSqlRawAsync("ANALYZE identity.login_attempts;");

        var plan = await ExplainAsync(context,
            """
            SELECT succeeded FROM identity.login_attempts
            WHERE email_normalized = 'user1@example.com'
            ORDER BY attempted_at DESC
            LIMIT 5
            """);

        plan.Should().Contain("ix_login_attempts_email_normalized_attempted_at",
            $"the lockout check must be index-assisted, but the planner chose:\n{plan}");
    }

    [Fact]
    public async Task A_failed_login_count_cannot_go_negative()
    {
        await using var context = await MigratedDatabaseAsync();
        var user = await UserAsync(context);

        var act = async () => await context.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE identity.users SET failed_login_count = -1 WHERE id = {user.Id}");

        (await act.Should().ThrowAsync<PostgresException>())
            .Which.ConstraintName.Should().Be("ck_users_failed_login_count");
    }

    // --------------------------------------------------------------------------------- roles

    [Fact]
    public async Task Two_system_roles_cannot_share_a_name()
    {
        await using var context = await MigratedDatabaseAsync();

        context.Roles.Add(Role.CreateSystemRole("Owner", RoleScope.Agency, "first"));
        await context.SaveChangesAsync();

        context.Roles.Add(Role.CreateSystemRole("Owner", RoleScope.Agency, "second"));

        // Both have a NULL agency, and PostgreSQL treats NULLs as distinct — so the composite
        // unique index does not catch this and the partial index has to.
        var act = async () => await context.SaveChangesAsync();

        var thrown = await act.Should().ThrowAsync<DbUpdateException>();
        thrown.Which.InnerException.Should().BeOfType<PostgresException>()
            .Which.ConstraintName.Should().Be("ix_roles_system_name");
    }

    [Fact]
    public async Task A_platform_role_may_not_belong_to_an_agency()
    {
        await using var context = await MigratedDatabaseAsync();
        var agency = await AgencyAsync(context);

        var act = async () => await context.Database.ExecuteSqlInterpolatedAsync(
            $"""
             INSERT INTO identity.roles
                 (id, agency_id, name, scope, is_system, description, created_at, updated_at)
             VALUES
                 ({Guid.CreateVersion7()}, {agency.Id}, 'Rogue Platform Role', 'Platform', false,
                  'should not be possible', now(), now())
             """);

        (await act.Should().ThrowAsync<PostgresException>())
            .Which.ConstraintName.Should().Be("ck_roles_platform_has_no_agency");
    }

    [Fact]
    public async Task One_person_can_hold_different_roles_in_different_agencies()
    {
        await using var context = await MigratedDatabaseAsync();

        var principal = Agency.RegisterPrincipal("Principal Limited", "principal", "NG", "NGN", "Africa/Lagos");
        var branch = Agency.RegisterSubAgent(principal, "Branch Limited", "branch");
        context.Agencies.AddRange(principal, branch);

        var owner = Role.CreateSystemRole("Owner", RoleScope.Agency, "owner");
        var agent = Role.CreateSystemRole("Agent", RoleScope.Agency, "agent");
        context.Roles.AddRange(owner, agent);

        var user = User.ForAgency(principal.Id, "both@example.com", "hash", "Both", "Roles");
        context.Users.Add(user);
        await context.SaveChangesAsync();

        // An owner in their own business, an agent in a branch they help out with. This is why
        // the grant carries an agency of its own rather than inheriting the user's.
        context.UserRoles.AddRange(
            UserRole.Grant(user.Id, owner.Id, principal.Id),
            UserRole.Grant(user.Id, agent.Id, branch.Id));

        await context.SaveChangesAsync();

        (await context.UserRoles.IgnoreQueryFilters().CountAsync(r => r.UserId == user.Id))
            .Should().Be(2);
    }

    [Fact]
    public async Task The_same_role_cannot_be_granted_twice_in_one_agency()
    {
        await using var context = await MigratedDatabaseAsync();
        var user = await UserAsync(context);

        var role = Role.CreateSystemRole("Owner", RoleScope.Agency, "owner");
        context.Roles.Add(role);
        await context.SaveChangesAsync();

        context.UserRoles.Add(UserRole.Grant(user.Id, role.Id, user.AgencyId!.Value));
        await context.SaveChangesAsync();

        context.UserRoles.Add(UserRole.Grant(user.Id, role.Id, user.AgencyId!.Value));

        var act = async () => await context.SaveChangesAsync();

        var thrown = await act.Should().ThrowAsync<DbUpdateException>();
        thrown.Which.InnerException.Should().BeOfType<PostgresException>()
            .Which.ConstraintName.Should().Be("ix_user_roles_user_id_role_id_agency_id");
    }

    // ------------------------------------------------------------------------------ seeding

    [Fact]
    public async Task Seeding_creates_the_permission_catalogue()
    {
        await using var context = await MigratedDatabaseAsync();

        await DatabaseSeeder.SeedAsync(context, _tenancy.Scope, new Argon2PasswordHasher());

        using var _ = _tenancy.Scope.Enter("test — reading seeded platform data");

        var codes = await context.Permissions.Select(p => p.Code).ToListAsync();

        codes.Should().BeEquivalentTo(PermissionCodes.All.Select(p => p.Code));
        codes.Should().Contain(PermissionCodes.BookingIssue);
        codes.Should().Contain(PermissionCodes.KybReview);
        codes.Should().Contain(PermissionCodes.MarginView);
    }

    [Fact]
    public async Task Seeding_creates_a_super_admin_a_verified_agent_a_sub_agent_and_a_pending_agent()
    {
        await using var context = await MigratedDatabaseAsync();

        await DatabaseSeeder.SeedAsync(context, _tenancy.Scope, new Argon2PasswordHasher());

        using var _ = _tenancy.Scope.Enter("test — reading seeded accounts across agencies");

        var superAdmin = await context.Users.SingleAsync(u => u.Email == IdentitySeedData.SuperAdminEmail);
        superAdmin.IsPlatformStaff.Should().BeTrue();

        var verifiedOwner = await context.Users.SingleAsync(u => u.Email == IdentitySeedData.VerifiedAgentEmail);
        var verifiedAgency = await context.Agencies.SingleAsync(a => a.Id == verifiedOwner.AgencyId);
        verifiedAgency.Status.Should().Be(AgencyStatus.Verified);

        var subAgentOwner = await context.Users.SingleAsync(u => u.Email == IdentitySeedData.SubAgentEmail);
        var subAgentAgency = await context.Agencies.SingleAsync(a => a.Id == subAgentOwner.AgencyId);
        subAgentAgency.Type.Should().Be(AgencyType.SubAgent);

        var pendingOwner = await context.Users.SingleAsync(u => u.Email == IdentitySeedData.PendingAgentEmail);
        var pendingAgency = await context.Agencies.SingleAsync(a => a.Id == pendingOwner.AgencyId);
        pendingAgency.Status.Should().Be(AgencyStatus.PendingVerification);

        // The pending owner is deliberately unverified — that is the state the onboarding
        // screens have to handle, and it needs to exist to be seen.
        pendingOwner.IsEmailVerified.Should().BeFalse();
        verifiedOwner.IsEmailVerified.Should().BeTrue();
    }

    [Fact]
    public async Task Seeded_passwords_are_hashed_and_verifiable()
    {
        await using var context = await MigratedDatabaseAsync();
        var hasher = new Argon2PasswordHasher();

        await DatabaseSeeder.SeedAsync(context, _tenancy.Scope, hasher);

        using var _ = _tenancy.Scope.Enter("test — reading a seeded account");
        var user = await context.Users.SingleAsync(u => u.Email == IdentitySeedData.SuperAdminEmail);

        user.PasswordHash.Should().NotBe(IdentitySeedData.DevelopmentPassword);
        user.PasswordHash.Should().StartWith("argon2id$");

        hasher.Verify(IdentitySeedData.DevelopmentPassword, user.PasswordHash)
            .Verified.Should().BeTrue();
    }

    [Fact]
    public async Task An_agent_role_cannot_see_margin()
    {
        await using var context = await MigratedDatabaseAsync();

        await DatabaseSeeder.SeedAsync(context, _tenancy.Scope, new Argon2PasswordHasher());

        using var _ = _tenancy.Scope.Enter("test — reading seeded roles");

        var agentRole = await context.Roles.SingleAsync(r => r.Name == Role.SystemRoles.Agent && r.AgencyId == null);

        var granted = await (
            from rolePermission in context.RolePermissions
            join permission in context.Permissions on rolePermission.PermissionId equals permission.Id
            where rolePermission.RoleId == agentRole.Id
            select permission.Code).ToListAsync();

        // The net rate is what Trips charges the agency. A counter agent has no reason to see
        // their employer's cost price.
        granted.Should().NotContain(PermissionCodes.MarginView);
        granted.Should().Contain(PermissionCodes.BookingCreate);
    }

    [Fact]
    public async Task No_agency_role_is_granted_a_platform_permission()
    {
        await using var context = await MigratedDatabaseAsync();

        await DatabaseSeeder.SeedAsync(context, _tenancy.Scope, new Argon2PasswordHasher());

        using var _ = _tenancy.Scope.Enter("test — auditing seeded role grants");

        var leaked = await (
            from role in context.Roles
            join rolePermission in context.RolePermissions on role.Id equals rolePermission.RoleId
            join permission in context.Permissions on rolePermission.PermissionId equals permission.Id
            where role.Scope == RoleScope.Agency
                  && PermissionCodes.PlatformOnly.Contains(permission.Code)
            select role.Name + " -> " + permission.Code).ToListAsync();

        // kyb.review on an agency role would let an agency approve its own KYB submission.
        leaked.Should().BeEmpty($"agency roles must not hold platform permissions: {string.Join(", ", leaked)}");
    }

    // ------------------------------------------------------------------------------ helpers

    private async Task<AppDbContext> MigratedDatabaseAsync([CallerMemberName] string testName = "")
    {
        var name = testName.ToLowerInvariant();
        var context = await _postgres.CreateEmptyDatabaseAsync(
            name[..Math.Min(name.Length, 60)], _tenancy.Tenant, _tenancy.Scope);

        await context.Database.MigrateAsync();
        return context;
    }

    private static async Task<Agency> AgencyAsync(AppDbContext context)
    {
        var agency = Agency.RegisterPrincipal("Test Limited", "test-agency", "NG", "NGN", "Africa/Lagos");
        context.Agencies.Add(agency);
        await context.SaveChangesAsync();
        return agency;
    }

    private static async Task<User> UserAsync(AppDbContext context)
    {
        var agency = await AgencyAsync(context);
        var user = User.ForAgency(agency.Id, "person@example.com", "hash", "Test", "Person");
        context.Users.Add(user);
        await context.SaveChangesAsync();
        return user;
    }

    private static async Task<string?> ScalarAsync(AppDbContext context, string sql)
    {
        await using var connection = new NpgsqlConnection(context.Database.GetConnectionString());
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        return (await command.ExecuteScalarAsync())?.ToString();
    }

    private static async Task<List<string>> QueryAsync(AppDbContext context, string sql)
    {
        await using var connection = new NpgsqlConnection(context.Database.GetConnectionString());
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        var rows = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(reader.GetString(0));
        }

        return rows;
    }

    private static async Task<string> ExplainAsync(AppDbContext context, string sql)
    {
        await using var connection = new NpgsqlConnection(context.Database.GetConnectionString());
        await connection.OpenAsync();

        await using (var settings = connection.CreateCommand())
        {
            settings.CommandText = "SET enable_seqscan = off;";
            await settings.ExecuteNonQueryAsync();
        }

        await using var command = connection.CreateCommand();
        command.CommandText = $"EXPLAIN (FORMAT TEXT) {sql}";

        var plan = new System.Text.StringBuilder();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            plan.AppendLine(reader.GetString(0));
        }

        return plan.ToString();
    }
}
