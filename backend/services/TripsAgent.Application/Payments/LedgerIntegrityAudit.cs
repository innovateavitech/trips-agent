using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TripsAgent.Application.Notifications;
using TripsAgent.Application.Persistence;
using TripsAgent.Application.Tenancy;
using TripsAgent.Domain.Common;
using TripsAgent.Domain.Payments;

namespace TripsAgent.Application.Payments;

/// <summary>What one run of the audit found.</summary>
/// <param name="ChecksRun">
/// How many checks ran. Here so a run can prove it was whole — a check that silently stopped
/// running looks exactly like a system with no problems.
/// </param>
/// <param name="NewExceptions">Discrepancies seen for the first time.</param>
/// <param name="RecurringExceptions">Discrepancies already on file, seen again.</param>
/// <param name="Duration">How long it took, so the 100k-entry budget can be watched.</param>
public sealed record LedgerAuditResult(
    int ChecksRun,
    int NewExceptions,
    int RecurringExceptions,
    TimeSpan Duration)
{
    /// <summary>True when the books balance and nothing is outstanding.</summary>
    public bool IsClean => NewExceptions == 0 && RecurringExceptions == 0;

    public int TotalExceptions => NewExceptions + RecurringExceptions;
}

/// <summary>The nightly proof that the books balance.</summary>
public interface ILedgerIntegrityAudit
{
    public Task<LedgerAuditResult> RunAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Proves, every night, that the ledger says what it should.
/// </summary>
/// <remarks>
/// <para>
/// Everything this looks for should be impossible. A deferred constraint trigger refuses an
/// unbalanced commit, the entries table is append-only with UPDATE and DELETE revoked from the
/// application role, and wallet balances move only through the domain. A clean run is the
/// expected result, and a finding means something got around all of that — a migration, a
/// support script, someone in psql, or a bug.
/// </para>
/// <para>
/// Which is exactly why it runs. A control that is never verified is indistinguishable from a
/// control that has stopped working, and in a double-entry ledger the cost of finding out late is
/// that the corruption is buried under a month of correct entries by the time anyone notices.
/// </para>
/// </remarks>
public sealed partial class LedgerIntegrityAudit : ILedgerIntegrityAudit
{
    /// <summary>How many checks a whole run performs.</summary>
    public const int CheckCount = 5;

    /// <summary>
    /// The most stale holds reported in one run.
    /// </summary>
    /// <remarks>
    /// A cap because a sweeper that has been down for a week could produce tens of thousands, and
    /// an alert listing all of them helps nobody. The oldest are the ones worth naming.
    /// </remarks>
    private const int MaxHoldsReported = 500;

    /// <summary>The most unposted payments reported in one run, oldest first. See <see cref="MaxHoldsReported"/>.</summary>
    private const int MaxPaymentsReported = 500;

    private readonly IAppDbContext _db;
    private readonly ILedgerIntegrityQueries _queries;
    private readonly IPlatformScope _platformScope;
    private readonly IPlatformAlerter _alerter;
    private readonly TimeProvider _clock;
    private readonly ILogger<LedgerIntegrityAudit> _logger;

    public LedgerIntegrityAudit(
        IAppDbContext db,
        ILedgerIntegrityQueries queries,
        IPlatformScope platformScope,
        IPlatformAlerter alerter,
        TimeProvider clock,
        ILogger<LedgerIntegrityAudit> logger)
    {
        _db = db;
        _queries = queries;
        _platformScope = platformScope;
        _alerter = alerter;
        _clock = clock;
        _logger = logger;
    }

