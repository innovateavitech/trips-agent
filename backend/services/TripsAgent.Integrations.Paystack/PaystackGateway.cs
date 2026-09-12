using System.Globalization;
using System.Net;
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

        if (!response.IsSuccessStatusCode)
        {
            // Paystack says WHY in the body, and EnsureSuccessStatusCode throws that away. The
            // difference is between "the payment provider is not responding" and the actual
            // answer — '"email" must be a valid email' — which is the one a person can act on.
            throw new PaymentGatewayException(
                $"Paystack refused to initialise {reference}: {(int)response.StatusCode} "
                + $"{await MessageFrom(response, cancellationToken)}");
        }

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
        var body = await SendVerifyAsync(reference, cancellationToken);

        if (!body.Status || body.Data is null)
        {
            // Paystack declined to give a verdict. That says nothing about whether the payer was
            // charged, so it is not a failure of the payment.
            return new GatewayVerification(
                GatewayPaymentOutcome.Pending, body.Message ?? "unknown", Money.Zero, Money.Zero, string.Empty, null, body.Message);
        }

        var data = body.Data;
        var outcome = Classify(data.Status);

        if (outcome != GatewayPaymentOutcome.Succeeded)
        {
            return new GatewayVerification(
                outcome,
                data.Status ?? "unknown",
                Money.Zero,
                Money.Zero,
                data.Currency ?? string.Empty,
                data.Reference,
                data.GatewayResponse);
        }

        // A success has to say how much was charged, as a whole number of minor units. Anything
        // else is a response we cannot credit from — and guessing would be worse than stopping.
        if (WholeMinorUnits(data.Amount) is not { } amount || amount < 0)
        {
            throw new PaymentGatewayException(
                $"Paystack reported {reference} successful without a usable amount ({Describe(data.Amount)}). "
                + "Nothing was credited.");
        }

        return new GatewayVerification(
            GatewayPaymentOutcome.Succeeded,
            data.Status ?? "success",
            new Money(amount),

            // Only recorded for reconciling settlements, never credited, so an absent or odd fee
            // is recorded as zero rather than blocking the payment.
            WholeMinorUnits(data.Fees) is { } fee and >= 0 ? new Money(fee) : Money.Zero,
            data.Currency ?? string.Empty,
            data.Reference,
            null);
    }

    /// <summary>
    /// Calls verify and reads the body, turning every way that can fail into one of our two
    /// gateway exceptions.
    /// </summary>
    private async Task<PaystackResponse<VerifyData>> SendVerifyAsync(string reference, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _http.GetAsync(
                new Uri($"/transaction/verify/{Uri.EscapeDataString(reference)}", UriKind.Relative), cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var status = (int)response.StatusCode;

                // A 5xx, a timeout or rate limiting: ask again later. Any other refusal is an
                // answer, just not one we can use.
                if (status >= 500 || response.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests)
                {
                    throw new PaymentGatewayUnavailableException(
                        $"Paystack answered HTTP {status} when asked to verify {reference}.");
                }

                throw new PaymentGatewayException($"Paystack refused to verify {reference}: HTTP {status}.");
            }

            return await response.Content.ReadFromJsonAsync<PaystackResponse<VerifyData>>(Json, cancellationToken)
                ?? throw new PaymentGatewayException($"Paystack returned an empty verify response for {reference}.");
        }
        catch (JsonException ex)
        {
            throw new PaymentGatewayException($"Paystack's verify response for {reference} could not be read.", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new PaymentGatewayUnavailableException($"Paystack could not be reached to verify {reference}.", ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            // HttpClient reports its own timeout as a cancellation. Only the caller's token
            // cancelling means "stop"; this means "Paystack was too slow".
            throw new PaymentGatewayUnavailableException($"Paystack timed out verifying {reference}.", ex);
        }
        catch (Polly.ExecutionRejectedException ex)
        {
            // The resilience pipeline's circuit breaker is open, or its own timeout fired.
            throw new PaymentGatewayUnavailableException($"Paystack calls are paused; {reference} was not verified.", ex);
        }
    }

    /// <summary>
    /// What a Paystack status means for the payment.
    /// </summary>
    /// <remarks>
    /// Only <c>failed</c> and <c>reversed</c> are final failures. Everything else — abandoned,
    /// ongoing, pending, processing, queued, and any status Paystack adds later — leaves the
    /// payment pending, because marking a payment failed that then settles tells the agent to pay
    /// twice.
    /// </remarks>
    private static GatewayPaymentOutcome Classify(string? status)
    {
        if (string.Equals(status, "success", StringComparison.OrdinalIgnoreCase))
        {
            return GatewayPaymentOutcome.Succeeded;
        }

        return string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase)
               || string.Equals(status, "reversed", StringComparison.OrdinalIgnoreCase)
            ? GatewayPaymentOutcome.Failed
            : GatewayPaymentOutcome.Pending;
    }

    /// <summary>The value as a whole number of minor units, or null if it is absent, null or fractional.</summary>
    private static long? WholeMinorUnits(JsonElement value) =>
        value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var minor) ? minor : null;

    private static string Describe(JsonElement value) =>
        value.ValueKind == JsonValueKind.Undefined ? "missing" : value.GetRawText();

    /// <summary>
    /// Verifies the <c>x-paystack-signature</c> header: HMAC-SHA512 of the raw body, keyed by the
    /// secret key.
    /// </summary>
    /// <remarks>
    /// Computed over the body exactly as received. Deserialising and re-serialising first would
    /// change whitespace and key order, and the signature would never match.
    /// </remarks>
    /// <summary>
    /// Asks Paystack to send money back to the card a payment came from.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>POST /refund</c>, naming the original transaction by our own reference. Paystack decides
    /// where the money goes from its record of the charge, so no card detail is sent or needed —
    /// which is the whole point of never having had one (decision 18).
    /// </para>
    /// <para>
    /// <b>Refunding twice is refused by Paystack, not by us</b>, and that refusal comes back as
    /// <see cref="GatewayRefundOutcome.AlreadyRefunded"/> rather than as an error: a redelivered
    /// message must find the refund already made and move on, not raise an alarm.
    /// </para>
    /// <para>
    /// A 5xx, a timeout or rate limiting is an <i>unknown</i> outcome and throws
    /// <see cref="PaymentGatewayUnavailableException"/>, so the caller retries rather than recording
    /// a refund nobody confirmed. That is the same discipline as the ticket-issue rule in ADR-0003,
    /// for the same reason: money that may or may not have moved is not money that did not move.
    /// </para>
    /// </remarks>
    public async Task<GatewayRefund> RefundAsync(
        string reference,
        Money amount,
        string reason,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);

        if (amount.AmountMinor <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(amount), amount.AmountMinor, "A refund must be positive.");
        }

        // Minor units again — kobo for NGN — and as a string for the same reason initialise sends
        // one: that is the type Paystack documents.
        var request = new
        {
            transaction = reference,
            amount = amount.AmountMinor.ToString(CultureInfo.InvariantCulture),
            merchant_note = Clip(reason),
        };

        HttpResponseMessage response;

        try
        {
            response = await _http.PostAsJsonAsync("/refund", request, Json, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            throw new PaymentGatewayUnavailableException($"Paystack could not be reached to refund {reference}.", ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            // HttpClient reports its own timeout as a cancellation. Only the caller's token
            // cancelling means "stop"; this means "Paystack was too slow".
            throw new PaymentGatewayUnavailableException($"Paystack did not answer in time when refunding {reference}.", ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var status = (int)response.StatusCode;
                var message = await MessageFrom(response, cancellationToken);

                if (status >= 500 || response.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests)
                {
                    throw new PaymentGatewayUnavailableException(
                        $"Paystack answered HTTP {status} when asked to refund {reference}: {message}");
                }

                // Paystack says so in the message rather than in a code of its own.
                if (message.Contains("already", StringComparison.OrdinalIgnoreCase)
                    && message.Contains("refund", StringComparison.OrdinalIgnoreCase))
                {
                    return new GatewayRefund(GatewayRefundOutcome.AlreadyRefunded, message, null, null);
                }

                return new GatewayRefund(GatewayRefundOutcome.Refused, $"HTTP {status}", null, message);
            }

            PaystackResponse<RefundData>? body;

            try
            {
                body = await response.Content.ReadFromJsonAsync<PaystackResponse<RefundData>>(Json, cancellationToken);
            }
            catch (JsonException ex)
            {
                throw new PaymentGatewayException($"Paystack's refund response for {reference} could not be read.", ex);
            }

            if (body is null || !body.Status || body.Data is null)
            {
                return new GatewayRefund(
                    GatewayRefundOutcome.Refused, "refused", null, body?.Message ?? "(no message)");
            }

            return new GatewayRefund(
                GatewayRefundOutcome.Accepted,
                body.Data.Status ?? "pending",
                body.Data.Id?.ToString(CultureInfo.InvariantCulture),
                null);
        }
    }

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

    /// <summary>
    /// Paystack's own explanation, for the log. Never the body verbatim: a failed response can
    /// echo the request, and the request carries a customer's email address.
    /// </summary>
    private static async Task<string> MessageFrom(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var problem = await response.Content.ReadFromJsonAsync<PaystackResponse<object>>(Json, cancellationToken);
            return problem?.Message is { Length: > 0 } message ? message : "(no message)";
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            return "(unreadable response)";
        }
    }

    /// <summary>How long a merchant note may be. Paystack truncates; this says so out loud.</summary>
    private static string Clip(string reason) => reason.Length <= 200 ? reason : reason[..200];

    private sealed record PaystackResponse<T>(bool Status, string? Message, T? Data);

    /// <summary>What we read off an accepted refund: its id, for reconciliation, and its status.</summary>
    private sealed record RefundData(
        [property: JsonPropertyName("id")] long? Id,
        [property: JsonPropertyName("status")] string? Status);

    private sealed record InitializeData(
        [property: JsonPropertyName("authorization_url")] string AuthorizationUrl,
        [property: JsonPropertyName("access_code")] string? AccessCode,
        [property: JsonPropertyName("reference")] string Reference);

    /// <summary>
    /// The fields we read off a verified transaction.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>amount</c> and <c>fees</c> are read as raw JSON rather than as <c>long</c>. Paystack
    /// documents <c>fees</c> as nullable — it is null until a charge settles — and a typed
    /// <c>long</c> throws on null, which turned an ordinary abandoned payment into a crash.
    /// </para>
    /// <para>
    /// <c>amount</c> is what was charged. When the payer bears the fee it includes the fee, so it
    /// is compared against, never credited in place of, what we asked for. See
    /// <c>PaymentTransaction.MarkSucceeded</c>.
    /// </para>
    /// </remarks>
    private sealed record VerifyData(
        [property: JsonPropertyName("status")] string? Status,
        [property: JsonPropertyName("reference")] string? Reference,
        [property: JsonPropertyName("amount")] JsonElement Amount,
        [property: JsonPropertyName("fees")] JsonElement Fees,
        [property: JsonPropertyName("currency")] string? Currency,
        [property: JsonPropertyName("gateway_response")] string? GatewayResponse);
}
