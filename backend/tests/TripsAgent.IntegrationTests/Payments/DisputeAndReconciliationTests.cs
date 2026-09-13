using System.Runtime.CompilerServices;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using TripsAgent.Application.Payments;
using TripsAgent.Domain.Common;
using TripsAgent.Domain.Payments;
using TripsAgent.IntegrationTests.Persistence;

namespace TripsAgent.IntegrationTests.Payments;

/// <summary>
/// Chargebacks and the daily gateway reconciliation (build plan F12, issue 69).
/// </summary>
[Collection(PostgresCollection.Name)]
public class DisputeAndReconciliationTests
{
    private readonly PostgresFixture _postgres;

    public DisputeAndReconciliationTests(PostgresFixture postgres) => _postgres = postgres;

    // ------------------------------------------------------------------ disputes

    [Fact]
    public async Task A_dispute_delivered_five_times_is_one_row_and_one_hold()
    {
        await using var world = await WorldAsync();
        var payment = await world.PayInAsync(1_000_000);
        world.BackOffice.Disputes["D1"] = Dispute(world, "D1", payment.Reference, 400_000, GatewayDisputeState.Open);

        for (var i = 0; i < 5; i++)
        {
            world.Db.ChangeTracker.Clear();
            await world.Disputes().SyncAsync("D1");
        }

        using var scope = world.Tenancy.Scope.Enter("test — reading disputes");
        var dispute = await world.Db.Disputes.AsNoTracking().SingleAsync();

        dispute.HoldOutcome.Should().Be(DisputeHoldOutcome.Held);
        (await world.WalletAsync()).BalanceMinor.AmountMinor.Should().Be(600_000);
        (await world.PlatformBalanceAsync(LedgerAccountType.DisputeHeld)).Should().Be(400_000);
    }

    [Fact]
    public async Task A_won_dispute_releases_the_hold_back_to_the_wallet()
    {
        await using var world = await WorldAsync();
        var payment = await world.PayInAsync(1_000_000);

        world.BackOffice.Disputes["D2"] = Dispute(world, "D2", payment.Reference, 400_000, GatewayDisputeState.Open);
        await world.Disputes().SyncAsync("D2");

        world.BackOffice.Disputes["D2"] = Dispute(world, "D2", payment.Reference, 400_000, GatewayDisputeState.MerchantWon);
        world.Db.ChangeTracker.Clear();
        await world.Disputes().SyncAsync("D2");
        world.Db.ChangeTracker.Clear();
        await world.Disputes().SyncAsync("D2");

        (await world.WalletAsync()).BalanceMinor.AmountMinor.Should().Be(1_000_000, "released exactly once");
        (await world.PlatformBalanceAsync(LedgerAccountType.DisputeHeld)).Should().Be(0);
        (await world.Db.Disputes.AsNoTracking().SingleAsync()).Status.Should().Be(DisputeStatus.Won);
    }

    [Fact]
    public async Task A_lost_dispute_sends_the_held_money_out_through_gateway_clearing()
    {
        await using var world = await WorldAsync();
        var payment = await world.PayInAsync(1_000_000);
        var clearingBefore = await world.PlatformBalanceAsync(LedgerAccountType.GatewayClearing);

        world.BackOffice.Disputes["D3"] = Dispute(world, "D3", payment.Reference, 400_000, GatewayDisputeState.MerchantLost);
        await world.Disputes().SyncAsync("D3");

        (await world.WalletAsync()).BalanceMinor.AmountMinor.Should().Be(600_000);
        (await world.PlatformBalanceAsync(LedgerAccountType.DisputeHeld)).Should().Be(0);
        (await world.PlatformBalanceAsync(LedgerAccountType.GatewayClearing)).Should().Be(clearingBefore - 400_000);
        (await world.Db.Disputes.AsNoTracking().SingleAsync()).Status.Should().Be(DisputeStatus.Lost);
    }

    [Fact]
    public async Task A_dispute_the_agency_cannot_cover_raises_an_exception_instead_of_doing_nothing()
    {
        await using var world = await WorldAsync();
        var payment = await world.PayInAsync(300_000);

        world.BackOffice.Disputes["D4"] = Dispute(world, "D4", payment.Reference, 900_000, GatewayDisputeState.Open);
        await world.Disputes().SyncAsync("D4");

        (await world.WalletAsync()).BalanceMinor.AmountMinor.Should().Be(300_000, "a wallet never goes negative");

        using var scope = world.Tenancy.Scope.Enter("test — reading the exception");
        (await world.Db.Disputes.AsNoTracking().SingleAsync()).HoldOutcome.Should().Be(DisputeHoldOutcome.Uncovered);
        (await world.Db.ReconciliationExceptions.CountAsync(e => e.Check == ReconciliationCheck.DisputeUncovered)).Should().Be(1);
        world.Alerts.Should().Contain(a => a.Severity == Application.Notifications.AlertSeverity.P1);
    }

