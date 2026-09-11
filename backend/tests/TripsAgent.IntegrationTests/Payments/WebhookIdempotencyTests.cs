using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using TripsAgent.Application.Notifications;
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
/// The guarantee the whole webhook path exists to make: a gateway that delivers the same event
/// over and over moves money exactly once.
/// </summary>
/// <remarks>
/// <para>
/// Paystack retries an unacknowledged event every three minutes for four tries, then hourly for
/// 72 hours. It also delivers an event the browser redirect has already caused us to verify. So
/// duplicate deliveries are the normal case, not the edge case, and "one delivery, one credit"
/// would be testing the path that never breaks.
/// </para>
/// <para>
/// The real <see cref="PaystackGateway"/> is used throughout — real signature checking, real JSON
/// mapping, real minor-unit handling. Only the socket is replaced, by a handler that returns the
/// payloads from Paystack's own documentation.
/// </para>
/// </remarks>
[Collection(PostgresCollection.Name)]
public class WebhookIdempotencyTests
{
    private const string SecretKey = "paystack-webhook-integration-key";

    private readonly PostgresFixture _postgres;

    public WebhookIdempotencyTests(PostgresFixture postgres) => _postgres = postgres;

    // ------------------------------------------------------------------- the key test

    [Fact]
    public async Task The_same_event_delivered_five_times_produces_one_ledger_transaction()
    {
        await using var world = await WorldAsync();

        var body = ChargeSuccess(world.Reference, paidMinor: 500_000, feeMinor: 7_500);
        var signature = Sign(body);

        // Five deliveries of the identical event, exactly as a retrying gateway sends them.
        var outcomes = new List<WebhookOutcome>();

        for (var delivery = 0; delivery < 5; delivery++)
        {
            outcomes.Add(await world.Handler.ReceiveAsync(body, signature));
            await world.Drain();
        }

        // The first is acted on; the other four are recognised as redeliveries.
        outcomes[0].Should().Be(WebhookOutcome.Accepted);
        outcomes.Skip(1).Should().AllBeEquivalentTo(WebhookOutcome.Duplicate);

        using var _ = world.Tenancy.Scope.Enter("test — counting what five deliveries produced");

        // One event row, because of the unique index on (gateway, event_id).
        (await world.Db.PaymentWebhookEvents.CountAsync()).Should().Be(1);

        // One ledger transaction — two entries, one debit and one credit, sharing a group.
        var entries = await world.Db.LedgerEntries.AsNoTracking().ToListAsync();
        entries.Should().HaveCount(2);
        entries.Select(e => e.TransactionGroupId).Distinct().Should().HaveCount(1);

        // One statement line.
        (await world.Db.WalletTransactions.CountAsync()).Should().Be(1);

        // And ₦5,000.00 in the wallet, once.
        var wallet = await world.Db.Wallets.AsNoTracking().SingleAsync();
        wallet.BalanceMinor.AmountMinor.Should().Be(500_000);

        // The gateway was asked to verify exactly once: after the first delivery posted the
        // payment, the others never got far enough to ask.
        world.VerifyCalls.Should().Be(1);
    }

    [Fact]
    public async Task Five_deliveries_arriving_at_once_still_credit_only_once()
    {
        // Payload and gateway agree here, so nothing distracts from what is being tested. That
        // they can disagree — and which one wins — is its own test below.
        await using var world = await WorldAsync(verifiedAmountMinor: 250_000);

        var body = ChargeSuccess(world.Reference, paidMinor: 250_000, feeMinor: 3_750);
        var signature = Sign(body);

        // Concurrent, which is the case a check-then-act in application code would fail: five
        // deliveries all read "not seen before" before any of them inserts.
        //
        // Each needs its own DbContext, the way five simultaneous requests would have.
        var receipts = await Task.WhenAll(
            Enumerable.Range(0, 5).Select(async _ =>
            {
                await using var scope = world.NewScope();
                return await scope.Handler.ReceiveAsync(body, signature);
            }));

        receipts.Count(r => r == WebhookOutcome.Accepted).Should().Be(
            1, "the unique index must let exactly one delivery through");
        receipts.Count(r => r == WebhookOutcome.Duplicate).Should().Be(4);

        await world.Drain();

        using var _ = world.Tenancy.Scope.Enter("test — counting after a concurrent burst");

        (await world.Db.PaymentWebhookEvents.CountAsync()).Should().Be(1);
        (await world.Db.LedgerEntries.CountAsync()).Should().Be(2);

        var wallet = await world.Db.Wallets.AsNoTracking().SingleAsync();
        wallet.BalanceMinor.AmountMinor.Should().Be(250_000);
    }

