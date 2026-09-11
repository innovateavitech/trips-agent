namespace TripsAgent.Contracts.Payments;

/// <summary>
/// A request to add funds to the caller's wallet.
/// </summary>
/// <param name="AmountMinor">
/// The amount in minor units — kobo for naira. ₦5,000 is <c>500000</c>, not <c>5000</c>.
/// </param>
/// <remarks>
/// Minor units all the way to the wire, deliberately. A JSON number that looked like
/// <c>5000.50</c> would be a double by the time most clients parsed it, and a wallet balance
/// assembled from doubles loses kobo it can never get back.
/// </remarks>
public sealed record StartTopUpRequest(long AmountMinor);

/// <summary>Where to send the agent to pay, and the reference to quote afterwards.</summary>
public sealed record StartTopUpResponse(string AuthorizationUrl, string Reference);

/// <summary>The wallet as the console shows it.</summary>
/// <param name="AvailableMinor">
/// Balance less anything held for orders in flight. This is the figure that decides whether a
/// booking can proceed, so it is the one to show.
/// </param>
public sealed record WalletBalanceResponse(
    long BalanceMinor,
    long ReservedMinor,
    long AvailableMinor,
    string Currency);

/// <summary>The limits the console should enforce before it lets a request through.</summary>
public sealed record TopUpLimitsResponse(long MinimumMinor, long MaximumMinor, string Currency);

/// <summary>What came of verifying a payment the agent has just returned from.</summary>
/// <param name="Status">
/// <c>succeeded</c>, <c>pending</c> or <c>failed</c>. Succeeded means the money is in the wallet.
/// Pending is normal rather than an error: the payer may not have finished, a transfer may not
/// have settled, the gateway may be briefly unreachable, or the payment may be waiting for a
/// person to review it. The console should poll rather than alarm anyone — and must never invite
/// the agent to pay again while a top-up is pending.
/// </param>
/// <param name="AmountMinor">The amount of the top-up: what is, or will be, credited.</param>
public sealed record VerifyTopUpResponse(string Status, string Reference, long? AmountMinor);
