using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using TripsAgent.Application.Auditing;
using TripsAgent.Application.Persistence;
using TripsAgent.Application.Tenancy;
using TripsAgent.Domain.Auditing;
using TripsAgent.Domain.Common;
using TripsAgent.Domain.Documents;
using TripsAgent.Domain.Identity;
using TripsAgent.Domain.Notifications;
using TripsAgent.Domain.Payments;
using TripsAgent.Domain.Platform;
using TripsAgent.Domain.Pricing;
using TripsAgent.Domain.Tenancy;
using TripsAgent.Domain.Tenancy.Kyb;
using TripsAgent.Infrastructure.Messaging;
using TripsAgent.Infrastructure.Persistence.Interceptors;

namespace TripsAgent.Infrastructure.Persistence;

/// <summary>
/// The single EF Core context for the platform.
/// </summary>
/// <remarks>
/// <para>
/// One context, many PostgreSQL schemas (<c>identity</c>, <c>tenancy</c>, <c>orders</c>, …).
/// Splitting into a context per bounded context would buy isolation we do not need and cost us
/// the one thing we do need: a single transaction spanning a wallet debit and an order line.
/// </para>
/// <para>
/// Entities are configured with <c>IEntityTypeConfiguration&lt;T&gt;</c> classes under
/// <c>Persistence/Configurations/</c>, never with attributes on the domain type. That keeps
/// <c>TripsAgent.Domain</c> free of any EF Core reference — an architecture test fails the build
/// if that slips.
/// </para>
/// </remarks>
public class AppDbContext : DbContext, IAppDbContext
{
    private readonly TimeProvider _clock;
    private readonly ITenantContext _tenantContext;
    private readonly IPlatformScope _platformScope;
    private readonly IAuditContext? _auditContext;

    /// <param name="options">Provider and connection.</param>
    /// <param name="clock">Source of every timestamp this context stamps.</param>
    /// <param name="tenantContext">The agency every tenant query filter compares against.</param>
    /// <param name="platformScope">The audited, opt-in cross-tenant bypass.</param>
    /// <param name="auditContext">
    /// The current actor, which the audit log's query filter reads. Optional so that tooling and
    /// tests which construct a context by hand keep working; the application always supplies it
    /// through dependency injection. Without one, the context behaves as a platform-wide caller.
    /// </param>
    public AppDbContext(
        DbContextOptions<AppDbContext> options,
        TimeProvider clock,
        ITenantContext tenantContext,
        IPlatformScope platformScope,
        IAuditContext? auditContext = null)
        : base(options)
    {
        _clock = clock;
        _tenantContext = tenantContext;
        _platformScope = platformScope;
        _auditContext = auditContext;
    }

    /// <summary>
    /// The platform audit trail. Append-only — the database rejects updates and deletes — so this
    /// set is for reading and for the interceptor that writes it, and nothing else.
    /// </summary>
    public DbSet<AuditLogEntry> AuditLogs => Set<AuditLogEntry>();

    /// <summary>
    /// The agency the global query filters compare against.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Read by the filter expressions below. It has to be a property on the context rather than a
    /// value captured while the model was built: EF caches one model per context type, so a
    /// captured value would freeze the first request's tenant into every later request's queries.
    /// Reading it through <c>this</c> makes EF treat it as a query parameter and re-evaluate it
    /// every time.
    /// </para>
    /// <para>
    /// <see cref="Guid.Empty"/> when no tenant is resolved, which matches no rows. Returning
    /// nothing is the safe failure; returning everything is the leak.
    /// </para>
    /// </remarks>
    public Guid CurrentAgencyId => _tenantContext.AgencyId ?? Guid.Empty;

    /// <summary>
    /// True while an audited <see cref="IPlatformScope"/> is open, which lifts the filters.
    /// </summary>
    /// <remarks>
    /// Deliberately not <c>IgnoreQueryFilters()</c>. That is a per-query escape hatch with no
    /// record of who used it or why; this one is centralised and logged.
    /// </remarks>
    public bool AllowCrossTenantAccess => _platformScope.IsActive;

    /// <summary>Travel businesses — the tenant every other business row belongs to.</summary>
    public DbSet<Agency> Agencies => Set<Agency>();

    /// <summary>Per-agency operational preferences. One row per agency.</summary>
    public DbSet<AgencySettings> AgencySettings => Set<AgencySettings>();

    /// <summary>Per-agency logo, colours and contact details. One row per agency.</summary>
    public DbSet<AgencyBranding> AgencyBranding => Set<AgencyBranding>();

