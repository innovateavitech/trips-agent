using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TripsAgent.Application.Notifications;
using TripsAgent.Application.Persistence;
using TripsAgent.Application.Tenancy;
using TripsAgent.Domain.Common;
using TripsAgent.Domain.Payments;

namespace TripsAgent.Application.Payments;

/// <summary>The limits on a single top-up.</summary>
/// <remarks>
/// A floor because a gateway's fixed fee can exceed a tiny top-up, so the platform would pay for
/// the privilege of receiving it. A ceiling because a mistyped amount — an extra zero or three —
/// should be a rejection rather than a refund conversation.
/// </remarks>
public static class TopUpLimits
{
    public static readonly Money Minimum = Money.FromMajor(500);

    public static readonly Money Maximum = Money.FromMajor(10_000_000);
}

/// <summary>What happened when an agent asked to add funds.</summary>
public abstract record StartTopUpOutcome
{
    private StartTopUpOutcome()
    {
    }

    /// <summary>Send the agent to <paramref name="AuthorizationUrl"/> to pay.</summary>
    public sealed record Started(string AuthorizationUrl, string Reference) : StartTopUpOutcome;

    /// <summary>
    /// The caller has no agency, or the agency is not verified or is suspended. Carries the
    /// explanation to show.
    /// </summary>
    public sealed record NotPermitted(string Reason) : StartTopUpOutcome;

    public sealed record AmountOutOfRange(string Reason) : StartTopUpOutcome;

    /// <summary>The gateway could not be reached. Nothing was charged.</summary>
    public sealed record GatewayUnavailable : StartTopUpOutcome;
}

/// <summary>
/// Starts a wallet top-up: checks the agency may, records the attempt, and asks the gateway for
/// a page to send the agent to.
/// </summary>
public sealed partial class StartTopUpHandler
{
    /// <summary>Shown to a caller with no agency — a Trips staff account, for instance.</summary>
    public const string NoAgencyReason = "Only a signed-in travel agency can add funds to a wallet.";

    private readonly IAppDbContext _db;
    private readonly IPaymentGateway _gateway;
    private readonly ITenantContext _tenant;
    private readonly TimeProvider _clock;
    private readonly TopUpCallbackUrl _callback;
    private readonly ILogger<StartTopUpHandler> _logger;

    public StartTopUpHandler(
        IAppDbContext db,
        IPaymentGateway gateway,
        ITenantContext tenant,
        TimeProvider clock,
        TopUpCallbackUrl callback,
        ILogger<StartTopUpHandler> logger)
    {
        _db = db;
        _gateway = gateway;
        _tenant = tenant;
        _clock = clock;
        _callback = callback;
        _logger = logger;
    }

    public async Task<StartTopUpOutcome> HandleAsync(Money amount, CancellationToken cancellationToken = default)
    {
        // A platform user holds wallet.fund through the super-admin role, but has no wallet to
        // fund. Refused here as well as at the endpoint, so no route can reach a crash.
        if (_tenant.AgencyId is not { } agencyId)
        {
            return new StartTopUpOutcome.NotPermitted(NoAgencyReason);
        }

        var agency = await _db.Agencies.FirstAsync(a => a.Id == agencyId, cancellationToken);

        // FRD §2.5 precondition. The same policy the onboarding screen uses to grey the button
        // out, so the explanation the agent reads is the same one enforced here.
        var funding = WalletFundingPolicy.For(agency);

        if (!funding.IsAllowed)
        {
            return new StartTopUpOutcome.NotPermitted(funding.Reason!);
        }

        if (amount < TopUpLimits.Minimum)
        {
            return new StartTopUpOutcome.AmountOutOfRange(
                $"The smallest top-up is {TopUpLimits.Minimum} {agency.BaseCurrency}.");
        }

        if (amount > TopUpLimits.Maximum)
        {
            return new StartTopUpOutcome.AmountOutOfRange(
                $"The largest single top-up is {TopUpLimits.Maximum} {agency.BaseCurrency}. "
                + "Add funds in more than one payment, or contact us for a bank transfer.");
        }

        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == _tenant.UserId, cancellationToken);

