using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TripsAgent.Application.Notifications;
using TripsAgent.Application.Persistence;
using TripsAgent.Application.Tenancy;
using TripsAgent.Domain.Common;
using TripsAgent.Domain.Payments;

namespace TripsAgent.Application.Payments;

/// <summary>Sends approved payouts. The Worker runs it; nothing else may.</summary>
public interface IPayoutTransferService
{
    /// <summary>Sends every payout Finance has approved. One transfer call per payout, ever.</summary>
    /// <returns>How many were handed to the gateway.</returns>
    public Task<int> SendApprovedAsync(CancellationToken cancellationToken = default);
}

/// <summary>Resolves payouts the gateway has not answered for. The only way out of an unknown outcome.</summary>
public interface IPayoutStatusPoller
{
    /// <summary>Asks the gateway about every payout still in flight.</summary>
    /// <returns>How many reached a final state.</returns>
    public Task<int> PollAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// The executor: it takes instructions somebody else has already authorised and carries them out.
/// </summary>
/// <remarks>
/// <para>
/// <b>This class calls <see cref="IBankTransfers.InitiateTransferAsync"/> at most once per payout,
/// ever.</b> Three things enforce that, and none of them is a comment:
/// </para>
/// <list type="number">
///   <item>
///     The payout is moved to <c>Sending</c> and <b>committed</b> before the call. A process that
///     dies mid-call comes back to a payout that is no longer <c>Approved</c>.
///   </item>
///   <item>
///     Only an <c>Approved</c> payout is ever picked up. <c>Sending</c> and <c>OutcomeUnknown</c>
///     belong to the poller, which asks rather than sends.
///   </item>
///   <item>
///     Our own reference goes to the gateway as the transfer's reference, so a second initiation
///     would be refused there too. That is the backstop, not the plan.
///   </item>
/// </list>
/// <para>
/// A timeout is recorded as <c>OutcomeUnknown</c>, never as a failure. See
/// docs/adr/0008-never-retry-payout-transfers.md — and if you are here to add a retry, read it
/// first.
/// </para>
/// </remarks>
public sealed partial class PayoutTransferService : IPayoutTransferService
{
    /// <summary>The most payouts one run sends. A bound, so a backlog does not become one long transaction.</summary>
    public const int BatchSize = 50;

    private readonly IAppDbContext _db;
    private readonly IBankTransfers _transfers;
    private readonly IPlatformScope _platformScope;
    private readonly PayoutSettlements _settlements;
    private readonly IPlatformAlerter _alerter;
    private readonly TimeProvider _clock;
    private readonly ILogger<PayoutTransferService> _logger;

    public PayoutTransferService(
        IAppDbContext db,
        IBankTransfers transfers,
        IPlatformScope platformScope,
        PayoutSettlements settlements,
        IPlatformAlerter alerter,
        TimeProvider clock,
        ILogger<PayoutTransferService> logger)
    {
        _db = db;
        _transfers = transfers;
        _platformScope = platformScope;
        _settlements = settlements;
        _alerter = alerter;
        _clock = clock;
        _logger = logger;
    }

    public async Task<int> SendApprovedAsync(CancellationToken cancellationToken = default)
    {
        List<Guid> due;

        using (var scope = _platformScope.Enter("payout sender — approved withdrawals span every agency"))
        {
            due = await _db.Payouts
                .AsNoTracking()
                .Where(payout => payout.Status == PayoutStatus.Approved)
                .OrderBy(payout => payout.ApprovedAt)
                .Select(payout => payout.Id)
                .Take(BatchSize)
                .ToListAsync(cancellationToken);
        }

        var sent = 0;

        foreach (var id in due)
        {
            if (await SendOneAsync(id, cancellationToken))
            {
                sent++;
            }
        }

        return sent;
    }

    /// <returns>True when this call handed the transfer to the gateway.</returns>
    private async Task<bool> SendOneAsync(Guid payoutId, CancellationToken cancellationToken)
    {
        using var scope = _platformScope.Enter("payout sender — sending one approved withdrawal");

        var payout = await _db.Payouts.FirstOrDefaultAsync(p => p.Id == payoutId, cancellationToken);

        if (payout is null || payout.Status != PayoutStatus.Approved)
        {
            // Somebody else took it, or it was rejected between the list and here.
            return false;
        }

        var account = await _db.AgencyBankAccounts
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == payout.BankAccountId, cancellationToken);

