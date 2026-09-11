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

    /// <summary>The agency is not verified, or is suspended. Carries the explanation to show.</summary>
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
        if (_tenant.AgencyId is not { } agencyId)
        {
            throw new InvalidOperationException("Funding a wallet needs a resolved tenant; this endpoint requires authentication.");
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

/// <summary>
/// Confirms a payment with the gateway and credits the wallet if it succeeded.
/// </summary>
/// <remarks>
/// Called both by the browser redirect and by the webhook. Neither is trusted for the outcome —
/// both cause us to <i>ask</i> the gateway, and only the gateway's answer credits anything.
/// </remarks>
public sealed partial class VerifyTopUpHandler
{
    private readonly IAppDbContext _db;
    private readonly IPaymentGateway _gateway;
    private readonly IPlatformScope _platformScope;
    private readonly WalletTopUpService _topUps;
    private readonly IEmailSender _email;
    private readonly TimeProvider _clock;
    private readonly ILogger<VerifyTopUpHandler> _logger;

    public VerifyTopUpHandler(
        IAppDbContext db,
        IPaymentGateway gateway,
        IPlatformScope platformScope,
        WalletTopUpService topUps,
        IEmailSender email,
        TimeProvider clock,
        ILogger<VerifyTopUpHandler> logger)
    {
        _db = db;
        _gateway = gateway;
        _platformScope = platformScope;
        _topUps = topUps;
        _email = email;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>
    /// Verifies one payment by our reference. Safe to call repeatedly.
    /// </summary>
    /// <returns>True when the wallet was credited by this call.</returns>
    public async Task<bool> HandleAsync(string reference, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);

        // A webhook arrives with no session, and a redirect may arrive with a different agency's
        // session than the one that paid. The payment reference identifies the tenant.
        using var scope = _platformScope.Enter(
            "payment verification — a gateway callback identifies its own payment, not a signed-in agency");

        var payment = await _db.PaymentTransactions
            .FirstOrDefaultAsync(p => p.Reference == reference, cancellationToken);

        if (payment is null)
        {
            LogUnknownReference(_logger, reference);
            return false;
        }

        if (payment.LedgerTransactionGroupId is not null)
        {
            // Already credited. This is the common case for a retried webhook, not an error.
            return false;
        }

        var verification = await _gateway.VerifyAsync(reference, cancellationToken);
        var now = _clock.GetUtcNow();

        if (!verification.Succeeded)
        {
            payment.MarkFailed(verification.FailureReason ?? verification.Status, now);
            await _db.SaveChangesAsync(cancellationToken);
            return false;
        }

        payment.MarkSucceeded(verification.AmountMinor, verification.FeeMinor, verification.GatewayReference, now);

        var credited = await _topUps.PostAsync(payment, cancellationToken);

        if (credited)
        {
            await SendReceiptAsync(payment, cancellationToken);
        }

        return credited;
    }

    private async Task SendReceiptAsync(PaymentTransaction payment, CancellationToken cancellationToken)
    {
        var recipient = await (
            from user in _db.Users
            join agency in _db.Agencies on user.AgencyId equals agency.Id
            where user.AgencyId == payment.AgencyId
            orderby user.CreatedAt
            select new { user.Email, user.FirstName, Name = agency.TradingName ?? agency.LegalName })
            .FirstOrDefaultAsync(cancellationToken);

        if (recipient is null)
        {
            return;
        }

        try
        {
            await _email.SendAsync(
                TopUpReceiptEmail.Create(
                    recipient.Email,
                    recipient.FirstName,
                    payment.VerifiedAmountMinor!.Value,
                    payment.Currency,
                    payment.Reference),
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The money is in the wallet. A receipt that did not send is a support question, not
            // a reason to fail a callback the gateway will then retry.
            LogReceiptFailed(_logger, ex, payment.Reference);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Verification asked for unknown payment reference {Reference}.")]
    private static partial void LogUnknownReference(ILogger logger, string reference);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "Top-up receipt for {Reference} could not be delivered. The wallet is credited.")]
    private static partial void LogReceiptFailed(ILogger logger, Exception exception, string reference);
}
