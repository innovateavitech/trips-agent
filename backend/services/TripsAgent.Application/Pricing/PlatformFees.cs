namespace TripsAgent.Application.Pricing;

/// <summary>
/// What share of a sale the platform takes from an agency, in basis points.
/// </summary>
/// <remarks>
/// <para>
/// The rate belongs to the agency's subscription tier (<c>transaction_fee_pct</c>, #64), which is
/// not built yet. This port is the seam it will plug into, so pricing, quotes and their stored
/// breakdown already carry the fee and nothing about them changes when tiers arrive.
/// </para>
/// <para>
/// Whatever the rate, the fee comes out of the agency's margin; it is never added to the price a
/// traveller pays. See <see cref="Domain.Pricing.MarkupEngine.Price"/>.
/// </para>
/// </remarks>
public interface IPlatformFeePolicy
{
    public Task<int> FeeBasisPointsAsync(Guid agencyId, CancellationToken cancellationToken = default);
}

/// <summary>
/// No platform fee, for anyone. Stands in until subscription tiers (#64) supply a real rate.
/// </summary>
/// <remarks>
/// Zero rather than a guessed rate: a guessed fee would be deducted from real agencies' margins on
/// every quote, and a quote can never be corrected afterwards.
/// </remarks>
public sealed class NoPlatformFeePolicy : IPlatformFeePolicy
{
    public Task<int> FeeBasisPointsAsync(Guid agencyId, CancellationToken cancellationToken = default) =>
        Task.FromResult(0);
}

/// <summary>How long a quote may be charged after it is worked out. Section <c>Pricing</c>.</summary>
public sealed class PriceQuoteOptions
{
    /// <summary>
    /// Thirty minutes: long enough to fill in passenger details and pay, short enough that a
    /// supplier's fare is unlikely to have moved underneath the quote.
    /// </summary>
    public TimeSpan Validity { get; init; } = TimeSpan.FromMinutes(30);
}

/// <summary>What the pricing screen needs to know before the agency has priced anything.</summary>
/// <param name="Currency">The agency's base currency — the only one it sells in for MVP.</param>
public sealed record PricingSettings(
    string Currency,
    int VatRateBasisPoints,
    int PlatformFeeBasisPoints,
    TimeSpan QuoteValidity);
