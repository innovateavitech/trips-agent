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
