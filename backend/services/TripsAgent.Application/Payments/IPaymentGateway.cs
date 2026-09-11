using TripsAgent.Domain.Common;

namespace TripsAgent.Application.Payments;

/// <summary>Where to send the payer, and the gateway's own reference for the attempt.</summary>
public sealed record GatewayInitialization(string AuthorizationUrl, string GatewayReference);

/// <summary>What the gateway says about a payment when asked directly.</summary>
/// <param name="Status">Its own status string, kept verbatim for support.</param>
/// <param name="AmountMinor">What was actually paid — not what we asked for.</param>
/// <param name="FeeMinor">The gateway's cut.</param>
public sealed record GatewayVerification(
    bool Succeeded,
    string Status,
    Money AmountMinor,
    Money FeeMinor,
    string Currency,
    string? GatewayReference,
    string? FailureReason);

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
    /// The only thing that may credit a wallet. A browser redirect says what the payer's browser
    /// was told, and a webhook body is whatever was posted to us — neither is the gateway
    /// answering a question we asked.
    /// </remarks>
    public Task<GatewayVerification> VerifyAsync(string reference, CancellationToken cancellationToken = default);

    /// <summary>
    /// True when <paramref name="signature"/> matches <paramref name="payload"/>.
    /// </summary>
    public bool IsValidSignature(string payload, string? signature);
}
