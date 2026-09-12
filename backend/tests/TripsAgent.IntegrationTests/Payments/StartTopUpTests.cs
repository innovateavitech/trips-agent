using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using TripsAgent.Application.Payments;
using TripsAgent.Application.Tenancy;
using TripsAgent.Domain.Common;
using TripsAgent.Domain.Payments;
using TripsAgent.Domain.Tenancy;
using TripsAgent.Infrastructure.Persistence;
using TripsAgent.Infrastructure.Tenancy;
using TripsAgent.Integrations.Paystack;
using TripsAgent.IntegrationTests.Identity;
using TripsAgent.IntegrationTests.Persistence;

namespace TripsAgent.IntegrationTests.Payments;

/// <summary>
/// The checks that happen before anyone is sent to a payment page: may this agency fund, and is
/// the amount sane.
/// </summary>
/// <remarks>
/// All of these are cheaper to enforce here than to unwind afterwards. A refund costs the
/// platform the gateway's fee and the agent a week of waiting, so a rejection before the charge
/// is the kind thing as well as the cheap thing.
/// </remarks>
[Collection(PostgresCollection.Name)]
public class StartTopUpTests
{
    private readonly PostgresFixture _postgres;

    public StartTopUpTests(PostgresFixture postgres) => _postgres = postgres;

    [Fact]
    public async Task A_verified_agency_is_sent_to_the_gateway()
    {
        await using var world = await WorldAsync();

        var outcome = await world.Handler.HandleAsync(Money.FromMajor(5_000));

        var started = outcome.Should().BeOfType<StartTopUpOutcome.Started>().Subject;
        started.AuthorizationUrl.Should().Be("https://checkout.paystack.com/3ni8kdavz62431k");

        using var _ = world.Tenancy.Scope.Enter("test — the attempt should be on file");

        // Recorded before the gateway was called, so a webhook already in flight has a row to
        // find. Pending, because nobody has paid yet.
        var payment = await world.Db.PaymentTransactions.AsNoTracking().SingleAsync();
        payment.Status.Should().Be(PaymentStatus.Pending);
        payment.AmountMinor.AmountMinor.Should().Be(500_000);
        payment.Purpose.Should().Be(PaymentPurpose.WalletTopUp);
        payment.Reference.Should().Be(started.Reference);
        payment.GatewayReference.Should().Be("re4lyvq3s3");

        // And nothing has moved yet.
        (await world.Db.LedgerEntries.CountAsync()).Should().Be(0);
    }

    [Theory]
    [InlineData(AgencyStatus.PendingVerification)]
    [InlineData(AgencyStatus.Rejected)]
    [InlineData(AgencyStatus.Suspended)]
    [InlineData(AgencyStatus.Terminated)]
    public async Task An_agency_that_is_not_verified_cannot_fund_its_wallet(AgencyStatus status)
    {
        await using var world = await WorldAsync(status);

        var outcome = await world.Handler.HandleAsync(Money.FromMajor(5_000));

        var denied = outcome.Should().BeOfType<StartTopUpOutcome.NotPermitted>().Subject;

        // The reason is shown to the agent as-is, so it has to be worth reading.
        denied.Reason.Should().NotBeNullOrWhiteSpace();
        denied.Reason.Should().NotContain("Status", "the agent should not be shown an enum name");

        using var _ = world.Tenancy.Scope.Enter("test — no attempt should exist");

        // Not even a pending row: the gateway was never called, so there is nothing to record.
        (await world.Db.PaymentTransactions.CountAsync()).Should().Be(0);
        world.GatewayCalls.Should().Be(0);
    }

    [Fact]
    public async Task An_amount_below_the_floor_is_refused()
    {
        await using var world = await WorldAsync();

        // ₦499.99 — a kobo under. The floor exists because a gateway's fixed fee can exceed a
        // tiny top-up, leaving the platform paying to receive money.
        var outcome = await world.Handler.HandleAsync(TopUpLimits.Minimum - new Money(1));

        outcome.Should().BeOfType<StartTopUpOutcome.AmountOutOfRange>()
            .Which.Reason.Should().Contain("500.00");

        world.GatewayCalls.Should().Be(0);
    }

