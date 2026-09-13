using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TripsAgent.Application.Notifications;
using TripsAgent.Application.Persistence;
using TripsAgent.Application.Tenancy;
using TripsAgent.Domain.Common;
using TripsAgent.Domain.Payments;

namespace TripsAgent.Application.Payments;

/// <summary>What the webhook path hands a dispute delivery to.</summary>
public interface IDisputeWebhookSink
{
    /// <summary>Brings our record of a dispute into line with what the gateway says about it.</summary>
    /// <returns>True when the gateway could not answer yet, so the delivery should be retried.</returns>
    public Task<bool> SyncAsync(string gatewayDisputeId, CancellationToken cancellationToken = default);
}

/// <summary>The dispute deadline job, as the job runner sees it.</summary>
public interface IDisputeDeadlineMonitor
{
    /// <summary>Reminds agencies whose deadline is near, and records the ones that passed.</summary>
    /// <returns>How many disputes it acted on.</returns>
    public Task<int> RunAsync(CancellationToken cancellationToken = default);
}

/// <summary>What happened when an agent filed evidence.</summary>
public enum SubmitEvidenceOutcome
{
    Submitted = 1,
    NotFound = 2,

    /// <summary>The deadline has passed, or the dispute is decided. Nothing was sent.</summary>
    TooLate = 3,

    /// <summary>A required field is missing.</summary>
    Invalid = 4,

    /// <summary>The gateway could not be reached. Nothing was recorded as filed.</summary>
    GatewayUnavailable = 5,
}

/// <summary>What the agent files. Customer details are prefilled from the order where we have them.</summary>
public sealed record EvidenceSubmission(
    string CustomerName,
    string CustomerEmail,
    string CustomerPhone,
    string ServiceDetails,
    DateOnly? DeliveryDate,
    string? Note,
    IReadOnlyList<Guid>? AssetIds);

/// <summary>
/// A chargeback, from the moment the gateway tells us to the moment the bank decides.
/// </summary>
/// <remarks>
/// <para>
/// <b>A dispute never silently does nothing.</b> Every path through <see cref="SyncAsync"/> either
/// moves money, or records why it could not and tells a person:
/// </para>
/// <list type="bullet">
///   <item>Opened: the money is frozen out of the agency's wallet, the agency is emailed the
///     deadline, and the platform is alerted.</item>
///   <item>Opened on an agency that cannot cover it: nothing is debited, a P1 exception and alert say
///     the platform is exposed.</item>
///   <item>Opened on a payment we have no record of: a P1 exception, because an unrecognised
///     chargeback is still money.</item>
///   <item>Won: the frozen money goes back to the wallet. Lost: it goes to the cardholder.</item>
/// </list>
/// <para>
/// <b>Who bears a lost chargeback</b> is the agency (build plan decision 2: the platform is merchant
/// of record and the money settled into the agency's wallet). Where the wallet cannot cover it, the
/// loss is the platform's until collected, and the books and the queue say so.
/// </para>
/// <para>
/// What the gateway says is fetched, never read off the webhook body — the same rule the payment
/// path follows. A replayed body with a valid signature must not be able to resolve a dispute.
/// </para>
/// </remarks>
public sealed partial class DisputeService : IDisputeWebhookSink, IDisputeDeadlineMonitor
{
    /// <summary>How close to the deadline a reminder goes out.</summary>
    public static readonly TimeSpan ReminderWindow = TimeSpan.FromHours(48);

    /// <summary>How often a reminder may repeat inside that window.</summary>
    public static readonly TimeSpan ReminderInterval = TimeSpan.FromHours(24);

    private readonly IAppDbContext _db;
    private readonly IGatewayBackOffice _gateway;
    private readonly IPlatformScope _platformScope;
    private readonly LedgerAccounts _accounts;
    private readonly INotifier _notifier;
    private readonly IPlatformAlerter _alerter;
    private readonly ITenantContext _tenant;
    private readonly TimeProvider _clock;
    private readonly ILogger<DisputeService> _logger;

