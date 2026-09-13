using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TripsAgent.Domain.Identity;
using TripsAgent.Domain.Tenancy;

namespace TripsAgent.Infrastructure.Persistence.Configurations.Identity;

/// <summary>Shared constants for the identity tables.</summary>
public static class IdentitySchema
{
    /// <summary>The PostgreSQL schema holding users, roles and credentials.</summary>
    public const string Name = "identity";

    /// <summary>
    /// Width of a stored token hash. Comfortably fits a hex or base64 SHA-256, and an Argon2id
    /// encoded hash with its parameters and salt.
    /// </summary>
    public const int HashLength = 256;
}

public sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("users", IdentitySchema.Name);
        builder.HasKey(user => user.Id);
        builder.Property(user => user.Id).ValueGeneratedNever();

        // citext, so Ada@example.com and ada@example.com are the same account. Case-insensitive
        // comparison in C# would not do: without a unique index on a case-insensitive type, two
        // concurrent signups can both pass the check and both insert.
        builder.Property(user => user.Email)
            .HasColumnType("citext")
            .HasMaxLength(320)
            .IsRequired();

        builder.HasIndex(user => user.Email)
            .IsUnique()
            .HasDatabaseName("ix_users_email");

        builder.Property(user => user.PasswordHash)
            .HasMaxLength(IdentitySchema.HashLength)
            .IsRequired();

        builder.Property(user => user.FirstName).HasMaxLength(100).IsRequired();
        builder.Property(user => user.LastName).HasMaxLength(100).IsRequired();
        builder.Property(user => user.PhoneNumber).HasMaxLength(32);

        builder.Property(user => user.Status)
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();

        builder.HasOne<Agency>()
            .WithMany()
            .HasForeignKey(user => user.AgencyId)
            // A user's account outlives an agency's deletion attempt; Restrict makes that
            // explicit rather than quietly deleting people.
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(user => user.AgencyId)
            .HasDatabaseName("ix_users_agency_id");
    }
}

public sealed class PermissionConfiguration : IEntityTypeConfiguration<Permission>
{
    public void Configure(EntityTypeBuilder<Permission> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("permissions", IdentitySchema.Name);
        builder.HasKey(permission => permission.Id);
        builder.Property(permission => permission.Id).ValueGeneratedNever();

        builder.Property(permission => permission.Code).HasMaxLength(64).IsRequired();
        builder.Property(permission => permission.Category).HasMaxLength(64).IsRequired();
        builder.Property(permission => permission.Description).HasMaxLength(256).IsRequired();

        // Code is what authorisation checks compare against, so it has to be unique and stable.
        builder.HasIndex(permission => permission.Code)
            .IsUnique()
            .HasDatabaseName("ix_permissions_code");
    }
}

public sealed class RoleConfiguration : IEntityTypeConfiguration<Role>
{
    public void Configure(EntityTypeBuilder<Role> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("roles", IdentitySchema.Name);
        builder.HasKey(role => role.Id);
        builder.Property(role => role.Id).ValueGeneratedNever();

        builder.Property(role => role.Name).HasMaxLength(64).IsRequired();
        builder.Property(role => role.Description).HasMaxLength(256).IsRequired();

        builder.Property(role => role.Scope)
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();

        builder.HasOne<Agency>()
            .WithMany()
            .HasForeignKey(role => role.AgencyId)
            .OnDelete(DeleteBehavior.Cascade);

        // A role name is unique within its agency; system roles (null agency) are unique
        // platform-wide. PostgreSQL treats NULLs as distinct in a unique index, so the system
        // half needs its own filtered index — see the migration.
        builder.HasIndex(role => new { role.AgencyId, role.Name })
            .IsUnique()
            .HasDatabaseName("ix_roles_agency_id_name");
    }
}

