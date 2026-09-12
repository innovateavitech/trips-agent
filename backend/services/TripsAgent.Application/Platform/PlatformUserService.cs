using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TripsAgent.Application.Auditing;
using TripsAgent.Application.Identity;
using TripsAgent.Application.Persistence;
using TripsAgent.Application.Tenancy;
using TripsAgent.Contracts.Platform;
using TripsAgent.Domain.Identity;

namespace TripsAgent.Application.Platform;

/// <summary>What happened to a back-office user action.</summary>
public abstract record PlatformUserOutcome
{
    private PlatformUserOutcome()
    {
    }

    public sealed record Done(PlatformUserResponse User) : PlatformUserOutcome;

    public sealed record NotFound : PlatformUserOutcome;

    public sealed record ReasonRequired : PlatformUserOutcome;

    /// <summary>Something about the request itself is wrong, and this is which field.</summary>
    public sealed record Invalid(string Field, string Detail) : PlatformUserOutcome;

    /// <summary>That email address is already an account, agency or platform.</summary>
    public sealed record EmailTaken : PlatformUserOutcome;
}

/// <summary>
/// Trips' own back-office accounts: who they are, and which of the four roles each holds.
/// </summary>
/// <remarks>
/// <para>
/// A back-office user is an ordinary <see cref="User"/> with no agency, holding a platform-scoped
/// <see cref="Role"/> through a <see cref="UserRole"/> whose agency is null. There is no parallel
/// table and no parallel permission system: the console reads the same roles the token is built
/// from, so what this screen says somebody can do is what they can actually do.
/// </para>
/// <para>
/// <b>No password is ever set here.</b> An account is created <see cref="UserStatus.Invited"/> and
/// the person sets their own through the ordinary reset flow. A password an administrator chose
/// is a password an administrator knows.
/// </para>
/// </remarks>
public sealed partial class PlatformUserService
{
    /// <summary>The audit action names this writes.</summary>
    public static class Actions
    {
        public const string Created = "platform_user.created";
        public const string RoleChanged = "platform_user.role_changed";
        public const string StatusChanged = "platform_user.status_changed";
    }

    private readonly IAppDbContext _db;
    private readonly IPlatformScope _platformScope;
    private readonly IAuditContext _audit;
    private readonly TimeProvider _clock;
    private readonly ILogger<PlatformUserService> _logger;

    public PlatformUserService(
        IAppDbContext db,
        IPlatformScope platformScope,
        IAuditContext audit,
        TimeProvider clock,
        ILogger<PlatformUserService> logger)
    {
        _db = db;
        _platformScope = platformScope;
        _audit = audit;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>Every back-office account, oldest first.</summary>
    public async Task<IReadOnlyList<PlatformUserResponse>> ListAsync(CancellationToken cancellationToken = default)
    {
        using var scope = _platformScope.Enter("Admin console — lists Trips' own back-office accounts");

        var users = await _db.Users.AsNoTracking()
            .Where(user => user.AgencyId == null)
            .OrderBy(user => user.CreatedAt)
            .ToListAsync(cancellationToken);

        var ids = users.Select(user => user.Id).ToList();
        var grants = await GrantsForAsync(ids, cancellationToken);

        return [.. users.Select(user => Describe(user, grants))];
    }

    /// <summary>The four roles a back-office account may hold, and what each allows.</summary>
    public async Task<IReadOnlyList<PlatformRoleResponse>> RolesAsync(CancellationToken cancellationToken = default)
    {
        using var scope = _platformScope.Enter("Admin console — lists the platform roles and their permissions");

        var roles = await _db.Roles.AsNoTracking()
            .Where(role => role.Scope == RoleScope.Platform && role.AgencyId == null)
            .OrderBy(role => role.Name)
            .ToListAsync(cancellationToken);

        var permissions = await (
            from grant in _db.RolePermissions.AsNoTracking()
            join permission in _db.Permissions.AsNoTracking() on grant.PermissionId equals permission.Id
            select new { grant.RoleId, permission.Code })
            .ToListAsync(cancellationToken);

        var byRole = permissions
            .GroupBy(grant => grant.RoleId)
            .ToDictionary(group => group.Key, group => group.Select(grant => grant.Code).Order().ToList());

        return [.. roles.Select(role => new PlatformRoleResponse(
            role.Id,
            role.Name,
            role.Description,
            byRole.TryGetValue(role.Id, out var codes) ? codes : []))];
    }

    /// <summary>Creates an invited back-office account with one role.</summary>
    public async Task<PlatformUserOutcome> CreateAsync(
        CreatePlatformUserRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!AgencyLifecycleService.IsUsableReason(request.Reason))
        {
            return new PlatformUserOutcome.ReasonRequired();
        }

        if (string.IsNullOrWhiteSpace(request.Email) || !request.Email.Contains('@', StringComparison.Ordinal))
        {
            return new PlatformUserOutcome.Invalid("email", "That is not an email address.");
        }

        if (string.IsNullOrWhiteSpace(request.FirstName) || string.IsNullOrWhiteSpace(request.LastName))
        {
            return new PlatformUserOutcome.Invalid("firstName", "A back-office account needs a real name on it.");
        }

        using var scope = _platformScope.Enter("Admin console — creates a Trips back-office account");

        var role = await PlatformRoleAsync(request.RoleName, cancellationToken);

        if (role is null)
        {
            return new PlatformUserOutcome.Invalid(
                "roleName",
                $"Choose one of: {string.Join(", ", Role.SystemRoles.Platform)}.");
        }

        var email = request.Email.Trim();

        // Checked before inserting for the message, not for the safety: the email column is citext
        // with a unique index, and two concurrent creations would race past this check into it.
        if (await _db.Users.AnyAsync(user => user.Email == email, cancellationToken))
        {
            return new PlatformUserOutcome.EmailTaken();
        }

        // Invited, not Active: they cannot sign in until they have set a password of their own.
        var user = User.ForPlatform(
            email,
            PlaceholderPasswordHash(),
            request.FirstName.Trim(),
            request.LastName.Trim(),
            UserStatus.Invited);

        _db.Users.Add(user);
        _db.UserRoles.Add(UserRole.GrantPlatform(user.Id, role.Id));

        _audit.SetReason($"{Actions.Created}: {request.Reason.Trim()}");
        await _db.SaveChangesAsync(cancellationToken);

        LogCreated(_logger, user.Id, role.Name, _audit.ActorUserId);

        return new PlatformUserOutcome.Done(
            Describe(user, await GrantsForAsync([user.Id], cancellationToken)));
    }