    public DisputeService(
        IAppDbContext db,
        IGatewayBackOffice gateway,
        IPlatformScope platformScope,
        LedgerAccounts accounts,
        INotifier notifier,
        IPlatformAlerter alerter,
        ITenantContext tenant,
        TimeProvider clock,
        ILogger<DisputeService> logger)
    {
        _db = db;
        _gateway = gateway;
        _platformScope = platformScope;
        _accounts = accounts;
        _notifier = notifier;
        _alerter = alerter;
        _tenant = tenant;
        _clock = clock;
        _logger = logger;
    }

    public async Task<bool> SyncAsync(string gatewayDisputeId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gatewayDisputeId);

        // The webhook said something happened; the gateway says what.
        var dispute = await _gateway.GetDisputeAsync(gatewayDisputeId, cancellationToken);

        if (dispute is null)
        {
            // Announced and not yet queryable. Ask again rather than drop it.
            return true;
        }

        using var scope = _platformScope.Enter("dispute webhook — a chargeback arrives with no session of its own");

        var existing = await _db.Disputes.FirstOrDefaultAsync(d => d.GatewayDisputeId == gatewayDisputeId, cancellationToken);

        if (existing is null)
        {
            existing = await OpenAsync(dispute, cancellationToken);

            if (existing is null)
            {
                return false;
            }
        }

        if (!existing.IsResolved && dispute.State != GatewayDisputeState.Open)
        {
            await ResolveAsync(existing, dispute, cancellationToken);
        }

