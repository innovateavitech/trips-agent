using TripsAgent.Domain.Auditing;
using TripsAgent.Domain.Common;

namespace TripsAgent.Domain.Payments;

/// <summary>What a payment is for.</summary>
public enum PaymentPurpose
{
    OrderPayment = 1,
    WalletTopUp = 2,
    Subscription = 3,
    Installment = 4,
}

/// <summary>Where a payment attempt has got to.</summary>
public enum PaymentStatus
{
    /// <summary>Initialised with the gateway; the payer has not finished.</summary>
    Pending = 1,

    /// <summary>Confirmed by the gateway, server-side.</summary>
    Succeeded = 2,

    /// <summary>The gateway says it failed.</summary>
    Failed = 3,

    /// <summary>Started and never completed. Distinct from failed: nobody was charged.</summary>
    Abandoned = 4,

    /// <summary>
    /// The gateway confirmed a payment, but not one we can credit: less than we asked for, or in
    /// another currency. Nothing is credited automatically; a person decides.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="Failed"/> because the payer <i>was</i> charged. Telling them it
    /// failed would invite them to pay again.
    /// </remarks>
    UnderReview = 5,
}

/// <summary>
/// One attempt to take money through a gateway.
/// </summary>
/// <remarks>
/// <para>
/// Every attempt gets a row, successful or not. The failures are what you need when an agent says
/// they were charged and the balance did not move.
/// </para>
/// <para>
/// <b>No card data, ever.</b> Card entry happens on the gateway's hosted page, which is what keeps
/// us in PCI SAQ-A. The most this row ever holds is an authorization code — a gateway token, not
/// a card number.
/// </para>
/// </remarks>
public sealed class PaymentTransaction : Entity, IAuditableEntity, ITenantScoped, IAuditLogged
{
    private PaymentTransaction()
    {
        Reference = string.Empty;
        Currency = string.Empty;
    }