        if (account?.GatewayRecipientCode is not { Length: > 0 } recipient)
        {
            // The destination lost its recipient code, which should be impossible. Not a failure
            // of the transfer, because no transfer was attempted — so the money stays parked and
            // a person is told.
            await AlertAsync(
                AlertSeverity.P2,
                $"Payout {payout.Reference} has no usable destination",
                $"Bank account {payout.BankAccountId} has no gateway recipient code, so {payout.Reference} "
                + "cannot be sent. The money is still in the platform's payout-payable account. Re-verify the "
                + "account, or reject the payout to return it to the agency's wallet.",
                payout.AgencyId,
                cancellationToken);

            return false;
        }

        await WarnIfFloatIsShortAsync(payout, cancellationToken);

        // Committed BEFORE the call. From this moment nothing in the system will pick this payout
        // up to send again — the sender only takes Approved — so a crash between here and the
        // gateway's answer leaves "we may have sent it", which the poller can resolve. The
        // opposite order leaves "approved, please send", which it cannot.
        payout.MarkSending(_clock.GetUtcNow());
        await _db.SaveChangesAsync(cancellationToken);

        GatewayTransfer transfer;

        try
        {
            // The one call. Never retried, never wrapped in a resilience pipeline. ADR-0008.
            transfer = await _transfers.InitiateTransferAsync(
                payout.Reference,
                recipient,
                payout.AmountMinor,
                payout.Currency,
                $"Withdrawal {payout.Reference}",
                cancellationToken);
        }
        catch (PaymentGatewayUnavailableException ex)
        {
            // Unknown, not failed. The money may be on its way. Asking again is the only safe
            // move, and the poller is what does the asking.
            LogOutcomeUnknown(_logger, ex, payout.Reference);

            payout.MarkOutcomeUnknown(ex.Message, _clock.GetUtcNow());
            await _db.SaveChangesAsync(cancellationToken);

            return true;
        }

        await ApplyAsync(payout, transfer, cancellationToken);

        return true;
    }

    /// <summary>
    /// Checks the platform's own balance and alerts if it will not cover the transfer.
    /// </summary>
    /// <remarks>
    /// A warning, not a gate. An empty float is Trips' problem and not the agent's, and telling
    /// them their withdrawal failed would be blaming them for it — so the transfer is attempted
    /// anyway and somebody is woken up.
    /// </remarks>
    private async Task WarnIfFloatIsShortAsync(Payout payout, CancellationToken cancellationToken)
    {
        Money? balance;

        try
        {
            balance = await _transfers.GetBalanceAsync(payout.Currency, cancellationToken);
        }
        catch (PaymentGatewayException)
        {
            return;
        }

        if (balance is { } float_ && float_ < payout.AmountMinor)
        {
            await AlertAsync(
                AlertSeverity.P1,
                "The platform's gateway balance will not cover a payout",
                $"Payout {payout.Reference} is for {payout.AmountMinor} {payout.Currency} and the gateway balance "
                + $"is {float_}. The transfer is being attempted anyway — if the gateway refuses it, the money "
                + "goes back to the agency's wallet and they are told their withdrawal did not go through, which "
                + "is not their fault. Fund the gateway account.",
                payout.AgencyId,
                cancellationToken);
        }
    }

    /// <summary>Records what the gateway said. Shared by the sender and the poller.</summary>
    internal Task ApplyAsync(Payout payout, GatewayTransfer transfer, CancellationToken cancellationToken) =>
        _settlements.ApplyAsync(payout, transfer, cancellationToken);

    private async Task AlertAsync(
        AlertSeverity severity,
        string title,
        string detail,
        Guid agencyId,
        CancellationToken cancellationToken)
    {
        try
        {
            await _alerter.RaiseAsync(
                new PlatformAlert(severity, title, detail, nameof(PayoutTransferService), agencyId), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogAlertFailed(_logger, ex, title);
        }
    }

    [LoggerMessage(Level = LogLevel.Error,
        Message = "Payout {Reference} was sent and the gateway did not answer. It will NOT be sent again; "
                  + "the status poller will ask what became of it.")]
    private static partial void LogOutcomeUnknown(ILogger logger, Exception exception, string reference);

    [LoggerMessage(Level = LogLevel.Critical, Message = "A payout alert could not be raised: {Title}")]
    private static partial void LogAlertFailed(ILogger logger, Exception exception, string title);
}

/// <summary>
/// Asks the gateway what became of every payout it has not answered for.
/// </summary>
/// <remarks>
/// <para>
/// The other half of ADR-0008. A payout in <c>Sending</c> or <c>OutcomeUnknown</c> is money that
/// has left the wallet and may or may not have left the platform, and the only way to find out is
/// to ask — a read, which may be repeated freely.
/// </para>
/// <para>
/// It gives up after <see cref="MaxStatusQueries"/> and tells a person. Guessing after N attempts
/// is the exact failure mode the whole design exists to prevent, so the give-up is an alert and
/// never a status change.
/// </para>
/// </remarks>
public sealed partial class PayoutStatusPoller : IPayoutStatusPoller
{
    /// <summary>How many payouts one run asks about.</summary>
    public const int BatchSize = 100;