        // Ours, not the gateway's — the gateway quotes it back on every webhook and verification,
        // and it is how we find our own row again.
        //
        // Not truncated, and the agency id is deliberately not in it. An earlier version was
        // $"TA-{agencyId:N}-{guid:N}" cut to 40 characters, which left four hex digits of the
        // guid — and the leading digits of a version 7 guid are a millisecond timestamp, so two
        // top-ups in the same instant produced the same reference and the second one threw.
        //
        // Keeping the agency out also keeps a tenant identifier off the payer's bank statement
        // and out of the gateway's dashboard. The payment row already records whose it is.
        var reference = $"TA-{Guid.CreateVersion7():N}";

        var payment = PaymentTransaction.Start(
            agencyId, _tenant.UserId, PaymentPurpose.WalletTopUp, amount, agency.BaseCurrency, reference);

        _db.PaymentTransactions.Add(payment);

        // Saved before the gateway is called. If the call times out, the attempt still exists and
        // the webhook that may already be on its way has a row to find.
        await _db.SaveChangesAsync(cancellationToken);

        try
        {
            var initialization = await _gateway.InitializeAsync(
                reference,
                amount,
                agency.BaseCurrency,
                user?.Email ?? $"billing@{agency.Slug}.invalid",
                _callback.Value,
                cancellationToken);

            payment.RecordGatewayReference(initialization.GatewayReference);
            await _db.SaveChangesAsync(cancellationToken);

            return new StartTopUpOutcome.Started(initialization.AuthorizationUrl, reference);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogInitializeFailed(_logger, ex, reference);

            payment.MarkFailed("The payment gateway could not be reached.", _clock.GetUtcNow());
            await _db.SaveChangesAsync(cancellationToken);

            return new StartTopUpOutcome.GatewayUnavailable();
        }
    }

    [LoggerMessage(Level = LogLevel.Error,
        Message = "Could not initialise payment {Reference} with the gateway. Nothing was charged.")]
    private static partial void LogInitializeFailed(ILogger logger, Exception exception, string reference);
}

/// <summary>Where the gateway sends the payer back to. Configuration, since the console owns it.</summary>
public sealed class TopUpCallbackUrl
{
    public TopUpCallbackUrl(string value) =>
        Value = string.IsNullOrWhiteSpace(value) ? "https://localhost:5173/wallet/top-up/complete" : value;

    public string Value { get; }
}

/// <summary>What one verification did.</summary>
public enum TopUpVerificationOutcome
{
    /// <summary>This call credited the wallet.</summary>
    Credited = 1,

    /// <summary>Somebody — a webhook, a redirect, an earlier retry — had already credited it.</summary>
    AlreadyCredited = 2,

    /// <summary>The gateway has no final answer yet. Nothing changed; ask again later.</summary>
    StillPending = 3,

    /// <summary>The gateway says it finally failed. Recorded as failed.</summary>
    Failed = 4,

    /// <summary>
    /// The payer was charged, but not in a way we can credit automatically. Held for a person,
    /// and an alert raised.
    /// </summary>
    UnderReview = 5,

    /// <summary>No payment of ours has this reference.</summary>
    UnknownReference = 6,
}

/// <summary>What the agent console is told about a top-up it has just returned from.</summary>
public abstract record AgentTopUpCheck
{
    private AgentTopUpCheck()
    {
    }

    /// <param name="Status"><c>succeeded</c>, <c>pending</c> or <c>failed</c>.</param>
    /// <param name="AmountMinor">The amount of the top-up — what is, or will be, credited.</param>
    public sealed record Found(string Status, string Reference, long AmountMinor) : AgentTopUpCheck;

    /// <summary>No payment with that reference belongs to the caller's agency.</summary>
    public sealed record NotFound : AgentTopUpCheck;

    /// <summary>The caller has no agency, so has no top-ups.</summary>
    public sealed record NoAgency : AgentTopUpCheck;
}

/// <summary>
/// Confirms a payment with the gateway and credits the wallet if it succeeded.
/// </summary>
/// <remarks>
/// <para>
/// Called both by the browser redirect and by the webhook. Neither is trusted for the outcome —
/// both cause us to <i>ask</i> the gateway, and only the gateway's answer credits anything.
/// </para>
/// <para>
/// The two routinely run at the same moment for the same payment, and the gateway call between
/// "is it posted?" and "post it" takes hundreds of milliseconds. So the loser of that race is
/// normal: its save is refused by the database (see <see cref="WalletTopUpService"/>), and this
/// handler discards the refused changes, re-reads the payment, and reports it as already
/// credited rather than failing.
/// </para>
/// </remarks>
public sealed partial class VerifyTopUpHandler
{
    /// <summary>
    /// How many times to try posting when another writer keeps getting there first.
    /// </summary>
    /// <remarks>
    /// A conflict with another posting of the same payment ends the loop on the first re-read.
    /// Retrying is for the other kind: a booking debiting the same wallet in the same instant,
    /// which changes the wallet's version without posting this payment.
    /// </remarks>
    public const int MaxPostAttempts = 3;