    /// <summary>Records an attempt about to be initialised with the gateway.</summary>
    public static PaymentTransaction Start(
        Guid agencyId,
        Guid? userId,
        PaymentPurpose purpose,
        Money amount,
        string currency,
        string reference,
        string? idempotencyKey = null,
        Guid? orderId = null)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(agencyId, Guid.Empty);
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);

        if (amount.AmountMinor <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(amount), amount.AmountMinor, "A payment must be positive.");
        }

        return new PaymentTransaction
        {
            AgencyId = agencyId,
            InitiatedByUserId = userId,
            Purpose = purpose,
            AmountMinor = amount,
            Currency = currency.Trim().ToUpperInvariant(),
            Reference = reference,
            IdempotencyKey = idempotencyKey,
            OrderId = orderId,
            Status = PaymentStatus.Pending,
        };
    }

    public Guid AgencyId { get; private set; }

    public Guid? InitiatedByUserId { get; private set; }

    public PaymentPurpose Purpose { get; private set; }

    /// <summary>What we asked the payer for.</summary>
    public Money AmountMinor { get; private set; }

    /// <summary>What the gateway says was actually paid. Null until verified.</summary>
    public Money? VerifiedAmountMinor { get; private set; }

    /// <summary>The gateway's cut. Recorded so net settlement can be reconciled.</summary>
    public Money FeeMinor { get; private set; }

    public string Currency { get; private set; }

    /// <summary>Our reference, unique platform-wide. What we give the gateway to quote back.</summary>
    public string Reference { get; private set; }

    /// <summary>The gateway's own reference, once it has one.</summary>
    public string? GatewayReference { get; private set; }

    public PaymentStatus Status { get; private set; }

    /// <summary>Set when the money is confirmed, server-side.</summary>
    public DateTimeOffset? VerifiedAt { get; private set; }

    /// <summary>
    /// Set once this payment has produced ledger entries, and checked before producing more.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is what makes crediting idempotent. A webhook and a browser redirect routinely both
    /// arrive for the same payment, and the gateway retries webhooks — without this, one top-up
    /// credits the wallet several times.
    /// </para>
    /// <para>
    /// Checking it in memory is not enough on its own: two callers can both read it as null
    /// before either writes. So it is also an EF Core concurrency token. The update that posts a
    /// payment only matches the row while the column is still null in the database, so the second
    /// of two racing posts matches nothing and its whole save is rolled back.
    /// </para>
    /// </remarks>
    public Guid? LedgerTransactionGroupId { get; private set; }

    /// <summary>Caller-supplied, to make a retried initialise return the same attempt.</summary>
    public string? IdempotencyKey { get; private set; }

    /// <summary>
    /// The order being paid for, on a payment whose purpose is <see cref="PaymentPurpose.OrderPayment"/>.
    /// Null for a top-up or a subscription, which pay for no order.
    /// </summary>
    /// <remarks>
    /// A traveller's card payment on a storefront credits the agency's wallet like any other payment,
    /// and then funds exactly this order (build plan F5). Without the link, the only way back from a
    /// gateway callback to the booking it paid for would be to parse a reference string.
    /// </remarks>
    public Guid? OrderId { get; private set; }

    /// <summary>Why it failed, from the gateway. For support, not for the agent.</summary>
    public string? FailureReason { get; private set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>True when the money is confirmed but the ledger has not been written yet.</summary>
    public bool AwaitsPosting => Status == PaymentStatus.Succeeded && LedgerTransactionGroupId is null;

    /// <summary>Records what the gateway returned when the attempt was created.</summary>
    public void RecordGatewayReference(string gatewayReference) => GatewayReference = gatewayReference;

    /// <summary>
    /// Records a server-side confirmation, and decides whether it can be credited.
    /// </summary>
    /// <param name="paidAmount">What the gateway says was actually charged.</param>
    /// <param name="fee">The gateway's cut, for settlement reconciliation.</param>
    /// <param name="paidCurrency">The currency the gateway says it was charged in.</param>
    /// <param name="gatewayReference">The gateway's own reference, if it sent one.</param>
    /// <param name="at">When the gateway confirmed it.</param>
    /// <remarks>
    /// <para>
    /// The wallet is credited <see cref="AmountMinor"/> — what we asked for — never the paid
    /// amount. When the payer bears the gateway's fee, the paid amount includes that fee, and
    /// crediting it would hand the agency money the platform never receives.
    /// </para>
    /// <para>
    /// That is only safe when the payment covers the request in the same currency. Anything else
    /// goes to <see cref="PaymentStatus.UnderReview"/> for a person to decide. Crediting the
    /// request when less arrived would credit money nobody paid; converting a currency here would
    /// be a pricing decision made by accident.
    /// </para>
    /// </remarks>
    public void MarkSucceeded(
        Money paidAmount,
        Money fee,
        string paidCurrency,
        string? gatewayReference,
        DateTimeOffset at)
    {
        if (LedgerTransactionGroupId is not null || Status == PaymentStatus.UnderReview)
        {
            // Already credited, or already waiting for a person. A second confirmation for the
            // same payment is normal and must be harmless.
            return;
        }

        // A payment confirmed earlier but never posted is decided again from this answer, so a
        // row recorded before these checks existed cannot be credited without passing them.
        VerifiedAmountMinor = paidAmount;
        FeeMinor = fee;
        VerifiedAt = at;

        if (!string.IsNullOrWhiteSpace(gatewayReference))
        {
            GatewayReference = gatewayReference;
        }

        var currency = (paidCurrency ?? string.Empty).Trim().ToUpperInvariant();

        if (!string.Equals(currency, Currency, StringComparison.Ordinal))
        {
            Status = PaymentStatus.UnderReview;
            FailureReason = $"Paid in '{currency}', but the top-up was requested in {Currency}.";
            return;
        }

        if (paidAmount < AmountMinor)
        {
            Status = PaymentStatus.UnderReview;
            FailureReason = $"Paid {paidAmount} {currency}, less than the {AmountMinor} requested.";
            return;
        }

        Status = PaymentStatus.Succeeded;
        FailureReason = null;
    }

    public void MarkFailed(string? reason, DateTimeOffset at)
    {
        if (Status is PaymentStatus.Succeeded or PaymentStatus.UnderReview)
        {
            // A failure notice arriving after the gateway confirmed a charge is the gateway being
            // noisy, not a reason to forget that the payer was charged.
            return;
        }

        Status = PaymentStatus.Failed;
        FailureReason = reason is { Length: > 500 } ? reason[..500] : reason;
        VerifiedAt = at;
    }

    public void MarkAbandoned(DateTimeOffset at)
    {
        if (Status != PaymentStatus.Pending)
        {
            return;
        }

        Status = PaymentStatus.Abandoned;
        VerifiedAt = at;
    }

    /// <summary>Records that this payment has been posted to the ledger, once.</summary>
    public void MarkPosted(Guid transactionGroupId)
    {
        if (LedgerTransactionGroupId is not null)
        {
            throw new InvalidOperationException(
                $"Payment {Reference} is already posted as ledger transaction {LedgerTransactionGroupId}. "
                + "Posting twice would credit the wallet twice.");
        }

        LedgerTransactionGroupId = transactionGroupId;
    }
}