    [Fact]
    public async Task The_floor_itself_is_allowed()
    {
        await using var world = await WorldAsync();

        // Boundaries are where off-by-one lives, and "minimum" must mean inclusive.
        (await world.Handler.HandleAsync(TopUpLimits.Minimum))
            .Should().BeOfType<StartTopUpOutcome.Started>();
    }

    [Fact]
    public async Task An_amount_above_the_ceiling_is_refused()
    {
        await using var world = await WorldAsync();

        // The ceiling catches a mistyped amount — an extra zero or three — before it becomes a
        // refund conversation.
        var outcome = await world.Handler.HandleAsync(TopUpLimits.Maximum + new Money(1));

        outcome.Should().BeOfType<StartTopUpOutcome.AmountOutOfRange>()
            .Which.Reason.Should().Contain("bank transfer", "the agent needs a way forward, not just a no");

        world.GatewayCalls.Should().Be(0);
    }

    [Fact]
    public async Task The_ceiling_itself_is_allowed()
    {
        await using var world = await WorldAsync();

        (await world.Handler.HandleAsync(TopUpLimits.Maximum))
            .Should().BeOfType<StartTopUpOutcome.Started>();
    }

    [Fact]
    public async Task A_gateway_outage_records_the_failure_and_charges_nobody()
    {
        await using var world = await WorldAsync(failWith: HttpStatusCode.ServiceUnavailable);

        var outcome = await world.Handler.HandleAsync(Money.FromMajor(5_000));

        outcome.Should().BeOfType<StartTopUpOutcome.GatewayUnavailable>();

        using var _ = world.Tenancy.Scope.Enter("test — the failed attempt is on file");

        // The row survives, marked failed. It is the evidence that answers "I clicked add funds
        // and nothing happened" — a silent failure leaves support nothing to look at.
        var payment = await world.Db.PaymentTransactions.AsNoTracking().SingleAsync();
        payment.Status.Should().Be(PaymentStatus.Failed);
        payment.FailureReason.Should().NotBeNullOrWhiteSpace();
        payment.LedgerTransactionGroupId.Should().BeNull();

        (await world.Db.LedgerEntries.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Each_top_up_gets_its_own_reference()
    {
        await using var world = await WorldAsync();

        var first = await world.Handler.HandleAsync(Money.FromMajor(5_000));
        var second = await world.Handler.HandleAsync(Money.FromMajor(5_000));

        var a = first.Should().BeOfType<StartTopUpOutcome.Started>().Subject.Reference;
        var b = second.Should().BeOfType<StartTopUpOutcome.Started>().Subject.Reference;

        // The reference is the unique index on payment_transactions and the handle a webhook
        // arrives with. Two attempts sharing one would make a delivery ambiguous.
        a.Should().NotBe(b);

        // And it fits the column, and Paystack's allowed character set.
        a.Length.Should().BeLessThanOrEqualTo(60);
        a.Should().MatchRegex("^[A-Za-z0-9=.-]+$");
    }

    // ------------------------------------------------------------------- helpers

    private async Task<World> WorldAsync(
        AgencyStatus status = AgencyStatus.Verified,
        HttpStatusCode? failWith = null,
        [CallerMemberName] string testName = "")
    {
        var name = testName.ToLowerInvariant();

        // [Theory] cases share a caller name, so the status keeps their databases apart.
        name = $"{name}-{status}".ToLowerInvariant();
        name = name[..Math.Min(name.Length, 55)];

        var clock = new ManualClock(DateTimeOffset.UtcNow);

        var agencyId = Guid.CreateVersion7();
        var tenancy = TestTenancy.For(agencyId);

        await using (var setup = await _postgres.CreateEmptyDatabaseAsync(name))
        {
            await setup.Database.MigrateAsync();
        }

        var db = _postgres.Connect(name, tenancy.Tenant, tenancy.Scope, clock);

        using (var _ = tenancy.Scope.Enter("test setup — an agency in a given state"))
        {
            var agency = Agency.RegisterPrincipal("Lagos Travel Limited", "lagos-travel", "NG", "NGN", "Africa/Lagos");

            // The id has to match the tenant the context is acting as, or the write guard
            // refuses the save.
            typeof(Domain.Common.Entity)
                .GetProperty(nameof(Domain.Common.Entity.Id))!
                .SetValue(agency, agencyId);

            ApplyStatus(agency, status, clock.GetUtcNow());

            db.Agencies.Add(agency);
            db.Wallets.Add(Wallet.OpenFor(agency.Id, "NGN"));
            await db.SaveChangesAsync();
        }

        var transport = new StubInitialize(failWith);

        var gateway = new PaystackGateway(
            new HttpClient(transport, disposeHandler: false) { BaseAddress = new Uri("https://api.paystack.test") },
            new PaystackOptions { SecretKey = "paystack-topup-test-key" });

        var handler = new StartTopUpHandler(
            db,
            gateway,
            tenancy.Tenant,
            clock,
            new TopUpCallbackUrl("https://console.test/wallet/top-up/complete"),
            NullLogger<StartTopUpHandler>.Instance);

        return new World(db, tenancy, handler, transport);
    }

    private static void ApplyStatus(Agency agency, AgencyStatus status, DateTimeOffset now)
    {
        switch (status)
        {
            case AgencyStatus.PendingVerification:
                break;
            case AgencyStatus.Verified:
                agency.MarkVerified(now);
                break;
            case AgencyStatus.Rejected:
                agency.MarkRejected();
                break;
            case AgencyStatus.Suspended:
                agency.MarkVerified(now);
                agency.Suspend("Test.", now);
                break;
            case AgencyStatus.Terminated:
                agency.MarkVerified(now);
                agency.Terminate("Test.", now);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(status), status, "Unhandled agency status.");
        }
    }

    /// <summary>Paystack's initialize endpoint, stubbed with the payload from its own docs.</summary>
    private sealed class StubInitialize : HttpMessageHandler
    {
        private readonly HttpStatusCode? _failWith;

        public StubInitialize(HttpStatusCode? failWith) => _failWith = failWith;

        public int Calls { get; private set; }

        /// <summary>The amount field as it went over the wire, so the units can be asserted.</summary>
        public string? SentBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);

            Calls++;

            if (request.Content is not null)
            {
                SentBody = await request.Content.ReadAsStringAsync(cancellationToken);
            }

            if (_failWith is { } failure)
            {
                return new HttpResponseMessage(failure)
                {
                    Content = new StringContent("""{"status":false,"message":"Service unavailable"}"""),
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    {"status":true,"message":"Authorization URL created","data":{"authorization_url":"https://checkout.paystack.com/3ni8kdavz62431k","access_code":"3ni8kdavz62431k","reference":"re4lyvq3s3"}}
                    """,
                    Encoding.UTF8,
                    "application/json"),
            };
        }
    }

    private sealed class World : IAsyncDisposable
    {
        private readonly StubInitialize _transport;

        public World(
            AppDbContext db,
            (TenantContext Tenant, PlatformScope Scope) tenancy,
            StartTopUpHandler handler,
            StubInitialize transport)
        {
            Db = db;
            Tenancy = tenancy;
            Handler = handler;
            _transport = transport;
        }

        public AppDbContext Db { get; }

        public (TenantContext Tenant, PlatformScope Scope) Tenancy { get; }

        public StartTopUpHandler Handler { get; }

        public int GatewayCalls => _transport.Calls;

        public string? SentBody => _transport.SentBody;

        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }
}
