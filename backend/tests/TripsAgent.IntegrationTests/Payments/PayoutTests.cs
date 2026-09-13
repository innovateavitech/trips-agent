using System.Runtime.CompilerServices;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using TripsAgent.Application.Payments;
using TripsAgent.Domain.Common;
using TripsAgent.Domain.Payments;
using TripsAgent.IntegrationTests.Persistence;

namespace TripsAgent.IntegrationTests.Payments;

/// <summary>
/// Money out (build plan F12, issue 69): what is withdrawable, the ledger moving before the bank,
/// two people for an approval, and a transfer that timed out never being sent again.
/// </summary>
[Collection(PostgresCollection.Name)]
public class PayoutTests
{
    private readonly PostgresFixture _postgres;

    public PayoutTests(PostgresFixture postgres) => _postgres = postgres;

    // ------------------------------------------------------------------ the withdrawable balance

    [Fact]
    public async Task Card_money_is_not_withdrawable_until_the_settlement_window_has_passed()
    {
        await using var world = await WorldAsync();

        await world.PayInAsync(1_000_000);

        var today = (await world.Payouts().WithdrawableAsync())!;
        today.BalanceMinor.Should().Be(1_000_000);
        today.PendingSettlementMinor.Should().Be(1_000_000);
        today.WithdrawableMinor.Should().Be(0, "money the gateway has not settled to us yet is somebody else's float");

        world.Clock.Advance(PayoutLimits.SettlementWindow + TimeSpan.FromMinutes(1));

        (await world.Payouts().WithdrawableAsync())!.WithdrawableMinor.Should().Be(1_000_000);
    }

    [Fact]
    public async Task Money_held_for_a_booking_is_not_withdrawable()
    {
        await using var world = await WorldAsync();
        await world.FundSettledAsync(1_000_000);

        var wallet = await world.Db.Wallets.SingleAsync();
        world.Db.WalletHolds.Add(wallet.PlaceHold(new Money(300_000), world.Clock.GetUtcNow(), TimeSpan.FromHours(1)));
        await world.Db.SaveChangesAsync();

        var balance = (await world.Payouts().WithdrawableAsync())!;

        balance.ReservedMinor.Should().Be(300_000);
        balance.WithdrawableMinor.Should().Be(700_000);

        var outcome = await world.Payouts().RequestAsync(new Money(800_000), null);
        outcome.Should().BeOfType<RequestPayoutOutcome.NotAllowed>()
            .Which.Reason.Should().Contain("held against bookings");
    }

    [Fact]
    public async Task Withdrawable_never_goes_below_zero()
    {
        await using var world = await WorldAsync();
        await world.PayInAsync(600_000);

        var wallet = await world.Db.Wallets.SingleAsync();
        world.Db.WalletHolds.Add(wallet.PlaceHold(new Money(500_000), world.Clock.GetUtcNow(), TimeSpan.FromHours(1)));
        await world.Db.SaveChangesAsync();

        (await world.Payouts().WithdrawableAsync())!.WithdrawableMinor.Should().Be(0);
    }

    // ------------------------------------------------------------------ requesting

    [Fact]
    public async Task A_request_moves_the_money_out_of_the_wallet_with_balanced_entries_first()
    {
        await using var world = await WorldAsync();
        await world.FundSettledAsync(2_000_000);

        var outcome = await world.Payouts().RequestAsync(new Money(1_500_000), null);

        var requested = outcome.Should().BeOfType<RequestPayoutOutcome.Requested>().Subject;

        (await world.WalletAsync()).BalanceMinor.AmountMinor.Should().Be(500_000);
        (await world.PlatformBalanceAsync(LedgerAccountType.PayoutPayable)).Should().Be(1_500_000);
        world.Transfers.Initiated.Should().BeEmpty("nothing is sent before a second person approves");

        var payout = await world.Db.Payouts.AsNoTracking().SingleAsync(p => p.Id == requested.PayoutId);
        payout.Status.Should().Be(PayoutStatus.Requested);
        payout.BankAccountId.Should().Be(world.BankAccountId);
    }