    /// <summary>Moves a back-office account onto a different role, replacing what it held.</summary>
    public async Task<PlatformUserOutcome> ChangeRoleAsync(
        Guid userId,
        ChangePlatformUserRoleRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!AgencyLifecycleService.IsUsableReason(request.Reason))
        {
            return new PlatformUserOutcome.ReasonRequired();
        }

        using var scope = _platformScope.Enter("Admin console — changes a back-office account's role");

        var user = await PlatformUserAsync(userId, cancellationToken);

        if (user is null)
        {
            return new PlatformUserOutcome.NotFound();
        }

        var role = await PlatformRoleAsync(request.RoleName, cancellationToken);

        if (role is null)
        {
            return new PlatformUserOutcome.Invalid(
                "roleName",
                $"Choose one of: {string.Join(", ", Role.SystemRoles.Platform)}.");
        }

        var held = await _db.UserRoles
            .Where(grant => grant.UserId == userId && grant.AgencyId == null)
            .ToListAsync(cancellationToken);

        if (held.Count == 1 && held[0].RoleId == role.Id)
        {
            return new PlatformUserOutcome.Invalid("roleName", $"They already hold {role.Name}.");
        }

        // Replace rather than add. A role is what somebody is, not a badge they collect, and two
        // platform roles at once means a permission is held for a reason nobody can name.
        _db.UserRoles.RemoveRange(held);
        _db.UserRoles.Add(UserRole.GrantPlatform(userId, role.Id));

        _audit.SetReason($"{Actions.RoleChanged}: {request.Reason.Trim()}");
        await _db.SaveChangesAsync(cancellationToken);

        LogRoleChanged(_logger, userId, role.Name, _audit.ActorUserId);