    public async Task<LedgerAuditResult> RunAsync(CancellationToken cancellationToken = default)
    {
        var startedAt = _clock.GetUtcNow();
        var stopwatch = Stopwatch.StartNew();

        // The audit reads every agency's books, because that is the job. One of the two or three
        // legitimate cross-tenant reads in the codebase, and it goes through PlatformScope — which
        // logs it — rather than reaching for IgnoreQueryFilters, which would not.
        using var scope = _platformScope.Enter(
            "nightly ledger integrity audit — the platform's books span every agency");

        var findings = new List<Finding>();

        findings.AddRange(await UnbalancedTransactionsAsync(cancellationToken));
        findings.AddRange(await WalletDriftAsync(cancellationToken));
        findings.AddRange(await OrphanedEntriesAsync(cancellationToken));
        findings.AddRange(await ExpiredHoldsAsync(startedAt, cancellationToken));
        findings.AddRange(await UnpostedPaymentsAsync(cancellationToken));

        var (created, recurring) = await RecordAsync(findings, startedAt, cancellationToken);

        stopwatch.Stop();

        var result = new LedgerAuditResult(CheckCount, created, recurring, stopwatch.Elapsed);

        if (findings.Count == 0)
        {
            LogClean(_logger, stopwatch.ElapsedMilliseconds);
            return result;
        }

        await AlertAsync(findings, cancellationToken);

        return result;
    }

    // --------------------------------------------------------------- the five checks

    private async Task<List<Finding>> UnbalancedTransactionsAsync(CancellationToken cancellationToken)
    {
        var groups = await _queries.UnbalancedTransactionGroupsAsync(cancellationToken);

        return [.. groups.Select(group => new Finding(
            ReconciliationCheck.UnbalancedTransaction,
            group.GroupId.ToString(),
            $"Transaction group {group.GroupId} does not balance: debits {new Money(group.DebitsMinor)}, "
            + $"credits {new Money(group.CreditsMinor)}. Money has been created or destroyed.",

            // Debits are what credits ought to equal, so debits are the expectation.
            new Money(group.DebitsMinor),
            new Money(group.CreditsMinor),
            null))];
    }

    private async Task<List<Finding>> WalletDriftAsync(CancellationToken cancellationToken)
    {
        var drifts = await _queries.WalletBalanceDriftAsync(cancellationToken);

        return [.. drifts.Select(drift => new Finding(
            ReconciliationCheck.WalletBalanceDrift,
            drift.WalletId.ToString(),
            $"Wallet {drift.WalletId} reads {new Money(drift.WalletBalanceMinor)} but its ledger "
            + $"account totals {new Money(drift.LedgerBalanceMinor)}. The ledger is the truth, so "
            + "the wallet projection has drifted.",

            // The ledger is authoritative, so it is the expected figure.
            new Money(drift.LedgerBalanceMinor),
            new Money(drift.WalletBalanceMinor),
            drift.AgencyId))];
    }

    private async Task<List<Finding>> OrphanedEntriesAsync(CancellationToken cancellationToken)
    {
        var orphans = await _queries.OrphanedEntriesAsync(cancellationToken);

        return [.. orphans.Select(orphan => new Finding(
            ReconciliationCheck.OrphanedLedgerEntry,
            orphan.EntryId.ToString(),
            $"Ledger entry {orphan.EntryId} references account {orphan.AccountId}, which does not "
            + "exist. The foreign key that should make this impossible may have been dropped.",
            Money.Zero,
            new Money(orphan.AmountMinor),
            null))];
    }

    /// <summary>
    /// Holds still held after their deadline.
    /// </summary>
    /// <remarks>
    /// LINQ rather than raw SQL, because this one is a plain indexed filter rather than an
    /// aggregate over the whole ledger — <c>ix_wallet_holds_status_expires_at</c> serves it
    /// directly.
    /// </remarks>
    private async Task<List<Finding>> ExpiredHoldsAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var stale = await _db.WalletHolds
            .AsNoTracking()
            .Where(hold => hold.Status == WalletHoldStatus.Held && hold.ExpiresAt < now)
            .OrderBy(hold => hold.ExpiresAt)
            .Take(MaxHoldsReported)
            .Select(hold => new { hold.Id, hold.AgencyId, hold.AmountMinor, hold.ExpiresAt })
            .ToListAsync(cancellationToken);

