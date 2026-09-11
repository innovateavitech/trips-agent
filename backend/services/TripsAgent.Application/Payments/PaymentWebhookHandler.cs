using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
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
    /// <summary>Processes one recorded event.</summary>
    public Task ProcessAsync(Guid webhookEventId, CancellationToken cancellationToken = default);

    /// <summary>Processes everything still pending. The backstop for a lost enqueue.</summary>
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
/// And the amount is never taken from the payload. The webhook says <i>something happened</i>;
/// what happened is established by asking the gateway. A replayed genuine body carries a valid
/// signature, so without that rule anyone who captured one could name their own top-up amount.
/// </para>
/// </remarks>
public sealed partial class PaymentWebhookHandler : IPaymentWebhookProcessor
{
    private readonly IAppDbContext _db;
    private readonly IPaymentGateway _gateway;
    private readonly IPlatformScope _platformScope;
    private readonly IWebhookDispatcher _dispatcher;
    private readonly VerifyTopUpHandler _verify;
    private readonly TimeProvider _clock;
    private readonly ILogger<PaymentWebhookHandler> _logger;

    public PaymentWebhookHandler(
        IAppDbContext db,
        IPaymentGateway gateway,
        IPlatformScope platformScope,
        IWebhookDispatcher dispatcher,
        VerifyTopUpHandler verify,
        TimeProvider clock,
        ILogger<PaymentWebhookHandler> logger)
    {
        _db = db;
        _gateway = gateway;
        _platformScope = platformScope;
        _dispatcher = dispatcher;
        _verify = verify;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>The event types that mean money moved. Everything else is recorded and ignored.</summary>
    private static readonly string[] ActedOn = ["charge.success"];

    /// <summary>
    /// Verifies and records a delivery. Fast, and safe to call with the same body repeatedly.
    /// </summary>
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
            // The unique index decides. If this throws, another delivery of the same event won.
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
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
    public async Task ProcessAsync(Guid webhookEventId, CancellationToken cancellationToken = default)
    {
        using var scope = _platformScope.Enter("gateway webhook — processing a recorded delivery");

        var record = await _db.PaymentWebhookEvents
            .FirstOrDefaultAsync(e => e.Id == webhookEventId, cancellationToken);

        if (record is null || record.ProcessingStatus != WebhookProcessingStatus.Pending)
        {
            // Already processed, ignored or dead-lettered. Re-running must not credit again.
            return;
        }

        if (!TryReadEnvelope(record.Payload, out var envelope) || string.IsNullOrWhiteSpace(envelope.Reference))
        {
            record.MarkIgnored(_clock.GetUtcNow());
            await _db.SaveChangesAsync(cancellationToken);
            return;
        }

        try
        {
            // Asks the gateway. The payload is a notification, not a source of truth about money.
            await _verify.HandleAsync(envelope.Reference, cancellationToken);

            record.MarkProcessed(_clock.GetUtcNow());
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogProcessingFailed(_logger, ex, record.EventId);

            // Back to Pending until the attempt budget runs out, then dead-lettered for a person.
            record.MarkFailed(ex.Message);
            await _db.SaveChangesAsync(cancellationToken);
        }
    }

    /// <summary>Processes every pending event. Runs on a timer as the backstop.</summary>
    public async Task<int> DrainAsync(CancellationToken cancellationToken = default)
    {
        List<Guid> pending;

        using (var scope = _platformScope.Enter("gateway webhook — finding pending deliveries"))
        {
            pending = await _db.PaymentWebhookEvents
                .AsNoTracking()
                .Where(e => e.ProcessingStatus == WebhookProcessingStatus.Pending)
                .OrderBy(e => e.CreatedAt)
                .Select(e => e.Id)
                .Take(200)
                .ToListAsync(cancellationToken);
        }

        var processed = 0;

        foreach (var id in pending)
        {
            await ProcessAsync(id, cancellationToken);
            processed++;
        }

        return processed;
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

    [LoggerMessage(Level = LogLevel.Error,
        Message = "Webhook {EventId} failed processing and will be retried.")]
    private static partial void LogProcessingFailed(ILogger logger, Exception exception, string eventId);
}