    [Fact]
    public async Task Below_the_minimum_and_past_the_daily_cap_are_refused()
    {
        await using var world = await WorldAsync();
        await world.FundSettledAsync(900_000_000);

        (await world.Payouts().RequestAsync(PayoutLimits.Minimum - new Money(1), null))
            .Should().BeOfType<RequestPayoutOutcome.NotAllowed>();

        (await world.Payouts().RequestAsync(PayoutLimits.DailyCap, null))
            .Should().BeOfType<RequestPayoutOutcome.Requested>();

        (await world.Payouts().RequestAsync(PayoutLimits.Minimum, null))
            .Should().BeOfType<RequestPayoutOutcome.NotAllowed>()
            .Which.Reason.Should().Contain("daily limit");
    }

    [Fact]
    public async Task A_newly_added_account_cannot_receive_money_during_its_cooling_off()
    {
        await using var world = await WorldAsync();
        await world.FundSettledAsync(2_000_000);

        var added = await world.BankAccounts().AddAsync("044", "9876543210", "Lagos Travel");
        var account = added.Should().BeOfType<AddBankAccountOutcome.Added>().Subject;

        var outcome = await world.Payouts().RequestAsync(new Money(1_000_000), account.BankAccountId);

        outcome.Should().BeOfType<RequestPayoutOutcome.DestinationNotReady>();

        world.Clock.Advance(PayoutLimits.NewAccountCoolingOff + TimeSpan.FromMinutes(1));

        (await world.Payouts().RequestAsync(new Money(1_000_000), account.BankAccountId))
            .Should().BeOfType<RequestPayoutOutcome.Requested>();
    }

    [Fact]
    public async Task Two_simultaneous_requests_for_the_whole_balance_produce_exactly_one_payout()
    {
        await using var world = await WorldAsync();
        await world.FundSettledAsync(1_000_000);

        var tenancyA = PayoutWorld.TenancyFor(world.AgencyId, world.OwnerId);
        var tenancyB = PayoutWorld.TenancyFor(world.AgencyId, world.OwnerId);

        var serviceA = world.Payouts(world.NewContext(tenancyA.Tenant, tenancyA.Scope), tenancyA);
        var serviceB = world.Payouts(world.NewContext(tenancyB.Tenant, tenancyB.Scope), tenancyB);

        var outcomes = await Task.WhenAll(
            serviceA.RequestAsync(new Money(1_000_000), null),
            serviceB.RequestAsync(new Money(1_000_000), null));

        outcomes.Count(o => o is RequestPayoutOutcome.Requested).Should().Be(1);
        (await world.Db.Payouts.CountAsync()).Should().Be(1);
        (await world.WalletAsync()).BalanceMinor.AmountMinor.Should().Be(0, "the wallet can never go negative");
    }

    // ------------------------------------------------------------------ approval

    [Fact]
    public async Task The_requester_cannot_approve_their_own_payout()
    {
        await using var world = await WorldAsync();
        await world.FundSettledAsync(1_000_000);

        var requested = (RequestPayoutOutcome.Requested)await world.Payouts().RequestAsync(new Money(600_000), null);

        var approve = () => world.Payouts().ApproveAsync(requested.PayoutId, world.OwnerId);

        await approve.Should().ThrowAsync<InvalidOperationException>();
        (await world.Payouts().ApproveAsync(requested.PayoutId, world.FinanceUserId)).Should().BeTrue();
    }

    [Fact]
    public async Task A_rejected_payout_puts_the_money_back_with_its_own_entries()
    {
        await using var world = await WorldAsync();
        await world.FundSettledAsync(1_000_000);

        var requested = (RequestPayoutOutcome.Requested)await world.Payouts().RequestAsync(new Money(600_000), null);

        (await world.Payouts().RejectAsync(requested.PayoutId, world.FinanceUserId, "Account under review")).Should().BeTrue();

        (await world.WalletAsync()).BalanceMinor.AmountMinor.Should().Be(1_000_000);
        (await world.PlatformBalanceAsync(LedgerAccountType.PayoutPayable)).Should().Be(0);

        using var scope = world.Tenancy.Scope.Enter("test — counting the payout's ledger groups");
        var groups = await world.Db.LedgerEntries.AsNoTracking()
            .Where(e => e.ReferenceType == nameof(Payout))
            .Select(e => e.TransactionGroupId)
            .Distinct()
            .CountAsync();

        groups.Should().Be(2, "the request and its return are two events; neither edits the other");
    }

