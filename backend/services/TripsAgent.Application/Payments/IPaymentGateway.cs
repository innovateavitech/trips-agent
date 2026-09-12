using TripsAgent.Domain.Common;

namespace TripsAgent.Application.Payments;

/// <summary>Where to send the payer, and the gateway's own reference for the attempt.</summary>
public sealed record GatewayInitialization(string AuthorizationUrl, string GatewayReference);

/// <summary>What a gateway's answer means for the payment.</summary>
public enum GatewayPaymentOutcome
{
    /// <summary>The payer was charged.</summary>
    Succeeded = 1,

    /// <summary>Finally failed — declined or reversed. Nobody was charged, or it was undone.</summary>
    Failed = 2,

    /// <summary>
    /// Not decided yet: abandoned, ongoing, pending, processing, queued, or a status we do not
    /// recognise.
    /// </summary>
    /// <remarks>
    /// Abandoned is here rather than under <see cref="Failed"/> because a payer who closed the
    /// page can come back, and a bank transfer or USSD payment settles minutes later. Telling
    /// them it failed invites them to pay a second time.
    /// </remarks>
    Pending = 3,
}

/// <summary>What the gateway says about a payment when asked directly.</summary>
/// <param name="Outcome">What the answer means for the payment.</param>
/// <param name="Status">Its own status string, kept verbatim for support.</param>
/// <param name="AmountMinor">
/// What the gateway says was charged. Not what we asked for — when the payer bears the fee it
/// includes the fee. Zero unless <paramref name="Outcome"/> is <see cref="GatewayPaymentOutcome.Succeeded"/>.
/// </param>
/// <param name="FeeMinor">The gateway's cut. Zero when the gateway did not say.</param>
public sealed record GatewayVerification(
    GatewayPaymentOutcome Outcome,
    string Status,
    Money AmountMinor,
    Money FeeMinor,
    string Currency,
    string? GatewayReference,
    string? FailureReason)
{
    public bool Succeeded => Outcome == GatewayPaymentOutcome.Succeeded;

    /// <summary>
    /// The reusable authorisation this payment left behind, when the gateway offered one.
    /// </summary>
    /// <remarks>
    /// An <c>init</c> property rather than another positional parameter, so the many places that
    /// build a verification for a one-off payment do not all have to say "and no authorisation".
    /// Null whenever the payment did not succeed, or the payer used a method that cannot be
    /// charged again.
    /// </remarks>
    public GatewayAuthorization? Authorization { get; init; }
}

/// <summary>
/// A token a gateway will accept instead of the payer, next time.
/// </summary>
/// <remarks>
/// <para>
/// <b>Nothing here is card data.</b> <paramref name="Code" /> is opaque and meaningless outside the
/// gateway, and the rest is what a person needs to recognise which card they are looking at. There
/// is no PAN, no CVV and no track data anywhere in this type, and there must never be: card entry
/// happens on the gateway's hosted page, which is the whole of why we are in PCI SAQ-A.
/// </para>
/// </remarks>
/// <param name="Code">The opaque token to charge against.</param>
/// <param name="Reusable">False when the gateway says this one cannot be charged again.</param>
/// <param name="Brand">"visa", "mastercard", "verve". For display only.</param>
/// <param name="Last4">The last four digits. For display only.</param>
/// <param name="ExpiryMonth">Two digits, so a console can warn before a card expires.</param>
/// <param name="ExpiryYear">Four digits.</param>
/// <param name="Bank">The issuing bank, when the gateway says.</param>
public sealed record GatewayAuthorization(
    string Code,
    bool Reusable,
    string? Brand,
    string? Last4,
    string? ExpiryMonth,
    string? ExpiryYear,
    string? Bank);

/// <summary>
/// Charging a payer who has already agreed, without sending them anywhere.
/// </summary>
/// <remarks>
/// <para>
/// Separate from <see cref="IPaymentGateway"/> on purpose. That port is about a payment somebody is
/// present for; this one is about a renewal at half past two in the morning, and the two have
/// different risks. A gateway that cannot do recurring charges can still implement the other.
/// </para>
/// <para>
/// The authorisation comes from the agency's first payment — the hosted checkout that started the
/// subscription (build-plan decision 18). Nothing here ever accepts card details, and an
/// implementation that did would be the first step towards taking them.
/// </para>
/// </remarks>
public interface IRecurringChargeGateway
{
    /// <summary>
    /// Charges <paramref name="authorizationCode"/> for <paramref name="amount"/>.
    /// </summary>
    /// <param name="reference">
    /// Our own reference for the attempt, unique per attempt. The gateway rejects a reference it has
    /// already seen, which is what makes a replayed billing run safe rather than a second charge.
    /// </param>
    /// <remarks>
    /// Returns the same shape as a verification, so a caller handles a recurring charge and a hosted
    /// payment the same way — including the important part, which is that
    /// <see cref="GatewayPaymentOutcome.Pending" /> is not a failure and must never be treated as one.
    /// </remarks>
    /// <exception cref="PaymentGatewayUnavailableException">The gateway could not be asked.</exception>
    /// <exception cref="PaymentGatewayException">It answered with something unusable.</exception>
    public Task<GatewayVerification> ChargeAsync(
        string reference,
        Money amount,
        string currency,
        string customerEmail,
        string authorizationCode,
        CancellationToken cancellationToken = default);
}

