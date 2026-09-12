using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using TripsAgent.Application.Auditing;
using TripsAgent.Application.Tenancy;
using TripsAgent.Application.Billing;
using TripsAgent.Application.Notifications;
using TripsAgent.Application.Payments;
using TripsAgent.Application.Persistence;
using TripsAgent.Domain.Auditing;
using TripsAgent.Domain.Billing;
using TripsAgent.Domain.Common;
using TripsAgent.Domain.Identity;
using TripsAgent.Domain.Tenancy;
using TripsAgent.Infrastructure.Auditing;
using TripsAgent.Infrastructure.Billing;
using TripsAgent.Infrastructure.Messaging;
using TripsAgent.Infrastructure.Persistence;
using TripsAgent.Infrastructure.Tenancy;
using TripsAgent.IntegrationTests.Identity;
using TripsAgent.IntegrationTests.Persistence;

namespace TripsAgent.IntegrationTests.Billing;

/// <summary>
/// A migrated, seeded database with the billing services pointed at it, and a gateway that does
/// whatever the test tells it to.
/// </summary>
/// <remarks>
/// The gateway is the only fake. Everything else — the entitlement resolver, the tier service, the
/// billing run, the invoice numbering — is the real thing against a real PostgreSQL, so the
/// database's own constraints, triggers and row-level security policies are part of every test here.
/// That is the point: the rules that matter most in this module are the ones the database enforces.
/// </remarks>
internal sealed class BillingWorld : IAsyncDisposable
{
    private readonly PostgresFixture _postgres;
    private readonly string _database;

    private BillingWorld(
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

        Gateway = new ScriptedGateway();
        Numbers = new SubscriptionNumberAllocator(db);
        Entitlements = new EntitlementService(db, tenancy.Scope, clock);
        Tiers = new TierAdminService(db, tenancy.Scope, audit, clock);
        SubAgents = new SubAgentAllowance(db, Entitlements, tenancy.Scope);
        PlatformFees = new EntitlementPlatformFeePolicy(Entitlements);

        Billing = new SubscriptionBillingRun(
            db,
            tenancy.Scope,
            Entitlements,
            Numbers,
            Gateway,
            new Notifier(db, new EfOutbox(db, clock)),
            audit,
            clock,
            NullLogger<SubscriptionBillingRun>.Instance);
    }

    public AppDbContext Db { get; }

    public (TenantContext Tenant, PlatformScope Scope) Tenancy { get; }

    public ManualClock Clock { get; }

    public AuditContext Audit { get; }

    public ScriptedGateway Gateway { get; }

    public ISubscriptionNumberAllocator Numbers { get; }

    public EntitlementService Entitlements { get; }

    public TierAdminService Tiers { get; }

    public SubAgentAllowance SubAgents { get; }

    public EntitlementPlatformFeePolicy PlatformFees { get; }

    public SubscriptionBillingRun Billing { get; }

    /// <summary>The Trips admin every tier change in these tests is attributed to.</summary>
    public Guid AdminUserId { get; } = Guid.CreateVersion7();

    public static async Task<BillingWorld> CreateAsync(PostgresFixture postgres, [CallerMemberName] string name = "")
    {
        ArgumentNullException.ThrowIfNull(postgres);

        // Real time, not a fixed date: the audit log is partitioned by month and the migration
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

        var world = new BillingWorld(db, tenancy, clock, audit, postgres, name);
        audit.ActorUserId = world.AdminUserId;
        audit.ActorType = AuditActorType.PlatformAdmin;

        return world;
    }

    /// <summary>Adds a verified agency with an owner, and returns its id.</summary>
    public async Task<Guid> AddAgencyAsync(string legalName, string slug)
    {
        using var _ = Tenancy.Scope.Enter("test setup — creates an agency to bill");

        var agency = Agency.RegisterPrincipal(legalName, slug, "NG", "NGN", "Africa/Lagos");
        agency.MarkVerified(Clock.GetUtcNow());

        Db.Agencies.Add(agency);
        Db.Users.Add(User.ForAgency(agency.Id, $"owner@{slug}.test", "argon2id$hash", "Owner", legalName));

        await Db.SaveChangesAsync();
        Audit.SetReason(null);

        return agency.Id;
    }