    // ------------------------------------------------------------------- signature

    [Fact]
    public async Task An_unsigned_delivery_is_rejected_and_records_nothing()
    {
        await using var world = await WorldAsync();

        var body = ChargeSuccess(world.Reference, paidMinor: 500_000, feeMinor: 7_500);

        (await world.Handler.ReceiveAsync(body, signature: null)).Should().Be(WebhookOutcome.Rejected);
        await world.Drain();

        using var _ = world.Tenancy.Scope.Enter("test — nothing should exist");

        // Not recorded at all. The endpoint is public, so a row per unsigned request would hand
        // anyone who found the URL a way to fill the table.
        (await world.Db.PaymentWebhookEvents.CountAsync()).Should().Be(0);
        (await world.Db.LedgerEntries.CountAsync()).Should().Be(0);

        var wallet = await world.Db.Wallets.AsNoTracking().SingleAsync();
        wallet.BalanceMinor.AmountMinor.Should().Be(0);

        world.VerifyCalls.Should().Be(0, "an unsigned body must not even cause an outbound call");
    }

    [Fact]
    public async Task A_body_edited_after_signing_is_rejected()
    {
        await using var world = await WorldAsync();

        var original = ChargeSuccess(world.Reference, paidMinor: 500_000, feeMinor: 7_500);
        var signature = Sign(original);

        // The attack this is here to stop: take a real signed event and add a zero.
        var tampered = original.Replace("\"amount\":500000", "\"amount\":5000000", StringComparison.Ordinal);

        (await world.Handler.ReceiveAsync(tampered, signature)).Should().Be(WebhookOutcome.Rejected);
        await world.Drain();

        using var _ = world.Tenancy.Scope.Enter("test — nothing should exist");
        (await world.Db.PaymentWebhookEvents.CountAsync()).Should().Be(0);

        var wallet = await world.Db.Wallets.AsNoTracking().SingleAsync();
        wallet.BalanceMinor.AmountMinor.Should().Be(0);
    }

    // ------------------------------------------------------------------- never trust the payload

    [Fact]
    public async Task The_amount_credited_comes_from_the_gateway_not_from_the_payload()
    {
        // The gateway will say ₦5,000.00 was paid.
        await using var world = await WorldAsync(verifiedAmountMinor: 500_000);

        // The delivery claims ₦900,000.00. Correctly signed, because it is a genuine body that
        // someone captured and replayed — signing proves origin, not honesty about amounts.
        var body = ChargeSuccess(world.Reference, paidMinor: 90_000_000, feeMinor: 0);

        await world.Handler.ReceiveAsync(body, Sign(body));
        await world.Drain();

        using var _ = world.Tenancy.Scope.Enter("test — checking what was credited");

        var wallet = await world.Db.Wallets.AsNoTracking().SingleAsync();
        wallet.BalanceMinor.AmountMinor.Should().Be(
            500_000, "the credited amount is the one the gateway confirmed, never the one in the body");

        var payment = await world.Db.PaymentTransactions.AsNoTracking().SingleAsync();
        payment.VerifiedAmountMinor!.Value.AmountMinor.Should().Be(500_000);
        payment.AmountMinor.AmountMinor.Should().Be(500_000, "what we originally asked for");
    }

    // ------------------------------------------------------------------- other event types

    [Fact]
    public async Task An_unknown_event_type_is_recorded_and_ignored()
    {
        await using var world = await WorldAsync();

        var body =
            $$$"""
              {"event":"subscription.create","data":{"id":990011,"reference":"{{{world.Reference}}}"}}
              """;

        (await world.Handler.ReceiveAsync(body, Sign(body))).Should().Be(WebhookOutcome.Ignored);
        await world.Drain();

        using var _ = world.Tenancy.Scope.Enter("test — the event should be on file");

        // Recorded, so the history is complete and a new event type can be spotted in the data
        // rather than guessed at. But nothing was acted on.
        var recorded = await world.Db.PaymentWebhookEvents.AsNoTracking().SingleAsync();
        recorded.EventType.Should().Be("subscription.create");
        recorded.ProcessingStatus.Should().Be(WebhookProcessingStatus.Ignored);

        (await world.Db.LedgerEntries.CountAsync()).Should().Be(0);
        world.VerifyCalls.Should().Be(0);
    }