        return stale.ConvertAll(hold => new Finding(
            ReconciliationCheck.ExpiredHoldOutstanding,
            hold.Id.ToString(),
            $"Hold {hold.Id} for {hold.AmountMinor} expired at {hold.ExpiresAt:u} and is still held. "
            + "The agency cannot spend these funds until it is released.",
            Money.Zero,
            hold.AmountMinor,
            hold.AgencyId));
    }

    /// <summary>
    /// Payments the gateway confirmed that never reached a wallet.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The one discrepancy the other checks cannot see. They compare the ledger with itself and
    /// with the wallets, and a payment that was never posted is in neither — so a charged payer
    /// who was never credited leaves the books perfectly balanced.
    /// </para>
    /// <para>
    /// Two ways to get here: posting failed after the gateway confirmed (it is posted in the same
    /// save as the confirmation, so this should not happen), or the payment is under review
    /// because the charge did not match the request. Both need a person. Served by the partial
    /// index <c>ix_payment_transactions_awaiting_posting</c>, which in a healthy system is empty.
    /// </para>
    /// </remarks>
    private async Task<List<Finding>> UnpostedPaymentsAsync(CancellationToken cancellationToken)
    {
        var unposted = await _db.PaymentTransactions
            .AsNoTracking()
            .Where(payment => payment.LedgerTransactionGroupId == null
                              && (payment.Status == PaymentStatus.Succeeded
                                  || payment.Status == PaymentStatus.UnderReview))
            .OrderBy(payment => payment.CreatedAt)
            .Take(MaxPaymentsReported)
            .Select(payment => new
            {
                payment.Id,
                payment.AgencyId,
                payment.Reference,
                payment.Status,
                payment.AmountMinor,
                payment.VerifiedAmountMinor,
                payment.Currency,
                payment.FailureReason,
            })
            .ToListAsync(cancellationToken);

        return unposted.ConvertAll(payment => new Finding(
            ReconciliationCheck.PaymentNotPosted,
            payment.Id.ToString(),
            $"Payment {payment.Reference} is {payment.Status} — the gateway reports {payment.VerifiedAmountMinor} "
            + $"{payment.Currency} paid — but nothing was credited. {payment.FailureReason}".TrimEnd(),

            // What the agency should have received, against what it did.
            payment.AmountMinor,
            Money.Zero,
            payment.AgencyId));
    }

    // --------------------------------------------------------------- recording

    /// <summary>
    /// Writes findings to <c>reconciliation_exceptions</c>, recognising ones already on file.
    /// </summary>
    /// <remarks>
    /// A problem that persists must not produce a new row every night: a hundred rows for one
    /// unresolved issue buries everything else. The existing row's counter goes up instead, which
    /// also answers the more useful question — how many nights has nobody looked at this.
    /// </remarks>
    private async Task<(int Created, int Recurring)> RecordAsync(
        List<Finding> findings,
        DateTimeOffset detectedAt,
        CancellationToken cancellationToken)
    {
        if (findings.Count == 0)
        {
            return (0, 0);
        }

        var subjects = findings.ConvertAll(finding => finding.Subject);

        var existing = await _db.ReconciliationExceptions
            .Where(exception => subjects.Contains(exception.Subject))
            .ToListAsync(cancellationToken);

        var created = 0;
        var recurring = 0;

        foreach (var finding in findings)
        {
            var match = existing.Find(
                exception => exception.Check == finding.Check && exception.Subject == finding.Subject);

            if (match is null)
            {
                _db.ReconciliationExceptions.Add(ReconciliationException.Record(
                    finding.Check,
                    finding.Subject,
                    finding.Detail,
                    finding.ExpectedMinor,
                    finding.ActualMinor,
                    finding.AgencyId,
                    detectedAt));

                created++;
            }
            else
            {
                match.SeenAgain(finding.ExpectedMinor, finding.ActualMinor, detectedAt);
                recurring++;
            }
        }

        await _db.SaveChangesAsync(cancellationToken);

        return (created, recurring);
    }