public sealed class RolePermissionConfiguration : IEntityTypeConfiguration<RolePermission>
{
    public void Configure(EntityTypeBuilder<RolePermission> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("role_permissions", IdentitySchema.Name);

        // A composite key, not a surrogate: the pair is the identity, and a surrogate would let
        // the same grant be inserted twice.
        builder.HasKey(rolePermission => new { rolePermission.RoleId, rolePermission.PermissionId });

        builder.HasOne<Role>()
            .WithMany()
            .HasForeignKey(rolePermission => rolePermission.RoleId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<Permission>()
            .WithMany()
            .HasForeignKey(rolePermission => rolePermission.PermissionId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(rolePermission => rolePermission.PermissionId)
            .HasDatabaseName("ix_role_permissions_permission_id");
    }
}

public sealed class UserRoleConfiguration : IEntityTypeConfiguration<UserRole>
{
    public void Configure(EntityTypeBuilder<UserRole> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("user_roles", IdentitySchema.Name);
        builder.HasKey(userRole => userRole.Id);
        builder.Property(userRole => userRole.Id).ValueGeneratedNever();

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(userRole => userRole.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<Role>()
            .WithMany()
            .HasForeignKey(userRole => userRole.RoleId)
            .OnDelete(DeleteBehavior.Restrict);

        // Nullable: a null agency is a Trips back-office grant, which belongs to no agency.
        builder.HasOne<Agency>()
            .WithMany()
            .HasForeignKey(userRole => userRole.AgencyId)
            .OnDelete(DeleteBehavior.Cascade);

        // Leading with agency_id because every tenant-scoped read filters on it first.
        builder.HasIndex(userRole => new { userRole.AgencyId, userRole.UserId })
            .HasDatabaseName("ix_user_roles_agency_id_user_id");

        // One human holds a given role in a given agency at most once. PostgreSQL treats NULLs as
        // distinct in a unique index, so platform grants need the filtered index below as well or
        // the same role could be granted to the same admin twice.
        builder.HasIndex(userRole => new { userRole.UserId, userRole.RoleId, userRole.AgencyId })
            .IsUnique()
            .HasDatabaseName("ix_user_roles_user_id_role_id_agency_id");

        builder.HasIndex(userRole => new { userRole.UserId, userRole.RoleId })
            .IsUnique()
            .HasFilter("agency_id IS NULL")
            .HasDatabaseName("ix_user_roles_platform_user_id_role_id");
    }
}

public sealed class RefreshTokenConfiguration : IEntityTypeConfiguration<RefreshToken>
{
    public void Configure(EntityTypeBuilder<RefreshToken> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("refresh_tokens", IdentitySchema.Name);
        builder.HasKey(token => token.Id);
        builder.Property(token => token.Id).ValueGeneratedNever();

        builder.Property(token => token.TokenHash)
            .HasMaxLength(IdentitySchema.HashLength)
            .IsRequired();

        builder.Property(token => token.CreatedByIp).HasMaxLength(45);   // fits IPv6

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(token => token.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        // Presenting a token means looking it up by its hash, so that lookup must be indexed —
        // and unique, since two rows sharing a hash would make reuse detection ambiguous.
        builder.HasIndex(token => token.TokenHash)
            .IsUnique()
            .HasDatabaseName("ix_refresh_tokens_token_hash");

        builder.HasIndex(token => token.UserId)
            .HasDatabaseName("ix_refresh_tokens_user_id");

        // Self-reference forming the rotation chain that makes reuse detectable.
        builder.HasOne<RefreshToken>()
            .WithMany()
            .HasForeignKey(token => token.ReplacedById)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class PasswordResetTokenConfiguration : IEntityTypeConfiguration<PasswordResetToken>
{
    public void Configure(EntityTypeBuilder<PasswordResetToken> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("password_reset_tokens", IdentitySchema.Name);
        builder.HasKey(token => token.Id);
        builder.Property(token => token.Id).ValueGeneratedNever();

        builder.Property(token => token.TokenHash)
            .HasMaxLength(IdentitySchema.HashLength)
            .IsRequired();

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(token => token.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(token => token.TokenHash)
            .IsUnique()
            .HasDatabaseName("ix_password_reset_tokens_token_hash");

        builder.HasIndex(token => token.UserId)
            .HasDatabaseName("ix_password_reset_tokens_user_id");
    }
}

public sealed class OtpCodeConfiguration : IEntityTypeConfiguration<OtpCode>
{
    public void Configure(EntityTypeBuilder<OtpCode> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("otp_codes", IdentitySchema.Name);
        builder.HasKey(code => code.Id);
        builder.Property(code => code.Id).ValueGeneratedNever();

        builder.Property(code => code.CodeHash)
            .HasMaxLength(IdentitySchema.HashLength)
            .IsRequired();

        builder.Property(code => code.Destination).HasMaxLength(320).IsRequired();

        builder.Property(code => code.Channel)
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(code => code.Purpose)
            .HasConversion<string>()
            .HasMaxLength(30)
            .IsRequired();

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(code => code.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        // Verification looks up the newest unconsumed code for a destination and purpose, and
        // rate limiting counts recent ones for the same destination.
        builder.HasIndex(code => new { code.Destination, code.Purpose, code.ExpiresAt })
            .HasDatabaseName("ix_otp_codes_destination_purpose_expires_at");

        builder.HasIndex(code => code.UserId)
            .HasDatabaseName("ix_otp_codes_user_id");
    }
}

public sealed class LoginAttemptConfiguration : IEntityTypeConfiguration<LoginAttempt>
{
    public void Configure(EntityTypeBuilder<LoginAttempt> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("login_attempts", IdentitySchema.Name);
        builder.HasKey(attempt => attempt.Id);
        builder.Property(attempt => attempt.Id).ValueGeneratedNever();

        builder.Property(attempt => attempt.EmailNormalized)
            .HasMaxLength(320)
            .IsRequired();

        builder.Property(attempt => attempt.IpAddress).HasMaxLength(45);
        builder.Property(attempt => attempt.UserAgent).HasMaxLength(512);

        // No FK to users: the attempts that matter most are against addresses that do not exist,
        // and a foreign key would make those unrecordable.
        builder.Property(attempt => attempt.UserId);

        // The lockout check reads the most recent attempts for one address. Descending on
        // attempted_at so that read is an index scan of the first few rows, not a sort.
        builder.HasIndex(attempt => new { attempt.EmailNormalized, attempt.AttemptedAt })
            .IsDescending(false, true)
            .HasDatabaseName("ix_login_attempts_email_normalized_attempted_at");
    }
}

public sealed class UserInvitationConfiguration : IEntityTypeConfiguration<UserInvitation>
{
    public void Configure(EntityTypeBuilder<UserInvitation> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("user_invitations", IdentitySchema.Name);
        builder.HasKey(invitation => invitation.Id);
        builder.Property(invitation => invitation.Id).ValueGeneratedNever();

        builder.Property(invitation => invitation.Email)
            .HasColumnType("citext")
            .HasMaxLength(320)
            .IsRequired();

        builder.Property(invitation => invitation.TokenHash)
            .HasMaxLength(IdentitySchema.HashLength)
            .IsRequired();

        builder.HasOne<Agency>()
            .WithMany()
            .HasForeignKey(invitation => invitation.AgencyId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<Role>()
            .WithMany()
            .HasForeignKey(invitation => invitation.RoleId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(invitation => invitation.TokenHash)
            .IsUnique()
            .HasDatabaseName("ix_user_invitations_token_hash");

        builder.HasIndex(invitation => new { invitation.AgencyId, invitation.Email })
            .HasDatabaseName("ix_user_invitations_agency_id_email");
    }
}