/// <summary>
/// A webhook delivery from a gateway, recorded so it is processed exactly once.
/// </summary>
/// <remarks>
/// <para>
/// Gateways retry. The same event arriving five times must produce one ledger transaction, and
/// the durable guarantee is a unique index on the gateway's event id — not a check-then-act in
/// application code, which two concurrent deliveries would both pass.
/// </para>
/// <para>
/// Processing is claimed the same way. A worker moves the row from <c>Pending</c> to
/// <c>Processing</c> with one conditional UPDATE, and only the worker whose update matched a row
/// goes on. The claim expires after <see cref="ProcessingLease"/>, so a worker that died
/// mid-flight does not strand the event.
/// </para>
/// </remarks>
public sealed class PaymentWebhookEvent : Entity, IAuditableEntity
{
    /// <summary>Failures that were our fault or the payload's, before the event is dead-lettered.</summary>
    public const int MaxAttempts = 5;

    /// <summary>
    /// Times the gateway could not answer, before the event is dead-lettered anyway.
    /// </summary>
    /// <remarks>
    /// Counted apart from <see cref="MaxAttempts"/>, so a ten-minute gateway outage does not use
    /// up an event's attempts. With the back-off capped at an hour this is roughly two days.
    /// Past that, a person should look.
    /// </remarks>
    public const int MaxTransientFailures = 48;

    /// <summary>How long a claim lasts before another worker may take the event over.</summary>
    public static readonly TimeSpan ProcessingLease = TimeSpan.FromMinutes(10);

    /// <summary>The longest wait between retries.</summary>
    public static readonly TimeSpan MaxRetryDelay = TimeSpan.FromHours(1);

    private PaymentWebhookEvent()
    {
        Gateway = string.Empty;
        EventId = string.Empty;
        EventType = string.Empty;
        Payload = string.Empty;
    }

    public static PaymentWebhookEvent Receive(
        string gateway,
        string eventId,
        string eventType,
        string payload,
        bool signatureValid) =>
        new()
        {
            Gateway = gateway,
            EventId = eventId,
            EventType = eventType,
            Payload = payload,
            SignatureValid = signatureValid,
            ProcessingStatus = signatureValid ? WebhookProcessingStatus.Pending : WebhookProcessingStatus.Rejected,
        };

    public string Gateway { get; private set; }

    /// <summary>The gateway's id for this event. Unique per gateway.</summary>
    public string EventId { get; private set; }

    public string EventType { get; private set; }