    /// <summary>Adds a sub-agent beneath <paramref name="principalId"/>.</summary>
    public async Task<Guid> AddSubAgentAsync(Guid principalId, string legalName, string slug)
    {
        using var _ = Tenancy.Scope.Enter("test setup — creates a sub-agent beneath a principal");

        var principal = await Db.Agencies.FirstAsync(agency => agency.Id == principalId);
        var subAgent = Agency.RegisterSubAgent(principal, legalName, slug);
        subAgent.MarkVerified(Clock.GetUtcNow());

        Db.Agencies.Add(subAgent);
        await Db.SaveChangesAsync();
        Audit.SetReason(null);

        return subAgent.Id;
    }

    /// <summary>
    /// Creates a published tier, priced, granting whatever the test needs.
    /// </summary>
    /// <remarks>
    /// Goes through <see cref="TierAdminService"/> rather than inserting rows, so a test that
    /// happens to pass only because it bypassed the service's own rules cannot.
    /// </remarks>
    public async Task<Guid> AddPublishedTierAsync(
        string code,
        string name,
        long amountMinor,
        IReadOnlyList<EntitlementGrant>? grants = null,
        int trialDays = 0,
        bool isFallback = false)
    {
        const string Reason = "Set up by an integration test.";

        var created = await Tiers.CreateAsync(
            new TierDraft(code, name, null, trialDays, 0, isFallback), Reason);

        var tierId = ((TierChangeOutcome.Saved)created).Tier.Id;

        if (amountMinor > 0)
        {
            await Tiers.SetPriceAsync(tierId, "NGN", BillingInterval.Monthly, amountMinor, Reason);
        }

        if (grants is { Count: > 0 })
        {
            await Tiers.SetEntitlementsAsync(tierId, grants, Reason);
        }

        await Tiers.PublishAsync(tierId, Reason);

        return tierId;
    }

    /// <summary>Puts <paramref name="agencyId"/> on <paramref name="tierId"/>, already paying.</summary>
    public async Task<Subscription> SubscribeAsync(Guid agencyId, Guid tierId, DateTimeOffset? startedAt = null)
    {
        using var _ = Tenancy.Scope.Enter("test setup — subscribes an agency to a tier");

        var now = startedAt ?? Clock.GetUtcNow();

        var tier = await Db.SubscriptionTiers
            .Include(candidate => candidate.Prices)
            .FirstAsync(candidate => candidate.Id == tierId);

        var subscription = Subscription.Start(agencyId, tier, tier.PriceAt("NGN", BillingInterval.Monthly, now), "NGN", now);

        Db.Subscriptions.Add(subscription);
        await Db.SaveChangesAsync();
        Audit.SetReason(null);

        Entitlements.Forget(agencyId);

        return subscription;
    }

    /// <summary>Gives <paramref name="agencyId"/> a card on file, so a renewal has something to charge.</summary>
    public async Task AddCardAsync(Guid agencyId)
    {
        using var _ = Tenancy.Scope.Enter("test setup — stores a reusable gateway authorisation");

        Db.PaymentAuthorizations.Add(PaymentAuthorization.Capture(
            agencyId, "paystack", $"AUTH_{agencyId:N}", "visa", "4242", "12", "2030", "Test Bank",
            Clock.GetUtcNow()));

        await Db.SaveChangesAsync();
        Audit.SetReason(null);
    }

    /// <summary>A second context acting as one agency, for the "can they see it?" half of a test.</summary>
    public AppDbContext ActingAs(Guid agencyId)
    {
        var tenancy = TestTenancy.For(agencyId);
        var audit = new AuditContext { AgencyId = agencyId, ActorType = AuditActorType.User };

        return _postgres.Connect(_database, tenancy.Tenant, tenancy.Scope, Clock, audit);
    }

    /// <summary>The agency's live subscription, reloaded.</summary>
    public async Task<Subscription?> SubscriptionOfAsync(Guid agencyId)
    {
        using var _ = Tenancy.Scope.Enter("test assertion — reads one agency's subscription");

        Db.ChangeTracker.Clear();

        return await Db.Subscriptions
            .Where(subscription => subscription.AgencyId == agencyId)
            .OrderByDescending(subscription => subscription.CurrentPeriodStart)
            .FirstOrDefaultAsync();
    }