    private readonly IAppDbContext _db;
    private readonly IPaymentGateway _gateway;
    private readonly ITenantContext _tenant;
    private readonly IPlatformScope _platformScope;
    private readonly WalletTopUpService _topUps;
    private readonly INotifier _notifications;
    private readonly IPlatformAlerter _alerter;
    private readonly IUniqueViolationDetector _uniqueViolations;
    private readonly TimeProvider _clock;
    private readonly ILogger<VerifyTopUpHandler> _logger;

    public VerifyTopUpHandler(
        IAppDbContext db,
        IPaymentGateway gateway,
        ITenantContext tenant,
        IPlatformScope platformScope,
        WalletTopUpService topUps,
        INotifier notifications,
        IPlatformAlerter alerter,
        IUniqueViolationDetector uniqueViolations,
        TimeProvider clock,
        ILogger<VerifyTopUpHandler> logger)
    {
        _db = db;
        _gateway = gateway;
        _tenant = tenant;
        _platformScope = platformScope;
        _topUps = topUps;
        _notifications = notifications;
        _alerter = alerter;
        _uniqueViolations = uniqueViolations;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>
    /// Verifies one payment by our reference, whoever it belongs to. Safe to call repeatedly and
    /// concurrently.
    /// </summary>
    /// <remarks>
    /// For callers with no agency of their own — the webhook. An agent's request goes through
    /// <see cref="CheckForAgentAsync"/>, which first proves the payment is theirs.
    /// </remarks>
    /// <exception cref="PaymentGatewayException">
    /// The gateway could not be asked, or its answer was unusable. The payment is left as it was.
    /// </exception>
    public async Task<TopUpVerificationOutcome> HandleAsync(string reference, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);

        // A webhook arrives with no session of its own. The payment reference identifies the
        // tenant, and the agent path has already checked ownership before getting here.
        using var scope = _platformScope.Enter(
            "payment verification — a gateway callback identifies its own payment, not a signed-in agency");

        var payment = await LoadAsync(reference, cancellationToken);

        if (payment is null)
        {
            LogUnknownReference(_logger, reference);
            return TopUpVerificationOutcome.UnknownReference;
        }

        if (payment.LedgerTransactionGroupId is not null)
        {
            // Already credited. This is the common case for a retried webhook, not an error.
            return TopUpVerificationOutcome.AlreadyCredited;
        }

        if (payment.Status == PaymentStatus.UnderReview)
        {
            // A person is deciding. Asking the gateway again cannot change that.
            return TopUpVerificationOutcome.UnderReview;
        }

        var verification = await _gateway.VerifyAsync(reference, cancellationToken);
        var now = _clock.GetUtcNow();

        switch (verification.Outcome)
        {
            case GatewayPaymentOutcome.Pending:
                // Not failed: an abandoned page can be resumed and a transfer can settle later.
                // The row stays Pending, so the console keeps saying "pending".
                return TopUpVerificationOutcome.StillPending;

            case GatewayPaymentOutcome.Failed:
                payment.MarkFailed(verification.FailureReason ?? verification.Status, now);
                await _db.SaveChangesAsync(cancellationToken);
                return TopUpVerificationOutcome.Failed;

            case GatewayPaymentOutcome.Succeeded:
                return await CreditAsync(payment, verification, now, cancellationToken);

            default:
                throw new InvalidOperationException($"Unhandled gateway outcome {verification.Outcome}.");
        }
    }

