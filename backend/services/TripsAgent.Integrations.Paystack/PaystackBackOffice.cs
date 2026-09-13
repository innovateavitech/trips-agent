using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using TripsAgent.Application.Payments;
using TripsAgent.Domain.Common;

namespace TripsAgent.Integrations.Paystack;

/// <summary>
/// Paystack's disputes and settlements, behind <see cref="IGatewayBackOffice"/>.
/// </summary>
/// <remarks>
/// <para>
/// Nothing here moves money, so it sits on the ordinary retrying client. Evidence may be filed
/// again until the deadline and replaces what was there, so a retried submission is harmless.
/// </para>
/// <para>
/// <b>To check on Paystack test mode before going live</b> (docs/BUILD_PLAN.md, F12): whether a
/// dispute's <c>refund_amount</c> is in kobo like every other Paystack amount — this reads it as
/// kobo and falls back to the disputed transaction's amount when it is absent — and the field name
/// of a settlement's date, read here from <c>settled_date</c> then <c>settlement_date</c>.
/// </para>
/// </remarks>
public sealed class PaystackBackOffice : IGatewayBackOffice
{
    /// <summary>Paystack's largest page. Fewer round trips for the same data.</summary>
    private const int PageSize = 100;

    /// <summary>A bound on paging, so a gateway that reports an endless page count cannot hang the job.</summary>
    private const int MaxPages = 500;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;

    public PaystackBackOffice(HttpClient http) => _http = http;

    public async Task<GatewayDispute?> GetDisputeAsync(string gatewayDisputeId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gatewayDisputeId);

        var (status, body) = await GetAsync<DisputeData>(
            $"/dispute/{Uri.EscapeDataString(gatewayDisputeId)}", "a dispute", allowNotFound: true, cancellationToken);

        if (status == HttpStatusCode.NotFound || body?.Data is not { } data)
        {
            return null;
        }

        var amount = Minor(data.RefundAmount) ?? Minor(data.Transaction?.Amount)
            ?? throw new PaymentGatewayException($"Paystack described dispute {gatewayDisputeId} without an amount.");

        var reference = data.Transaction?.Reference
            ?? throw new PaymentGatewayException($"Paystack described dispute {gatewayDisputeId} without its transaction.");

