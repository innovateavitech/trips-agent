using System.Net;
using System.Net.Http.Headers;
using System.Text;
using TripsAgent.Application.Suppliers;
using TripsAgent.Domain.Suppliers;

namespace TripsAgent.Integrations.TripsAfrica;

/// <summary>One answer from Trips Africa: its status and its body, as text.</summary>
public sealed record TripsAfricaResponse(HttpStatusCode StatusCode, string Body)
{
    public bool IsSuccess => (int)StatusCode is >= 200 and < 300;

    public bool IsClientError => (int)StatusCode is >= 400 and < 500;
}

/// <summary>
/// Sends one request to Trips Africa, authenticated for the product, and tagged for the audit handler.
/// </summary>
/// <remarks>
/// <para>
/// <b>Sends once.</b> There is no retry in here, and none can be added underneath: the client is
/// registered through <c>AddSupplierHttpClient</c>, which refuses any handler but the audit handler.
/// The only retry in this integration is <see cref="TripsAfricaSearchRunner"/>'s, above this line and
/// for search alone — the one call that is a pure read.
/// </para>
/// <para>
/// Two clients, because they need two timeouts: a search attempt gives up after twenty seconds (#33),
/// while a booking call waits a minute, since a slow issue call abandoned early becomes an unknown
/// outcome to poll for (ADR-0003).
/// </para>
/// </remarks>
public abstract class TripsAfricaHttp
{
    private readonly HttpClient _http;

    protected TripsAfricaHttp(HttpClient http) => _http = http;

    public async Task<TripsAfricaResponse> PostAsync(
        string path,
        string jsonBody,
        SupplierProductType product,
        SupplierCredentials credentials,
        Guid supplierId,
        SupplierOperation operation,
        SupplierCallContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(credentials);

        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(jsonBody, Encoding.UTF8, "application/json"),
        };

        Authorise(request, product, credentials);
        request.ForSupplierCall(supplierId, operation, context);

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        return new TripsAfricaResponse(response.StatusCode, body);
    }

    /// <summary>
    /// The two products authenticate differently. Flights: a static bearer token. Buses: a bearer
    /// derived from the merchant key, with the key itself in a header. The audit handler redacts both
    /// before anything is stored.
    /// </summary>
    private static void Authorise(HttpRequestMessage request, SupplierProductType product, SupplierCredentials credentials)
    {
        request.Headers.Add("MerchantCode", credentials.MerchantCode);

        if (product == SupplierProductType.Bus)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue(
                "Bearer",
                ConfirmationHash.BusBearerToken(credentials.MerchantKey, credentials.MerchantCode));
            request.Headers.Add("MerchantKey", credentials.MerchantKey);
            return;
        }

        if (string.IsNullOrEmpty(credentials.BearerToken))
        {
            throw new InvalidOperationException(
                "The Trips Africa flight API needs a bearer token, and the credential in use has none. "
                + "Store one with the credential, or set TripsAfrica__BearerToken in .env.");
        }

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credentials.BearerToken);
    }
}

/// <summary>Searches, twenty seconds an attempt.</summary>
public sealed class TripsAfricaSearchHttp(HttpClient http) : TripsAfricaHttp(http);

/// <summary>Confirm, issue, status: a minute, and never retried.</summary>
public sealed class TripsAfricaBookingHttp(HttpClient http) : TripsAfricaHttp(http);
