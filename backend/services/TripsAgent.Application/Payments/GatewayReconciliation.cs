using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TripsAgent.Application.Notifications;
using TripsAgent.Application.Persistence;
using TripsAgent.Application.Tenancy;
using TripsAgent.Domain.Common;
using TripsAgent.Domain.Payments;

namespace TripsAgent.Application.Payments;

/// <summary>The daily reconciliation, as the job runner sees it.</summary>
public interface IGatewayReconciliation
{
    /// <summary>Reconciles yesterday, in Lagos.</summary>
    public Task<ReconciliationRun> RunYesterdayAsync(CancellationToken cancellationToken = default);

    /// <summary>Reconciles one Lagos day. Safe to run again for any past day.</summary>
    public Task<ReconciliationRun> RunAsync(DateOnly businessDate, CancellationToken cancellationToken = default);
}

/// <summary>
/// Matches what the gateway settled against what our books say was paid, one Lagos day at a time.
/// </summary>
/// <remarks>
/// <para>
/// <b>It reads and it reports. It never corrects.</b> A reconciler that writes to the ledger it
/// reconciles cannot detect its own mistakes, and an automatic correction destroys the evidence of
/// what went wrong. Every mismatch becomes a <see cref="ReconciliationException"/> a person works
/// through.
/// </para>
/// <para>
/// <b>Idempotent.</b> One <see cref="ReconciliationRun"/> row per gateway per day — a unique index,
/// not the job's memory — re-opened in place on a re-run. Exceptions are keyed on their check and a
/// subject naming the day and the transaction, so a second run of the same day bumps a counter on
/// each exception it finds again rather than raising it twice.
/// </para>
/// <para>What it checks, for the settlements the gateway paid out that day:</para>
/// <list type="bullet">
///   <item>Each settlement's own arithmetic: its lines add up to its gross and fees, and gross less
///     fees is the net it paid. <b>One kobo out is an exception</b>; a tolerance is how a systematic
///     leak stays invisible.</item>
///   <item>Each line matches a payment of ours by reference, to the kobo, and appears once.</item>
///   <item>Every payment we recorded as succeeded <see cref="SettlementLagDays"/> days before has
///     been settled by now. Earlier days are skipped: a payment still inside the lag is late, not
///     missing, and a queue of false positives is a queue nobody reads.</item>
/// </list>
/// </remarks>
public sealed partial class GatewayReconciliation : IGatewayReconciliation
{
    /// <summary>
    /// How many days a payment may take to settle before its absence is worth an exception.
    /// </summary>
    /// <remarks>
    /// Paystack settles Nigerian cards on T+1 working days; three calendar days covers a Friday
    /// payment settled on Monday.
    /// </remarks>
    public const int SettlementLagDays = 3;

    private readonly IAppDbContext _db;
    private readonly IGatewayBackOffice _gateway;
    private readonly IPaymentGateway _payments;
    private readonly IPlatformScope _platformScope;
    private readonly IPlatformAlerter _alerter;
    private readonly TimeProvider _clock;
    private readonly ILogger<GatewayReconciliation> _logger;

    public GatewayReconciliation(
        IAppDbContext db,
        IGatewayBackOffice gateway,
        IPaymentGateway payments,
        IPlatformScope platformScope,
        IPlatformAlerter alerter,
        TimeProvider clock,
        ILogger<GatewayReconciliation> logger)
    {
        _db = db;
        _gateway = gateway;
        _payments = payments;
        _platformScope = platformScope;
        _alerter = alerter;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>The zone reconciliation days are counted in.</summary>
    public static TimeZoneInfo Lagos { get; } = TimeZoneInfo.FindSystemTimeZoneById("Africa/Lagos");

    public Task<ReconciliationRun> RunYesterdayAsync(CancellationToken cancellationToken = default)
    {
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(_clock.GetUtcNow(), Lagos).DateTime);
        return RunAsync(today.AddDays(-1), cancellationToken);
    }

