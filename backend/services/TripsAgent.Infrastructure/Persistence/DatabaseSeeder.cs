using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TripsAgent.Application.Identity;
using TripsAgent.Application.Tenancy;
using TripsAgent.Domain.Identity;
using TripsAgent.Domain.Tenancy;

namespace TripsAgent.Infrastructure.Persistence;

/// <summary>
/// Loads the development dataset. Invoked by <c>dotnet run -- seed</c>.
/// </summary>
/// <remarks>
/// <para>
/// The point is that a developer who has just cloned the repository can sign in and see a
/// populated console, rather than an empty one they cannot tell apart from a broken one.
/// </para>
/// <para>
/// Every insert is keyed on a stable slug and skipped when the row already exists, so running
/// this twice is harmless. It refuses to run against a database that is missing migrations,
/// because a half-migrated schema produces confusing errors rather than useful ones.
/// </para>
/// </remarks>
public static partial class DatabaseSeeder
{
    /// <summary>The argument that triggers a seed run instead of serving traffic.</summary>
    public const string CommandName = "seed";

    /// <summary>Slug of the demo principal agency.</summary>
    public const string PrincipalSlug = "lagos-travel";

    /// <summary>Slug of the demo sub-agent, managed by the principal.</summary>
    public const string SubAgentSlug = "ikeja-branch";