    // --------------------------------------------------------------- alerting

    private async Task AlertAsync(List<Finding> findings, CancellationToken cancellationToken)
    {
        // Unposted payments are P1 too, but the advice below about bypassed triggers is wrong
        // for them, so they get an alert of their own.
        var unposted = findings.FindAll(finding => finding.Check == ReconciliationCheck.PaymentNotPosted);
        var wrongBooks = findings.FindAll(
            finding => finding.Severity == ReconciliationSeverity.P1 && finding.Check != ReconciliationCheck.PaymentNotPosted);
        var behindWork = findings.FindAll(finding => finding.Severity == ReconciliationSeverity.P2);

        LogFindings(_logger, findings.Count, wrongBooks.Count + unposted.Count);

        if (unposted.Count > 0)
        {
            await _alerter.RaiseAsync(
                new PlatformAlert(
                    AlertSeverity.P1,
                    $"{unposted.Count} payment(s) were charged but never credited",
                    Describe(unposted)
                    + "\n\nEach payer was charged and their agency's wallet was not credited. For each one, check "
                    + "the payment in the gateway's dashboard, then credit the agency by an adjustment or refund "
                    + "the payer. Full detail is in payments.reconciliation_exceptions.",
                    nameof(LedgerIntegrityAudit),
                    unposted[0].AgencyId),
                cancellationToken);
        }

        if (wrongBooks.Count > 0)
        {
            await _alerter.RaiseAsync(
                new PlatformAlert(
                    AlertSeverity.P1,
                    $"Ledger integrity audit found {wrongBooks.Count} discrepancy(ies) in the books",
                    Describe(wrongBooks)
                    + "\n\nThese checks should never fire. A deferred constraint trigger refuses an "
                    + "unbalanced commit and the entries table is append-only, so something bypassed "
                    + "both — look for a recent migration, a support script, or direct database "
                    + "access.\n\nFull detail is in payments.reconciliation_exceptions.",
                    nameof(LedgerIntegrityAudit),
                    wrongBooks[0].AgencyId),
                cancellationToken);
        }

        if (behindWork.Count > 0)
        {
            await _alerter.RaiseAsync(
                new PlatformAlert(
                    AlertSeverity.P2,
                    $"{behindWork.Count} wallet hold(s) are outstanding past their deadline",
                    Describe(behindWork)
                    + "\n\nNo figure is wrong — held funds are still accounted for — but the "
                    + "affected agencies cannot spend them. Check that the hold sweeper is running.",
                    nameof(LedgerIntegrityAudit),
                    behindWork[0].AgencyId),
                cancellationToken);
        }
    }

    /// <summary>The first few findings in full, then a count. An alert nobody can read is no alert.</summary>
    private static string Describe(List<Finding> findings)
    {
        const int Shown = 10;

        var described = string.Join('\n', findings.Take(Shown).Select(finding => $"  • {finding.Detail}"));

        return findings.Count > Shown
            ? $"{described}\n  … and {findings.Count - Shown} more."
            : described;
    }

    /// <summary>One discrepancy, before it becomes a row.</summary>
    private readonly record struct Finding(
        ReconciliationCheck Check,
        string Subject,
        string Detail,
        Money ExpectedMinor,
        Money ActualMinor,
        Guid? AgencyId)
    {
        /// <summary>Taken from the check, so the domain decides and this cannot disagree with it.</summary>
        public ReconciliationSeverity Severity => Check.Severity();
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Ledger integrity audit passed every check in {ElapsedMilliseconds}ms. The books balance.")]
    private static partial void LogClean(ILogger logger, long elapsedMilliseconds);

    [LoggerMessage(Level = LogLevel.Critical,
        Message = "Ledger integrity audit found {TotalCount} discrepancy(ies), {P1Count} of them P1.")]
    private static partial void LogFindings(ILogger logger, int totalCount, int p1Count);
}