    /// <summary>
    /// What the agent console asks after the payment page: verify the caller's own payment, and
    /// say where it stands.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Ownership is checked first, through the ordinary tenant filter, and nothing else happens
    /// for a payment that is not the caller's — no gateway call, no write. A reference is not a
    /// secret: it sits in the payer's browser history and on their receipt.
    /// </para>
    /// <para>
    /// A gateway that cannot be reached is not the agent's problem. They have probably just been
    /// charged, and an error invites them to pay again. The payment stays pending, the webhook
    /// finishes the job, and the console is told "pending".
    /// </para>
    /// </remarks>
    public async Task<AgentTopUpCheck> CheckForAgentAsync(string reference, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);

        if (!_tenant.HasTenant)
        {
            return new AgentTopUpCheck.NoAgency();
        }

        // No platform scope here, deliberately: the tenant filter decides.
        var owned = await _db.PaymentTransactions
            .AsNoTracking()
            .AnyAsync(p => p.Reference == reference, cancellationToken);

        if (!owned)
        {
            return new AgentTopUpCheck.NotFound();
        }

        try
        {
            await HandleAsync(reference, cancellationToken);
        }
        catch (Exception ex) when (ex is PaymentGatewayException or DbUpdateException)
        {
            // Leave whatever the failed attempt staged unsaved; the read below comes from the
            // database, which is where the truth is.
            _db.ChangeTracker.Clear();
            LogAgentVerifyDeferred(_logger, ex, reference);
        }

        // Read back rather than trusting HandleAsync's result: a webhook having beaten us to it
        // is a success for the agent even though this call did nothing.
        var payment = await _db.PaymentTransactions
            .AsNoTracking()
            .FirstAsync(p => p.Reference == reference, cancellationToken);