    public async Task<ReconciliationRun> RunAsync(DateOnly businessDate, CancellationToken cancellationToken = default)
    {
        // Every agency's payments against the platform's own settlements: the job is cross-tenant by nature.
        using var scope = _platformScope.Enter("daily gateway reconciliation — settlements span every agency");

        var (windowStart, windowEnd) = DayInUtc(businessDate);
        var now = _clock.GetUtcNow();

        var run = await _db.ReconciliationRuns.FirstOrDefaultAsync(
            r => r.Type == ReconciliationRunType.GatewaySettlement && r.Gateway == _payments.Name && r.BusinessDate == businessDate,
            cancellationToken);

        if (run is null)
        {
            run = ReconciliationRun.Start(
                ReconciliationRunType.GatewaySettlement, _payments.Name, businessDate, windowStart, windowEnd, "NGN", now);
            _db.ReconciliationRuns.Add(run);
        }
        else
        {
            run.Restart(now);
        }

        // Written before any work, so a run that dies is a Running or Failed row rather than an absence.
        await _db.SaveChangesAsync(cancellationToken);

        try
        {
            await ReconcileAsync(run, businessDate, windowStart, windowEnd, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogRunFailed(_logger, ex, businessDate);

            _db.ChangeTracker.Clear();
            var failed = await _db.ReconciliationRuns.FirstAsync(r => r.Id == run.Id, cancellationToken);
            failed.Fail(ex.Message, _clock.GetUtcNow());
            await _db.SaveChangesAsync(cancellationToken);

            await AlertAsync(
                AlertSeverity.P1,
                $"Gateway reconciliation for {businessDate:yyyy-MM-dd} failed",
                $"The run stopped with: {ex.Message}\n\nNothing was reconciled for that day. It is safe to run again.",
                cancellationToken);

            return failed;
        }

        return run;
    }

    private async Task ReconcileAsync(
        ReconciliationRun run,
        DateOnly businessDate,
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd,
        CancellationToken cancellationToken)
    {
        var (lagStart, _) = DayInUtc(businessDate.AddDays(-SettlementLagDays));

        // One read from the lagged day to the end of this one: this day's settlements are checked in
        // full, and the whole span answers "has that older payment settled by now".
        var settlements = await _gateway.SettlementsAsync(lagStart, windowEnd, cancellationToken);
        var today = settlements.Where(s => s.SettledAt >= windowStart && s.SettledAt < windowEnd).ToList();

        var findings = new List<Finding>();
        var day = businessDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

        foreach (var settlement in today)
        {
            CheckArithmetic(settlement, findings);
        }

        var lines = today.SelectMany(s => s.Lines).ToList();
        var references = lines.Select(line => line.Reference).Distinct().ToList();

        var payments = await _db.PaymentTransactions
            .AsNoTracking()
            .Where(p => references.Contains(p.Reference))
            .ToDictionaryAsync(p => p.Reference, cancellationToken);

        var matched = 0;
        var ledgerGross = Money.Zero;

        foreach (var group in lines.GroupBy(line => line.Reference))
        {
            var line = group.First();

            if (group.Count() > 1)
            {
                findings.Add(new Finding(
                    ReconciliationCheck.GatewaySettlementUnbalanced,
                    $"duplicate:{line.Reference}",
                    $"Payment {line.Reference} appears {group.Count()} times in the settlements of {day}. The gateway may "
                    + "have paid it out twice, or our books will count it twice.",
                    line.AmountMinor,
                    new Money(group.Sum(l => l.AmountMinor.AmountMinor)),
                    null));
            }

            if (!payments.TryGetValue(line.Reference, out var payment))
            {
                findings.Add(new Finding(
                    ReconciliationCheck.GatewayTransactionUnknown,
                    $"settled:{line.Reference}",
                    $"The gateway settled {line.AmountMinor} {line.Currency} for reference '{line.Reference}' on {day}, "
                    + "and we have no payment with that reference. Money arrived that our books cannot explain.",
                    Money.Zero,
                    line.AmountMinor,
                    null));

                continue;
            }

            // What the gateway told us was charged when it verified the payment, which is the figure
            // it settles; what we asked for only when it never said.
            var recorded = payment.VerifiedAmountMinor ?? payment.AmountMinor;
            ledgerGross += recorded;

            if (recorded != line.AmountMinor || payment.Status != PaymentStatus.Succeeded)
            {
                findings.Add(new Finding(
                    ReconciliationCheck.GatewayAmountMismatch,
                    $"settled:{line.Reference}",
                    $"Payment {line.Reference} is {payment.Status} for {recorded} {payment.Currency} in our books, and the "
                    + $"gateway settled {line.AmountMinor} {line.Currency} for it on {day}.",
                    recorded,
                    line.AmountMinor,
                    payment.AgencyId));

                continue;
            }

            matched++;
        }

        await UnsettledAsync(businessDate, settlements, findings, cancellationToken);

        var raised = await RecordAsync(run.Id, findings, cancellationToken);

        run.Complete(
            recordsExamined: lines.Count,
            recordsMatched: matched,
            exceptionsRaised: raised,
            gatewayGross: new Money(today.Sum(s => s.GrossMinor.AmountMinor)),
            gatewayFees: new Money(today.Sum(s => s.FeesMinor.AmountMinor)),
            gatewayNet: new Money(today.Sum(s => s.NetMinor.AmountMinor)),
            ledgerGross: ledgerGross,
            _clock.GetUtcNow());

        await _db.SaveChangesAsync(cancellationToken);

        if (findings.Count > 0)
        {
            var worst = findings.Any(f => f.Check.Severity() == ReconciliationSeverity.P1) ? AlertSeverity.P1 : AlertSeverity.P2;

            await AlertAsync(
                worst,
                $"Gateway reconciliation for {day}: {findings.Count} mismatch(es)",
                string.Join('\n', findings.Take(20).Select(f => $"- {f.Check}: {f.Detail}"))
                + (findings.Count > 20 ? $"\n…and {findings.Count - 20} more in the reconciliation queue." : string.Empty),
                cancellationToken);
        }
    }

    /// <summary>Gross less fees is net, and the lines add up to both. To the kobo.</summary>
    private static void CheckArithmetic(GatewaySettlement settlement, List<Finding> findings)
    {
        var linesGross = new Money(settlement.Lines.Sum(line => line.AmountMinor.AmountMinor));
        var linesFees = new Money(settlement.Lines.Sum(line => line.FeeMinor.AmountMinor));

        if (settlement.GrossMinor - settlement.FeesMinor != settlement.NetMinor)
        {
            findings.Add(new Finding(
                ReconciliationCheck.GatewaySettlementUnbalanced,
                $"settlement:{settlement.SettlementId}:net",
                $"Settlement {settlement.SettlementId} reports gross {settlement.GrossMinor} less fees {settlement.FeesMinor}, "
                + $"which is {settlement.GrossMinor - settlement.FeesMinor}, but paid out {settlement.NetMinor}.",
                settlement.GrossMinor - settlement.FeesMinor,
                settlement.NetMinor,
                null));
        }

        if (linesGross != settlement.GrossMinor || linesFees != settlement.FeesMinor)
        {
            findings.Add(new Finding(
                ReconciliationCheck.GatewaySettlementUnbalanced,
                $"settlement:{settlement.SettlementId}:lines",
                $"Settlement {settlement.SettlementId} reports gross {settlement.GrossMinor} and fees {settlement.FeesMinor}; "
                + $"its {settlement.Lines.Count} transactions add up to {linesGross} and {linesFees}.",
                settlement.GrossMinor,
                linesGross,
                null));
        }
    }

    /// <summary>Payments we recorded as succeeded on the lagged day that no settlement has paid by now.</summary>
    private async Task UnsettledAsync(
        DateOnly businessDate,
        IReadOnlyList<GatewaySettlement> settlements,
        List<Finding> findings,
        CancellationToken cancellationToken)
    {
        var lagged = businessDate.AddDays(-SettlementLagDays);
        var (from, to) = DayInUtc(lagged);

        var settled = settlements.SelectMany(s => s.Lines).Select(line => line.Reference).ToHashSet(StringComparer.Ordinal);

        var recorded = await _db.PaymentTransactions
            .AsNoTracking()
            .Where(p => p.Status == PaymentStatus.Succeeded && p.VerifiedAt >= from && p.VerifiedAt < to)
            .Select(p => new { p.Reference, p.AmountMinor, p.VerifiedAmountMinor, p.Currency, p.AgencyId })
            .ToListAsync(cancellationToken);

        foreach (var payment in recorded.Where(p => !settled.Contains(p.Reference)))
        {
            var amount = payment.VerifiedAmountMinor ?? payment.AmountMinor;

            findings.Add(new Finding(
                ReconciliationCheck.GatewayPaymentUnsettled,
                $"unsettled:{payment.Reference}",
                $"Payment {payment.Reference} for {amount} {payment.Currency} succeeded on {lagged:yyyy-MM-dd} and no gateway "
                + $"settlement up to {businessDate:yyyy-MM-dd} includes it.",
                amount,
                Money.Zero,
                payment.AgencyId));
        }
    }

    /// <summary>Writes each finding once, keyed on check and subject; a repeat is counted.</summary>
    /// <returns>How many exceptions this run raised or found again.</returns>
    private async Task<int> RecordAsync(Guid runId, List<Finding> findings, CancellationToken cancellationToken)
    {
        if (findings.Count == 0)
        {
            return 0;
        }

        // One finding per (check, subject) even if this run produced it twice.
        var distinct = findings.GroupBy(f => (f.Check, f.Subject)).Select(g => g.First()).ToList();
        var subjects = distinct.ConvertAll(f => f.Subject);

        var existing = await _db.ReconciliationExceptions
            .Where(e => subjects.Contains(e.Subject))
            .ToListAsync(cancellationToken);

        var now = _clock.GetUtcNow();

        foreach (var finding in distinct)
        {
            var match = existing.Find(e => e.Check == finding.Check && e.Subject == finding.Subject);

            if (match is null)
            {
                _db.ReconciliationExceptions.Add(ReconciliationException.Record(
                    finding.Check, finding.Subject, Clip(finding.Detail), finding.Expected, finding.Actual,
                    finding.AgencyId, now, runId));
            }
            else
            {
                match.SeenAgain(finding.Expected, finding.Actual, now, runId);
            }
        }

        return distinct.Count;
    }

    /// <summary>A Lagos calendar day as a half-open UTC window. Converted here, at the boundary.</summary>
    public static (DateTimeOffset Start, DateTimeOffset End) DayInUtc(DateOnly day)
    {
        var start = day.ToDateTime(TimeOnly.MinValue);
        var offset = Lagos.GetUtcOffset(start);

        var utcStart = new DateTimeOffset(start, offset).ToUniversalTime();
        return (utcStart, utcStart.AddDays(1));
    }

    private static string Clip(string detail) => detail.Length <= 2000 ? detail : detail[..2000];

    private async Task AlertAsync(AlertSeverity severity, string title, string detail, CancellationToken cancellationToken)
    {
        try
        {
            await _alerter.RaiseAsync(new PlatformAlert(severity, title, detail, nameof(GatewayReconciliation)), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogAlertFailed(_logger, ex, title);
        }
    }

    private sealed record Finding(
        ReconciliationCheck Check,
        string Subject,
        string Detail,
        Money Expected,
        Money Actual,
        Guid? AgencyId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Gateway reconciliation for {BusinessDate} failed.")]
    private static partial void LogRunFailed(ILogger logger, Exception exception, DateOnly businessDate);

    [LoggerMessage(Level = LogLevel.Critical, Message = "A reconciliation alert could not be raised: {Title}")]
    private static partial void LogAlertFailed(ILogger logger, Exception exception, string title);
}