    /// <summary>People who can sign in — agency staff and Trips back-office staff.</summary>
    public DbSet<User> Users => Set<User>();

    /// <summary>Named bundles of permissions. Null agency means a system role.</summary>
    public DbSet<Role> Roles => Set<Role>();

    /// <summary>The platform-wide permission catalogue. Seeded, never created at runtime.</summary>
    public DbSet<Permission> Permissions => Set<Permission>();

    public DbSet<RolePermission> RolePermissions => Set<RolePermission>();

    public DbSet<UserRole> UserRoles => Set<UserRole>();

    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    public DbSet<PasswordResetToken> PasswordResetTokens => Set<PasswordResetToken>();

    public DbSet<OtpCode> OtpCodes => Set<OtpCode>();

    /// <summary>The audit trail behind the lockout rule.</summary>
    public DbSet<LoginAttempt> LoginAttempts => Set<LoginAttempt>();

    public DbSet<UserInvitation> UserInvitations => Set<UserInvitation>();

    /// <summary>One row per attempt an agency makes at proving it is a real business.</summary>
    public DbSet<KybSubmission> KybSubmissions => Set<KybSubmission>();

    /// <summary>The files attached to those submissions.</summary>
    public DbSet<KybDocument> KybDocuments => Set<KybDocument>();

    /// <summary>Things in the platform that need a person to act. Read across agencies.</summary>
    public DbSet<AdminAlert> AdminAlerts => Set<AdminAlert>();

    /// <summary>The double-entry ledger — the source of truth for money.</summary>
    public DbSet<LedgerAccount> LedgerAccounts => Set<LedgerAccount>();

    public DbSet<LedgerEntry> LedgerEntries => Set<LedgerEntry>();

    /// <summary>Agency balances. A projection of the ledger, never an independent truth.</summary>
    public DbSet<Wallet> Wallets => Set<Wallet>();

    public DbSet<WalletHold> WalletHolds => Set<WalletHold>();

    /// <summary>What the agent's statement renders.</summary>
    public DbSet<WalletTransaction> WalletTransactions => Set<WalletTransaction>();

    /// <summary>Every attempt to take money, successful or not.</summary>
    public DbSet<PaymentTransaction> PaymentTransactions => Set<PaymentTransaction>();

    /// <summary>Gateway deliveries, recorded so each is processed exactly once.</summary>
    public DbSet<PaymentWebhookEvent> PaymentWebhookEvents => Set<PaymentWebhookEvent>();

    /// <summary>Discrepancies the nightly integrity audit found. Not tenant-scoped; see IAppDbContext.</summary>
    public DbSet<ReconciliationException> ReconciliationExceptions => Set<ReconciliationException>();

    public DbSet<DocumentNumberFormat> DocumentNumberFormats => Set<DocumentNumberFormat>();

    public DbSet<GeneratedDocument> GeneratedDocuments => Set<GeneratedDocument>();

    /// <summary>
    /// Read-only in practice: counters are changed only by <c>DocumentNumberAllocator</c>'s locking
    /// upsert, never by loading a row and saving it back. Deliberately absent from IAppDbContext.
    /// </summary>
    public DbSet<DocumentNumberSequence> DocumentNumberSequences => Set<DocumentNumberSequence>();

    /// <inheritdoc />
    public DbSet<MarkupRule> MarkupRules => Set<MarkupRule>();

    /// <inheritdoc />
    public DbSet<PriceQuote> PriceQuotes => Set<PriceQuote>();

    /// <summary>Every notification queued, and what happened to it. Tenant-scoped.</summary>
    public DbSet<Notification> Notifications => Set<Notification>();

    /// <summary>Notification wording, seeded from <c>NotificationTemplateCatalog</c>. Platform-wide.</summary>
    public DbSet<NotificationTemplate> NotificationTemplates => Set<NotificationTemplate>();

    /// <summary>Addresses we no longer send to. Platform-wide.</summary>
    public DbSet<SuppressedEmailAddress> SuppressedEmailAddresses => Set<SuppressedEmailAddress>();

    /// <summary>
    /// The agency whose audit rows the caller may see, or null for a platform-wide caller.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Read by the audit log's own query filter below. It has to be an instance member of the
    /// context for EF to re-read it per instance; a captured local would be baked into the cached
    /// model, and every later request would be filtered by whoever happened to make the first one.
    /// </para>
    /// <para>
    /// Distinct from <see cref="CurrentAgencyId"/>, which drives the tenancy filters: this one is
    /// nullable because a platform-wide caller legitimately sees the platform-wide audit rows,
    /// whereas an unresolved tenant must match nothing at all.
    /// </para>
    /// </remarks>
    private Guid? AuditAgencyId => _auditContext?.AgencyId;

