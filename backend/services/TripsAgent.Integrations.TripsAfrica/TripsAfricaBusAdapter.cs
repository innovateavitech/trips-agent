using System.Text.Json;
using Microsoft.Extensions.Logging;
using TripsAgent.Application.Suppliers;
using TripsAgent.Domain.Suppliers;

namespace TripsAgent.Integrations.TripsAfrica;

/// <summary>
/// Trips Africa buses: one-way search (#34) and price confirmation (#35).
/// </summary>
/// <remarks>
/// A separate adapter from flights because Trips Africa treats them as separate APIs: different
/// endpoints, and a bearer token derived from the merchant key rather than a static one. Why search
/// is one-way only is on <see cref="TripsAfricaBusMapping"/>.
/// </remarks>
public sealed partial class TripsAfricaBusAdapter : ISupplierAdapter
{
    private const string SearchPath = "api/Bus/SearchBus";
    private const string ConfirmPath = "api/Bus/ConfirmTicketPrice";

    private readonly TripsAfricaSearchRunner _searcher;
    private readonly TripsAfricaBookingHttp _booking;
    private readonly TripsAfricaCredentials _credentials;
    private readonly TripsAfricaSupplier _supplier;
    private readonly ILogger<TripsAfricaBusAdapter> _logger;

    public TripsAfricaBusAdapter(
        TripsAfricaSearchRunner searcher,
        TripsAfricaBookingHttp booking,
        TripsAfricaCredentials credentials,
        TripsAfricaSupplier supplier,
        ILogger<TripsAfricaBusAdapter> logger)
    {
        _searcher = searcher;
        _booking = booking;
        _credentials = credentials;
        _supplier = supplier;
        _logger = logger;
    }

    public string SupplierCode => TripsAfricaOptions.SupplierCode;

    public IReadOnlyCollection<SupplierProductType> Products { get; } = [SupplierProductType.Bus];

    public async Task<SupplierSearchResult> SearchAsync(
        SupplierCallContext context,
        SupplierSearchQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(query);

        if (query.ProductType != SupplierProductType.Bus)
        {
            throw new ArgumentException($"The bus adapter was asked to search {query.ProductType}.", nameof(query));
        }

        var body = JsonSerializer.Serialize(TripsAfricaBusMapping.ToBusSearchRequest(query), TripsAfricaMapping.Json);
        var response = await _searcher.SearchAsync(SearchPath, body, SupplierProductType.Bus, context, cancellationToken);

        try
        {
            var result = TripsAfricaBusMapping.MapBusSearch(response.Body, out var dropped);

            if (dropped > 0)
            {
                LogOffersDropped(_logger, dropped, dropped + result.Offers.Count);
            }

            return result;
        }
        catch (JsonException ex)
        {
            throw new SupplierUnavailableException("Trips Africa's bus search answer could not be read. Nothing was bought.", ex);
        }
    }

    /// <summary>Locks the price and holds the seats. Sent once, like every booking call.</summary>
    public async Task<SupplierPriceConfirmation> ConfirmPriceAsync(
        SupplierCallContext context,
        SupplierPriceConfirmationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);

        var credentials = await _credentials.ForAsync(context, cancellationToken);
        var supplierId = await _supplier.IdAsync(cancellationToken);

        var response = await _booking.PostAsync(
            ConfirmPath,
            TripsAfricaBusMapping.ToBusConfirmBody(request),
            SupplierProductType.Bus,
            credentials,
            supplierId,
            SupplierOperation.ConfirmPrice,
            context,
            cancellationToken);

        if (response.IsClientError)
        {
            throw new SupplierRequestRejectedException(
                (int)response.StatusCode,
                $"Trips Africa refused the bus price confirmation with HTTP {(int)response.StatusCode}.");
        }

        if (!response.IsSuccess)
        {
            throw new SupplierCallOutcomeUnknownException(
                SupplierOperation.ConfirmPrice,
                new HttpRequestException($"Trips Africa answered the bus price confirmation with HTTP {(int)response.StatusCode}."));
        }

        // "Domestic" and "Road" are what the documented bus issue call sends back.
        return new SupplierPriceConfirmation(
            request.SupplierSessionId,
            TripType: "Domestic",
            TripMode: "Road",
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
        throw new NotImplementedException("Trips Africa travel rules are not built yet.");

    public Task<SupplierCancellationResult> CancelAsync(
        SupplierCallContext context,
        SupplierCancellationRequest request,
        CancellationToken cancellationToken = default) =>
        throw new NotImplementedException("Trips Africa bus cancellation is documented but not built yet.");

    [LoggerMessage(Level = LogLevel.Warning, Message = "Dropped {Dropped} of {Total} Trips Africa bus offers that could not be read in full")]
    private static partial void LogOffersDropped(ILogger logger, int dropped, int total);
}