    /// <summary>The raw body, kept for replay and for arguing with a gateway about what they sent.</summary>
    public string Payload { get; private set; }

    public bool SignatureValid { get; private set; }

    public WebhookProcessingStatus ProcessingStatus { get; private set; }

    /// <summary>Failures that count towards <see cref="MaxAttempts"/>.</summary>
    public int Attempts { get; private set; }

    /// <summary>Times the gateway could not be reached. See <see cref="MaxTransientFailures"/>.</summary>
    public int TransientFailures { get; private set; }

    public string? LastError { get; private set; }

    /// <summary>Not before this. Null means as soon as possible.</summary>
    public DateTimeOffset? NextAttemptAt { get; private set; }

    /// <summary>While <c>Processing</c>: when the current claim lapses.</summary>
    public DateTimeOffset? ClaimExpiresAt { get; private set; }

    public DateTimeOffset? ProcessedAt { get; private set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>True once retries are exhausted and a person has to look.</summary>
    public bool IsDeadLettered => ProcessingStatus == WebhookProcessingStatus.DeadLettered;

    /// <summary>
    /// How long to wait after the <paramref name="failures"/>th failure: 1, 2, 4, 8 minutes and so
    /// on, capped at <see cref="MaxRetryDelay"/>.
    /// </summary>
    public static TimeSpan RetryDelay(int failures)
    {
        var exponent = Math.Clamp(failures - 1, 0, 10);
        var delay = TimeSpan.FromMinutes(1 << exponent);

        return delay < MaxRetryDelay ? delay : MaxRetryDelay;
    }

    public void MarkProcessed(DateTimeOffset at)
    {
        ProcessingStatus = WebhookProcessingStatus.Processed;
        ProcessedAt = at;
        NextAttemptAt = null;
        ClaimExpiresAt = null;
    }

    /// <summary>An event type we do not act on. Recorded, not an error.</summary>
    public void MarkIgnored(DateTimeOffset at)
    {
        ProcessingStatus = WebhookProcessingStatus.Ignored;
        ProcessedAt = at;
        NextAttemptAt = null;
        ClaimExpiresAt = null;
    }

    /// <summary>
    /// Records a failure that retrying may not fix, and uses up an attempt.
    /// </summary>
    /// <remarks>Dead-letters the event once <see cref="MaxAttempts"/> is reached.</remarks>
    public void MarkFailed(string error, DateTimeOffset now)
    {
        Attempts++;
        ScheduleRetry(error, now, exhausted: Attempts >= MaxAttempts);
    }

    /// <summary>
    /// Records that the gateway could not give an answer, without using up an attempt.
    /// </summary>
    public void RetryLater(string reason, DateTimeOffset now)
    {
        TransientFailures++;
        ScheduleRetry(reason, now, exhausted: TransientFailures >= MaxTransientFailures);
    }

    private void ScheduleRetry(string error, DateTimeOffset now, bool exhausted)
    {
        LastError = error is { Length: > 1000 } ? error[..1000] : error;
        ClaimExpiresAt = null;

        if (exhausted)
        {
            ProcessingStatus = WebhookProcessingStatus.DeadLettered;
            NextAttemptAt = null;
            return;
        }

        ProcessingStatus = WebhookProcessingStatus.Pending;
        NextAttemptAt = now + RetryDelay(Attempts + TransientFailures);
    }
}

public enum WebhookProcessingStatus
{
    Pending = 1,
    Processed = 2,

    /// <summary>A type we do not act on. Recorded so the history is complete.</summary>
    Ignored = 3,

    /// <summary>Signature did not verify. Kept as evidence rather than discarded.</summary>
    Rejected = 4,

    /// <summary>Retried to exhaustion. Needs a person.</summary>
    DeadLettered = 5,

    /// <summary>Claimed by a worker, which is verifying it now. See <c>ClaimExpiresAt</c>.</summary>
    Processing = 6,
}