    [Fact]
    public async Task Two_different_events_about_one_payment_are_both_recorded()
    {
        await using var world = await WorldAsync();

        var success = ChargeSuccess(world.Reference, paidMinor: 500_000, feeMinor: 7_500);
        var dispute =
            $$$"""
              {"event":"charge.dispute.create","data":{"id":4099260516,"reference":"{{{world.Reference}}}"}}
              """;

        (await world.Handler.ReceiveAsync(success, Sign(success))).Should().Be(WebhookOutcome.Accepted);

        // Same transaction id, different event. The event id is built from both, so this must not
        // collide with the success above — deduplicating these together would lose the dispute.
        (await world.Handler.ReceiveAsync(dispute, Sign(dispute))).Should().Be(WebhookOutcome.Ignored);

        await world.Drain();

        using var _ = world.Tenancy.Scope.Enter("test — both events on file");
        (await world.Db.PaymentWebhookEvents.CountAsync()).Should().Be(2);
    }

    // ------------------------------------------------------------------- failure

    [Fact]
    public async Task A_failed_payment_leaves_the_balance_untouched()
    {
        // The gateway says this one failed, whatever the delivery claims.
        await using var world = await WorldAsync(gatewayStatus: "failed");

        var body = ChargeSuccess(world.Reference, paidMinor: 500_000, feeMinor: 0);

        await world.Handler.ReceiveAsync(body, Sign(body));
        await world.Drain();

        using var _ = world.Tenancy.Scope.Enter("test — nothing should have moved");

        (await world.Db.LedgerEntries.CountAsync()).Should().Be(0);
        (await world.Db.WalletTransactions.CountAsync()).Should().Be(0);

        var wallet = await world.Db.Wallets.AsNoTracking().SingleAsync();
        wallet.BalanceMinor.AmountMinor.Should().Be(0);

        var payment = await world.Db.PaymentTransactions.AsNoTracking().SingleAsync();
        payment.Status.Should().Be(PaymentStatus.Failed);
        payment.LedgerTransactionGroupId.Should().BeNull();
    }