    /// <summary>True when the process was started to seed rather than to serve.</summary>
    public static bool IsSeedCommand(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        return args.Contains(CommandName, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Seeds the development dataset. Returns a process exit code: 0 on success, 1 on failure.
    /// </summary>
    public static async Task<int> RunAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(services);

        await using var scope = services.CreateAsyncScope();

        var logger = scope.ServiceProvider
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger(typeof(DatabaseSeeder));

        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var platformScope = scope.ServiceProvider.GetRequiredService<IPlatformScope>();

        try
        {
            var pending = (await dbContext.Database.GetPendingMigrationsAsync(cancellationToken)).ToArray();

            if (pending.Length > 0)
            {
                LogPendingMigrations(logger, pending.Length);
                return 1;
            }

            var passwordHasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();

            var created = await SeedAsync(dbContext, platformScope, passwordHasher, cancellationToken);

            if (created == 0)
            {
                LogNothingToDo(logger);
            }
            else
            {
                LogSeeded(logger, created);
            }

            return 0;
        }
        catch (Exception ex)
        {
            LogFailed(logger, ex);
            return 1;
        }
    }

    /// <summary>
    /// Inserts the demo agencies if they are not already there. Returns how many were created.
    /// </summary>
    /// <remarks>
    /// Exposed separately from <see cref="RunAsync"/> so integration tests can seed a database
    /// without building a service provider first.
    /// </remarks>
    public static async Task<int> SeedAsync(
        AppDbContext dbContext,
        IPlatformScope platformScope,
        IPasswordHasher passwordHasher,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(platformScope);
        ArgumentNullException.ThrowIfNull(passwordHasher);

        // Seeding is platform work: it creates rows for several agencies, and its "have I run
        // already?" check has to see across all of them. Without the scope the tenant filter
        // hides the rows it just wrote and the seeder would insert duplicates on every run.
        using var _ = platformScope.Enter("database seeding — writes and verifies rows across agencies");

        // Permissions and system roles are platform data, not sample data: they belong in every
        // environment, and they are seeded even when the demo agencies already exist.
        var permissions = await SeedPermissionsAsync(dbContext, cancellationToken);
        var roles = await SeedSystemRolesAsync(dbContext, permissions, cancellationToken);

        if (await dbContext.Agencies.AnyAsync(a => a.Slug == PrincipalSlug, cancellationToken))
        {
            return 0;
        }

        // A principal with one sub-agent beneath it: the smallest dataset that exercises the
        // hierarchy, so a subtree query has something to return. Plus a third, unverified agency
        // so the KYB review queue has something waiting in it.
        var principal = Agency.RegisterPrincipal(
            legalName: "Lagos Travel Services Limited",
            slug: PrincipalSlug,
            countryCode: "NG",
            baseCurrency: "NGN",
            timezone: "Africa/Lagos",
            tradingName: "Lagos Travel");

        var subAgent = Agency.RegisterSubAgent(
            principal,
            legalName: "Ikeja Branch Travel Limited",
            slug: SubAgentSlug,
            tradingName: "Ikeja Branch");

        var pendingAgency = Agency.RegisterPrincipal(
            legalName: "Pending Travel Limited",
            slug: IdentitySeedData.PendingAgencySlug,
            countryCode: "NG",
            baseCurrency: "NGN",
            timezone: "Africa/Lagos",
            tradingName: "Pending Travel");

        // The demo principal and its branch are through KYB; the third is deliberately not.
        var now = DateTimeOffset.UtcNow;
        principal.MarkVerified(now);
        subAgent.MarkVerified(now);

        dbContext.Agencies.AddRange(principal, subAgent, pendingAgency);

        foreach (var agency in new[] { principal, subAgent, pendingAgency })
        {
            dbContext.AgencySettings.Add(AgencySettings.CreateDefault(agency));
            dbContext.AgencyBranding.Add(AgencyBranding.CreateDefault(agency));
        }

        SeedUsers(dbContext, passwordHasher, roles, principal, subAgent, pendingAgency, now);

        await dbContext.SaveChangesAsync(cancellationToken);

        return 3;
    }

    /// <summary>
    /// Writes any permission in the catalogue that is not in the database yet.
    /// </summary>
    /// <remarks>
    /// Additive rather than replace-all: a permission that has been removed from the code but is
    /// still granted by somebody's custom role should be revoked deliberately, not vanish under a
    /// deploy and silently widen or narrow what that role can do.
    /// </remarks>
    private static async Task<Dictionary<string, Guid>> SeedPermissionsAsync(
        AppDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var existing = await dbContext.Permissions
            .ToDictionaryAsync(permission => permission.Code, permission => permission.Id, StringComparer.Ordinal, cancellationToken);

        foreach (var (code, category, description) in PermissionCodes.All)
        {
            if (existing.ContainsKey(code))
            {
                continue;
            }

            var permission = Permission.Create(code, category, description);
            dbContext.Permissions.Add(permission);
            existing[code] = permission.Id;
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        return existing;
    }

    /// <summary>Writes the system roles and their permission grants.</summary>
    private static async Task<Dictionary<string, Guid>> SeedSystemRolesAsync(
        AppDbContext dbContext,
        Dictionary<string, Guid> permissions,
        CancellationToken cancellationToken)
    {
        var existing = await dbContext.Roles
            .Where(role => role.AgencyId == null)
            .ToDictionaryAsync(role => role.Name, role => role.Id, StringComparer.Ordinal, cancellationToken);

        foreach (var (name, scope, description, grantedCodes) in IdentitySeedData.SystemRoles)
        {
            if (existing.ContainsKey(name))
            {
                continue;
            }

            var role = Role.CreateSystemRole(name, scope, description);
            dbContext.Roles.Add(role);
            existing[name] = role.Id;

            foreach (var code in grantedCodes)
            {
                dbContext.RolePermissions.Add(RolePermission.Create(role.Id, permissions[code]));
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        return existing;
    }

    /// <summary>
    /// Creates the four accounts the milestone asks for: a Trips super admin, an owner at a
    /// verified agency, an owner at its sub-agent, and an owner at an agency still awaiting KYB.
    /// </summary>
    private static void SeedUsers(
        AppDbContext dbContext,
        IPasswordHasher passwordHasher,
        Dictionary<string, Guid> roles,
        Agency principal,
        Agency subAgent,
        Agency pendingAgency,
        DateTimeOffset now)
    {
        // Hashed once and shared: Argon2id is deliberately slow, and hashing the same development
        // password five times adds a second to every seed run for no benefit.
        var passwordHash = passwordHasher.Hash(IdentitySeedData.DevelopmentPassword);

        var superAdmin = User.ForPlatform(
            IdentitySeedData.SuperAdminEmail, passwordHash, "Ada", "Okonkwo");

        var operationsAdmin = User.ForPlatform(
            IdentitySeedData.OperationsAdminEmail, passwordHash, "Chidi", "Balogun");

        var verifiedAgentOwner = User.ForAgency(
            principal.Id, IdentitySeedData.VerifiedAgentEmail, passwordHash, "Ngozi", "Adeyemi");

        var subAgentOwner = User.ForAgency(
            subAgent.Id, IdentitySeedData.SubAgentEmail, passwordHash, "Emeka", "Nwosu");

        var pendingAgentOwner = User.ForAgency(
            pendingAgency.Id, IdentitySeedData.PendingAgentEmail, passwordHash, "Folake", "Adebayo");

        // Everyone except the pending owner has confirmed their address. Leaving that one
        // unverified is the point: it is the state the onboarding screens have to handle.
        foreach (var user in new[] { superAdmin, operationsAdmin, verifiedAgentOwner, subAgentOwner })
        {
            user.MarkEmailVerified(now);
        }

        dbContext.Users.AddRange(
            superAdmin, operationsAdmin, verifiedAgentOwner, subAgentOwner, pendingAgentOwner);

        // Platform staff hold their role against no agency of their own, so their grant is
        // recorded against the agency they are acting on — here, the demo principal.
        dbContext.UserRoles.AddRange(
            UserRole.Grant(superAdmin.Id, roles[Role.SystemRoles.SuperAdmin], principal.Id),
            UserRole.Grant(operationsAdmin.Id, roles[Role.SystemRoles.OperationsAdmin], principal.Id),
            UserRole.Grant(verifiedAgentOwner.Id, roles[Role.SystemRoles.Owner], principal.Id),
            UserRole.Grant(subAgentOwner.Id, roles[Role.SystemRoles.Owner], subAgent.Id),
            UserRole.Grant(pendingAgentOwner.Id, roles[Role.SystemRoles.Owner], pendingAgency.Id));
    }

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Database has {Count} migration(s) still to apply. Run `dotnet run --project "
                  + "services/TripsAgent.Api -- migrate` first — seeding a half-built schema fails "
                  + "in ways that are hard to read.")]
    private static partial void LogPendingMigrations(ILogger logger, int count);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Seed data is already present; nothing to do.")]
    private static partial void LogNothingToDo(ILogger logger);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Seeded {Count} agencies.")]
    private static partial void LogSeeded(ILogger logger, int count);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Seeding failed. Nothing was committed — the whole seed runs in one transaction.")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}