    // ------------------------------------------------------------------ sending (ADR-0008)

    [Fact]
    public async Task A_transfer_that_times_out_is_never_sent_again_and_the_poller_resolves_it()
    {
        await using var world = await WorldAsync();
        var payoutId = await ApprovedPayoutAsync(world, 700_000);

        world.Transfers.OnInitiate = _ => throw new PaymentGatewayUnavailableException("Paystack did not answer in time.");

        await world.Sender().SendApprovedAsync();
        await world.Sender().SendApprovedAsync();
        await world.Sender().SendApprovedAsync();

        world.Transfers.Initiated.Should().HaveCount(1, "a timeout is an unknown outcome, not a failure to retry");

        var unknown = await world.Db.Payouts.AsNoTracking().SingleAsync(p => p.Id == payoutId);
        unknown.Status.Should().Be(PayoutStatus.OutcomeUnknown);
        (await world.WalletAsync()).BalanceMinor.AmountMinor.Should().Be(300_000, "an unknown outcome returns nothing to the wallet");

        // The gateway, asked, says it went through.
        world.Transfers.OnStatus = _ => new GatewayTransfer(TransferOutcome.Succeeded, "success", "TRF_9", null);

        await world.Poller().PollAsync();

        world.Transfers.Initiated.Should().HaveCount(1);
        world.Transfers.StatusQueries.Should().NotBeEmpty();

        var paid = await world.Db.Payouts.AsNoTracking().SingleAsync(p => p.Id == payoutId);
        paid.Status.Should().Be(PayoutStatus.Paid);
        (await world.PlatformBalanceAsync(LedgerAccountType.PayoutPayable)).Should().Be(0);
        (await world.WalletAsync()).BalanceMinor.AmountMinor.Should().Be(300_000);
    }

    [Fact]
    public async Task A_timeout_the_gateway_later_reports_failed_returns_the_money_once()
    {
        await using var world = await WorldAsync();
        var payoutId = await ApprovedPayoutAsync(world, 700_000);

        world.Transfers.OnInitiate = _ => throw new PaymentGatewayUnavailableException("timeout");
        await world.Sender().SendApprovedAsync();

        world.Transfers.OnStatus = _ => new GatewayTransfer(TransferOutcome.Failed, "failed", "TRF_2", "Account closed");

        await world.Poller().PollAsync();
        await world.Poller().PollAsync();

        world.Transfers.Initiated.Should().HaveCount(1);

        var failed = await world.Db.Payouts.AsNoTracking().SingleAsync(p => p.Id == payoutId);
        failed.Status.Should().Be(PayoutStatus.Failed);
        failed.FailureReason.Should().Be("Account closed");
        (await world.WalletAsync()).BalanceMinor.AmountMinor.Should().Be(1_000_000, "returned exactly once");
    }

    [Fact]
    public async Task A_payout_the_bank_returns_after_it_was_paid_is_reversed_with_both_legs_on_the_books()
    {
        await using var world = await WorldAsync();
        var payoutId = await ApprovedPayoutAsync(world, 700_000);

        world.Transfers.OnInitiate = _ => new GatewayTransfer(TransferOutcome.Succeeded, "success", "TRF_3", null);
        await world.Sender().SendApprovedAsync();

        var clearingAfterPaid = await world.PlatformBalanceAsync(LedgerAccountType.GatewayClearing);

        var payout = await world.Db.Payouts.SingleAsync(p => p.Id == payoutId);
        await world.Settlements().ApplyAsync(payout, new GatewayTransfer(TransferOutcome.Reversed, "reversed", "TRF_3", "Name mismatch"));

        (await world.Db.Payouts.AsNoTracking().SingleAsync(p => p.Id == payoutId)).Status.Should().Be(PayoutStatus.Reversed);
        (await world.WalletAsync()).BalanceMinor.AmountMinor.Should().Be(1_000_000);
        (await world.PlatformBalanceAsync(LedgerAccountType.GatewayClearing)).Should().Be(clearingAfterPaid + 700_000);
    }