    /// <summary>The agency's invoices, newest first, with their lines.</summary>
    public async Task<List<SubscriptionInvoice>> InvoicesOfAsync(Guid agencyId)
    {
        using var _ = Tenancy.Scope.Enter("test assertion — reads one agency's subscription invoices");

        Db.ChangeTracker.Clear();

        return await Db.SubscriptionInvoices
            .Include(invoice => invoice.Lines)
            .Where(invoice => invoice.AgencyId == agencyId)
            .OrderByDescending(invoice => invoice.IssuedAt)
            .ToListAsync();
    }

    /// <summary>Every charge attempt against one agency's invoices, oldest first.</summary>
    public async Task<List<SubscriptionChargeAttempt>> AttemptsOfAsync(Guid agencyId)
    {
        using var _ = Tenancy.Scope.Enter("test assertion — reads one agency's charge attempts");

        Db.ChangeTracker.Clear();

        return await Db.SubscriptionChargeAttempts
            .Where(attempt => attempt.AgencyId == agencyId)
            .OrderBy(attempt => attempt.AttemptedAt)
            .ThenBy(attempt => attempt.AttemptNumber)
            .ToListAsync();
    }

    /// <summary>The next subscription invoice number, for a test about the numbering itself.</summary>
    public Task<string> NumbersNextInvoiceAsync() => Numbers.NextInvoiceNumberAsync(Clock.GetUtcNow());

    public ValueTask DisposeAsync() => Db.DisposeAsync();
}

/// <summary>
/// A recurring-charge gateway that answers however the test says, and counts what it was asked.
/// </summary>
/// <remarks>
/// Deliberately not a mocking library. What these tests care about is how many times a card was
/// charged and with what reference, and a hand-written stub that records both reads better in the
/// assertion than a set-up expectation does.
/// </remarks>
internal sealed class ScriptedGateway : IRecurringChargeGateway, IPaymentGateway
{
    private readonly Queue<Func<string, GatewayVerification>> _answers = new();

    public string Name => "paystack";

    /// <summary>Every reference this gateway was asked to charge, in order.</summary>
    public List<string> Charges { get; } = [];

    /// <summary>What to answer when the script runs out. Defaults to a decline.</summary>
    public Func<string, GatewayVerification> Default { get; set; } = Decline("The bank declined the payment.");

    public void WillSucceed(long amountMinor = 0) => _answers.Enqueue(Succeed(amountMinor));

    public void WillDecline(string reason = "The bank declined the payment.") => _answers.Enqueue(Decline(reason));

    public void WillBeUnreachable() =>
        _answers.Enqueue(_ => throw new PaymentGatewayUnavailableException("Paystack could not be reached."));

    public Task<GatewayVerification> ChargeAsync(
        string reference,
        Money amount,
        string currency,
        string customerEmail,
        string authorizationCode,
        CancellationToken cancellationToken = default)
    {
        Charges.Add(reference);

        var answer = _answers.Count > 0 ? _answers.Dequeue() : Default;
        var verification = answer(reference);

        return Task.FromResult(verification.Succeeded && verification.AmountMinor.IsZero
            ? verification with { AmountMinor = amount, Currency = currency }
            : verification with { Currency = verification.Currency is { Length: 3 } ? verification.Currency : currency });
    }

    public Task<GatewayInitialization> InitializeAsync(
        string reference,
        Money amount,
        string currency,
        string customerEmail,
        string callbackUrl,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new GatewayInitialization($"https://checkout.test/{reference}", $"GW_{reference}"));

    public Task<GatewayVerification> VerifyAsync(string reference, CancellationToken cancellationToken = default)
    {
        var answer = _answers.Count > 0 ? _answers.Dequeue() : Default;

        return Task.FromResult(answer(reference));
    }

    public bool IsValidSignature(string payload, string? signature) => true;

    private static Func<string, GatewayVerification> Succeed(long amountMinor) =>
        reference => new GatewayVerification(
            GatewayPaymentOutcome.Succeeded, "success", new Money(amountMinor), Money.Zero, "NGN", $"GW_{reference}", null)
        {
            Authorization = new GatewayAuthorization("AUTH_TEST", true, "visa", "4242", "12", "2030", "Test Bank"),
        };

    private static Func<string, GatewayVerification> Decline(string reason) =>
        reference => new GatewayVerification(
            GatewayPaymentOutcome.Failed, "failed", Money.Zero, Money.Zero, "NGN", $"GW_{reference}", reason);
}
