using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TripsAgent.Application.Notifications;
using TripsAgent.Application.Persistence;
using TripsAgent.Application.Tenancy;
using TripsAgent.Domain.Payments;

namespace TripsAgent.Application.Payments;

/// <summary>What the receiver did with a delivery.</summary>
public enum WebhookOutcome
{
    /// <summary>Signature did not verify. Nothing was recorded and nothing was acted on.</summary>
    Rejected = 1,

    /// <summary>Seen before. Nothing done, which is the whole point.</summary>
    Duplicate = 2,

    /// <summary>Recorded and queued for processing.</summary>
    Accepted = 3,

    /// <summary>A type we do not act on. Recorded, not an error.</summary>
    Ignored = 4,
}

/// <summary>Hands a recorded webhook to the background, so the receiver can answer immediately.</summary>
/// <remarks>
/// A port rather than Hangfire directly, because <c>Application</c> does not depend on a job
/// runner. The API enqueues; the Worker executes.
/// </remarks>
public interface IWebhookDispatcher
{
    public Task EnqueueAsync(Guid webhookEventId, CancellationToken cancellationToken = default);
}

/// <summary>The processing half of the webhook path, as the job runner sees it.</summary>
public interface IPaymentWebhookProcessor
{
    /// <summary>Processes one recorded event, if no other worker has it.</summary>
    public Task ProcessAsync(Guid webhookEventId, CancellationToken cancellationToken = default);

    /// <summary>Processes everything due. The backstop for a lost enqueue, and the retry loop.</summary>
    /// <returns>How many events this run claimed.</returns>
    public Task<int> DrainAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Receives gateway webhooks and acts on each exactly once.
/// </summary>
/// <remarks>
/// <para>
/// Split in two on purpose. <see cref="ReceiveAsync"/> verifies, records and returns — one
/// insert, no outbound calls — because a gateway that does not get a prompt 200 retries, and
/// retries while we are still working on the first delivery are how double-crediting bugs get
/// their chance. <see cref="ProcessAsync"/> then does the slow part off the request thread.
/// </para>
/// <para>
/// The order inside <see cref="ReceiveAsync"/> matters. Signature first, before the body is
/// parsed or anything is looked up — an unsigned request must never reach code that trusts its
/// contents, and it must not be able to write a row either, or an open endpoint becomes free
/// storage for anyone who finds it.
/// </para>
/// <para>
/// Then the unique index on <c>(gateway, event_id)</c>. Gateways retry, and two retries can
/// arrive at once — a check-then-act in application code would let both through. Inserting the
/// event row first means the database decides which delivery is the real one.
/// </para>
/// <para>
/// Processing is claimed the same way. The enqueued job and the timed drain can both reach one
/// event, and so can two overlapping drains; a conditional UPDATE from Pending to Processing lets
/// exactly one of them go on.
/// </para>
/// <para>
/// And the amount is never taken from the payload. The webhook says <i>something happened</i>;
/// what happened is established by asking the gateway. A replayed genuine body carries a valid
/// signature, so without that rule anyone who captured one could name their own top-up amount.
/// </para>
/// </remarks>
public sealed partial class PaymentWebhookHandler : IPaymentWebhookProcessor
{
    /// <summary>The most events one drain run claims.</summary>
    public const int DrainBatchSize = 200;

    private readonly IAppDbContext _db;
    private readonly IPaymentGateway _gateway;
    private readonly IPlatformScope _platformScope;
    private readonly IWebhookDispatcher _dispatcher;
    private readonly VerifyTopUpHandler _verify;
    private readonly IPlatformAlerter _alerter;
    private readonly IUniqueViolationDetector _uniqueViolations;
    private readonly TimeProvider _clock;
    private readonly ILogger<PaymentWebhookHandler> _logger;