        return new GatewayDispute(
            gatewayDisputeId,
            reference,
            new Money(amount),
            data.Currency ?? data.Transaction?.Currency ?? "NGN",
            Classify(data.Resolution),
            data.Status ?? "unknown",
            data.Resolution,
            data.Category,
            data.Message?.Body ?? data.Note,
            Instant(data.CreatedAt) ?? DateTimeOffset.UtcNow,
            Instant(data.DueAt) ?? (Instant(data.CreatedAt) ?? DateTimeOffset.UtcNow).AddDays(3));
    }

    public async Task SubmitEvidenceAsync(
        string gatewayDisputeId,
        DisputeEvidence evidence,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gatewayDisputeId);
        ArgumentNullException.ThrowIfNull(evidence);

        var request = new
        {
            customer_email = evidence.CustomerEmail,
            customer_name = evidence.CustomerName,
            customer_phone = evidence.CustomerPhone,
            service_details = evidence.ServiceDetails,
            delivery_date = evidence.DeliveryDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        };

        HttpResponseMessage response;

        try
        {
            response = await _http.PostAsJsonAsync(
                $"/dispute/{Uri.EscapeDataString(gatewayDisputeId)}/evidence", request, Json, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            throw new PaymentGatewayUnavailableException("Paystack could not be reached to file dispute evidence.", ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new PaymentGatewayUnavailableException("Paystack did not answer in time when filing dispute evidence.", ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var code = (int)response.StatusCode;
                var message = await MessageFrom(response, cancellationToken);

                throw IsTransient(response.StatusCode)
                    ? new PaymentGatewayUnavailableException($"Paystack answered HTTP {code} filing evidence: {message}")
                    : new PaymentGatewayException($"Paystack refused the evidence: {message}");
            }
        }
    }

    public async Task<IReadOnlyList<GatewaySettlement>> SettlementsAsync(
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd,
        CancellationToken cancellationToken = default)
    {
        var settlements = new List<GatewaySettlement>();

        var fromText = Uri.EscapeDataString(windowStart.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture));
        var toText = Uri.EscapeDataString(windowEnd.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture));

        // Every page, not the first one. See the port's remarks.
        for (var page = 1; page <= MaxPages; page++)
        {
            var (_, body) = await GetAsync<List<SettlementData>>(
                $"/settlement?from={fromText}&to={toText}&perPage={PageSize}&page={page}",
                "settlements", allowNotFound: false, cancellationToken);

            foreach (var settlement in body?.Data ?? [])
            {
                var id = settlement.Id?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
                var gross = Minor(settlement.TotalAmount) ?? 0;
                var fees = Minor(settlement.TotalFees) ?? 0;
                var net = Minor(settlement.EffectiveAmount) ?? gross - fees;

                settlements.Add(new GatewaySettlement(
                    id,
                    Instant(settlement.SettledDate) ?? Instant(settlement.SettlementDate) ?? windowStart,
                    new Money(gross),
                    new Money(fees),
                    new Money(net),
                    settlement.Currency ?? "NGN",
                    await LinesAsync(id, cancellationToken)));
            }

            if (body?.Meta?.PageCount is not { } pages || page >= pages)
            {
                break;
            }
        }

        return settlements;
    }

    private async Task<IReadOnlyList<GatewaySettlementLine>> LinesAsync(string settlementId, CancellationToken cancellationToken)
    {
        var lines = new List<GatewaySettlementLine>();

        for (var page = 1; page <= MaxPages; page++)
        {
            var (_, body) = await GetAsync<List<SettlementTransactionData>>(
                $"/settlement/{Uri.EscapeDataString(settlementId)}/transactions?perPage={PageSize}&page={page}",
                "settlement transactions", allowNotFound: false, cancellationToken);

            foreach (var line in body?.Data ?? [])
            {
                lines.Add(new GatewaySettlementLine(
                    line.Reference ?? string.Empty,
                    new Money(Minor(line.Amount) ?? 0),
                    new Money(Minor(line.Fees) ?? 0),
                    line.Currency ?? "NGN"));
            }

            if (body?.Meta?.PageCount is not { } pages || page >= pages)
            {
                break;
            }
        }

        return lines;
    }

    /// <summary>Paystack's two dispute fields, read together.</summary>
    /// <remarks>
    /// <c>merchant-accepted</c> means we accepted the chargeback: the money goes back. <c>declined</c>
    /// means the dispute was declined in our favour. Anything else, including a resolved status with
    /// a resolution we do not recognise, stays open — a dispute marked decided on a guess moves money
    /// on a guess.
    /// </remarks>
    private static GatewayDisputeState Classify(string? resolution) => resolution?.ToLowerInvariant() switch
    {
        "merchant-accepted" => GatewayDisputeState.MerchantLost,
        "declined" => GatewayDisputeState.MerchantWon,
        _ => GatewayDisputeState.Open,
    };

    private async Task<(HttpStatusCode Status, PaystackResponse<T>? Body)> GetAsync<T>(
        string path,
        string what,
        bool allowNotFound,
        CancellationToken cancellationToken)
    {
        HttpResponseMessage response;

        try
        {
            response = await _http.GetAsync(new Uri(path, UriKind.Relative), cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            throw new PaymentGatewayUnavailableException($"Paystack could not be reached for {what}.", ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new PaymentGatewayUnavailableException($"Paystack did not answer in time for {what}.", ex);
        }
        catch (Polly.ExecutionRejectedException ex)
        {
            throw new PaymentGatewayUnavailableException($"Paystack calls are paused; {what} was not read.", ex);
        }

        using (response)
        {
            if (allowNotFound && response.StatusCode == HttpStatusCode.NotFound)
            {
                return (response.StatusCode, null);
            }

            if (!response.IsSuccessStatusCode)
            {
                var code = (int)response.StatusCode;
                var message = await MessageFrom(response, cancellationToken);

                throw IsTransient(response.StatusCode)
                    ? new PaymentGatewayUnavailableException($"Paystack answered HTTP {code} for {what}: {message}")
                    : new PaymentGatewayException($"Paystack refused to give {what}: HTTP {code} {message}");
            }

            try
            {
                return (response.StatusCode,
                    await response.Content.ReadFromJsonAsync<PaystackResponse<T>>(Json, cancellationToken));
            }
            catch (JsonException ex)
            {
                throw new PaymentGatewayException($"Paystack's response for {what} could not be read.", ex);
            }
        }
    }

    private static bool IsTransient(HttpStatusCode status) =>
        (int)status >= 500 || status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests;

    /// <summary>A whole number of minor units, or null when absent or fractional.</summary>
    private static long? Minor(JsonElement? value) =>
        value is { ValueKind: JsonValueKind.Number } number && number.TryGetInt64(out var minor) ? minor : null;

    /// <summary>An instant, converted to UTC here at the boundary, where it arrives.</summary>
    private static DateTimeOffset? Instant(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed.ToUniversalTime()
            : null;

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

    private sealed record PaystackResponse<T>(bool Status, string? Message, T? Data, MetaData? Meta);

    private sealed record MetaData([property: JsonPropertyName("pageCount")] int? PageCount);

    private sealed record DisputeMessage([property: JsonPropertyName("body")] string? Body);

    private sealed record DisputeTransaction(
        [property: JsonPropertyName("reference")] string? Reference,
        [property: JsonPropertyName("amount")] JsonElement? Amount,
        [property: JsonPropertyName("currency")] string? Currency);

    private sealed record DisputeData(
        [property: JsonPropertyName("refund_amount")] JsonElement? RefundAmount,
        [property: JsonPropertyName("currency")] string? Currency,
        [property: JsonPropertyName("status")] string? Status,
        [property: JsonPropertyName("resolution")] string? Resolution,
        [property: JsonPropertyName("category")] string? Category,
        [property: JsonPropertyName("note")] string? Note,
        [property: JsonPropertyName("message")] DisputeMessage? Message,
        [property: JsonPropertyName("due_at")] string? DueAt,
        [property: JsonPropertyName("created_at")] string? CreatedAt,
        [property: JsonPropertyName("transaction")] DisputeTransaction? Transaction);

    private sealed record SettlementData(
        [property: JsonPropertyName("id")] long? Id,
        [property: JsonPropertyName("total_amount")] JsonElement? TotalAmount,
        [property: JsonPropertyName("total_fees")] JsonElement? TotalFees,
        [property: JsonPropertyName("effective_amount")] JsonElement? EffectiveAmount,
        [property: JsonPropertyName("currency")] string? Currency,
        [property: JsonPropertyName("settled_date")] string? SettledDate,
        [property: JsonPropertyName("settlement_date")] string? SettlementDate);

    private sealed record SettlementTransactionData(
        [property: JsonPropertyName("reference")] string? Reference,
        [property: JsonPropertyName("amount")] JsonElement? Amount,
        [property: JsonPropertyName("fees")] JsonElement? Fees,
        [property: JsonPropertyName("currency")] string? Currency);
}