        return new PlatformUserOutcome.Done(
            Describe(user, await GrantsForAsync([userId], cancellationToken)));
    }

    /// <summary>Suspends or reactivates a back-office account.</summary>
    /// <remarks>
    /// Deactivated is deliberately not offered: their past actions still need an actor, and the
    /// audit viewer reads the name off this row. Suspension already stops them signing in.
    /// </remarks>
    public async Task<PlatformUserOutcome> ChangeStatusAsync(
        Guid userId,
        ChangePlatformUserStatusRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!AgencyLifecycleService.IsUsableReason(request.Reason))
        {
            return new PlatformUserOutcome.ReasonRequired();
        }

        if (!Enum.TryParse<UserStatus>(request.Status, ignoreCase: true, out var status)
            || status is not (UserStatus.Active or UserStatus.Suspended))
        {
            return new PlatformUserOutcome.Invalid("status", "Choose Active or Suspended.");
        }

        using var scope = _platformScope.Enter("Admin console — suspends or reactivates a back-office account");

        var user = await PlatformUserAsync(userId, cancellationToken);

        if (user is null)
        {
            return new PlatformUserOutcome.NotFound();
        }

        if (userId == _audit.ActorUserId && status == UserStatus.Suspended)
        {
            // Nothing technical stops it; it is just always a mistake, and the person who did it
            // is the one who can no longer sign in to undo it.
            return new PlatformUserOutcome.Invalid("status", "You cannot suspend your own account.");
        }

        if (status == UserStatus.Suspended)
        {
            user.Suspend();
        }
        else
        {
            user.Reactivate();
        }

        _audit.SetReason($"{Actions.StatusChanged}: {request.Reason.Trim()}");
        await _db.SaveChangesAsync(cancellationToken);

        return new PlatformUserOutcome.Done(
            Describe(user, await GrantsForAsync([userId], cancellationToken)));
    }

    /// <summary>
    /// A hash that matches nothing.
    /// </summary>
    /// <remarks>
    /// The column is required and the type never sees a plaintext password, so an invited account
    /// needs something in it. This is deliberately not a hash of any password: Argon2id output
    /// never looks like this, so no input can verify against it, and the account cannot be signed
    /// into until the reset flow replaces it.
    /// </remarks>
    private static string PlaceholderPasswordHash() => $"invited${Guid.CreateVersion7():N}";

    private Task<User?> PlatformUserAsync(Guid userId, CancellationToken cancellationToken) =>
        _db.Users.FirstOrDefaultAsync(user => user.Id == userId && user.AgencyId == null, cancellationToken);

    private async Task<Role?> PlatformRoleAsync(string? name, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(name) || !Role.SystemRoles.Platform.Contains(name.Trim()))
        {
            return null;
        }

        var wanted = name.Trim();

        return await _db.Roles.FirstOrDefaultAsync(
            role => role.Name == wanted && role.Scope == RoleScope.Platform && role.AgencyId == null,
            cancellationToken);
    }

    /// <summary>Each user's platform roles and the permissions behind them, in one pair of queries.</summary>
    private async Task<Dictionary<Guid, (List<string> Roles, List<string> Permissions)>> GrantsForAsync(
        IReadOnlyList<Guid> userIds,
        CancellationToken cancellationToken)
    {
        var held = await (
            from grant in _db.UserRoles.AsNoTracking()
            join role in _db.Roles.AsNoTracking() on grant.RoleId equals role.Id
            where userIds.Contains(grant.UserId) && grant.AgencyId == null
            select new { grant.UserId, grant.RoleId, role.Name })
            .ToListAsync(cancellationToken);

        var roleIds = held.Select(grant => grant.RoleId).Distinct().ToList();

        var permissions = await (
            from grant in _db.RolePermissions.AsNoTracking()
            join permission in _db.Permissions.AsNoTracking() on grant.PermissionId equals permission.Id
            where roleIds.Contains(grant.RoleId)
            select new { grant.RoleId, permission.Code })
            .ToListAsync(cancellationToken);

        var codesByRole = permissions
            .GroupBy(grant => grant.RoleId)
            .ToDictionary(group => group.Key, group => group.Select(grant => grant.Code).ToList());

        return held
            .GroupBy(grant => grant.UserId)
            .ToDictionary(
                group => group.Key,
                group => (
                    Roles: group.Select(grant => grant.Name).Distinct().Order().ToList(),
                    Permissions: group
                        .SelectMany(grant => codesByRole.TryGetValue(grant.RoleId, out var codes) ? codes : [])
                        .Distinct()
                        .Order()
                        .ToList()));
    }

    private static PlatformUserResponse Describe(
        User user,
        Dictionary<Guid, (List<string> Roles, List<string> Permissions)> grants)
    {
        var held = grants.TryGetValue(user.Id, out var found) ? found : ([], []);

        return new PlatformUserResponse(
            user.Id,
            user.Email,
            user.FirstName,
            user.LastName,
            user.Status.ToString(),
            held.Roles,
            held.Permissions,
            user.LastLoginAt,
            user.CreatedAt);
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Back-office account {UserId} created as {RoleName} by {ActorUserId}.")]
    private static partial void LogCreated(ILogger logger, Guid userId, string roleName, Guid? actorUserId);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Back-office account {UserId} moved to {RoleName} by {ActorUserId}.")]
    private static partial void LogRoleChanged(ILogger logger, Guid userId, string roleName, Guid? actorUserId);
}
