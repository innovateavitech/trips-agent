using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using TripsAgent.Application.Payments;
using TripsAgent.Domain.Common;

namespace TripsAgent.Integrations.Paystack;

/// <summary>
/// Paystack Transfers, behind <see cref="IBankTransfers"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Registered on an HTTP client with no retry policy, on purpose.</b> See
/// <c>DependencyInjection.AddPaystackTransfers</c> and
/// docs/adr/0008-never-retry-payout-transfers.md. If you are here because a transfer timed out and
/// you are wondering why nothing retried it: that is the feature. Ask
/// <see cref="GetTransferAsync"/> what happened instead.
/// </para>
/// <para>
/// Amounts go to Paystack in minor units, which is how <see cref="Money"/> already holds them.
/// There is deliberately no arithmetic anywhere in this file.
/// </para>
/// <para>
/// One operational prerequisite this code cannot enforce: <b>transfer OTP must be disabled</b> on
/// the Paystack account, or every transfer comes back <c>otp</c> and waits for a human to type a
/// code into a dashboard. That is a setting, not a code change. A transfer that comes back
/// needing one is reported as <see cref="TransferOutcome.Unknown"/> — it may yet complete, so it
/// must not be treated as failed — and the payout poller will keep asking until somebody notices.
/// </para>
/// </remarks>
public sealed class PaystackBankTransfers : IBankTransfers
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;

    public PaystackBankTransfers(HttpClient http) => _http = http;

    public string Name => "paystack";

    public async Task<IReadOnlyList<BankListing>> ListBanksAsync(CancellationToken cancellationToken = default)
    {
        // Nigeria only: the payout rail is NUBAN, and a bank we cannot address is a bank that
        // would fail at the moment money moved rather than at the moment it was chosen.
        var body = await GetAsync<List<BankData>>("/bank?country=nigeria&perPage=100", "the bank list", cancellationToken);

        return body.Data is null
            ? []
            : body.Data
                .Where(bank => bank.Code is { Length: > 0 } && bank.Name is { Length: > 0 })
                .Select(bank => new BankListing(bank.Code!, bank.Name!))
                .ToList();
    }

    public async Task<ResolvedBankAccount> ResolveAccountAsync(
        string accountNumber,
        string bankCode,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountNumber);
        ArgumentException.ThrowIfNullOrWhiteSpace(bankCode);

        var path = $"/bank/resolve?account_number={Uri.EscapeDataString(accountNumber)}"
                   + $"&bank_code={Uri.EscapeDataString(bankCode)}";

        using var response = await SendAsync(HttpMethod.Get, path, content: null, "resolve an account", cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var status = (int)response.StatusCode;
            var message = await MessageFrom(response, cancellationToken);

            if (IsTransient(response.StatusCode))
            {
                throw new PaymentGatewayUnavailableException(
                    $"Paystack answered HTTP {status} when asked to resolve an account: {message}");
            }

            // Paystack answers 4xx for "cannot resolve", which is not an error condition — it is
            // the answer. Almost always a mistyped digit, and the person who typed it can fix it.
            return new ResolvedBankAccount(BankAccountResolution.NotFound, string.Empty, message);
        }

        var body = await ReadAsync<ResolveData>(response, "resolve", cancellationToken);

        if (!body.Status || body.Data?.AccountName is not { Length: > 0 } name)
        {
            return new ResolvedBankAccount(
                BankAccountResolution.Refused, string.Empty, body.Message ?? "Paystack would not name the account.");
        }

        return new ResolvedBankAccount(BankAccountResolution.Resolved, name, null);
    }

    public async Task<string> CreateRecipientAsync(
        string accountNumber,
        string bankCode,
        string accountName,
        string currency,
        CancellationToken cancellationToken = default)
    {
        var request = new
        {
            type = "nuban",
            name = accountName,
            account_number = accountNumber,
            bank_code = bankCode,
            currency,
        };

        var body = await PostAsync<RecipientData>("/transferrecipient", request, "create a transfer recipient", cancellationToken);

        return body.Data?.RecipientCode is { Length: > 0 } code
            ? code
            : throw new PaymentGatewayException(
                $"Paystack created a transfer recipient without giving it a code: {body.Message ?? "(no message)"}");
    }

    public async Task<Money?> GetBalanceAsync(string currency, CancellationToken cancellationToken = default)
    {
        PaystackResponse<List<BalanceData>> body;

        try
        {
            body = await GetAsync<List<BalanceData>>("/balance", "the platform balance", cancellationToken);
        }
        catch (PaymentGatewayUnavailableException)
        {
            // Not knowing the float is not a reason to stop. The check exists so an empty balance
            // raises an operational alert rather than failing an agent's payout as if it were
            // their fault; if the check itself cannot run, the transfer's own answer will say.
            return null;
        }

        var match = body.Data?.Find(
            balance => string.Equals(balance.Currency, currency, StringComparison.OrdinalIgnoreCase));

        return match?.Balance is { } minor ? new Money(minor) : null;
    }

    /// <summary>
    /// Sends money. Called at most once per payout, and never retried. See the class remarks.
    /// </summary>
    public async Task<GatewayTransfer> InitiateTransferAsync(
        string reference,
        string recipientCode,
        Money amount,
        string currency,
        string reason,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);
        ArgumentException.ThrowIfNullOrWhiteSpace(recipientCode);

        if (amount.AmountMinor <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(amount), amount.AmountMinor, "A transfer must be positive.");
        }

        var request = new
        {
            source = "balance",
            reference,
            recipient = recipientCode,
            amount = amount.AmountMinor.ToString(CultureInfo.InvariantCulture),
            currency,
            reason = Clip(reason),
        };

        // No retry, no resilience pipeline, no second attempt anywhere below this line. Every
        // failure to get an answer becomes PaymentGatewayUnavailableException, which the caller
        // records as an unknown outcome. See ADR-0008.
        using var response = await SendAsync(
            HttpMethod.Post, "/transfer", JsonContent.Create(request, options: Json), "initiate a transfer", cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var status = (int)response.StatusCode;
            var message = await MessageFrom(response, cancellationToken);

            if (IsTransient(response.StatusCode))
            {
                // Unknown, not failed. The transfer may already be on its way.
                throw new PaymentGatewayUnavailableException(
                    $"Paystack answered HTTP {status} when asked to transfer {reference}: {message}");
            }

            // Paystack says so in the message rather than in a code of its own. A duplicate
            // reference means our own earlier attempt got through — the one outcome that must
            // never be read as a failure.
            if (message.Contains("duplicate", StringComparison.OrdinalIgnoreCase)
                || message.Contains("already", StringComparison.OrdinalIgnoreCase))
            {
                return new GatewayTransfer(
                    TransferOutcome.Unknown, "duplicate", null,
                    $"Paystack has this reference already: {message}. Asking for its status.");
            }

            return new GatewayTransfer(TransferOutcome.Refused, $"http_{status}", null, message);
        }

        var body = await ReadAsync<TransferData>(response, "transfer", cancellationToken);

        if (!body.Status || body.Data is null)
        {
            return new GatewayTransfer(TransferOutcome.Refused, "refused", null, body.Message ?? "(no message)");
        }

        return new GatewayTransfer(
            Classify(body.Data.Status), body.Data.Status ?? "unknown", body.Data.TransferCode, body.Data.FailureReason);
    }

    public async Task<GatewayTransfer> GetTransferAsync(string reference, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);

        using var response = await SendAsync(
            HttpMethod.Get,
            $"/transfer/verify/{Uri.EscapeDataString(reference)}",
            content: null,
            "verify a transfer",
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var status = (int)response.StatusCode;
            var message = await MessageFrom(response, cancellationToken);

            if (IsTransient(response.StatusCode))
            {
                throw new PaymentGatewayUnavailableException(
                    $"Paystack answered HTTP {status} when asked about transfer {reference}: {message}");
            }

            // A 404 here means Paystack has no record under our reference. That is *not* proof the
            // money did not move — a transfer created moments ago may not be queryable yet — so it
            // stays unknown and the poller asks again.
            return new GatewayTransfer(TransferOutcome.Unknown, $"http_{status}", null, message);
        }

        var body = await ReadAsync<TransferData>(response, "transfer verify", cancellationToken);

        return body.Status && body.Data is not null
            ? new GatewayTransfer(
                Classify(body.Data.Status), body.Data.Status ?? "unknown", body.Data.TransferCode, body.Data.FailureReason)
            : new GatewayTransfer(TransferOutcome.Unknown, "no_answer", null, body.Message);
    }

    /// <summary>
    /// What a Paystack transfer status means.
    /// </summary>
    /// <remarks>
    /// Only <c>success</c>, <c>failed</c> and <c>reversed</c> are answers. <c>otp</c>,
    /// <c>pending</c>, <c>processing</c>, <c>abandoned</c> and anything Paystack adds later leave
    /// the transfer undecided — and undecided must never collapse into failed, because a failed
    /// payout puts the money back in the wallet and an agency paid by both the bank and the books
    /// has been paid twice.
    /// </remarks>
    private static TransferOutcome Classify(string? status) => status?.ToLowerInvariant() switch
    {
        "success" => TransferOutcome.Succeeded,
        "failed" => TransferOutcome.Failed,
        "reversed" => TransferOutcome.Reversed,
        "pending" or "processing" or "queued" or "received" => TransferOutcome.Queued,
        _ => TransferOutcome.Unknown,
    };

    private static bool IsTransient(HttpStatusCode status) =>
        (int)status >= 500 || status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests;

    private async Task<PaystackResponse<T>> GetAsync<T>(string path, string what, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Get, path, content: null, what, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var status = (int)response.StatusCode;
            var message = await MessageFrom(response, cancellationToken);

            throw IsTransient(response.StatusCode)
                ? new PaymentGatewayUnavailableException($"Paystack answered HTTP {status} for {what}: {message}")
                : new PaymentGatewayException($"Paystack refused to give {what}: HTTP {status} {message}");
        }

        return await ReadAsync<T>(response, what, cancellationToken);
    }

    private async Task<PaystackResponse<T>> PostAsync<T>(
        string path,
        object request,
        string what,
        CancellationToken cancellationToken)
    {
        using var response = await SendAsync(
            HttpMethod.Post, path, JsonContent.Create(request, options: Json), what, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var status = (int)response.StatusCode;
            var message = await MessageFrom(response, cancellationToken);

            throw IsTransient(response.StatusCode)
                ? new PaymentGatewayUnavailableException($"Paystack answered HTTP {status} when asked to {what}: {message}")
                : new PaymentGatewayException($"Paystack refused to {what}: HTTP {status} {message}");
        }

        var body = await ReadAsync<T>(response, what, cancellationToken);

        return body.Status
            ? body
            : throw new PaymentGatewayException($"Paystack refused to {what}: {body.Message ?? "(no message)"}");
    }

    /// <summary>Sends the request, turning every way the network can fail into our own exception.</summary>
    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string path,
        HttpContent? content,
        string what,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative)) { Content = content };

        try
        {
            return await _http.SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            throw new PaymentGatewayUnavailableException($"Paystack could not be reached to {what}.", ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            // HttpClient reports its own timeout as a cancellation. Only the caller's token
            // cancelling means "stop"; this means "Paystack was too slow" — which, for a transfer,
            // is an unknown outcome and never a failure.
            throw new PaymentGatewayUnavailableException($"Paystack did not answer in time when asked to {what}.", ex);
        }
    }

    private static async Task<PaystackResponse<T>> ReadAsync<T>(
        HttpResponseMessage response,
        string what,
        CancellationToken cancellationToken)
    {
        try
        {
            return await response.Content.ReadFromJsonAsync<PaystackResponse<T>>(Json, cancellationToken)
                   ?? throw new PaymentGatewayException($"Paystack returned an empty {what} response.");
        }
        catch (JsonException ex)
        {
            throw new PaymentGatewayException($"Paystack's {what} response could not be read.", ex);
        }
    }

    /// <summary>
    /// Paystack's own explanation, for the log. Never the body verbatim: a failed response can
    /// echo the request, and a transfer request carries a bank account number.
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

    /// <summary>How long a transfer reason may be. Paystack truncates; this says so out loud.</summary>
    private static string Clip(string reason) => reason.Length <= 100 ? reason : reason[..100];

    private sealed record PaystackResponse<T>(bool Status, string? Message, T? Data);

    private sealed record BankData(
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("code")] string? Code);

    private sealed record ResolveData(
        [property: JsonPropertyName("account_number")] string? AccountNumber,
        [property: JsonPropertyName("account_name")] string? AccountName);

    private sealed record RecipientData(
        [property: JsonPropertyName("recipient_code")] string? RecipientCode);

    private sealed record BalanceData(
        [property: JsonPropertyName("currency")] string? Currency,
        [property: JsonPropertyName("balance")] long? Balance);

    private sealed record TransferData(
        [property: JsonPropertyName("status")] string? Status,
        [property: JsonPropertyName("transfer_code")] string? TransferCode,
        [property: JsonPropertyName("reference")] string? Reference,
        [property: JsonPropertyName("reason")] string? FailureReason);
}