    /// <summary>
    /// How many times one payout is asked about before a person is told instead.
    /// </summary>
    /// <remarks>
    /// At one run every fifteen minutes this is a little over a day of asking. A transfer the
    /// gateway still cannot account for after a day is not going to resolve itself, and the answer
    /// is in their dashboard rather than in another poll.
    /// </remarks>
    public const int MaxStatusQueries = 96;

    private readonly IAppDbContext _db;
    private readonly IBankTransfers _transfers;
    private readonly IPlatformScope _platformScope;
    private readonly PayoutSettlements _settlements;
    private readonly IPlatformAlerter _alerter;
    private readonly ILogger<PayoutStatusPoller> _logger;

    public PayoutStatusPoller(
        IAppDbContext db,
        IBankTransfers transfers,
        IPlatformScope platformScope,
        PayoutSettlements settlements,
        IPlatformAlerter alerter,
        ILogger<PayoutStatusPoller> logger)
    {
        _db = db;
        _transfers = transfers;
        _platformScope = platformScope;
        _settlements = settlements;
        _alerter = alerter;
        _logger = logger;
    }

    public async Task<int> PollAsync(CancellationToken cancellationToken = default)
    {
        List<Guid> inFlight;

        using (var scope = _platformScope.Enter("payout status poller — withdrawals in flight span every agency"))
        {
            inFlight = await _db.Payouts
                .AsNoTracking()
                .Where(payout => (payout.Status == PayoutStatus.Sending || payout.Status == PayoutStatus.OutcomeUnknown)
                                 && payout.StatusQueries < MaxStatusQueries)
                .OrderBy(payout => payout.SentAt)
                .Select(payout => payout.Id)
                .Take(BatchSize)
                .ToListAsync(cancellationToken);
        }

        var settled = 0;

        foreach (var id in inFlight)
        {
            if (await PollOneAsync(id, cancellationToken))
            {
                settled++;
            }
        }

        return settled;
    }

    /// <returns>True when this call reached a final state for the payout.</returns>
    private async Task<bool> PollOneAsync(Guid payoutId, CancellationToken cancellationToken)
    {
        using var scope = _platformScope.Enter("payout status poller — asking about one withdrawal");

        var payout = await _db.Payouts.FirstOrDefaultAsync(p => p.Id == payoutId, cancellationToken);

        if (payout is null || !payout.AwaitsGatewayAnswer)
        {
            return false;
        }

        GatewayTransfer transfer;

        try
        {
            // A read. Retrying this is safe and is the whole point: it is what an unknown outcome
            // is resolved by, and it never sends anything.
            transfer = await _transfers.GetTransferAsync(payout.Reference, cancellationToken);
        }
        catch (PaymentGatewayUnavailableException ex)
        {
            LogUnreachable(_logger, ex, payout.Reference);

            payout.RecordStatusQuery();
            await _db.SaveChangesAsync(cancellationToken);

            return false;
        }

        payout.RecordStatusQuery();

        if (transfer.Outcome is TransferOutcome.Unknown or TransferOutcome.Queued)
        {
            await _db.SaveChangesAsync(cancellationToken);
            await GiveUpIfExhaustedAsync(payout, transfer, cancellationToken);

            return false;
        }

        await _settlements.ApplyAsync(payout, transfer, cancellationToken);

        return payout.IsSettled;
    }

