using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using TripsAgent.Application.Suppliers;
using TripsAgent.Domain.Suppliers;
using TripsAgent.Integrations.TripsAfrica;

namespace TripsAgent.UnitTests.Suppliers.TripsAfrica;

/// <summary>
/// The adapters over a stub network: which endpoint, which headers, and — the part that matters most —
/// what is retried and what never is (#33, #34, #35, ADR-0003).
/// </summary>
public class TripsAfricaAdapterTests
{
    private static readonly Guid SupplierId = Guid.CreateVersion7();
    private static readonly SupplierCallContext Context = new(AgencyId: Guid.CreateVersion7());

    private const string EmptyFlightAnswer = """{ "SessionId": "s", "ResultList": [] }""";
    private const string EmptyBusAnswer = """{ "TripsSessionId": "s", "FirstLeg": { "ResultList": [] } }""";

    // ------------------------------------------------------------------------ endpoints and auth

    [Fact]
    public async Task A_search_between_nigerian_airports_goes_to_the_domestic_endpoint()
    {
        var network = new ScriptedNetwork(Answer(HttpStatusCode.OK, EmptyFlightAnswer));

        await Flight(network).SearchAsync(Context, FlightQuery("LOS", "ABV"));

        network.Requests.Should().ContainSingle().Which.Path.Should().Be("/api/Flight/Domestic/SearchFlight");
    }

    [Fact]
    public async Task A_search_abroad_goes_to_the_international_endpoint()
    {
        var network = new ScriptedNetwork(Answer(HttpStatusCode.OK, EmptyFlightAnswer));

        await Flight(network).SearchAsync(Context, FlightQuery("LOS", "LHR"));

        network.Requests.Should().ContainSingle().Which.Path.Should().Be("/api/Flight/SearchFlight");
    }

    [Fact]
    public async Task A_flight_call_carries_the_static_bearer_token_and_the_merchant_code()
    {
        var network = new ScriptedNetwork(Answer(HttpStatusCode.OK, EmptyFlightAnswer));

        await Flight(network).SearchAsync(Context, FlightQuery("LOS", "LHR"));

        var request = network.Requests.Should().ContainSingle().Subject;
        request.Authorization.Should().Be("Bearer flight-bearer-token");
        request.Headers.Should().Contain("MerchantCode", "TESTCODE");
        request.Headers.Should().NotContainKey("MerchantKey", "the flight API is not sent the key at all");
    }

    [Fact]
    public async Task A_bus_call_derives_its_bearer_from_the_key_and_sends_the_key_in_its_own_header()
    {
        var network = new ScriptedNetwork(Answer(HttpStatusCode.OK, EmptyBusAnswer));

        await Bus(network).SearchAsync(Context, BusQuery());

        var request = network.Requests.Should().ContainSingle().Subject;
        request.Path.Should().Be("/api/Bus/SearchBus");
        request.Authorization.Should().Be("Bearer " + ConfirmationHash.BusBearerToken("test-merchant-key", "TESTCODE"));
        request.Headers.Should().Contain("MerchantKey", "test-merchant-key");
    }

    // ------------------------------------------------------------------------------ retrying

    [Fact]
    public async Task A_search_the_supplier_failed_to_answer_is_asked_again_because_it_is_a_read()
    {
        var network = new ScriptedNetwork(
            Answer(HttpStatusCode.ServiceUnavailable, "down"),
            Answer(HttpStatusCode.OK, EmptyFlightAnswer));

        var result = await Flight(network).SearchAsync(Context, FlightQuery("LOS", "LHR"));

        result.Offers.Should().BeEmpty();
        network.Requests.Should().HaveCount(2);
    }