        return false;
    }

    /// <summary>Records a new dispute, freezes its money and tells everyone who needs to know.</summary>
    /// <returns>Null when the payment is not ours to attach it to.</returns>
    private async Task<Dispute?> OpenAsync(GatewayDispute gateway, CancellationToken cancellationToken)
    {
        var now = _clock.GetUtcNow();

        var payment = await _db.PaymentTransactions
            .FirstOrDefaultAsync(p => p.Reference == gateway.TransactionReference, cancellationToken);

        if (payment is null)
        {
            await RaiseExceptionAsync(
                ReconciliationCheck.GatewayTransactionUnknown,
                $"dispute:{gateway.GatewayDisputeId}",
                $"The gateway raised dispute {gateway.GatewayDisputeId} for {gateway.AmountMinor} {gateway.Currency} "
                + $"against payment reference '{gateway.TransactionReference}', which matches no payment of ours. "
                + $"The evidence deadline is {gateway.DueAt:u}. Find the charge in the gateway's dashboard.",
                gateway.AmountMinor,
                null,
                now,
                cancellationToken);

            return null;
        }

        // Clamped so the database's "due after opened" rule holds even if the gateway reports a
        // deadline already spent — a record of a dispute we heard about late is still a record.
        var due = gateway.DueAt > gateway.OpenedAt ? gateway.DueAt : gateway.OpenedAt.AddMinutes(1);

        var dispute = Dispute.Open(
            payment.AgencyId,
            payment.Id,
            payment.Reference,
            gateway.GatewayDisputeId,
            gateway.AmountMinor,
            gateway.Currency,
            gateway.Category,
            gateway.Reason,
            gateway.OpenedAt,
            due,
            payment.OrderId);

        _db.Disputes.Add(dispute);

        var wallet = await _db.Wallets.FirstOrDefaultAsync(
            w => w.AgencyId == payment.AgencyId && w.Currency == dispute.Currency, cancellationToken);

        string holdNote;

        if (wallet is { Status: WalletStatus.Active } && wallet.AvailableMinor >= dispute.AmountMinor)
        {
            var groupId = await PostAsync(
                dispute, wallet, LedgerAccountType.DisputeHeld, fromWallet: true, WalletTransactionType.DisputeHold,
                -dispute.AmountMinor, $"Held for a disputed payment — {dispute.PaymentReference}", now, cancellationToken);

            dispute.RecordHeld(groupId);
            holdNote = $"We have held {Naira(dispute.AmountMinor)} from your wallet until the bank decides. "
                       + "It comes back if the dispute is decided in your favour.";
        }
        else
        {
            var reason = wallet is null
                ? "The agency has no wallet in this currency."
                : wallet.Status != WalletStatus.Active
                    ? $"The agency's wallet is {wallet.Status}."
                    : $"The wallet has {wallet.AvailableMinor} available against {dispute.AmountMinor} disputed.";

            dispute.RecordUncovered(reason);
            holdNote = $"Your wallet could not cover the {Naira(dispute.AmountMinor)} in dispute. If the dispute is "
                       + "lost, the amount will be taken from your wallet as funds arrive.";

            await RaiseExceptionAsync(
                ReconciliationCheck.DisputeUncovered,
                $"dispute:{gateway.GatewayDisputeId}",
                $"Dispute {gateway.GatewayDisputeId} for {dispute.AmountMinor} {dispute.Currency} could not be held: "
                + $"{reason} If the chargeback stands, the platform is out of pocket until it is collected.",
                dispute.AmountMinor,
                dispute.AgencyId,
                now,
                cancellationToken,
                saveNow: false);
        }

        await NotifyAgencyAsync(
            dispute,
            NotificationTemplateCatalog.PaymentsDisputeOpened,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["amount"] = Naira(dispute.AmountMinor),
                ["reference"] = dispute.PaymentReference,
                ["reason"] = dispute.Reason ?? dispute.Category ?? "not given",
                ["dueBy"] = Deadline(dispute.EvidenceDueAt),
                ["holdNote"] = holdNote,
            },
            $"{NotificationTemplateCatalog.PaymentsDisputeOpened}:{dispute.Id}",
            cancellationToken);

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Two deliveries raced and the unique index let one through; its money is held once. Any
            // other failure finds no winner here and propagates, so the delivery is retried.
            _db.ChangeTracker.Clear();

            var winner = await _db.Disputes.FirstOrDefaultAsync(
                d => d.GatewayDisputeId == gateway.GatewayDisputeId, cancellationToken);

            if (winner is null)
            {
                throw;
            }

            return winner;
        }

        await AlertAsync(
            dispute.HoldOutcome == DisputeHoldOutcome.Uncovered ? AlertSeverity.P1 : AlertSeverity.P2,
            $"Chargeback of {dispute.AmountMinor} {dispute.Currency} opened, evidence due {Deadline(dispute.EvidenceDueAt)}",
            $"Dispute {dispute.GatewayDisputeId} on payment {dispute.PaymentReference}. Hold: {dispute.HoldOutcome}. "
            + "The agency has been emailed and asked for evidence.",
            dispute.AgencyId,
            cancellationToken);

        return dispute;
    }

    /// <summary>Moves the money the way the bank decided. Once.</summary>
    private async Task ResolveAsync(Dispute dispute, GatewayDispute gateway, CancellationToken cancellationToken)
    {
        var now = _clock.GetUtcNow();
        var wallet = await _db.Wallets.FirstOrDefaultAsync(
            w => w.AgencyId == dispute.AgencyId && w.Currency == dispute.Currency, cancellationToken);

        var won = gateway.State == GatewayDisputeState.MerchantWon;
        Guid? groupId = null;
        string moneyNote;

        if (dispute.HoldOutcome == DisputeHoldOutcome.Held && wallet is not null)
        {
            if (won)
            {
                // Back out of dispute-held and into the wallet.
                groupId = await PostAsync(
                    dispute, wallet, LedgerAccountType.DisputeHeld, fromWallet: false, WalletTransactionType.DisputeReleased,
                    dispute.AmountMinor, $"Dispute won, hold released — {dispute.PaymentReference}", now, cancellationToken);

                moneyNote = $"The {Naira(dispute.AmountMinor)} we held is back in your wallet.";
            }
            else
            {
                // Out of dispute-held and out of the platform's gateway balance: the bank took it.
                groupId = await ChargeBackHeldAsync(dispute, wallet, now, cancellationToken);
                moneyNote = $"The {Naira(dispute.AmountMinor)} held when the dispute opened has gone to the cardholder.";
            }
        }
        else if (!won && wallet is { Status: WalletStatus.Active } && wallet.AvailableMinor >= dispute.AmountMinor)
        {
            // Uncovered at the time, and the agency can cover it now: straight from wallet to cardholder.
            groupId = await PostAsync(
                dispute, wallet, LedgerAccountType.GatewayClearing, fromWallet: true, WalletTransactionType.DisputeLost,
                -dispute.AmountMinor, $"Dispute lost — {dispute.PaymentReference}", now, cancellationToken);

            moneyNote = $"{Naira(dispute.AmountMinor)} has been taken from your wallet and returned to the cardholder.";
        }
        else if (!won)
        {
            moneyNote = $"Your wallet could not cover the {Naira(dispute.AmountMinor)}. Our finance team will be in touch.";

            await RaiseExceptionAsync(
                ReconciliationCheck.DisputeUncovered,
                $"dispute:{dispute.GatewayDisputeId}",
                $"Dispute {dispute.GatewayDisputeId} was LOST for {dispute.AmountMinor} {dispute.Currency} and the agency's "
                + "wallet could not cover it. The platform has borne the chargeback; collect it from the agency.",
                dispute.AmountMinor,
                dispute.AgencyId,
                now,
                cancellationToken,
                saveNow: false);
        }
        else
        {
            moneyNote = "Nothing was held from your wallet, so nothing changes there.";
        }

        var resolution = gateway.Resolution ?? gateway.Status;

        if (won)
        {
            dispute.MarkWon(resolution, now, groupId);
        }
        else
        {
            dispute.MarkLost(resolution, now, groupId);
        }

        await NotifyAgencyAsync(
            dispute,
            NotificationTemplateCatalog.PaymentsDisputeResolved,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["amount"] = Naira(dispute.AmountMinor),
                ["reference"] = dispute.PaymentReference,
                ["outcome"] = won ? "decided in your favour" : "lost",
                ["moneyNote"] = moneyNote,
            },
            $"{NotificationTemplateCatalog.PaymentsDisputeResolved}:{dispute.Id}",
            cancellationToken);

        // The version token: a webhook and a retry reaching the same dispute cannot both move its money.
        await _db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Files evidence with the gateway before the deadline, and keeps a copy of what was sent.</summary>
    public async Task<SubmitEvidenceOutcome> SubmitEvidenceAsync(
        Guid disputeId,
        EvidenceSubmission submission,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(submission);

        // Tenant-filtered: an agency files evidence only on its own disputes.
        var dispute = await _db.Disputes.FirstOrDefaultAsync(d => d.Id == disputeId, cancellationToken);

        if (dispute is null)
        {
            return SubmitEvidenceOutcome.NotFound;
        }

        var now = _clock.GetUtcNow();

        if (!dispute.AcceptsEvidence(now))
        {
            // Refused here with a clear answer rather than sent and refused by the gateway.
            return SubmitEvidenceOutcome.TooLate;
        }

        if (string.IsNullOrWhiteSpace(submission.CustomerName)
            || string.IsNullOrWhiteSpace(submission.CustomerEmail)
            || string.IsNullOrWhiteSpace(submission.CustomerPhone)
            || string.IsNullOrWhiteSpace(submission.ServiceDetails))
        {
            return SubmitEvidenceOutcome.Invalid;
        }

        var evidence = new DisputeEvidence(
            submission.CustomerName.Trim(),
            submission.CustomerEmail.Trim(),
            submission.CustomerPhone.Trim(),
            submission.ServiceDetails.Trim(),
            submission.DeliveryDate);

        try
        {
            await _gateway.SubmitEvidenceAsync(dispute.GatewayDisputeId, evidence, cancellationToken);
        }
        catch (PaymentGatewayUnavailableException)
        {
            return SubmitEvidenceOutcome.GatewayUnavailable;
        }

        // Exactly what was sent. "What did we actually submit" is the first question when one is lost.
        dispute.RecordEvidence(
            submission.Note,
            submission.AssetIds is { Count: > 0 } ids ? JsonSerializer.Serialize(ids) : null,
            JsonSerializer.Serialize(evidence),
            _tenant.UserId,
            now);

        await _db.SaveChangesAsync(cancellationToken);

        return SubmitEvidenceOutcome.Submitted;
    }

    public async Task<int> RunAsync(CancellationToken cancellationToken = default)
    {
        using var scope = _platformScope.Enter("dispute deadline monitor — open chargebacks span every agency");

        var now = _clock.GetUtcNow();
        var remindFrom = now + ReminderWindow;
        var remindedBefore = now - ReminderInterval;

        var due = await _db.Disputes
            .Where(d => d.Status == DisputeStatus.Open
                        && d.EvidenceDueAt <= remindFrom
                        && (d.LastReminderAt == null || d.LastReminderAt <= remindedBefore || d.EvidenceDueAt <= now))
            .OrderBy(d => d.EvidenceDueAt)
            .Take(200)
            .ToListAsync(cancellationToken);

        foreach (var dispute in due)
        {
            if (dispute.EvidenceDueAt <= now)
            {
                dispute.MarkExpired(now);

                await AlertAsync(
                    AlertSeverity.P1,
                    $"Dispute evidence deadline missed: {dispute.AmountMinor} {dispute.Currency}",
                    $"Nothing was filed for dispute {dispute.GatewayDisputeId} (payment {dispute.PaymentReference}) by "
                    + $"{dispute.EvidenceDueAt:u}. It will most likely be lost; its money moves when the gateway resolves it.",
                    dispute.AgencyId,
                    cancellationToken);
            }
            else
            {
                dispute.RecordReminder(now);

                await NotifyAgencyAsync(
                    dispute,
                    NotificationTemplateCatalog.PaymentsDisputeReminder,
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["amount"] = Naira(dispute.AmountMinor),
                        ["reference"] = dispute.PaymentReference,
                        ["dueBy"] = Deadline(dispute.EvidenceDueAt),
                        ["hoursLeft"] = ((int)Math.Ceiling((dispute.EvidenceDueAt - now).TotalHours))
                            .ToString(CultureInfo.InvariantCulture),
                    },
                    $"{NotificationTemplateCatalog.PaymentsDisputeReminder}:{dispute.Id}:{now:yyyyMMddHH}",
                    cancellationToken);
            }
        }

        await _db.SaveChangesAsync(cancellationToken);

        return due.Count;
    }

    /// <summary>
    /// Moves a dispute's money between the wallet and a platform account, with a statement line.
    /// </summary>
    /// <param name="fromWallet">True to take money out of the wallet; false to put it back.</param>
    private async Task<Guid> PostAsync(
        Dispute dispute,
        Wallet wallet,
        LedgerAccountType platformAccount,
        bool fromWallet,
        WalletTransactionType statementType,
        Money signedAmount,
        string description,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        using var scope = _platformScope.Enter("dispute posting — moves chargeback money against the platform's own accounts");

        var walletAccount = await _accounts.AgencyWalletAsync(dispute.AgencyId, dispute.Currency, cancellationToken);
        var platform = await _accounts.PlatformAsync(platformAccount, dispute.Currency, cancellationToken);

        var transaction = LedgerTransaction.Begin(now, nameof(Dispute), dispute.Id);

        transaction = fromWallet
            ? transaction.Debit(walletAccount, dispute.AmountMinor, description).Credit(platform, dispute.AmountMinor, description)
            : transaction.Debit(platform, dispute.AmountMinor, description).Credit(walletAccount, dispute.AmountMinor, description);

        _db.LedgerEntries.AddRange(transaction.Build());

        var balanceBefore = wallet.BalanceMinor;

        if (fromWallet)
        {
            wallet.Debit(dispute.AmountMinor);
        }
        else
        {
            wallet.Credit(dispute.AmountMinor);
        }

        _db.WalletTransactions.Add(WalletTransaction.Record(
            wallet, statementType, signedAmount, balanceBefore, description, transaction.TransactionGroupId, now));

        return transaction.TransactionGroupId;
    }

    /// <summary>
    /// A lost dispute whose money was held: out of dispute-held, out of the gateway balance.
    /// </summary>
    /// <remarks>
    /// The wallet does not move — the money left it when the dispute opened — but the statement
    /// still gets a line saying so, with no amount, so the agent reads "held, then lost" rather than
    /// "held" forever.
    /// </remarks>
    private async Task<Guid> ChargeBackHeldAsync(Dispute dispute, Wallet wallet, DateTimeOffset now, CancellationToken cancellationToken)
    {
        using var scope = _platformScope.Enter("dispute posting — a lost chargeback leaves the platform's gateway balance");

        var held = await _accounts.PlatformAsync(LedgerAccountType.DisputeHeld, dispute.Currency, cancellationToken);
        var clearing = await _accounts.PlatformAsync(LedgerAccountType.GatewayClearing, dispute.Currency, cancellationToken);

        var description = $"Dispute lost, held amount returned to the cardholder — {dispute.PaymentReference}";

        var transaction = LedgerTransaction
            .Begin(now, nameof(Dispute), dispute.Id)
            .Debit(held, dispute.AmountMinor, description)
            .Credit(clearing, dispute.AmountMinor, description);

        _db.LedgerEntries.AddRange(transaction.Build());

        _db.WalletTransactions.Add(WalletTransaction.Record(
            wallet, WalletTransactionType.DisputeLost, Money.Zero, wallet.BalanceMinor, description,
            transaction.TransactionGroupId, now));

        return transaction.TransactionGroupId;
    }

    /// <summary>Records a discrepancy once per subject; a repeat is counted, not duplicated.</summary>
    private async Task RaiseExceptionAsync(
        ReconciliationCheck check,
        string subject,
        string detail,
        Money amount,
        Guid? agencyId,
        DateTimeOffset now,
        CancellationToken cancellationToken,
        bool saveNow = true)
    {
        var existing = await _db.ReconciliationExceptions
            .FirstOrDefaultAsync(e => e.Check == check && e.Subject == subject, cancellationToken);

        if (existing is null)
        {
            _db.ReconciliationExceptions.Add(
                ReconciliationException.Record(check, subject, detail, amount, Money.Zero, agencyId, now));
        }
        else
        {
            existing.SeenAgain(amount, Money.Zero, now);
        }

        if (saveNow)
        {
            await _db.SaveChangesAsync(cancellationToken);
            await AlertAsync(AlertSeverity.P1, $"Chargeback needs attention: {check}", detail, agencyId, cancellationToken);
        }
    }

    /// <summary>Emails the agency's owners — whoever holds the evidence.</summary>
    private async Task NotifyAgencyAsync(
        Dispute dispute,
        string templateKey,
        Dictionary<string, string> values,
        string dedupeKey,
        CancellationToken cancellationToken)
    {
        var recipient = await _db.Users
            .AsNoTracking()
            .Where(user => user.AgencyId == dispute.AgencyId && user.Status == Domain.Identity.UserStatus.Active)
            .OrderBy(user => user.CreatedAt)
            .Select(user => new { user.Id, user.Email, user.FirstName, user.LastName })
            .FirstOrDefaultAsync(cancellationToken);

        if (recipient is null)
        {
            return;
        }

        await _notifier.QueueEmailAsync(
            new EmailNotificationRequest(
                dispute.AgencyId,
                templateKey,
                recipient.Email,
                $"{recipient.FirstName} {recipient.LastName}".Trim(),
                values,
                dedupeKey,
                recipient.Id),
            cancellationToken);
    }

    private async Task AlertAsync(
        AlertSeverity severity,
        string title,
        string detail,
        Guid? agencyId,
        CancellationToken cancellationToken)
    {
        try
        {
            await _alerter.RaiseAsync(new PlatformAlert(severity, title, detail, nameof(DisputeService), agencyId), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogAlertFailed(_logger, ex, title);
        }
    }

    /// <summary>A deadline as an agent in Lagos reads it. Converted for display only; stored in UTC.</summary>
    private static string Deadline(DateTimeOffset instant)
    {
        var lagos = TimeZoneInfo.ConvertTime(instant, TimeZoneInfo.FindSystemTimeZoneById("Africa/Lagos"));
        return lagos.ToString("d MMMM yyyy, HH:mm 'WAT'", CultureInfo.InvariantCulture);
    }

    private static string Naira(Money amount) => "₦" + amount;

    [LoggerMessage(Level = LogLevel.Critical, Message = "A dispute alert could not be raised: {Title}")]
    private static partial void LogAlertFailed(ILogger logger, Exception exception, string title);
}
