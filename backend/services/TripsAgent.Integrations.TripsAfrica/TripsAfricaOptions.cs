using TripsAgent.Domain.Suppliers;

namespace TripsAgent.Integrations.TripsAfrica;

/// <summary>
/// The non-secret settings for Trips Africa: <c>TripsAfrica:BaseUrl</c>, <c>TripsAfrica:TimeoutSeconds</c>
/// and the rest. The merchant key and bearer token are secrets and come from
/// <c>ISupplierCredentialStore</c>, never from here — see <see cref="DependencyInjection"/>.
/// </summary>
public sealed class TripsAfricaOptions
{
    public const string SectionName = "TripsAfrica";

    /// <summary>The <c>suppliers.code</c> both adapters serve.</summary>
    public const string SupplierCode = "trips_africa";

    public string BaseUrl { get; init; } = "https://api.staging.trips.ng";

    /// <summary>Which merchant account's credentials to use.</summary>
    public SupplierEnvironment Environment { get; init; } = SupplierEnvironment.Staging;

    /// <summary>
    /// How long a booking call — confirm, issue, status — may take before its outcome is recorded as
    /// unknown. Long, because a slow but successful issue call that we gave up on becomes an unknown
    /// outcome to poll for (ADR-0003). Only bounds the wait; nothing is ever retried on this client.
    /// </summary>
    public TimeSpan BookingTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// How long the ticket-issue call may take (#36: 45 seconds) before its outcome is recorded as
    /// unknown and handed to the status poller. The issue call has a client and a timeout of its own,
    /// set apart from every other call (ADR-0003).
    /// </summary>
    /// <remarks>
    /// Must stay below <c>SupplierPollSchedule.IssueRecoveryDelay</c> — startup refuses otherwise — so the
    /// poller never takes over an issue call that is still legitimately waiting for its answer.
    /// </remarks>
    public TimeSpan IssueTimeout { get; init; } = TimeSpan.FromSeconds(45);

    /// <summary>
    /// How long one search attempt may take (#33: 20 seconds). Search is a read, so it is the one call
    /// the adapter does retry — see <see cref="SearchAttempts"/>.
    /// </summary>
    public TimeSpan SearchTimeout { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>Attempts per search, the first included. Search only: nothing else is ever retried.</summary>
    public int SearchAttempts { get; init; } = 2;

    /// <summary>Consecutive failed searches that open the circuit, so a supplier outage fails fast.</summary>
    public int CircuitBreakerThreshold { get; init; } = 5;

    /// <summary>How long an open circuit refuses searches before letting one through to test the supplier.</summary>
    public TimeSpan CircuitBreakerCooldown { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Results per page. Trips Africa pages flight search fifty at a time.</summary>
    public int PageSize { get; init; } = 50;

    // ----------------------------------------------------------------- the fallback credential
    //
    // The encrypted supplier_credentials table is where a merchant key belongs, and when it holds one
    // for the platform, it wins. These three are the fallback for an environment that has not stored
    // one yet — set from TripsAfrica__MerchantKey and friends in the gitignored .env, exactly as the
    // Paystack secret key is. Never logged: this type deliberately has no ToString that prints them.

    public string? MerchantCode { get; init; }

    public string? MerchantKey { get; init; }

    /// <summary>Flights only. Buses derive their bearer token from the key and code.</summary>
    public string? BearerToken { get; init; }

    /// <summary>
    /// True when the fallback is actually configured — present, and not the <c>REPLACE_ME</c> that
    /// <c>.env.example</c> ships with, which would otherwise be sent to the supplier as a real key.
    /// </summary>
    public bool HasFallbackCredential =>
        IsSet(MerchantCode) && IsSet(MerchantKey);

    public override string ToString() =>
        $"TripsAfricaOptions {{ BaseUrl = {BaseUrl}, Environment = {Environment}, MerchantCode = {MerchantCode}, "
        + "MerchantKey = [redacted], BearerToken = [redacted] }";

    private static bool IsSet(string? value) =>
        !string.IsNullOrWhiteSpace(value) && !string.Equals(value, "REPLACE_ME", StringComparison.Ordinal);
}