        return new AgentTopUpCheck.Found(StatusFor(payment), payment.Reference, payment.AmountMinor.AmountMinor);
    }

    /// <summary>The three words the console understands.</summary>
    /// <remarks>
    /// "succeeded" only once the money is in the wallet. A payment the gateway confirmed but that
    /// has not been posted, or is held for review, is "pending" — telling an agent they have
    /// funds they cannot spend is its own support call.
    /// </remarks>
    private static string StatusFor(PaymentTransaction payment) => payment switch
    {
        { LedgerTransactionGroupId: not null } => "succeeded",
        { Status: PaymentStatus.Failed or PaymentStatus.Abandoned } => "failed",
        _ => "pending",
    };

    private async Task<TopUpVerificationOutcome> CreditAsync(
        PaymentTransaction payment,
        GatewayVerification verification,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            if (payment.LedgerTransactionGroupId is not null)
            {
                return TopUpVerificationOutcome.AlreadyCredited;
            }

            if (payment.Status == PaymentStatus.UnderReview)
            {
                return TopUpVerificationOutcome.UnderReview;
            }

            payment.MarkSucceeded(
                verification.AmountMinor,
                verification.FeeMinor,
                verification.Currency,
                verification.GatewayReference,
                now);

            try
            {
                if (payment.Status == PaymentStatus.UnderReview)
                {
                    await _db.SaveChangesAsync(cancellationToken);
                    await AlertUnderReviewAsync(payment, cancellationToken);
                    return TopUpVerificationOutcome.UnderReview;
                }

                if (!await _topUps.PostAsync(payment, cancellationToken))
                {
                    return TopUpVerificationOutcome.AlreadyCredited;
                }

                await SendReceiptAsync(payment, cancellationToken);
                return TopUpVerificationOutcome.Credited;
            }
            catch (DbUpdateException ex) when (ex is DbUpdateConcurrencyException || _uniqueViolations.IsUniqueViolation(ex))
            {
                // Another writer committed first. Its save and ours cannot both stand, and the
                // database has already rolled ours back — but our rejected changes are still
                // tracked, and the next save on this context would send them again. Discard them.
                _db.ChangeTracker.Clear();

                LogPostingConflict(_logger, payment.Reference, attempt);

                if (attempt >= MaxPostAttempts)
                {
                    throw;
                }

                payment = await LoadAsync(payment.Reference, cancellationToken)
                    ?? throw new InvalidOperationException($"Payment {verification.GatewayReference} disappeared mid-verification.");
            }
        }
    }

    private Task<PaymentTransaction?> LoadAsync(string reference, CancellationToken cancellationToken) =>
        _db.PaymentTransactions.FirstOrDefaultAsync(p => p.Reference == reference, cancellationToken);

    /// <summary>
    /// Tells the platform team a payer was charged and not credited.
    /// </summary>
    /// <remarks>
    /// P1, because an agency is short money it paid. Failures to alert are logged, not thrown:
    /// the payment is already safely recorded as under review, and the nightly audit reports it
    /// again until someone acts.
    /// </remarks>
    private async Task AlertUnderReviewAsync(PaymentTransaction payment, CancellationToken cancellationToken)
    {
        try
        {
            await _alerter.RaiseAsync(
                new PlatformAlert(
                    AlertSeverity.P1,
                    $"Top-up {payment.Reference} was charged but cannot be credited automatically",
                    $"The gateway confirmed payment {payment.Reference}, but not one the wallet can be credited "
                    + $"from: {payment.FailureReason}\n\n"
                    + $"Requested {payment.AmountMinor} {payment.Currency}; the gateway reports "
                    + $"{payment.VerifiedAmountMinor} paid.\n\n"
                    + "Nothing was credited. Check the payment in the gateway's dashboard, then either credit "
                    + "the agency by an adjustment or refund the payer. The payment is in "
                    + "payments.payment_transactions with status UnderReview.",
                    nameof(VerifyTopUpHandler),
                    payment.AgencyId),
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogAlertFailed(_logger, ex, payment.Reference);
        }
    }

    /// <summary>
    /// Queues the receipt for the agency's owner. The Worker sends it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A second, small save straight after the posting, rather than part of it: the posting is the
    /// money path and owns its own transaction, retry loop and conflict handling, and a receipt has
    /// no business being able to fail it. The cost is a narrow window — the process dying between
    /// the two saves — in which the money lands and the receipt is never queued.
    /// </para>
    /// <para>
    /// Keyed on the payment, so a webhook and the agent's own check racing to credit the same
    /// payment can never queue two receipts.
    /// </para>
    /// </remarks>
    private async Task SendReceiptAsync(PaymentTransaction payment, CancellationToken cancellationToken)
    {
        try
        {
            var recipient = await _db.Users
                .Where(user => user.AgencyId == payment.AgencyId)
                .OrderBy(user => user.CreatedAt)
                .Select(user => new { user.Id, user.Email, user.FirstName })
                .FirstOrDefaultAsync(cancellationToken);

            if (recipient is null)
            {
                return;
            }

            await _notifications.QueueEmailAsync(
                new EmailNotificationRequest(
                    payment.AgencyId,
                    NotificationTemplateCatalog.WalletTopUpReceipt,
                    recipient.Email,
                    recipient.FirstName,
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        // What was credited, which is what was asked for — not the gateway's figure,
                        // which includes the fee when the payer bears it.
                        ["amount"] = $"{payment.Currency} {payment.AmountMinor}",
                        ["reference"] = payment.Reference,
                    },
                    DedupeKey: $"{NotificationTemplateCatalog.WalletTopUpReceipt}:{payment.Id}",
                    RecipientUserId: recipient.Id),
                cancellationToken);

            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The money is in the wallet, and nothing after the posting commits may fail the credit.
            // Deliberately every exception, not just DbUpdateException: a database blip that outlasts
            // EF's retries arrives as RetryLimitExceededException, and a failed read as an
            // NpgsqlException. Either escaping would fail a callback the gateway then retries, or
            // show an agent who has been charged and credited an error that invites them to pay
            // again. A missing receipt is a support question. Discard what was staged so no later
            // save on this context tries it again.
            _db.ChangeTracker.Clear();
            LogReceiptFailed(_logger, ex, payment.Reference);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Verification asked for unknown payment reference {Reference}.")]
    private static partial void LogUnknownReference(ILogger logger, string reference);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Posting {Reference} lost a race with another writer (attempt {Attempt}); re-reading.")]
    private static partial void LogPostingConflict(ILogger logger, string reference, int attempt);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Could not verify {Reference} for the agent now; reporting it as pending for the webhook to finish.")]
    private static partial void LogAgentVerifyDeferred(ILogger logger, Exception exception, string reference);

    [LoggerMessage(Level = LogLevel.Critical,
        Message = "Could not raise the alert for top-up {Reference}, held for review. The nightly audit will report it.")]
    private static partial void LogAlertFailed(ILogger logger, Exception exception, string reference);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "Top-up receipt for {Reference} could not be delivered. The wallet is credited.")]
    private static partial void LogReceiptFailed(ILogger logger, Exception exception, string reference);
}
