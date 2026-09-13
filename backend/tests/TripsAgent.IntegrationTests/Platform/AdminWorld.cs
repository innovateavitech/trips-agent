using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using TripsAgent.Application.Auditing;
using TripsAgent.Application.Platform;
using TripsAgent.Application.Tenancy;
using TripsAgent.Domain.Auditing;
using TripsAgent.Domain.Identity;
using TripsAgent.Domain.Payments;
using TripsAgent.Domain.Tenancy;
using TripsAgent.Infrastructure.Auditing;
using TripsAgent.Infrastructure.Persistence;
using TripsAgent.Infrastructure.Tenancy;
using TripsAgent.IntegrationTests.Identity;
using TripsAgent.IntegrationTests.Persistence;

namespace TripsAgent.IntegrationTests.Platform;

/// <summary>
/// A migrated, seeded database with the back-office services pointed at it.
/// </summary>
/// <remarks>
/// Shared by every test in this folder because they all need the same three things: a platform
/// scope, an audit context with a named actor, and a few agencies in different states. Building
/// it once keeps each test about the behaviour it is checking.
/// </remarks>
internal sealed class AdminWorld : IAsyncDisposable
{
    private readonly PostgresFixture _postgres;
    private readonly string _database;

    private AdminWorld(
        AppDbContext db,
        (TenantContext Tenant, PlatformScope Scope) tenancy,
        ManualClock clock,
        AuditContext audit,
        PostgresFixture postgres,
        string database)
    {
        Db = db;
        Tenancy = tenancy;
        Clock = clock;
        Audit = audit;
        _postgres = postgres;
        _database = database;

        Directory = new AgencyDirectoryService(db, tenancy.Scope);
        Lifecycle = new AgencyLifecycleService(
            db, tenancy.Scope, audit, clock, NullLogger<AgencyLifecycleService>.Instance);
        Exports = new AgencyExportService(db, tenancy.Scope, audit, clock);
        Storefront = new StorefrontAvailability(db, tenancy.Scope);
        Dashboard = new OperationsDashboardService(db, tenancy.Scope, clock);
        AuditLog = new AuditLogQueryService(db, tenancy.Scope);
        PlatformUsers = new PlatformUserService(
            db, tenancy.Scope, audit, clock, NullLogger<PlatformUserService>.Instance);
    }

    public AppDbContext Db { get; }

    public (TenantContext Tenant, PlatformScope Scope) Tenancy { get; }

    public ManualClock Clock { get; }

    public AuditContext Audit { get; }

    public AgencyDirectoryService Directory { get; }

    public AgencyLifecycleService Lifecycle { get; }

    public AgencyExportService Exports { get; }

    public StorefrontAvailability Storefront { get; }

    public OperationsDashboardService Dashboard { get; }

    public AuditLogQueryService AuditLog { get; }

    public PlatformUserService PlatformUsers { get; }

    /// <summary>The Trips admin every action in these tests is attributed to.</summary>
    public Guid AdminUserId { get; } = Guid.CreateVersion7();

    public static async Task<AdminWorld> CreateAsync(
        PostgresFixture postgres,
        [CallerMemberName] string name = "")
    {
        ArgumentNullException.ThrowIfNull(postgres);

        // Real time, not a fixed date: the audit log is partitioned by month, and the migration
        // creates partitions around today. Tests move the clock relatively.
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var tenancy = TestTenancy.None();

        await using (var setup = await postgres.CreateEmptyDatabaseAsync(name, tenancy.Tenant, tenancy.Scope, clock))
        {
            await setup.Database.MigrateAsync();
            await ReferenceDataSeeder.EnsureAsync(setup, tenancy.Scope);
        }

        var audit = new AuditContext();
        var db = postgres.Connect(name, tenancy.Tenant, tenancy.Scope, clock, audit);

        var world = new AdminWorld(db, tenancy, clock, audit, postgres, name);
        audit.ActorUserId = world.AdminUserId;
        audit.ActorType = AuditActorType.PlatformAdmin;
        audit.ActorIpAddress = "198.51.100.7";

        return world;
    }

    /// <summary>Adds an agency in whatever state the test needs, and returns its id.</summary>
    public async Task<Guid> AddAgencyAsync(
        string legalName,
        string slug,
        AgencyStatus status = AgencyStatus.Verified,
        string? tradingName = null)
    {
        using var _ = Tenancy.Scope.Enter("test setup — creates an agency to administer");

        var agency = Agency.RegisterPrincipal(legalName, slug, "NG", "NGN", "Africa/Lagos", tradingName);
        var now = Clock.GetUtcNow();

        switch (status)
        {
            case AgencyStatus.Verified:
                agency.MarkVerified(now);
                break;
            case AgencyStatus.Rejected:
                agency.MarkRejected();
                break;
            case AgencyStatus.Suspended:
                agency.MarkVerified(now);
                agency.Suspend("Set up by a test.", now);
                break;
            case AgencyStatus.Terminated:
                agency.MarkVerified(now);
                agency.Terminate("Set up by a test.", now);
                break;
            default:
                break;
        }

        Db.Agencies.Add(agency);
        Db.Users.Add(User.ForAgency(agency.Id, $"owner@{slug}.test", "argon2id$hash", "Owner", legalName));

        if (status is AgencyStatus.Verified or AgencyStatus.Suspended)
        {
            Db.Wallets.Add(Wallet.OpenFor(agency.Id, "NGN"));
        }

        await Db.SaveChangesAsync();

        // Nothing written here is an admin action, so it must not be mistaken for one later.
        Audit.SetReason(null);

        return agency.Id;
    }

    /// <summary>
    /// A second context acting as one agency, for the "can they see it?" half of a test.
    /// </summary>
    /// <remarks>
    /// The audit context carries the agency too, because that is what the API's middleware does
    /// for a signed-in agency user — and the audit log's own query filter reads it rather than the
    /// tenant. A test that left it null would be testing a request shape that never happens.
    /// </remarks>
    public AppDbContext ActingAs(Guid agencyId)
    {
        var tenancy = TestTenancy.For(agencyId);
        var audit = new AuditContext { AgencyId = agencyId, ActorType = AuditActorType.User };

        return _postgres.Connect(_database, tenancy.Tenant, tenancy.Scope, Clock, audit);
    }

    public ValueTask DisposeAsync() => Db.DisposeAsync();
}
