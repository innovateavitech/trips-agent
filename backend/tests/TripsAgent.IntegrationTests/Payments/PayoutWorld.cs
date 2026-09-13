using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using TripsAgent.Application.Messaging;
using TripsAgent.Application.Notifications;
using TripsAgent.Application.Payments;
using TripsAgent.Application.Tenancy;
using TripsAgent.Domain.Common;
using TripsAgent.Domain.Identity;
using TripsAgent.Domain.Payments;
using TripsAgent.Domain.Tenancy;
using TripsAgent.Infrastructure.Persistence;
using TripsAgent.IntegrationTests.Identity;
using TripsAgent.IntegrationTests.Persistence;

namespace TripsAgent.IntegrationTests.Payments;

/// <summary>
/// One verified agency with a wallet, a verified bank account past its cooling-off period, an owner,
/// a Finance user, and every payout, dispute and reconciliation service wired to in-process fakes.
/// </summary>
internal sealed class PayoutWorld : IAsyncDisposable
{
    private readonly PostgresFixture _postgres;
    private readonly List<AppDbContext> _extra = [];

    private PayoutWorld(PostgresFixture postgres, string name, ManualClock clock)
    {
        _postgres = postgres;
        Name = name;
        Clock = clock;
    }

    public string Name { get; }

    public ManualClock Clock { get; }

    public AppDbContext Db { get; private set; } = null!;

    public (TenantContext Tenant, Infrastructure.Tenancy.PlatformScope Scope) Tenancy { get; private set; }

    public Guid AgencyId { get; private set; }

    public Guid OwnerId { get; private set; }

    public Guid FinanceUserId { get; private set; }

    public Guid BankAccountId { get; private set; }

    public FakeBankTransfers Transfers { get; } = new();

    public FakeBackOffice BackOffice { get; } = new();

    public List<PlatformAlert> Alerts { get; } = [];

    public static async Task<PayoutWorld> CreateAsync(PostgresFixture postgres, string testName)
    {
        var name = "po_" + testName.ToLowerInvariant()[..Math.Min(testName.Length, 55)];
        var world = new PayoutWorld(postgres, name, new ManualClock(new DateTimeOffset(2026, 9, 10, 9, 0, 0, TimeSpan.Zero)));
        await world.SeedAsync();
        return world;
    }

    private async Task SeedAsync()
    {
        var setupTenancy = TestTenancy.None();
        var setup = await _postgres.CreateEmptyDatabaseAsync(Name, setupTenancy.Tenant, setupTenancy.Scope, Clock);
        await setup.Database.MigrateAsync();

        using (setupTenancy.Scope.Enter("test setup — an agency, its wallet, users and a bank account"))
        {
            var agency = Agency.RegisterPrincipal("Lagos Travel Limited", "lagos-travel", "NG", "NGN", "Africa/Lagos");
            agency.MarkVerified(Clock.GetUtcNow());
            setup.Agencies.Add(agency);
            setup.Wallets.Add(Wallet.OpenFor(agency.Id, "NGN"));
            setup.LedgerAccounts.Add(LedgerAccount.ForAgency(agency.Id, LedgerAccountType.AgencyWallet, "NGN", "Wallet"));

            var owner = User.ForAgency(agency.Id, $"owner-{Guid.NewGuid():N}@lagostravel.example.com", "hash", "Ada", "Obi");
            var finance = User.ForPlatform($"finance-{Guid.NewGuid():N}@tripsagent.example.com", "hash", "Fola", "Finance");
            setup.Users.AddRange(owner, finance);

            var account = AgencyBankAccount.Capture(agency.Id, "058", "GTBank", "0123456789", "Lagos Travel", "NGN", owner.Id);
            account.MarkVerified("LAGOS TRAVEL LIMITED", "RCP_test", Clock.GetUtcNow().AddDays(-2));
            account.MakeDefault();
            setup.AgencyBankAccounts.Add(account);

            await setup.SaveChangesAsync();

            AgencyId = agency.Id;
            OwnerId = owner.Id;
            FinanceUserId = finance.Id;
            BankAccountId = account.Id;
        }

        await setup.DisposeAsync();

        Tenancy = TenancyFor(AgencyId, OwnerId);
        Db = NewContext(Tenancy.Tenant, Tenancy.Scope);
    }