    [Fact]
    public async Task A_delivery_for_an_unknown_reference_is_recorded_without_crediting_anything()
    {
        await using var world = await WorldAsync();

        var body = ChargeSuccess("TA-NOT-A-REAL-REFERENCE", paidMinor: 500_000, feeMinor: 0);

        (await world.Handler.ReceiveAsync(body, Sign(body))).Should().Be(WebhookOutcome.Accepted);
        await world.Drain();

        using var _ = world.Tenancy.Scope.Enter("test — recorded, nothing credited");

        // Processed, not failed: there is nothing to retry. A reference we do not know is most
        // likely another integration on the same Paystack account, not an error of ours.
        var recorded = await world.Db.PaymentWebhookEvents.AsNoTracking().SingleAsync();
        recorded.ProcessingStatus.Should().Be(WebhookProcessingStatus.Processed);

        (await world.Db.LedgerEntries.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task A_gateway_outage_during_processing_leaves_the_event_pending_for_retry()
    {
        await using var world = await WorldAsync(failVerifyWith: HttpStatusCode.ServiceUnavailable);

        var body = ChargeSuccess(world.Reference, paidMinor: 500_000, feeMinor: 0);

        // The delivery is still acknowledged — the event is safely on disk, and the gateway must
        // not be asked to redeliver just because our own call to it failed.
        (await world.Handler.ReceiveAsync(body, Sign(body))).Should().Be(WebhookOutcome.Accepted);
        await world.Drain();

        using var _ = world.Tenancy.Scope.Enter("test — pending, not lost");

        var recorded = await world.Db.PaymentWebhookEvents.AsNoTracking().SingleAsync();
        recorded.ProcessingStatus.Should().Be(WebhookProcessingStatus.Pending);
        recorded.Attempts.Should().Be(1);
        recorded.LastError.Should().NotBeNullOrWhiteSpace();

        (await world.Db.LedgerEntries.CountAsync()).Should().Be(0);

        // And when the gateway comes back, the retry credits it — once.
        world.GatewayRecovers();
        await world.Drain();

        var wallet = await world.Db.Wallets.AsNoTracking().SingleAsync();
        wallet.BalanceMinor.AmountMinor.Should().Be(500_000);

        (await world.Db.PaymentWebhookEvents.AsNoTracking().SingleAsync())
            .ProcessingStatus.Should().Be(WebhookProcessingStatus.Processed);
    }

    [Fact]
    public async Task An_event_that_keeps_failing_is_dead_lettered_rather_than_retried_forever()
    {
        await using var world = await WorldAsync(failVerifyWith: HttpStatusCode.ServiceUnavailable);

        var body = ChargeSuccess(world.Reference, paidMinor: 500_000, feeMinor: 0);
        await world.Handler.ReceiveAsync(body, Sign(body));

        for (var attempt = 0; attempt < PaymentWebhookEvent.MaxAttempts; attempt++)
        {
            await world.Drain();
        }

        using var _ = world.Tenancy.Scope.Enter("test — dead-lettered");

        var recorded = await world.Db.PaymentWebhookEvents.AsNoTracking().SingleAsync();
        recorded.ProcessingStatus.Should().Be(WebhookProcessingStatus.DeadLettered);
        recorded.Attempts.Should().Be(PaymentWebhookEvent.MaxAttempts);

        // And the drain stops picking it up, so a permanently broken event cannot spin forever.
        (await world.Drain()).Should().Be(0);
    }

    // ------------------------------------------------------------------- helpers

    /// <summary>A <c>charge.success</c> delivery, shaped like the ones Paystack documents.</summary>
    private static string ChargeSuccess(string reference, long paidMinor, long feeMinor) =>
        $$$"""
          {"event":"charge.success","data":{"id":4099260516,"domain":"test","status":"success","reference":"{{{reference}}}","amount":{{{paidMinor}}},"fees":{{{feeMinor}}},"currency":"NGN","gateway_response":"Successful","channel":"card"}}
          """;

    private static string Sign(string body) =>
        Convert.ToHexString(
                HMACSHA512.HashData(Encoding.UTF8.GetBytes(SecretKey), Encoding.UTF8.GetBytes(body)))
            .ToLower(CultureInfo.InvariantCulture);

    private async Task<World> WorldAsync(
        long verifiedAmountMinor = 500_000,
        string gatewayStatus = "success",
        HttpStatusCode? failVerifyWith = null,
        [CallerMemberName] string testName = "")
    {
        var name = testName.ToLowerInvariant();
        name = name[..Math.Min(name.Length, 55)];

        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var tenancy = TestTenancy.None();

        await using (var setup = await _postgres.CreateEmptyDatabaseAsync(name))
        {
            await setup.Database.MigrateAsync();
        }

        // As the application role, so the whole money path — receive, drain, verify, post — runs
        // under row-level security (ADR-0006). A flow that only works as a superuser fails here.
        var db = _postgres.Connect(name, tenancy.Tenant, tenancy.Scope, clock);

        var reference = $"TA-{Guid.CreateVersion7():N}"[..30];

        using (var _ = tenancy.Scope.Enter("test setup — an agency, a wallet and a pending payment"))
        {
            var agency = Agency.RegisterPrincipal("Lagos Travel Limited", "lagos-travel", "NG", "NGN", "Africa/Lagos");
            agency.MarkVerified(clock.GetUtcNow());
            db.Agencies.Add(agency);

            db.Wallets.Add(Wallet.OpenFor(agency.Id, "NGN"));

            db.PaymentTransactions.Add(PaymentTransaction.Start(
                agency.Id, null, PaymentPurpose.WalletTopUp, new Money(500_000), "NGN", reference));

            await db.SaveChangesAsync();
        }

        var transport = new StubPaystack(verifiedAmountMinor, gatewayStatus, failVerifyWith);

        return new World(db, tenancy, clock, reference, transport, _postgres, name);
    }

    /// <summary>
    /// Paystack's HTTP surface, stubbed. The real <see cref="PaystackGateway"/> sits on top of
    /// it, so signature checking and JSON mapping are the production code throughout.
    /// </summary>
    private sealed class StubPaystack : HttpMessageHandler
    {
        private readonly long _verifiedAmountMinor;
        private readonly string _status;

        public StubPaystack(long verifiedAmountMinor, string status, HttpStatusCode? failWith)
        {
            _verifiedAmountMinor = verifiedAmountMinor;
            _status = status;
            FailWith = failWith;
        }

        public HttpStatusCode? FailWith { get; set; }

        public int VerifyCalls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            VerifyCalls++;

            if (FailWith is { } failure)
            {
                return Task.FromResult(new HttpResponseMessage(failure)
                {
                    Content = new StringContent("""{"status":false,"message":"Service unavailable"}"""),
                });
            }

            var succeeded = string.Equals(_status, "success", StringComparison.OrdinalIgnoreCase);
            var gatewayResponse = succeeded ? "Successful" : "Declined";

            // Shaped like the sample in Paystack's Verify Transaction docs — including
            // requested_amount differing from amount, which is why only amount is ever credited.
            var body =
                $$$"""
                  {"status":true,"message":"Verification successful","data":{"id":4099260516,"domain":"test","status":"{{{_status}}}","reference":"ref","amount":{{{_verifiedAmountMinor}}},"fees":7500,"currency":"NGN","gateway_response":"{{{gatewayResponse}}}","requested_amount":99999999}}
                  """;

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class World : IAsyncDisposable
    {
        private readonly PostgresFixture _postgres;
        private readonly string _databaseName;
        private readonly StubPaystack _transport;
        private readonly List<AppDbContext> _extraContexts = [];

        public World(
            AppDbContext db,
            (TenantContext Tenant, PlatformScope Scope) tenancy,
            ManualClock clock,
            string reference,
            StubPaystack transport,
            PostgresFixture postgres,
            string databaseName)
        {
            Db = db;
            Tenancy = tenancy;
            Clock = clock;
            Reference = reference;
            _transport = transport;
            _postgres = postgres;
            _databaseName = databaseName;

            Handler = BuildHandler(db, tenancy);
        }

        public AppDbContext Db { get; }

        public (TenantContext Tenant, PlatformScope Scope) Tenancy { get; }

        public ManualClock Clock { get; }

        public string Reference { get; }

        public PaymentWebhookHandler Handler { get; }

        public int VerifyCalls => _transport.VerifyCalls;

        public void GatewayRecovers() => _transport.FailWith = null;

        /// <summary>Runs the drain the Worker runs on its timer. Returns how many it processed.</summary>
        public Task<int> Drain() => Handler.DrainAsync();

        /// <summary>
        /// A second handler on its own DbContext — one concurrent request's worth of state.
        /// </summary>
        public ScopedHandler NewScope()
        {
            var tenancy = TestTenancy.None();
            var db = _postgres.Connect(_databaseName, tenancy.Tenant, tenancy.Scope, Clock);

            lock (_extraContexts)
            {
                _extraContexts.Add(db);
            }

            return new ScopedHandler(BuildHandler(db, tenancy));
        }

        private PaymentWebhookHandler BuildHandler(
            AppDbContext db,
            (TenantContext Tenant, PlatformScope Scope) tenancy)
        {
            var gateway = new PaystackGateway(
                new HttpClient(_transport, disposeHandler: false)
                {
                    BaseAddress = new Uri("https://api.paystack.test"),
                },
                new PaystackOptions { SecretKey = SecretKey });

            var topUps = new WalletTopUpService(db, Clock);

            var verify = new VerifyTopUpHandler(
                db,
                gateway,
                tenancy.Scope,
                topUps,
                new DiscardingEmailSender(),
                Clock,
                NullLogger<VerifyTopUpHandler>.Instance);

            return new PaymentWebhookHandler(
                db,
                gateway,
                tenancy.Scope,

                // Nothing is enqueued: the tests call DrainAsync explicitly so that the moment
                // processing happens is visible in the test rather than decided by Hangfire.
                new NoOpDispatcher(),
                verify,
                Clock,
                NullLogger<PaymentWebhookHandler>.Instance);
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var context in _extraContexts)
            {
                await context.DisposeAsync();
            }

            await Db.DisposeAsync();
        }
    }

    private sealed class ScopedHandler : IAsyncDisposable
    {
        public ScopedHandler(PaymentWebhookHandler handler) => Handler = handler;

        public PaymentWebhookHandler Handler { get; }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class NoOpDispatcher : IWebhookDispatcher
    {
        public Task EnqueueAsync(Guid webhookEventId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class DiscardingEmailSender : IEmailSender
    {
        public Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