    /// <summary>Stops asking and tells a person, without inventing an outcome.</summary>
    private async Task GiveUpIfExhaustedAsync(
        Payout payout,
        GatewayTransfer transfer,
        CancellationToken cancellationToken)
    {
        if (payout.StatusQueries < MaxStatusQueries)
        {
            return;
        }

        using var scope = _platformScope.Enter(
            "payout status poller — recording a withdrawal the gateway will not account for");

        var subject = $"payout:{payout.Id}";

        var existing = await _db.ReconciliationExceptions.FirstOrDefaultAsync(
            e => e.Check == ReconciliationCheck.PayoutOutcomeUnknown && e.Subject == subject, cancellationToken);

        if (existing is null)
        {
            _db.ReconciliationExceptions.Add(ReconciliationException.Record(
                ReconciliationCheck.PayoutOutcomeUnknown,
                subject,
                $"Payout {payout.Reference} for {payout.AmountMinor} {payout.Currency} was sent to the gateway and "
                + $"has been asked about {payout.StatusQueries} times without a final answer. Its last reported "
                + $"status was '{transfer.Status}'. The money is out of the agency's wallet and in the platform's "
                + "payout-payable account. Find the transfer in the gateway's dashboard and settle it by hand — "
                + "nothing in this system will send it again, and nothing should.",
                payout.AmountMinor,
                Money.Zero,
                payout.AgencyId,
                payout.SentAt ?? payout.RequestedAt));

            await _db.SaveChangesAsync(cancellationToken);

            try
            {
                await _alerter.RaiseAsync(
                    new PlatformAlert(
                        AlertSeverity.P2,
                        $"Payout {payout.Reference} has no answer from the gateway",
                        $"{payout.AmountMinor} {payout.Currency} left agency {payout.AgencyId}'s wallet and the "
                        + "gateway will not say whether it reached the bank. It is in the reconciliation queue as "
                        + "PayoutOutcomeUnknown. Resolve it from the gateway's dashboard. Do not re-send it.",
                        nameof(PayoutStatusPoller),
                        payout.AgencyId),
                    cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogAlertFailed(_logger, ex, payout.Reference);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "The gateway could not be asked about payout {Reference}; it stays in flight and will be asked again.")]
    private static partial void LogUnreachable(ILogger logger, Exception exception, string reference);

    [LoggerMessage(Level = LogLevel.Critical,
        Message = "Payout {Reference} was given up on and the alert could not be raised.")]
    private static partial void LogAlertFailed(ILogger logger, Exception exception, string reference);
}

/// <summary>
/// Turns the gateway's verdict on a transfer into books and a notification.
/// </summary>
/// <remarks>
/// <para>
/// One class, shared by the sender and the poller, because both can reach the same payout and both
/// would otherwise have their own idea of what "paid" writes. The wallet's and the payout's
/// version tokens make the loser's save match no row, so exactly one set of entries is written.
/// </para>
/// <para>
/// The three outcomes are three different movements of money:
/// </para>
/// <list type="bullet">
///   <item>
///     <b>Paid:</b> debit payout-payable, credit gateway clearing. The money has left the
///     platform's balance at the gateway, so the asset goes down and the promise is discharged.
///   </item>
///   <item>
///     <b>Failed or refused:</b> debit payout-payable, credit the wallet. It never left.
///   </item>
///   <item>
///     <b>Reversed:</b> the bank sent it back days later. Debit gateway clearing, credit the
///     wallet — the money came back into the platform's balance and then back to the agency. Both
///     legs stay on the books, because both happened.
///   </item>
/// </list>
/// </remarks>
public sealed class PayoutSettlements
{
    private readonly IAppDbContext _db;
    private readonly PayoutService _payouts;
    private readonly LedgerAccounts _accounts;
    private readonly IPlatformScope _platformScope;
    private readonly INotifier _notifier;
    private readonly TimeProvider _clock;

    public PayoutSettlements(
        IAppDbContext db,
        PayoutService payouts,
        LedgerAccounts accounts,
        IPlatformScope platformScope,
        INotifier notifier,
        TimeProvider clock)
    {
        _db = db;
        _payouts = payouts;
        _accounts = accounts;
        _platformScope = platformScope;
        _notifier = notifier;
        _clock = clock;
    }

    /// <summary>Applies one gateway verdict to one payout, if it is still waiting for one.</summary>
    public async Task ApplyAsync(Payout payout, GatewayTransfer transfer, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payout);
        ArgumentNullException.ThrowIfNull(transfer);

        payout.RecordGatewayTransfer(transfer.TransferCode, transfer.Status);

        var now = _clock.GetUtcNow();

        switch (transfer.Outcome)
        {
            case TransferOutcome.Succeeded:
                await PaidAsync(payout, transfer, now, cancellationToken);
                break;

            case TransferOutcome.Failed:
            case TransferOutcome.Refused:
                await ReturnedAsync(
                    payout, transfer, PayoutStatus.Failed, WalletTransactionType.PayoutReturned, now, cancellationToken);
                break;

            case TransferOutcome.Reversed:
                await ReturnedAsync(
                    payout, transfer, PayoutStatus.Reversed, WalletTransactionType.PayoutReturned, now, cancellationToken);
                break;

            case TransferOutcome.Queued:
            case TransferOutcome.Unknown:
            default:
                // Not an answer. Nothing moves, and the poller asks again.
                break;
        }

        await _db.SaveChangesAsync(cancellationToken);
    }

    private async Task PaidAsync(Payout payout, GatewayTransfer transfer, DateTimeOffset now, CancellationToken cancellationToken)
    {
        Guid groupId;

        using (var scope = _platformScope.Enter(
                   "payout settlement — discharges the platform's payout-payable against its gateway balance"))
        {
            var payable = await _accounts.PlatformAsync(LedgerAccountType.PayoutPayable, payout.Currency, cancellationToken);
            var clearing = await _accounts.PlatformAsync(LedgerAccountType.GatewayClearing, payout.Currency, cancellationToken);

            var description = $"Withdrawal paid — {payout.Reference}";

            var transaction = LedgerTransaction
                .Begin(now, nameof(Payout), payout.Id)
                .Debit(payable, payout.AmountMinor, description)
                .Credit(clearing, payout.AmountMinor, description);

            _db.LedgerEntries.AddRange(transaction.Build());
            groupId = transaction.TransactionGroupId;
        }

        payout.MarkPaid(transfer.Status, now, groupId);

        // No wallet statement line: the money left the wallet when the withdrawal was requested,
        // and a second line now would read as a second withdrawal.
        await NotifyAsync(payout, NotificationTemplateCatalog.PaymentsPayoutPaid, null, cancellationToken);
    }

    private async Task ReturnedAsync(
        Payout payout,
        GatewayTransfer transfer,
        PayoutStatus status,
        WalletTransactionType statementType,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var reason = transfer.FailureReason is { Length: > 0 } stated
            ? stated
            : $"The gateway reported the transfer as {transfer.Status}.";

        using var scope = _platformScope.Enter("payout settlement — returning a withdrawal to the agency's wallet");

        var wallet = await _db.Wallets.FirstAsync(w => w.AgencyId == payout.AgencyId, cancellationToken);

        if (status == PayoutStatus.Reversed)
        {
            // It really did leave, and really did come back. The money re-enters the platform's
            // gateway balance and then the wallet, in one balanced group naming both legs.
            var clearing = await _accounts.PlatformAsync(LedgerAccountType.GatewayClearing, payout.Currency, cancellationToken);
            var walletAccount = await _accounts.AgencyWalletAsync(payout.AgencyId, payout.Currency, cancellationToken);

            var description = $"Withdrawal returned by the bank — {payout.Reference}";

            var transaction = LedgerTransaction
                .Begin(now, nameof(Payout), payout.Id)
                .Debit(clearing, payout.AmountMinor, description)
                .Credit(walletAccount, payout.AmountMinor, description);

            _db.LedgerEntries.AddRange(transaction.Build());

            var balanceBefore = wallet.BalanceMinor;
            wallet.Credit(payout.AmountMinor);

            _db.WalletTransactions.Add(WalletTransaction.Record(
                wallet, statementType, payout.AmountMinor, balanceBefore, description,
                transaction.TransactionGroupId, now));

            payout.MarkReversed(reason, now, transaction.TransactionGroupId);
        }
        else
        {
            var groupId = await _payouts.ReturnToWalletAsync(
                payout, wallet, statementType, $"Withdrawal did not go through — {payout.Reference}", now,
                cancellationToken);

            payout.MarkFailed(reason, now, groupId);
        }

        await NotifyAsync(payout, NotificationTemplateCatalog.PaymentsPayoutReturned, reason, cancellationToken);
    }

    /// <summary>Tells the agency what happened to their money.</summary>
    private async Task NotifyAsync(
        Payout payout,
        string templateKey,
        string? reason,
        CancellationToken cancellationToken)
    {
        var recipient = await _db.Users
            .AsNoTracking()
            .Where(user => user.Id == payout.RequestedByUserId)
            .Select(user => new { user.Id, user.Email, user.FirstName, user.LastName })
            .FirstOrDefaultAsync(cancellationToken);

        if (recipient is null)
        {
            return;
        }

        var account = await _db.AgencyBankAccounts
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == payout.BankAccountId, cancellationToken);

        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["amount"] = $"₦{payout.AmountMinor}",
            ["bankName"] = account?.BankName ?? "your bank",
            ["maskedNumber"] = account?.MaskedNumber ?? string.Empty,
            ["reference"] = payout.Reference,
        };

        if (reason is not null)
        {
            values["reason"] = reason;
        }

        await _notifier.QueueEmailAsync(
            new EmailNotificationRequest(
                payout.AgencyId,
                templateKey,
                recipient.Email,
                $"{recipient.FirstName} {recipient.LastName}".Trim(),
                values,
                $"{templateKey}:{payout.Id}",
                recipient.Id),
            cancellationToken);
    }
}
