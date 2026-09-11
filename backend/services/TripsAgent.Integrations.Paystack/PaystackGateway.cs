using System.Globalization;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TripsAgent.Application.Payments;
using TripsAgent.Domain.Common;

namespace TripsAgent.Integrations.Paystack;

/// <summary>Credentials and endpoints for Paystack.</summary>
public sealed class PaystackOptions
{
    /// <summary>Paystack's API root.</summary>
    public string BaseUrl { get; init; } = "https://api.paystack.co";

    /// <summary>
    /// The secret key. Test keys begin <c>sk_test_</c>.
    /// </summary>
    /// <remarks>
    /// Never committed. Supplied through configuration, and a live key belongs only in a secret
    /// store. It also keys the webhook signature, so rotating it invalidates in-flight webhooks.
    /// </remarks>
    public string SecretKey { get; init; } = string.Empty;
}

/// <summary>
/// Paystack, behind <see cref="IPaymentGateway"/>.
/// </summary>
/// <remarks>
/// <para>
/// Card entry happens on Paystack's hosted page. Nothing here accepts, stores or forwards card
/// details — that is what keeps us in PCI SAQ-A, and it is a property of the design rather than
/// a rule anyone has to follow.
/// </para>
/// <para>
/// Amounts go to Paystack in the currency's minor unit, which is exactly how
/// <see cref="Money"/> already stores them. There is deliberately no multiplication here: every
/// bug in this area comes from converting between major and minor units in one more place than
/// necessary.
/// </para>
/// </remarks>
public sealed class PaystackGateway : IPaymentGateway
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;
    private readonly PaystackOptions _options;

    public PaystackGateway(HttpClient http, PaystackOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _http = http;
        _options = options;
    }

    public string Name => "paystack";

    public async Task<GatewayInitialization> InitializeAsync(
        string reference,
        Money amount,
        string currency,
        string customerEmail,
        string callbackUrl,
        CancellationToken cancellationToken = default)
    {
        // amount is already minor units — kobo for NGN — which is exactly what Paystack's
        // "subunit of the supported currency" means. There is deliberately no conversion here.
        //
        // Sent as a string because that is the type Paystack documents for this field. It
        // accepts a JSON number too, but a documented contract is the one to code against.
        var request = new
        {
            reference,
            amount = amount.AmountMinor.ToString(CultureInfo.InvariantCulture),
            currency,
            email = customerEmail,
            callback_url = callbackUrl,
        };

        using var response = await _http.PostAsJsonAsync("/transaction/initialize", request, Json, cancellationToken);

        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<PaystackResponse<InitializeData>>(Json, cancellationToken)
            ?? throw new InvalidOperationException("Paystack returned an empty initialize response.");

        if (!body.Status || body.Data is null)
        {
            throw new InvalidOperationException($"Paystack refused to initialize {reference}: {body.Message}");
        }

        return new GatewayInitialization(body.Data.AuthorizationUrl, body.Data.Reference);
    }

    public async Task<GatewayVerification> VerifyAsync(string reference, CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync(
            new Uri($"/transaction/verify/{Uri.EscapeDataString(reference)}", UriKind.Relative), cancellationToken);

        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<PaystackResponse<VerifyData>>(Json, cancellationToken)
            ?? throw new InvalidOperationException("Paystack returned an empty verify response.");

        if (!body.Status || body.Data is null)
        {
            return new GatewayVerification(
                false, body.Message ?? "unknown", Money.Zero, Money.Zero, string.Empty, null, body.Message);
        }

        var data = body.Data;
        var succeeded = string.Equals(data.Status, "success", StringComparison.OrdinalIgnoreCase);

        return new GatewayVerification(
            succeeded,
            data.Status ?? "unknown",
            new Money(data.Amount),
            new Money(data.Fees),
            data.Currency ?? string.Empty,
            data.Reference,
            succeeded ? null : data.GatewayResponse);
    }

    /// <summary>
    /// Verifies the <c>x-paystack-signature</c> header: HMAC-SHA512 of the raw body, keyed by the
    /// secret key.
    /// </summary>
    /// <remarks>
    /// Computed over the body exactly as received. Deserialising and re-serialising first would
    /// change whitespace and key order, and the signature would never match.
    /// </remarks>
    public bool IsValidSignature(string payload, string? signature)
    {
        if (string.IsNullOrWhiteSpace(signature) || string.IsNullOrEmpty(_options.SecretKey))
        {
            return false;
        }

        var computed = Convert.ToHexString(
                HMACSHA512.HashData(
                    Encoding.UTF8.GetBytes(_options.SecretKey),
                    Encoding.UTF8.GetBytes(payload)))
            .ToLower(CultureInfo.InvariantCulture);

        // Fixed-time: a byte-by-byte comparison leaks how much of the signature matched, which is
        // enough to forge one a character at a time.
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(computed),
            Encoding.UTF8.GetBytes(signature.Trim().ToLower(CultureInfo.InvariantCulture)));
    }

    private sealed record PaystackResponse<T>(bool Status, string? Message, T? Data);

    private sealed record InitializeData(
        [property: JsonPropertyName("authorization_url")] string AuthorizationUrl,
        [property: JsonPropertyName("access_code")] string? AccessCode,
        [property: JsonPropertyName("reference")] string Reference);

    /// <summary>
    /// The fields we read off a verified transaction.
    /// </summary>
    /// <remarks>
    /// <c>amount</c> is what was actually paid; Paystack also returns <c>requested_amount</c>,
    /// and its own documentation shows a sample where the two differ. Only <c>amount</c> is
    /// mapped, so there is nothing here for a caller to credit by mistake.
    /// </remarks>
    private sealed record VerifyData(
        [property: JsonPropertyName("status")] string? Status,
        [property: JsonPropertyName("reference")] string? Reference,
        [property: JsonPropertyName("amount")] long Amount,
        [property: JsonPropertyName("fees")] long Fees,
        [property: JsonPropertyName("currency")] string? Currency,
        [property: JsonPropertyName("gateway_response")] string? GatewayResponse);
}
