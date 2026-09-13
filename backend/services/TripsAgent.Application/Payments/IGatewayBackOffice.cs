using TripsAgent.Domain.Common;

namespace TripsAgent.Application.Payments;

/// <summary>What the gateway has decided about a dispute.</summary>
public enum GatewayDisputeState
{
    /// <summary>Waiting on evidence, or on the bank.</summary>
    Open = 1,

    /// <summary>The charge stood: the merchant keeps the money.</summary>
    MerchantWon = 2,

    /// <summary>The cardholder's bank took the money back.</summary>
    MerchantLost = 3,
}

/// <summary>A dispute, as the gateway describes it when asked directly.</summary>
/// <param name="GatewayDisputeId">The gateway's id.</param>
/// <param name="TransactionReference">Our reference for the payment being disputed.</param>
/// <param name="AmountMinor">How much is being taken back.</param>
/// <param name="Currency">ISO 4217.</param>
/// <param name="State">What it means.</param>
/// <param name="Status">The gateway's own status, verbatim.</param>
/// <param name="Resolution">The gateway's own resolution, verbatim, once there is one.</param>
/// <param name="Category">The gateway's category, verbatim.</param>
/// <param name="Reason">What the cardholder said.</param>
/// <param name="OpenedAt">When it was raised.</param>
/// <param name="DueAt">The evidence deadline, in UTC.</param>
public sealed record GatewayDispute(
    string GatewayDisputeId,
    string TransactionReference,
    Money AmountMinor,
    string Currency,
    GatewayDisputeState State,
    string Status,
    string? Resolution,
    string? Category,
    string? Reason,
    DateTimeOffset OpenedAt,
    DateTimeOffset DueAt);

/// <summary>What is filed with the gateway to contest a dispute.</summary>
public sealed record DisputeEvidence(
    string CustomerName,
    string CustomerEmail,
    string CustomerPhone,
    string ServiceDetails,
    DateOnly? DeliveryDate);

/// <summary>One transaction inside a gateway settlement.</summary>
/// <param name="Reference">Our reference for the payment.</param>
/// <param name="AmountMinor">Gross, as charged.</param>
/// <param name="FeeMinor">The gateway's cut of it.</param>
/// <param name="Currency">ISO 4217.</param>
public sealed record GatewaySettlementLine(string Reference, Money AmountMinor, Money FeeMinor, string Currency);

/// <summary>One payout from the gateway to the platform's bank, and what it was made of.</summary>
/// <param name="SettlementId">The gateway's id for it.</param>
/// <param name="SettledAt">When the gateway paid it out.</param>
/// <param name="GrossMinor">What the gateway says the transactions came to.</param>
/// <param name="FeesMinor">What it kept.</param>
/// <param name="NetMinor">What it actually paid.</param>
/// <param name="Currency">ISO 4217.</param>
/// <param name="Lines">Every transaction in it — every page of them.</param>
public sealed record GatewaySettlement(
    string SettlementId,
    DateTimeOffset SettledAt,
    Money GrossMinor,
    Money FeesMinor,
    Money NetMinor,
    string Currency,
    IReadOnlyList<GatewaySettlementLine> Lines);

/// <summary>
/// The gateway's back office: disputes and settlements.
/// </summary>
/// <remarks>
/// Kept apart from <see cref="IPaymentGateway"/> and <see cref="IBankTransfers"/> because nothing
/// here moves money. Every call is a read except <see cref="SubmitEvidenceAsync"/>, which the
/// gateway accepts again until the deadline — agents send a first pass and then find the better
/// document — so this port may sit behind an ordinary retrying client.
/// </remarks>
public interface IGatewayBackOffice
{
    /// <summary>Asks the gateway about one dispute. A webhook body is a notification, not the truth.</summary>
    /// <returns>Null when the gateway has no such dispute.</returns>
    public Task<GatewayDispute?> GetDisputeAsync(string gatewayDisputeId, CancellationToken cancellationToken = default);

    /// <summary>Files evidence contesting a dispute. Re-filing before the deadline replaces it.</summary>
    public Task SubmitEvidenceAsync(
        string gatewayDisputeId,
        DisputeEvidence evidence,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Every settlement paid out in a window, with every transaction in each — all pages.
    /// </summary>
    /// <remarks>
    /// Implementations must page to the end. A reconciliation that reads the first page and stops
    /// reports a clean day while missing most of it.
    /// </remarks>
    public Task<IReadOnlyList<GatewaySettlement>> SettlementsAsync(
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd,
        CancellationToken cancellationToken = default);
}