    /// <summary>
    /// Installs the tenant write guard.
    /// </summary>
    /// <remarks>
    /// Added here rather than at registration on purpose. An interceptor wired up in
    /// <c>AddDbContext</c> is only present when whoever composed the options remembered it —
    /// and a test, a background worker or a one-off tool that builds its own
    /// <see cref="DbContextOptions"/> would silently run without the guard. Doing it in the
    /// context means every instance has it, however it was constructed.
    /// </remarks>
    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);

        base.OnConfiguring(optionsBuilder);

        optionsBuilder.AddInterceptors(
            new TenantStampingInterceptor(_tenantContext, _platformScope),

            // Tells PostgreSQL the tenant, so row-level security holds even where a filter is
            // missing (ADR-0006). Structural for the same reason as the stamper: every context
            // gets it, however it was constructed.
            new TenantSessionInterceptor(_tenantContext, _platformScope));
    }

    /// <summary>Messages waiting to be published, and the record of those that were. See <see cref="OutboxMessage"/>.</summary>
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    /// <summary>Which consumer has processed which message. See <see cref="InboxMessage"/>.</summary>
    public DbSet<InboxMessage> InboxMessages => Set<InboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Extensions the schema depends on. EF emits CREATE EXTENSION IF NOT EXISTS for each
        // in the migration, so a fresh database is usable without any manual psql step.
        //   citext — case-insensitive email, so Ada@x.com and ada@x.com cannot both register
        //   ltree  — the agency hierarchy path, GIST-indexed for subtree queries
        modelBuilder.HasPostgresExtension("citext");
        modelBuilder.HasPostgresExtension("ltree");

        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);

        // An agency sees its own audit trail and nothing else. Platform-wide rows carry no
        // agency_id and are visible only to a caller with no agency of their own — a Trips
        // back-office user or a background job.
        //
        // This is the audit log's own filter, not the tenancy mechanism below. AuditLogEntry is
        // deliberately not ITenantScoped: its nullable agency_id and platform-wide rows do not fit
        // the "match my agency exactly" rule. Pointing it at ITenantContext instead of
        // IAuditContext is a behaviour change, so it is left to #21's author — see the PR.
        modelBuilder.Entity<AuditLogEntry>()
            .HasQueryFilter(entry => AuditAgencyId == null || entry.AgencyId == AuditAgencyId);

        ApplyTenantQueryFilters(modelBuilder);
    }

    /// <summary>
    /// Adds a global query filter to every entity that belongs to an agency.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Applied by convention over <see cref="ITenantScoped"/> rather than entity by entity,
    /// because entity-by-entity is a list somebody eventually forgets to add to — and the
    /// consequence of forgetting is one agency reading another's customers and prices.
    /// <c>TenantFilterCoverageTests</c> fails the build if an entity slips through.
    /// </para>
    /// <para>
    /// <see cref="Agency"/> itself is filtered separately: it is not owned by an agency, it
    /// <i>is</i> one, so the predicate is "me and my sub-agents" rather than a column match.
    /// </para>
    /// </remarks>
    private void ApplyTenantQueryFilters(ModelBuilder modelBuilder)
    {
        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            var clrType = entityType.ClrType;

            if (!typeof(ITenantScoped).IsAssignableFrom(clrType))
            {
                continue;
            }

            // Builds:  entity => AllowCrossTenantAccess || entity.AgencyId == CurrentAgencyId
            var entity = Expression.Parameter(clrType, "entity");
            var context = Expression.Constant(this);

            var agencyIdOfRow = Expression.Property(entity, nameof(ITenantScoped.AgencyId));
            var currentAgencyId = Expression.Property(context, nameof(CurrentAgencyId));
            var bypass = Expression.Property(context, nameof(AllowCrossTenantAccess));

            var predicate = Expression.OrElse(
                bypass,
                Expression.Equal(agencyIdOfRow, currentAgencyId));

            modelBuilder.Entity(clrType)
                .HasQueryFilter(Expression.Lambda(predicate, entity));
        }

        // An agency sees itself and, if it is a principal, its own sub-agents. Depth is capped at
        // 2, so "parent is me" is the whole subtree below me.
        modelBuilder.Entity<Agency>().HasQueryFilter(agency =>
            AllowCrossTenantAccess
            || agency.Id == CurrentAgencyId
            || agency.ParentAgencyId == CurrentAgencyId);

        // Users, roles and invitations carry a *nullable* agency — null means Trips platform
        // staff, or a system role shared by everyone — so they cannot implement ITenantScoped,
        // which requires a non-nullable one. Their filters are written out instead.
        //
        // A system role (null agency) is visible to every agency by design: they are the Owner,
        // Manager and Agent roles we ship. A platform *user* is not.
        modelBuilder.Entity<User>().HasQueryFilter(user =>
            AllowCrossTenantAccess || user.AgencyId == CurrentAgencyId);

        modelBuilder.Entity<Role>().HasQueryFilter(role =>
            AllowCrossTenantAccess || role.AgencyId == CurrentAgencyId || role.AgencyId == null);

        modelBuilder.Entity<UserInvitation>().HasQueryFilter(invitation =>
            AllowCrossTenantAccess || invitation.AgencyId == CurrentAgencyId);

        // Credentials are reached through their user, so they follow that user's agency. Written
        // as a subquery rather than a join so the filter composes with any query EF builds.
        modelBuilder.Entity<RefreshToken>().HasQueryFilter(token =>
            AllowCrossTenantAccess
            || Users.Any(user => user.Id == token.UserId && user.AgencyId == CurrentAgencyId));

        modelBuilder.Entity<PasswordResetToken>().HasQueryFilter(token =>
            AllowCrossTenantAccess
            || Users.Any(user => user.Id == token.UserId && user.AgencyId == CurrentAgencyId));

        // Permissions are a platform-wide catalogue with no owner, and login attempts are
        // deliberately unfiltered: the ones worth investigating are against addresses that match
        // no account, so there is no agency to attribute them to. Both are read only by
        // platform tooling and by the sign-in path itself, never by an agency-facing query.
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        base.ConfigureConventions(configurationBuilder);

        // Money is a long in the database, always. See MoneyConverter for why.
        Conventions.MoneyConventions.Apply(configurationBuilder);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        CaptureDomainEvents();
        StampTimestamps();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        CaptureDomainEvents();
        StampTimestamps();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    /// <summary>
    /// Moves every domain event raised since the last save into <c>platform.outbox_messages</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the transactional outbox, in one method. The events become rows in the same change
    /// tracker as the business change, so the single save that follows writes both in one database
    /// transaction. There is no moment at which the order is saved but its <c>OrderPlaced</c> is not,
    /// or the other way round. The Worker publishes the rows afterwards — see
    /// <see cref="OutboxDispatcher"/> and docs/adr/0005-own-the-transactional-outbox.md.
    /// </para>
    /// <para>
    /// Events are pulled off the aggregate as they are captured, so if this save fails and the caller
    /// saves again on the same context, the already-captured rows are retried with it — not doubled.
    /// </para>
    /// </remarks>
    private void CaptureDomainEvents()
    {
        // Materialised first: adding outbox rows while walking the change tracker would change the
        // collection mid-enumeration.
        var sources = ChangeTracker.Entries<IHasDomainEvents>()
            .Select(entry => entry.Entity)
            .ToList();

        if (sources.Count == 0)
        {
            return;
        }

        var now = _clock.GetUtcNow();

        foreach (var source in sources)
        {
            var agencyId = (source as ITenantScoped)?.AgencyId;

            foreach (var domainEvent in source.PullDomainEvents())
            {
                OutboxMessages.Add(OutboxMessage.Create(domainEvent, agencyId, now));
            }
        }
    }

    /// <summary>
    /// Sets <c>CreatedAt</c>/<c>UpdatedAt</c> on every touched auditable entity.
    /// </summary>
    /// <remarks>
    /// Done here rather than in each handler so it cannot be forgotten. The time comes from an
    /// injected <see cref="TimeProvider"/>, not <c>DateTimeOffset.UtcNow</c>, so a test can pin
    /// the clock and assert on expiry windows — OTP codes, ticket time limits, refresh tokens —
    /// without sleeping.
    /// </remarks>
    private void StampTimestamps()
    {
        var now = _clock.GetUtcNow();

        foreach (EntityEntry<IAuditableEntity> entry in ChangeTracker.Entries<IAuditableEntity>())
        {
            switch (entry.State)
            {
                case EntityState.Added:
                    entry.Entity.CreatedAt = now;
                    entry.Entity.UpdatedAt = now;
                    break;

                case EntityState.Modified:
                    entry.Entity.UpdatedAt = now;

                    // Guard against a detached-then-attached entity rewriting its own birthday.
                    entry.Property(e => e.CreatedAt).IsModified = false;
                    break;
            }
        }
    }
}
