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
        string? idempotencyKey = null)
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
    /// This is what makes crediting idempotent. A webhook and a browser redirect routinely both
    /// arrive for the same payment, and the gateway retries webhooks — without this, one top-up
    /// credits the wallet several times.
    /// </remarks>
    public Guid? LedgerTransactionGroupId { get; private set; }

    /// <summary>Caller-supplied, to make a retried initialise return the same attempt.</summary>
    public string? IdempotencyKey { get; private set; }

    /// <summary>Why it failed, from the gateway. For support, not for the agent.</summary>
    public string? FailureReason { get; private set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>True when the money is confirmed but the ledger has not been written yet.</summary>
    public bool AwaitsPosting => Status == PaymentStatus.Succeeded && LedgerTransactionGroupId is null;

    /// <summary>Records what the gateway returned when the attempt was created.</summary>
    public void RecordGatewayReference(string gatewayReference) => GatewayReference = gatewayReference;

    /// <summary>
    /// Records a server-side confirmation.
    /// </summary>
    /// <param name="verifiedAmount">
    /// What the gateway says was paid — which is not assumed to equal what we asked for. A payer
    /// can be charged a different amount, and crediting the requested figure rather than the paid
    /// one is how a wallet gains money nobody paid.
    /// </param>
    public void MarkSucceeded(Money verifiedAmount, Money fee, string? gatewayReference, DateTimeOffset at)
    {
        if (Status == PaymentStatus.Succeeded)
        {
            return;   // a second confirmation for the same payment is normal and must be harmless
        }

        Status = PaymentStatus.Succeeded;
        VerifiedAmountMinor = verifiedAmount;
        FeeMinor = fee;
        VerifiedAt = at;

        if (!string.IsNullOrWhiteSpace(gatewayReference))
        {
            GatewayReference = gatewayReference;
        }
    }

    public void MarkFailed(string? reason, DateTimeOffset at)
    {
        if (Status == PaymentStatus.Succeeded)
        {
            // A failure notice arriving after a confirmed success is the gateway being noisy, not
            // a reason to unwind money that is already in the ledger.
            return;
        }

        Status = PaymentStatus.Failed;
        FailureReason = reason;
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
/// Gateways retry. The same event arriving five times must produce one ledger transaction, and
/// the durable guarantee is a unique index on the gateway's event id — not a check-then-act in
/// application code, which two concurrent deliveries would both pass.
/// </remarks>
public sealed class PaymentWebhookEvent : Entity, IAuditableEntity
{
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

    public int Attempts { get; private set; }

    public string? LastError { get; private set; }

    public DateTimeOffset? ProcessedAt { get; private set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public void MarkProcessed(DateTimeOffset at)
    {
        ProcessingStatus = WebhookProcessingStatus.Processed;
        ProcessedAt = at;
    }

    /// <summary>An event type we do not act on. Recorded, not an error.</summary>
    public void MarkIgnored(DateTimeOffset at)
    {
        ProcessingStatus = WebhookProcessingStatus.Ignored;
        ProcessedAt = at;
    }

    public void MarkFailed(string error)
    {
        Attempts++;
        LastError = error;
        ProcessingStatus = Attempts >= MaxAttempts
            ? WebhookProcessingStatus.DeadLettered
            : WebhookProcessingStatus.Pending;
    }

    /// <summary>Tries before the event is dead-lettered for a person to look at.</summary>
    public const int MaxAttempts = 5;
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
}