    [Fact]
    public async Task A_payout_nobody_can_account_for_is_handed_to_a_person_and_never_guessed()
    {
        await using var world = await WorldAsync();
        var payoutId = await ApprovedPayoutAsync(world, 700_000);

        world.Transfers.OnInitiate = _ => throw new PaymentGatewayUnavailableException("timeout");
        await world.Sender().SendApprovedAsync();

        for (var i = 0; i < PayoutStatusPoller.MaxStatusQueries + 3; i++)
        {
            await world.Poller().PollAsync();
        }

        world.Transfers.StatusQueries.Should().HaveCount(PayoutStatusPoller.MaxStatusQueries);
        (await world.Db.Payouts.AsNoTracking().SingleAsync(p => p.Id == payoutId)).Status.Should().Be(PayoutStatus.OutcomeUnknown);

        using var scope = world.Tenancy.Scope.Enter("test — reading the exception");
        (await world.Db.ReconciliationExceptions.CountAsync(e => e.Check == ReconciliationCheck.PayoutOutcomeUnknown))
            .Should().Be(1);
    }

    // ------------------------------------------------------------------ bank accounts

    [Fact]
    public async Task The_banks_name_for_the_account_is_stored_not_the_typed_one()
    {
        await using var world = await WorldAsync();
        world.Transfers.Resolution = new ResolvedBankAccount(BankAccountResolution.Resolved, "CHUKWU EMEKA", null);

        var outcome = await world.BankAccounts().AddAsync("044", "1112223334", "Lagos Travel Limited");

        var added = outcome.Should().BeOfType<AddBankAccountOutcome.Added>().Subject;
        added.AccountName.Should().Be("CHUKWU EMEKA");
        added.NameMatchesBusiness.Should().BeFalse("a personal name on an agency's payout account is worth a flag");

        var account = await world.Db.AgencyBankAccounts.AsNoTracking().SingleAsync(a => a.Id == added.BankAccountId);
        account.AccountNameResolved.Should().Be("CHUKWU EMEKA");
        account.Status.Should().Be(BankAccountStatus.Verified);
        account.IsDefault.Should().BeFalse("the agency already has a default");
    }

    [Fact]
    public async Task An_account_the_bank_does_not_recognise_is_rejected_and_cannot_be_paid()
    {
        await using var world = await WorldAsync();
        world.Transfers.Resolution = new ResolvedBankAccount(BankAccountResolution.NotFound, string.Empty, "Could not resolve");

        var outcome = await world.BankAccounts().AddAsync("044", "1112223334", "Lagos Travel");

        var notResolved = outcome.Should().BeOfType<AddBankAccountOutcome.NotResolved>().Subject;
        var account = await world.Db.AgencyBankAccounts.AsNoTracking().SingleAsync(a => a.Id == notResolved.BankAccountId);
        account.Status.Should().Be(BankAccountStatus.Rejected);
        account.CanReceiveMoney.Should().BeFalse();
    }

    private static async Task<Guid> ApprovedPayoutAsync(PayoutWorld world, long amountMinor)
    {
        await world.FundSettledAsync(1_000_000);

        var requested = (RequestPayoutOutcome.Requested)await world.Payouts().RequestAsync(new Money(amountMinor), null);
        (await world.Payouts().ApproveAsync(requested.PayoutId, world.FinanceUserId)).Should().BeTrue();

        world.Db.ChangeTracker.Clear();
        return requested.PayoutId;
    }

    private Task<PayoutWorld> WorldAsync([CallerMemberName] string testName = "") =>
        PayoutWorld.CreateAsync(_postgres, testName);
}