    /// <summary>A tenant acting as one agency user, with its own platform scope.</summary>
    public static (TenantContext Tenant, Infrastructure.Tenancy.PlatformScope Scope) TenancyFor(Guid agencyId, Guid userId)
    {
        var tenant = new TenantContext();
        tenant.SetTenant(agencyId, userId: userId);

        return (tenant, new Infrastructure.Tenancy.PlatformScope(tenant, NullLogger<Infrastructure.Tenancy.PlatformScope>.Instance));
    }

    /// <summary>A further context on the same database, as the owner role, acting as the given tenant.</summary>
    public AppDbContext NewContext(ITenantContext tenant, IPlatformScope scope)
    {
        var context = _postgres.Connect(Name, tenant, scope, Clock, asApplicationRole: false);
        _extra.Add(context);
        return context;
    }

    public static Notifier Notifier(AppDbContext db) => new(db, new NullOutbox());

    public PayoutService Payouts(AppDbContext? db = null, (TenantContext Tenant, Infrastructure.Tenancy.PlatformScope Scope)? tenancy = null)
    {
        var context = db ?? Db;
        var scope = tenancy?.Scope ?? Tenancy.Scope;
        var tenant = tenancy?.Tenant ?? Tenancy.Tenant;

        return new PayoutService(context, new PayoutBalances(context, Clock), new LedgerAccounts(context), scope, tenant, Clock);
    }

    public PayoutSettlements Settlements() =>
        new(Db, Payouts(), new LedgerAccounts(Db), Tenancy.Scope, Notifier(Db), Clock);

    public PayoutTransferService Sender() =>
        new(Db, Transfers, Tenancy.Scope, Settlements(), new CapturingAlerter(Alerts), Clock, NullLogger<PayoutTransferService>.Instance);

    public PayoutStatusPoller Poller() =>
        new(Db, Transfers, Tenancy.Scope, Settlements(), new CapturingAlerter(Alerts), NullLogger<PayoutStatusPoller>.Instance);

    public BankAccountService BankAccounts() => new(Db, Transfers, Tenancy.Tenant, Notifier(Db), Clock);

    public DisputeService Disputes() =>
        new(Db, BackOffice, Tenancy.Scope, new LedgerAccounts(Db), Notifier(Db), new CapturingAlerter(Alerts),
            Tenancy.Tenant, Clock, NullLogger<DisputeService>.Instance);

    public GatewayReconciliation Reconciliation() =>
        new(Db, BackOffice, new NamedGateway(), Tenancy.Scope, new CapturingAlerter(Alerts), Clock,
            NullLogger<GatewayReconciliation>.Instance);

    /// <summary>A real card payment, credited to the wallet through the real posting path.</summary>
    public async Task<PaymentTransaction> PayInAsync(long amountMinor, string? reference = null)
    {
        var payment = PaymentTransaction.Start(
            AgencyId, OwnerId, PaymentPurpose.WalletTopUp, new Money(amountMinor), "NGN", reference ?? $"TU-{Guid.NewGuid():N}");

        payment.MarkSucceeded(new Money(amountMinor), new Money(1_500), "NGN", payment.Reference, Clock.GetUtcNow());
        Db.PaymentTransactions.Add(payment);
        await Db.SaveChangesAsync();

        using (Tenancy.Scope.Enter("test — posting a top-up to the platform's clearing account"))
        {
            await new WalletTopUpService(Db, Clock).PostAsync(payment);
        }

        return payment;
    }

    /// <summary>Tops up and lets the money settle, so it is withdrawable.</summary>
    public async Task FundSettledAsync(long amountMinor)
    {
        await PayInAsync(amountMinor);
        Clock.Advance(PayoutLimits.SettlementWindow + TimeSpan.FromMinutes(1));
    }

    public async Task<Wallet> WalletAsync()
    {
        Db.ChangeTracker.Clear();
        return await Db.Wallets.AsNoTracking().SingleAsync();
    }