    [Fact]
    public async Task A_dispute_on_a_payment_we_do_not_know_is_recorded_not_dropped()
    {
        await using var world = await WorldAsync();

        world.BackOffice.Disputes["D5"] = Dispute(world, "D5", "NOT-OURS", 50_000, GatewayDisputeState.Open);
        await world.Disputes().SyncAsync("D5");

        using var scope = world.Tenancy.Scope.Enter("test — reading the exception");
        (await world.Db.ReconciliationExceptions.SingleAsync()).Check.Should().Be(ReconciliationCheck.GatewayTransactionUnknown);
    }

    [Fact]
    public async Task Evidence_is_filed_before_the_deadline_and_refused_after_it()
    {
        await using var world = await WorldAsync();
        var payment = await world.PayInAsync(1_000_000);
        world.BackOffice.Disputes["D6"] = Dispute(world, "D6", payment.Reference, 100_000, GatewayDisputeState.Open);
        await world.Disputes().SyncAsync("D6");

        var disputeId = (await world.Db.Disputes.AsNoTracking().SingleAsync()).Id;
        var evidence = new EvidenceSubmission("Tunde Bello", "tunde@example.com", "+2348000000000", "Lagos–Abuja flight", null, null, null);

        (await world.Disputes().SubmitEvidenceAsync(disputeId, evidence)).Should().Be(SubmitEvidenceOutcome.Submitted);
        world.BackOffice.Evidence.Should().ContainSingle();

        world.Clock.Advance(TimeSpan.FromDays(10));
        world.Db.ChangeTracker.Clear();

        (await world.Disputes().SubmitEvidenceAsync(disputeId, evidence)).Should().Be(SubmitEvidenceOutcome.TooLate);
        world.BackOffice.Evidence.Should().ContainSingle("nothing is sent after the deadline");
    }

