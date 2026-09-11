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
    /// True when <paramref name="signature"/> matches <paramref name="payload"/>.
    /// </summary>
    public bool IsValidSignature(string payload, string? signature);
}