    /// <summary>A platform account's balance straight from the ledger, in its own normal direction.</summary>
    public async Task<long> PlatformBalanceAsync(LedgerAccountType type)
    {
        using var scope = Tenancy.Scope.Enter("test — reading a platform ledger account");

        var account = await Db.LedgerAccounts.AsNoTracking()
            .SingleAsync(a => a.AgencyId == null && a.AccountType == type && a.Currency == "NGN");

        var entries = await Db.LedgerEntries.AsNoTracking().Where(e => e.AccountId == account.Id).ToListAsync();
        var debits = new Money(entries.Where(e => e.Direction == LedgerDirection.Debit).Sum(e => e.AmountMinor.AmountMinor));
        var credits = new Money(entries.Where(e => e.Direction == LedgerDirection.Credit).Sum(e => e.AmountMinor.AmountMinor));

        return type.BalanceOf(debits, credits).AmountMinor;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var context in _extra)
        {
            await context.DisposeAsync();
        }
    }

    private sealed class NullOutbox : IOutbox
    {
        public void Enqueue<TMessage>(TMessage message, Guid? agencyId = null)
            where TMessage : class
        {
        }
    }

    private sealed class CapturingAlerter(List<PlatformAlert> alerts) : IPlatformAlerter
    {
        public Task RaiseAsync(PlatformAlert alert, CancellationToken cancellationToken = default)
        {
            lock (alerts)
            {
                alerts.Add(alert);
            }

            return Task.CompletedTask;
        }
    }

    /// <summary>Only its name is used by reconciliation.</summary>
    private sealed class NamedGateway : IPaymentGateway
    {
        public string Name => "paystack";

        public Task<GatewayInitialization> InitializeAsync(string reference, Money amount, string currency, string customerEmail, string callbackUrl, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<GatewayVerification> VerifyAsync(string reference, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<GatewayRefund> RefundAsync(string reference, Money amount, string reason, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public bool IsValidSignature(string payload, string? signature) => false;
    }
}

/// <summary>The payout rail, in process, counting every call that could move money.</summary>
internal sealed class FakeBankTransfers : IBankTransfers
{
    public string Name => "paystack";

    /// <summary>Every initiate call, by reference. The number that must never exceed one per payout.</summary>
    public ConcurrentQueue<string> Initiated { get; } = new();

    public ConcurrentQueue<string> StatusQueries { get; } = new();

    /// <summary>What initiate does. Defaults to "accepted, queued".</summary>
    public Func<string, GatewayTransfer> OnInitiate { get; set; } =
        _ => new GatewayTransfer(TransferOutcome.Queued, "pending", "TRF_1", null);

    /// <summary>What a status query answers. Defaults to "no idea yet".</summary>
    public Func<string, GatewayTransfer> OnStatus { get; set; } =
        _ => new GatewayTransfer(TransferOutcome.Unknown, "pending", null, null);

    public ResolvedBankAccount Resolution { get; set; } =
        new(BankAccountResolution.Resolved, "ACME TOURS LIMITED", null);

    public Task<IReadOnlyList<BankListing>> ListBanksAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<BankListing>>([new BankListing("058", "GTBank"), new BankListing("044", "Access Bank")]);

    public Task<ResolvedBankAccount> ResolveAccountAsync(string accountNumber, string bankCode, CancellationToken cancellationToken = default) =>
        Task.FromResult(Resolution);

    public Task<string> CreateRecipientAsync(string accountNumber, string bankCode, string accountName, string currency, CancellationToken cancellationToken = default) =>
        Task.FromResult($"RCP_{accountNumber}");

    public Task<Money?> GetBalanceAsync(string currency, CancellationToken cancellationToken = default) =>
        Task.FromResult<Money?>(Money.FromMajor(100_000_000));

    public Task<GatewayTransfer> InitiateTransferAsync(string reference, string recipientCode, Money amount, string currency, string reason, CancellationToken cancellationToken = default)
    {
        Initiated.Enqueue(reference);
        return Task.FromResult(OnInitiate(reference));
    }

    public Task<GatewayTransfer> GetTransferAsync(string reference, CancellationToken cancellationToken = default)
    {
        StatusQueries.Enqueue(reference);
        return Task.FromResult(OnStatus(reference));
    }
}

/// <summary>The gateway's disputes and settlements, in process.</summary>
internal sealed class FakeBackOffice : IGatewayBackOffice
{
    public Dictionary<string, GatewayDispute> Disputes { get; } = [];

    public List<GatewaySettlement> Settlements { get; } = [];

    public List<(string DisputeId, DisputeEvidence Evidence)> Evidence { get; } = [];

    public Task<GatewayDispute?> GetDisputeAsync(string gatewayDisputeId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Disputes.GetValueOrDefault(gatewayDisputeId));

    public Task SubmitEvidenceAsync(string gatewayDisputeId, DisputeEvidence evidence, CancellationToken cancellationToken = default)
    {
        Evidence.Add((gatewayDisputeId, evidence));
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<GatewaySettlement>> SettlementsAsync(DateTimeOffset windowStart, DateTimeOffset windowEnd, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<GatewaySettlement>>(
            Settlements.Where(s => s.SettledAt >= windowStart && s.SettledAt < windowEnd).ToList());
}