    public PaymentWebhookHandler(
        IAppDbContext db,
        IPaymentGateway gateway,
        IPlatformScope platformScope,
        IWebhookDispatcher dispatcher,
        VerifyTopUpHandler verify,
        IPlatformAlerter alerter,
        IUniqueViolationDetector uniqueViolations,
        TimeProvider clock,
        ILogger<PaymentWebhookHandler> logger)
    {
        _db = db;
        _gateway = gateway;
        _platformScope = platformScope;
        _dispatcher = dispatcher;
        _verify = verify;
        _alerter = alerter;
        _uniqueViolations = uniqueViolations;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>The event types that mean money moved. Everything else is recorded and ignored.</summary>
    private static readonly string[] ActedOn = ["charge.success"];

    /// <summary>
    /// Verifies and records a delivery. Fast, and safe to call with the same body repeatedly.
    /// </summary>
    /// <remarks>
    /// Throws when the delivery could not be recorded for any reason other than having been
    /// recorded already. The endpoint then answers 5xx and the gateway redelivers — which is
    /// safe, because the unique index turns the redelivery into a duplicate once one succeeds.
    /// </remarks>
    public async Task<WebhookOutcome> ReceiveAsync(
        string? rawBody,
        string? signature,
        CancellationToken cancellationToken = default)
    {
        var body = rawBody ?? string.Empty;

        // First, before parsing. An unsigned body is not evidence of anything.
        if (!_gateway.IsValidSignature(body, signature))
        {
            LogSignatureRejected(_logger);
            return WebhookOutcome.Rejected;
        }

        if (!TryReadEnvelope(body, out var envelope))
        {
            return WebhookOutcome.Rejected;
        }

        // The delivery names its own payment and arrives with no session of its own.
        using var scope = _platformScope.Enter("gateway webhook — recording a delivery");

        var acted = ActedOn.Contains(envelope.EventType, StringComparer.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(envelope.Reference);

        var record = PaymentWebhookEvent.Receive(
            _gateway.Name, envelope.EventId, envelope.EventType, body, signatureValid: true);

        if (!acted)
        {
            // Recorded and ignored, not an error. A gateway sends many event types and most of
            // them are none of our business.
            record.MarkIgnored(_clock.GetUtcNow());
        }

        _db.PaymentWebhookEvents.Add(record);

        try
        {
            // The unique index decides. If it refuses, another delivery of the same event won.
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (_uniqueViolations.IsUniqueViolation(ex))
        {
            // Only a unique violation means "already received". Any other failure means this
            // delivery was never recorded, and acknowledging it would lose it for good — so
            // those propagate, and the gateway is told to try again.
            _db.ChangeTracker.Clear();
            LogDuplicate(_logger, envelope.EventId);
            return WebhookOutcome.Duplicate;
        }

        if (!acted)
        {
            return WebhookOutcome.Ignored;
        }

        // Fire-and-forget by design: a failure to enqueue is logged, not thrown, because the
        // event is already durably recorded and DrainAsync will find it.
        try
        {
            await _dispatcher.EnqueueAsync(record.Id, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogEnqueueFailed(_logger, ex, record.Id);
        }

        return WebhookOutcome.Accepted;
    }

    /// <summary>Processes one recorded event: asks the gateway what happened, and credits if so.</summary>
    public Task ProcessAsync(Guid webhookEventId, CancellationToken cancellationToken = default) =>
        ProcessClaimedAsync(webhookEventId, cancellationToken);

    /// <summary>Processes every event that is due. Runs on a timer.</summary>
    public async Task<int> DrainAsync(CancellationToken cancellationToken = default)
    {
        List<Guid> due;
        var now = _clock.GetUtcNow();

        using (var scope = _platformScope.Enter("gateway webhook — finding deliveries due for processing"))
        {
            due = await Due(now)
                .AsNoTracking()
                .OrderBy(e => e.CreatedAt)
                .Select(e => e.Id)
                .Take(DrainBatchSize)
                .ToListAsync(cancellationToken);
        }

        var claimed = 0;

        foreach (var id in due)
        {
            if (await ProcessClaimedAsync(id, cancellationToken))
            {
                claimed++;
            }
        }

        return claimed;
    }

    /// <summary>
    /// Events a worker may take: pending and past their back-off, or claimed by a worker whose
    /// claim has lapsed — most likely one that died mid-flight.
    /// </summary>
    private IQueryable<PaymentWebhookEvent> Due(DateTimeOffset now) =>
        _db.PaymentWebhookEvents.Where(e =>
            (e.ProcessingStatus == WebhookProcessingStatus.Pending
             && (e.NextAttemptAt == null || e.NextAttemptAt <= now))
            || (e.ProcessingStatus == WebhookProcessingStatus.Processing && e.ClaimExpiresAt <= now));

    /// <returns>True when this call claimed the event.</returns>
    private async Task<bool> ProcessClaimedAsync(Guid webhookEventId, CancellationToken cancellationToken)
    {
        using var scope = _platformScope.Enter("gateway webhook — processing a recorded delivery");

        var now = _clock.GetUtcNow();
        var claimUntil = now + PaymentWebhookEvent.ProcessingLease;

        // The claim: one UPDATE, conditional on the event still being up for grabs. PostgreSQL
        // row-locks it, so of two workers racing here exactly one sees "1 row" and goes on. A
        // read-then-check in C# would let both through, and both would verify and post.
        var claimed = await Due(now)
            .Where(e => e.Id == webhookEventId)
            .ExecuteUpdateAsync(
                set => set
                    .SetProperty(e => e.ProcessingStatus, WebhookProcessingStatus.Processing)
                    .SetProperty(e => e.ClaimExpiresAt, claimUntil)
                    .SetProperty(e => e.UpdatedAt, now),
                cancellationToken);

        if (claimed != 1)
        {
            // Done already, dead-lettered, waiting out a back-off, or another worker has it.
            return false;
        }

        // Untracked: the verification below may clear the change tracker, and this row is
        // re-read fresh before it is written.
        var payload = await _db.PaymentWebhookEvents
            .AsNoTracking()
            .Where(e => e.Id == webhookEventId)
            .Select(e => new { e.EventId, e.EventType, e.Payload })
            .FirstAsync(cancellationToken);

        if (!TryReadEnvelope(payload.Payload, out var envelope) || string.IsNullOrWhiteSpace(envelope.Reference))
        {
            await FinishAsync(webhookEventId, record => record.MarkIgnored(_clock.GetUtcNow()), cancellationToken);
            return true;
        }

        try
        {
            // Asks the gateway. The payload is a notification, not a source of truth about money.
            var outcome = await _verify.HandleAsync(envelope.Reference, cancellationToken);

            if (outcome == TopUpVerificationOutcome.StillPending)
            {
                // The gateway announced a charge but will not yet confirm it. Marking the event
                // processed would mean nothing ever asks again, so it waits and retries.
                await FinishAsync(
                    webhookEventId,
                    record => record.RetryLater("The gateway has not confirmed the payment yet.", _clock.GetUtcNow()),
                    cancellationToken);

                return true;
            }

            await FinishAsync(webhookEventId, record => record.MarkProcessed(_clock.GetUtcNow()), cancellationToken);
        }
        catch (PaymentGatewayUnavailableException ex)
        {
            // Not our fault and not the payload's. Back off without using up an attempt, so a
            // gateway outage does not dead-letter every payment that arrived during it.
            LogGatewayUnavailable(_logger, ex, payload.EventId);

            _db.ChangeTracker.Clear();
            await FinishAsync(webhookEventId, record => record.RetryLater(ex.Message, _clock.GetUtcNow()), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogProcessingFailed(_logger, ex, payload.EventId);

            // Whatever the failed attempt staged is still tracked, and would be re-sent by the
            // save below and fail again. Discard it, then record the failure on its own.
            _db.ChangeTracker.Clear();
            await FinishAsync(webhookEventId, record => record.MarkFailed(ex.Message, _clock.GetUtcNow()), cancellationToken);
        }

        return true;
    }

    /// <summary>Re-reads the event, applies <paramref name="apply"/>, saves, and alerts on a dead letter.</summary>
    private async Task FinishAsync(
        Guid webhookEventId,
        Action<PaymentWebhookEvent> apply,
        CancellationToken cancellationToken)
    {
        var record = await _db.PaymentWebhookEvents.FirstAsync(e => e.Id == webhookEventId, cancellationToken);

        apply(record);
        await _db.SaveChangesAsync(cancellationToken);

        if (record.IsDeadLettered)
        {
            await AlertDeadLetterAsync(record, cancellationToken);
        }
    }

    /// <summary>
    /// A money event has stopped retrying. Somebody may have been charged and not credited.
    /// </summary>
    /// <remarks>
    /// P1, and never silent: the gateway was told 200 long ago, so it will not redeliver, and
    /// nothing else in the system will pick this event up again.
    /// </remarks>
    private async Task AlertDeadLetterAsync(PaymentWebhookEvent record, CancellationToken cancellationToken)
    {
        TryReadEnvelope(record.Payload, out var envelope);

        try
        {
            await _alerter.RaiseAsync(
                new PlatformAlert(
                    AlertSeverity.P1,
                    $"Payment webhook {record.EventId} was dead-lettered — a charged payment may not be credited",
                    $"The {record.EventType} webhook for payment reference {envelope.Reference ?? "(none)"} stopped "
                    + $"retrying after {record.Attempts} failed attempt(s) and {record.TransientFailures} time(s) the "
                    + $"gateway could not be reached.\n\nLast error: {record.LastError}\n\n"
                    + "The gateway will not redeliver it. Check the payment in the gateway's dashboard; if it was "
                    + "paid, fix the cause and set this event back to Pending in payments.payment_webhook_events "
                    + "so the drain retries it.",
                    nameof(PaymentWebhookHandler)),
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogAlertFailed(_logger, ex, record.EventId);
        }
    }

    /// <summary>The few fields we read out of a delivery.</summary>
    private readonly record struct Envelope(string EventId, string EventType, string? Reference);

    private bool TryReadEnvelope(string body, out Envelope envelope)
    {
        envelope = default;

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            var eventType = root.TryGetProperty("event", out var e) ? e.GetString() ?? "unknown" : "unknown";
            var data = root.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Object ? d : default;

            var reference = data.ValueKind == JsonValueKind.Object && data.TryGetProperty("reference", out var r)
                ? r.GetString()
                : null;

            // Paystack sends no event id of its own, so the event type plus the transaction's id
            // identifies the delivery. Two different events about one payment stay distinct; the
            // same event redelivered collides on the unique index, which is what we want.
            var id = data.ValueKind == JsonValueKind.Object && data.TryGetProperty("id", out var transactionId)
                ? $"{eventType}:{transactionId}"
                : $"{eventType}:{reference}";

            envelope = new Envelope(id, eventType, reference);
            return true;
        }
        catch (JsonException ex)
        {
            // Correctly signed but unparseable. Worth an alarm — it means the gateway changed
            // something, or the key is shared with something that is not the gateway.
            LogUnparseable(_logger, ex);
            return false;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "A webhook arrived with a missing or invalid signature and was rejected unparsed.")]
    private static partial void LogSignatureRejected(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "A correctly signed webhook could not be parsed — the gateway may have changed its payload.")]
    private static partial void LogUnparseable(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Webhook {EventId} has already been received; ignoring the redelivery.")]
    private static partial void LogDuplicate(ILogger logger, string eventId);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Webhook {WebhookEventId} was recorded but could not be queued; the drain will pick it up.")]
    private static partial void LogEnqueueFailed(ILogger logger, Exception exception, Guid webhookEventId);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "The gateway could not be reached for webhook {EventId}; it will be retried after a back-off.")]
    private static partial void LogGatewayUnavailable(ILogger logger, Exception exception, string eventId);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "Webhook {EventId} failed processing and will be retried.")]
    private static partial void LogProcessingFailed(ILogger logger, Exception exception, string eventId);

    [LoggerMessage(Level = LogLevel.Critical,
        Message = "Webhook {EventId} was dead-lettered and the alert could not be raised.")]
    private static partial void LogAlertFailed(ILogger logger, Exception exception, string eventId);
}
