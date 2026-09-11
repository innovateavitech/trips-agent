using System.Text.Json;
using Microsoft.Extensions.Logging;
using TripsAgent.Application.Suppliers;
using TripsAgent.Domain.Suppliers;

namespace TripsAgent.Integrations.TripsAfrica;

/// <summary>
/// Trips Africa flights: international and domestic search (#33) and price confirmation (#35).
/// </summary>
/// <remarks>
/// <para>
/// Issuing (#36), status polling (#37) and fare rules arrive with their own issues and throw
/// <see cref="NotImplementedException"/> until then — honestly unbuilt, rather than quietly doing
/// nothing. Cancellation throws <see cref="SupplierOperationNotSupportedException"/> for good: Trips
/// Africa documents no flight cancellation, and a cancel that did nothing would refund a ticket that
/// still flies.
/// </para>
/// </remarks>
public sealed partial class TripsAfricaFlightAdapter : ISupplierAdapter
{
    private const string SearchPath = "api/Flight/SearchFlight";
    private const string DomesticSearchPath = "api/Flight/Domestic/SearchFlight";
    private const string ConfirmPath = "api/Flight/ConfirmTicketPrice";
    private const string DomesticConfirmPath = "api/Flight/Domestic/ConfirmTicketPrice";

    private readonly TripsAfricaSearchRunner _searcher;
    private readonly TripsAfricaBookingHttp _booking;
    private readonly TripsAfricaCredentials _credentials;
    private readonly TripsAfricaSupplier _supplier;
    private readonly TripsAfricaOptions _options;
    private readonly ILogger<TripsAfricaFlightAdapter> _logger;

    public TripsAfricaFlightAdapter(
        TripsAfricaSearchRunner searcher,
        TripsAfricaBookingHttp booking,
        TripsAfricaCredentials credentials,
        TripsAfricaSupplier supplier,
        TripsAfricaOptions options,
        ILogger<TripsAfricaFlightAdapter> logger)
    {
        _searcher = searcher;
        _booking = booking;
        _credentials = credentials;
        _supplier = supplier;
        _options = options;
        _logger = logger;
    }

    public string SupplierCode => TripsAfricaOptions.SupplierCode;

    public IReadOnlyCollection<SupplierProductType> Products { get; } = [SupplierProductType.Flight];

    public async Task<SupplierSearchResult> SearchAsync(
        SupplierCallContext context,
        SupplierSearchQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(query);

        if (query.ProductType != SupplierProductType.Flight)
        {
            throw new ArgumentException($"The flight adapter was asked to search {query.ProductType}.", nameof(query));
        }

        var domestic = TripsAfricaMapping.IsDomestic(query);
        var body = JsonSerializer.Serialize(TripsAfricaMapping.ToFlightSearchRequest(query, _options.PageSize), TripsAfricaMapping.Json);

        var response = await _searcher.SearchAsync(
            domestic ? DomesticSearchPath : SearchPath, body, SupplierProductType.Flight, context, cancellationToken);

        try
        {
            var result = TripsAfricaMapping.MapFlightSearch(response.Body, out var dropped);

            if (dropped > 0)
            {
                LogOffersDropped(_logger, dropped, dropped + result.Offers.Count);
            }

            return result;
        }
        catch (JsonException ex)
        {
            throw new SupplierUnavailableException("Trips Africa's flight search answer could not be read. Nothing was bought.", ex);
        }
    }

    /// <summary>
    /// Locks the price and holds a PNR. <b>Sent once</b>: it holds a seat, so it is never retried, and
    /// a 5xx is reported as an unknown outcome rather than as a refusal.
    /// </summary>
    public async Task<SupplierPriceConfirmation> ConfirmPriceAsync(
        SupplierCallContext context,
        SupplierPriceConfirmationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);

        var credentials = await _credentials.ForAsync(context, cancellationToken);
        var supplierId = await _supplier.IdAsync(cancellationToken);
        var domestic = TripsAfricaMapping.IsDomesticOffer(request.Reference);

        var response = await _booking.PostAsync(
            domestic ? DomesticConfirmPath : ConfirmPath,
            TripsAfricaMapping.ToFlightConfirmBody(request),
            SupplierProductType.Flight,
            credentials,
            supplierId,
            SupplierOperation.ConfirmPrice,
            context,
            cancellationToken);

        if (response.IsClientError)
        {
            throw new SupplierRequestRejectedException(
                (int)response.StatusCode,
                $"Trips Africa refused the price confirmation with HTTP {(int)response.StatusCode}.");
        }

        if (!response.IsSuccess)
        {
            throw new SupplierCallOutcomeUnknownException(
                SupplierOperation.ConfirmPrice,
                new HttpRequestException($"Trips Africa answered the price confirmation with HTTP {(int)response.StatusCode}."));
        }

        return new SupplierPriceConfirmation(
            request.SupplierSessionId,
            TripType: domestic ? "Domestic" : "International",
            TripMode: "Flight",
            TripsAfricaMapping.MapConfirmations(response.Body, credentials.MerchantKey));
    }

    public Task<SupplierIssueResult> IssueAsync(
        SupplierCallContext context,
        SupplierIssueRequest request,
        CancellationToken cancellationToken = default) =>
        throw new NotImplementedException("Trips Africa ticket issue is built in #36.");

    public Task<SupplierStatusResult> GetStatusAsync(
        SupplierCallContext context,
        SupplierStatusQuery query,
        CancellationToken cancellationToken = default) =>
        throw new NotImplementedException("Trips Africa booking status polling is built in #37.");

    public Task<SupplierFareRules> GetRulesAsync(
        SupplierCallContext context,
        SupplierRulesQuery query,
        CancellationToken cancellationToken = default) =>
        throw new NotImplementedException("Trips Africa fare rules are not built yet.");

    public Task<SupplierCancellationResult> CancelAsync(
        SupplierCallContext context,
        SupplierCancellationRequest request,
        CancellationToken cancellationToken = default) =>
        throw new SupplierOperationNotSupportedException(SupplierCode, SupplierOperation.Cancel, SupplierProductType.Flight);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Dropped {Dropped} of {Total} Trips Africa flight offers that could not be read in full")]
    private static partial void LogOffersDropped(ILogger logger, int dropped, int total);
}