    [Fact]
    public async Task A_missed_deadline_is_recorded_and_alerted()
    {
        await using var world = await WorldAsync();
        var payment = await world.PayInAsync(1_000_000);
        world.BackOffice.Disputes["D7"] = Dispute(world, "D7", payment.Reference, 100_000, GatewayDisputeState.Open);
        await world.Disputes().SyncAsync("D7");

        world.Clock.Advance(TimeSpan.FromDays(4));
        world.Db.ChangeTracker.Clear();
        await world.Disputes().RunAsync();

        (await world.Db.Disputes.AsNoTracking().SingleAsync()).Status.Should().Be(DisputeStatus.Expired);
        world.Alerts.Should().Contain(a => a.Title.Contains("deadline missed", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------ reconciliation

    [Fact]
    public async Task A_day_that_matches_to_the_kobo_completes_clean()
    {
        await using var world = await WorldAsync();
        var payment = await world.PayInAsync(1_000_000);
        var day = new DateOnly(2026, 9, 11);

        world.BackOffice.Settlements.Add(Settlement(day, ("S1", payment.Reference, 1_000_000, 15_000)));

        var run = await world.Reconciliation().RunAsync(day);

        run.Status.Should().Be(ReconciliationRunStatus.Completed);
        run.RecordsMatched.Should().Be(1);
        run.IsClean.Should().BeTrue();
    }

    [Fact]
    public async Task Every_kind_of_mismatch_becomes_an_exception_and_a_rerun_duplicates_none()
    {
        await using var world = await WorldAsync();
        var matched = await world.PayInAsync(1_000_000);
        var mismatched = await world.PayInAsync(500_000);
        var day = new DateOnly(2026, 9, 11);

        world.BackOffice.Settlements.Add(Settlement(
            day,
            ("S1", matched.Reference, 1_000_000, 15_000),
            ("S1", mismatched.Reference, 499_999, 7_500),
            ("S1", "UNKNOWN-REF", 20_000, 300)));

        // A payment three days before the business day that no settlement has paid.
        world.Clock.Advance(TimeSpan.FromDays(-2));
        await world.PayInAsync(250_000, "LATE-ONE");

        var first = await world.Reconciliation().RunAsync(day);
        world.Db.ChangeTracker.Clear();
        var second = await world.Reconciliation().RunAsync(day);

        first.Id.Should().Be(second.Id, "one run per gateway per day");

        using var scope = world.Tenancy.Scope.Enter("test — reading exceptions");
        var exceptions = await world.Db.ReconciliationExceptions.AsNoTracking().ToListAsync();

        exceptions.Select(e => e.Check).Should().BeEquivalentTo(
        [
            ReconciliationCheck.GatewayAmountMismatch,
            ReconciliationCheck.GatewayTransactionUnknown,
            ReconciliationCheck.GatewayPaymentUnsettled,
        ]);

        exceptions.Should().OnlyContain(e => e.TimesSeen == 2, "the second run found the same problems again, not new ones");
        (await world.Db.ReconciliationRuns.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task A_settlement_whose_own_arithmetic_is_out_by_a_kobo_is_an_exception()
    {
        await using var world = await WorldAsync();
        var payment = await world.PayInAsync(1_000_000);
        var day = new DateOnly(2026, 9, 11);
        var (start, _) = GatewayReconciliation.DayInUtc(day);

        world.BackOffice.Settlements.Add(new GatewaySettlement(
            "S9", start.AddHours(8), new Money(1_000_000), new Money(15_000), new Money(985_001), "NGN",
            [new GatewaySettlementLine(payment.Reference, new Money(1_000_000), new Money(15_000), "NGN")]));

        var run = await world.Reconciliation().RunAsync(day);

        run.IsClean.Should().BeFalse();

        using var scope = world.Tenancy.Scope.Enter("test — reading exceptions");
        (await world.Db.ReconciliationExceptions.SingleAsync()).Check.Should().Be(ReconciliationCheck.GatewaySettlementUnbalanced);
    }

    [Fact]
    public async Task A_run_that_fails_leaves_a_failed_row_not_an_absence()
    {
        await using var world = await WorldAsync();
        var day = new DateOnly(2026, 9, 11);

        var run = await new GatewayReconciliation(
                world.Db, new BrokenBackOffice(), new NamedGatewayStub(), world.Tenancy.Scope, new NoAlerts(), world.Clock,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<GatewayReconciliation>.Instance)
            .RunAsync(day);

        run.Status.Should().Be(ReconciliationRunStatus.Failed);
        run.FailureReason.Should().NotBeNullOrWhiteSpace();
    }

    // ------------------------------------------------------------------ helpers

    private static GatewayDispute Dispute(PayoutWorld world, string id, string reference, long amountMinor, GatewayDisputeState state)
    {
        var opened = world.Clock.GetUtcNow();

        return new GatewayDispute(
            id, reference, new Money(amountMinor), "NGN", state,
            state == GatewayDisputeState.Open ? "awaiting-merchant-feedback" : "resolved",
            state switch
            {
                GatewayDisputeState.MerchantWon => "declined",
                GatewayDisputeState.MerchantLost => "merchant-accepted",
                _ => null,
            },
            "chargeback", "I do not recognise this charge", opened, opened.AddDays(3));
    }

    private static GatewaySettlement Settlement(DateOnly day, params (string Id, string Reference, long Amount, long Fee)[] lines)
    {
        var (start, _) = GatewayReconciliation.DayInUtc(day);
        var gross = lines.Sum(line => line.Amount);
        var fees = lines.Sum(line => line.Fee);

        return new GatewaySettlement(
            lines[0].Id, start.AddHours(10), new Money(gross), new Money(fees), new Money(gross - fees), "NGN",
            lines.Select(line => new GatewaySettlementLine(line.Reference, new Money(line.Amount), new Money(line.Fee), "NGN")).ToList());
    }

    private Task<PayoutWorld> WorldAsync([CallerMemberName] string testName = "") =>
        PayoutWorld.CreateAsync(_postgres, testName);

    private sealed class BrokenBackOffice : IGatewayBackOffice
    {
        public Task<GatewayDispute?> GetDisputeAsync(string gatewayDisputeId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task SubmitEvidenceAsync(string gatewayDisputeId, DisputeEvidence evidence, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<GatewaySettlement>> SettlementsAsync(DateTimeOffset windowStart, DateTimeOffset windowEnd, CancellationToken cancellationToken = default) =>
            throw new PaymentGatewayUnavailableException("Paystack answered HTTP 503 for settlements.");
    }

    private sealed class NoAlerts : Application.Notifications.IPlatformAlerter
    {
        public Task RaiseAsync(Application.Notifications.PlatformAlert alert, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class NamedGatewayStub : IPaymentGateway
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