    [Fact]
    public async Task A_search_the_supplier_refused_is_not_asked_again_because_the_answer_would_be_the_same()
    {
        var network = new ScriptedNetwork(Answer(HttpStatusCode.BadRequest, "bad route"));

        var act = () => Flight(network).SearchAsync(Context, FlightQuery("LOS", "LHR"));

        (await act.Should().ThrowAsync<SupplierRequestRejectedException>()).Which.StatusCode.Should().Be(400);
        network.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task A_search_that_never_gets_an_answer_says_the_supplier_is_unavailable_and_nothing_was_bought()
    {
        var network = new ScriptedNetwork(
            Answer(HttpStatusCode.BadGateway, "x"),
            Answer(HttpStatusCode.BadGateway, "x"));

        var act = () => Flight(network).SearchAsync(Context, FlightQuery("LOS", "LHR"));

        (await act.Should().ThrowAsync<SupplierUnavailableException>()).Which.Message.Should().Contain("Nothing was bought");
        network.Requests.Should().HaveCount(2, "two attempts, as configured, and no more");
    }

    [Fact]
    public async Task After_repeated_failures_the_circuit_opens_and_a_search_sends_nothing_at_all()
    {
        var options = new TripsAfricaOptions { SearchAttempts = 1, CircuitBreakerThreshold = 2 };
        var clock = new ManualClock();
        var circuits = new TripsAfricaSearchCircuits(options, clock);
        var network = new ScriptedNetwork(Enumerable.Repeat(Answer(HttpStatusCode.BadGateway, "x"), 10).ToArray());
        var adapter = Flight(network, options, circuits);

        for (var i = 0; i < 2; i++)
        {
            await adapter.Invoking(a => a.SearchAsync(Context, FlightQuery("LOS", "LHR")))
                .Should().ThrowAsync<SupplierUnavailableException>();
        }

        var sentBefore = network.Requests.Count;
        var paused = () => adapter.SearchAsync(Context, FlightQuery("LOS", "LHR"));

        (await paused.Should().ThrowAsync<SupplierUnavailableException>()).Which.Message.Should().Contain("paused");
        network.Requests.Should().HaveCount(sentBefore, "an open circuit fails fast without touching the network");

        // After the cooldown one search is let through; a single failure re-opens it at once.
        clock.Advance(options.CircuitBreakerCooldown + TimeSpan.FromSeconds(1));
        await paused.Should().ThrowAsync<SupplierUnavailableException>();
        network.Requests.Should().HaveCount(sentBefore + 1);

        await paused.Should().ThrowAsync<SupplierUnavailableException>();
        network.Requests.Should().HaveCount(sentBefore + 1, "the failed trial re-opened the circuit");
    }

    [Fact]
    public async Task A_price_confirmation_is_sent_once_and_a_server_error_is_an_unknown_outcome_not_a_refusal()
    {
        var network = new ScriptedNetwork(Answer(HttpStatusCode.InternalServerError, "oops"), Answer(HttpStatusCode.OK, "[]"));

        var act = () => Flight(network).ConfirmPriceAsync(Context, ConfirmRequest());

        await act.Should().ThrowAsync<SupplierCallOutcomeUnknownException>();
        network.Requests.Should().ContainSingle("a confirmation holds a seat, and is never retried");
    }

    [Fact]
    public async Task A_flight_cannot_be_cancelled_through_trips_africa_and_says_so()
    {
        var act = () => Flight(new ScriptedNetwork()).CancelAsync(
            Context, new SupplierCancellationRequest(SupplierProductType.Flight, "1|A"));

        await act.Should().ThrowAsync<SupplierOperationNotSupportedException>();
    }

    // ---------------------------------------------------------------------------- building

    private static TripsAfricaFlightAdapter Flight(
        ScriptedNetwork network,
        TripsAfricaOptions? options = null,
        TripsAfricaSearchCircuits? circuits = null)
    {
        options ??= new TripsAfricaOptions();
        var (runner, booking, credentials, supplier) = Parts(network, options, circuits);
        return new TripsAfricaFlightAdapter(runner, booking, credentials, supplier, options, NullLogger<TripsAfricaFlightAdapter>.Instance);
    }

    private static TripsAfricaBusAdapter Bus(ScriptedNetwork network)
    {
        var options = new TripsAfricaOptions();
        var (runner, booking, credentials, supplier) = Parts(network, options, circuits: null);
        return new TripsAfricaBusAdapter(runner, booking, credentials, supplier, NullLogger<TripsAfricaBusAdapter>.Instance);
    }

    private static (TripsAfricaSearchRunner, TripsAfricaBookingHttp, TripsAfricaCredentials, TripsAfricaSupplier) Parts(
        ScriptedNetwork network,
        TripsAfricaOptions options,
        TripsAfricaSearchCircuits? circuits)
    {
        // Straight onto the stub: these tests are about the adapter's own behaviour. That the audit
        // handler wraps every supplier client is SupplierHttpClientRegistrationTests' job.
        var http = new HttpClient(network) { BaseAddress = new Uri("https://trips.test/") };
        var credentials = new TripsAfricaCredentials(new FixedCredentialStore(), options);
        var supplier = new TripsAfricaSupplier(SupplierId);
        var runner = new TripsAfricaSearchRunner(
            new TripsAfricaSearchHttp(http),
            credentials,
            supplier,
            circuits ?? new TripsAfricaSearchCircuits(options, TimeProvider.System),
            options,
            NullLogger<TripsAfricaSearchRunner>.Instance);

        return (runner, new TripsAfricaBookingHttp(http), credentials, supplier);
    }

    private static SupplierSearchQuery FlightQuery(string origin, string destination) =>
        new(
            SupplierProductType.Flight,
            SupplierTripShape.OneWay,
            [new SupplierSearchLeg(origin, destination, new DateOnly(2026, 10, 2))],
            new SupplierPassengerCounts(1));

    private static SupplierSearchQuery BusQuery() =>
        new(
            SupplierProductType.Bus,
            SupplierTripShape.OneWay,
            [new SupplierSearchLeg("60", "51", new DateOnly(2026, 10, 3))],
            new SupplierPassengerCounts(1));

    private static SupplierPriceConfirmationRequest ConfirmRequest() =>
        new(
            SupplierProductType.Flight,
            "sess-intl-0001",
            "5:2:0:7:-",
            new SupplierOfferReference("5", "2", "0", "7"),
            [new SupplierPassenger(PassengerType.Adult, "Ngozi", "Adeyemi", Email: "ngozi@example.test")]);

    private static Func<HttpResponseMessage> Answer(HttpStatusCode status, string body) =>
        () => new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    /// <summary>Answers each request with the next scripted response, and remembers what was sent.</summary>
    private sealed class ScriptedNetwork(params Func<HttpResponseMessage>[] script) : HttpMessageHandler
    {
        private readonly Queue<Func<HttpResponseMessage>> _script = new(script);

        public List<SentRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);

            Requests.Add(new SentRequest(
                request.RequestUri!.AbsolutePath,
                request.Headers.Authorization?.ToString(),
                request.Headers.ToDictionary(header => header.Key, header => string.Join(",", header.Value)),
                body));

            return _script.Count > 0
                ? _script.Dequeue()()
                : throw new InvalidOperationException("The test sent more requests than it scripted answers for.");
        }
    }

    private sealed record SentRequest(string Path, string? Authorization, Dictionary<string, string> Headers, string Body);

    private sealed class FixedCredentialStore : ISupplierCredentialStore
    {
        public Task<SupplierCredentials?> FindAsync(
            string supplierCode,
            SupplierEnvironment environment,
            Guid? agencyId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<SupplierCredentials?>(new SupplierCredentials(
                Guid.CreateVersion7(), supplierCode, environment, AgencyId: null, "TESTCODE", "test-merchant-key", "flight-bearer-token"));

        public Task<Guid> SaveAsync(
            string supplierCode,
            SupplierEnvironment environment,
            Guid? agencyId,
            string merchantCode,
            string merchantKey,
            string? bearerToken,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 9, 11, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