/// <summary>What the gateway said when asked to send money back.</summary>
/// <param name="Outcome">What its answer means for the refund.</param>
/// <param name="Status">Its own status string, kept verbatim for support.</param>
/// <param name="GatewayRefundReference">Its reference for the refund, for reconciliation.</param>
public sealed record GatewayRefund(
    GatewayRefundOutcome Outcome,
    string Status,
    string? GatewayRefundReference,
    string? FailureReason)
{
    /// <summary>True when the gateway has accepted the refund and will pay it out.</summary>
    public bool Accepted => Outcome is GatewayRefundOutcome.Accepted or GatewayRefundOutcome.AlreadyRefunded;
}

/// <summary>What a gateway's answer to a refund request means.</summary>
public enum GatewayRefundOutcome
{
    /// <summary>
    /// Taken on. The money reaches the card when the gateway's own settlement does, which for a
    /// Nigerian card is days rather than seconds — so "accepted" is the strongest thing that can
    /// honestly be said at this moment.
    /// </summary>
    Accepted = 1,

    /// <summary>The gateway has already refunded this payment. Nothing was sent twice.</summary>
    AlreadyRefunded = 2,

    /// <summary>Refused, and asking again will not change that — too old, or already reversed.</summary>
    Refused = 3,
}

/// <summary>
/// Taking money, as the application sees it.
/// </summary>
/// <remarks>
/// <para>
/// A port, so Paystack is one implementation rather than something the domain knows about. The
/// FRD is Nigeria-first and Paystack is the obvious choice, but gateway relationships change and
/// an agent-own-gateway mode is already in the plan.
/// </para>
/// <para>
/// Notice what is <b>not</b> here: nothing takes card details. The payer enters them on the
/// gateway's hosted page, which is what keeps us in PCI SAQ-A. An interface that could accept a
/// PAN would be the first step towards one that does.
/// </para>
/// </remarks>
public interface IPaymentGateway
{
    /// <summary>The gateway's name, as recorded on webhook events.</summary>
    public string Name { get; }

    /// <summary>
    /// Creates a payment attempt and returns where to send the payer.
    /// </summary>
    public Task<GatewayInitialization> InitializeAsync(
        string reference,
        Money amount,
        string currency,
        string customerEmail,
        string callbackUrl,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Asks the gateway what actually happened.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The only thing that may credit a wallet. A browser redirect says what the payer's browser
    /// was told, and a webhook body is whatever was posted to us — neither is the gateway
    /// answering a question we asked.
    /// </para>
    /// <para>
    /// Throws <see cref="PaymentGatewayUnavailableException"/> when the gateway could not be
    /// asked, and <see cref="PaymentGatewayException"/> when it answered with something unusable —
    /// including a success that does not say how much was paid, which must never be credited.
    /// </para>
    /// </remarks>
    public Task<GatewayVerification> VerifyAsync(string reference, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends money back to the card a payment came from.
    /// </summary>
    /// <param name="reference">Our reference for the original payment.</param>
    /// <param name="amount">
    /// How much to send back, which may be less than was paid: one line of a multi-line booking is
    /// refunded on its own.
    /// </param>
    /// <param name="reason">Why, for the gateway's own record and for the payer's statement.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// <para>
    /// <b>Card details are not needed and are never sent.</b> A refund names the original payment,
    /// and the gateway knows where the money came from. Nothing here could carry a card number.
    /// </para>
    /// <para>
    /// Throws <see cref="PaymentGatewayUnavailableException"/> when the gateway could not be asked —
    /// which is an unknown outcome, not a failure, and the caller must not record a refund it did
    /// not get an answer for.
    /// </para>
    /// </remarks>
    public Task<GatewayRefund> RefundAsync(
        string reference,
        Money amount,
        string reason,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// True when <paramref name="signature"/> matches <paramref name="payload"/>.
    /// </summary>
    public bool IsValidSignature(string payload, string? signature);
}
