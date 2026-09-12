using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TripsAgent.Application.Suppliers;
using TripsAgent.Domain.Suppliers;
using TripsAgent.Integrations.TripsAfrica;

namespace TripsAgent.UnitTests.Suppliers.TripsAfrica;

/// <summary>
/// Issuing a ticket and asking where a booking got to, against the documented payloads (#36, #37).
/// </summary>
/// <remarks>
/// The rule that matters most is at the bottom: through the real client registration, an issue call
/// that times out or answers 5xx is sent exactly once (ADR-0003).
/// </remarks>
public class TripsAfricaTicketingTests
{
    private static readonly Guid SupplierId = Guid.CreateVersion7();
    private static readonly SupplierCallContext Context = new(AgencyId: Guid.CreateVersion7(), SupplierBookingId: Guid.CreateVersion7());

    // --------------------------------------------------------------------- TripType × TripMode

    [Theory]
    [InlineData(SupplierProductType.Flight, "International", "Flight", "International", "Flight")]
    [InlineData(SupplierProductType.Flight, "Domestic", "Flight", "Domestic", "Flight")]
    [InlineData(SupplierProductType.Flight, "domestic", null, "Domestic", "Flight")]
    [InlineData(SupplierProductType.Bus, "Domestic", "Road", "Domestic", "Road")]
    [InlineData(SupplierProductType.Bus, "International", "Road", "International", "Road")]
    [InlineData(SupplierProductType.Bus, null, null, "Domestic", "Road")]
    public void The_issue_request_carries_the_trip_type_and_mode_in_the_suppliers_words(
        SupplierProductType product, string? tripType, string? tripMode, string expectedType, string expectedMode)
    {
        var body = TripsAfricaMapping.ToIssueBody(IssueRequest(product, tripType, tripMode), product);

        using var json = JsonDocument.Parse(body);
        json.RootElement.GetProperty("SessionId").GetString().Should().Be("8646790ccb9a4d0997a6b52693287256");
        json.RootElement.GetProperty("TripType").GetString().Should().Be(expectedType);
        json.RootElement.GetProperty("TripMode").GetString().Should().Be(expectedMode);
    }

    [Theory]
    [InlineData(SupplierProductType.Flight, null, "Flight")]
    [InlineData(SupplierProductType.Flight, "Regional", "Flight")]
    [InlineData(SupplierProductType.Flight, "International", "Road")]
    [InlineData(SupplierProductType.Bus, "Domestic", "Flight")]
    public void A_trip_type_or_mode_that_does_not_fit_is_refused_before_anything_is_sent(
        SupplierProductType product, string? tripType, string? tripMode)
    {
        var build = () => TripsAfricaMapping.ToIssueBody(IssueRequest(product, tripType, tripMode), product);

        build.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void A_booking_is_never_issued_through_the_other_products_adapter()
    {
        var build = () => TripsAfricaMapping.ToIssueBody(IssueRequest(SupplierProductType.Bus, "Domestic", "Road"), SupplierProductType.Flight);

        build.Should().Throw<ArgumentException>();
    }

    // ------------------------------------------------------------------------ reading answers

    [Theory]
    [InlineData("TicketPending", 3)]
    [InlineData("TicketIssued", 2)]
    [InlineData("Booking", 0)]
    public void The_documented_issue_answer_is_read_with_its_status_by_name(string status, int code)
    {
        var json = $$"""{ "Pnr": "RE6MIK", "IsSuccessful": true, "Message": "Successful", "BookingStatus": "{{status}}" }""";

        TripsAfricaMapping.TryReadIssueAnswer(json, out var answer).Should().BeTrue();

        answer.IsSuccessful.Should().BeTrue();
        answer.Pnr.Should().Be("RE6MIK");
        answer.StatusCode.Should().Be(code);
    }

    [Fact]
    public void The_documented_failure_writes_null_as_a_string_and_it_reads_as_nothing()
    {
        const string json = """{ "Pnr": "null", "IsSuccessful": false, "Message": "Invalid sessionId", "BookingStatus": "null" }""";

        TripsAfricaMapping.TryReadIssueAnswer(json, out var answer).Should().BeTrue();

        answer.IsSuccessful.Should().BeFalse();
        answer.Pnr.Should().BeNull();
        answer.StatusCode.Should().BeNull();
        answer.Message.Should().Be("Invalid sessionId");
    }

    [Fact]
    public void An_issue_answer_that_is_not_json_is_not_read()
    {
        TripsAfricaMapping.TryReadIssueAnswer("<html>Bad gateway</html>", out _).Should().BeFalse();
    }

    [Fact]
    public void The_status_query_answer_is_read_and_a_missing_booking_is_the_documented_code_100()
    {
        TripsAfricaMapping.TryReadBookingStatus(
            """{ "StatusCode": 3, "StatusDescription": "TicketPending", "ErrorList": [] }""", out var pending, out _).Should().BeTrue();

        TripsAfricaMapping.TryReadBookingStatus(
            """{ "StatusCode": 100, "StatusDescription": "Error", "ErrorList": ["No Valid booking found for the given record"] }""",
            out var missing,
            out var message).Should().BeTrue();

        pending.Should().Be(3);
        missing.Should().Be(100);
        message.Should().Contain("No Valid booking found");
        TripsAfricaMapping.TryReadBookingStatus("""{ "StatusDescription": "Pending" }""", out _, out _).Should().BeFalse();
    }

    [Fact]
    public void The_bus_reservation_answer_is_read_from_the_documented_sample()
    {
        const string json = """
            { "ReferenceNumber": null, "BookingReferenceId": "1EA30721AGEJ", "BookingStatus": 2, "AgentName": "LIBRA Motors",
              "BookingStatusName": "TicketIssued" }
            """;

        TripsAfricaMapping.TryReadBusReservation(json, out var code, out var pnr, out _).Should().BeTrue();

        code.Should().Be(2);
        pnr.Should().Be("1EA30721AGEJ");
    }

    [Theory]
    [InlineData(0, SupplierBookingStatus.Failed)]
    [InlineData(1, SupplierBookingStatus.Cancelled)]
    [InlineData(2, SupplierBookingStatus.Ticketed)]
    [InlineData(3, SupplierBookingStatus.TicketPending)]
    [InlineData(11, SupplierBookingStatus.Failed)]
    [InlineData(100, null)]
    [InlineData(42, null)]
    public void Each_status_code_means_what_the_documentation_and_the_reversal_rules_say(int code, SupplierBookingStatus? expected)
    {
        TripsAfricaMapping.StatusFor(code).Should().Be(expected);
    }

    // --------------------------------------------------------------------- through the adapter

    [Fact]
    public async Task An_issue_call_goes_to_the_v2_endpoint_once_and_reads_the_answer()
    {
        var network = new CountingHandler(HttpStatusCode.OK, """{ "Pnr": "RE6MIK", "IsSuccessful": true, "Message": "Successful", "BookingStatus": "TicketPending" }""");

        var result = await Ticketing(network).IssueAsync(SupplierProductType.Flight, Context, IssueRequest(SupplierProductType.Flight, "International", "Flight"), default);

        network.Paths.Should().ContainSingle().Which.Should().Be("/api/v2/ticketing/issue");
        result.Outcome.Should().Be(SupplierIssueOutcome.Accepted);
        result.Status.Should().Be(SupplierBookingStatus.TicketPending);
        result.Pnr.Should().Be("RE6MIK");
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task A_server_error_on_issue_is_an_unknown_outcome_and_is_sent_once(HttpStatusCode status)
    {
        var network = new CountingHandler(status, "down");

        var result = await Ticketing(network).IssueAsync(SupplierProductType.Flight, Context, IssueRequest(SupplierProductType.Flight, "International", "Flight"), default);

        result.Outcome.Should().Be(SupplierIssueOutcome.Unknown, "a 5xx may still have issued a ticket");
        network.Attempts.Should().Be(1);
    }

    [Fact]
    public async Task A_refusal_on_issue_is_reported_as_rejected_and_is_sent_once()
    {
        var network = new CountingHandler(HttpStatusCode.BadRequest, """{ "Pnr": "null", "IsSuccessful": false, "Message": "Invalid sessionId", "BookingStatus": "null" }""");

        var result = await Ticketing(network).IssueAsync(SupplierProductType.Bus, Context, IssueRequest(SupplierProductType.Bus, "Domestic", "Road"), default);

        result.Outcome.Should().Be(SupplierIssueOutcome.Rejected);
        result.HttpStatusCode.Should().Be(400);
        result.Message.Should().Be("Invalid sessionId");
        network.Attempts.Should().Be(1);
    }

    [Fact]
    public async Task A_flight_status_query_sends_the_confirmation_code_and_surname()
    {
        var network = new CountingHandler(HttpStatusCode.OK, """{ "StatusCode": 2, "StatusDescription": "TicketIssued", "ErrorList": [] }""");

        var result = await Ticketing(network).GetStatusAsync(
            SupplierProductType.Flight, Context, new SupplierStatusQuery(SupplierProductType.Flight, "36516|12QFDT", PassengerSurname: "Adama"), default);

        network.Paths.Should().ContainSingle().Which.Should().Be("/api/Flight/GetBookingStatus");
        network.Bodies.Single().Should().Contain("\"ConfirmationCode\":\"36516|12QFDT\"").And.Contain("\"Surname\":\"Adama\"");
        result.Outcome.Should().Be(SupplierPollOutcome.Answered);
        result.SupplierStatusCode.Should().Be(2);
        result.Status.Should().Be(SupplierBookingStatus.Ticketed);
    }

    [Fact]
    public async Task A_bus_booking_with_a_pnr_is_looked_up_by_reservation()
    {
        var network = new CountingHandler(HttpStatusCode.OK, """{ "BookingReferenceId": "1EA30721AGEJ", "BookingStatus": 3, "BookingStatusName": "TicketPending" }""");

        var result = await Ticketing(network).GetStatusAsync(
            SupplierProductType.Bus, Context, new SupplierStatusQuery(SupplierProductType.Bus, "code", Pnr: "1EA30721AGEJ", PassengerSurname: "Mike"), default);

        network.Paths.Should().ContainSingle().Which.Should().Be("/api/Bus/MyReservation");
        network.Bodies.Single().Should().Contain("\"BookingReferenceType\":10");
        result.Status.Should().Be(SupplierBookingStatus.TicketPending);
    }

    [Fact]
    public async Task A_status_query_with_no_surname_is_refused_before_anything_is_sent()
    {
        var network = new CountingHandler(HttpStatusCode.OK, "{}");

        var ask = () => Ticketing(network).GetStatusAsync(
            SupplierProductType.Flight, Context, new SupplierStatusQuery(SupplierProductType.Flight, "36516|12QFDT"), default);

        await ask.Should().ThrowAsync<ArgumentException>();
        network.Attempts.Should().Be(0);
    }

    [Fact]
    public async Task An_unknown_booking_is_reported_as_not_found()
    {
        var network = new CountingHandler(HttpStatusCode.NotFound, "");

        var result = await Ticketing(network).GetStatusAsync(
            SupplierProductType.Flight, Context, new SupplierStatusQuery(SupplierProductType.Flight, "x", PassengerSurname: "Adama"), default);

        result.Outcome.Should().Be(SupplierPollOutcome.NotFound);
    }

    // ------------------------------------------------------- ADR-0003, through the real registration

    [Fact]
    public async Task Through_the_real_registration_a_timed_out_issue_call_is_sent_exactly_once()
    {
        var network = CountingHandler.NeverAnswering();
        await using var provider = RealRegistration(network, issueTimeoutSeconds: 1);

        var watch = Stopwatch.StartNew();
        var result = await RealTicketing(provider).IssueAsync(
            SupplierProductType.Flight, Context, IssueRequest(SupplierProductType.Flight, "International", "Flight"), default);

        result.Outcome.Should().Be(SupplierIssueOutcome.Unknown, "a timeout is an unknown outcome, not a failure");
        network.Attempts.Should().Be(1, "the issue call is never retried");
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10), "the issue client's own timeout ended it");
    }

    [Fact]
    public async Task Through_the_real_registration_a_server_error_on_issue_is_sent_exactly_once()
    {
        var network = new CountingHandler(HttpStatusCode.ServiceUnavailable, "down");
        await using var provider = RealRegistration(network, issueTimeoutSeconds: 45);

        var result = await RealTicketing(provider).IssueAsync(
            SupplierProductType.Flight, Context, IssueRequest(SupplierProductType.Flight, "International", "Flight"), default);

        result.Outcome.Should().Be(SupplierIssueOutcome.Unknown);
        network.Attempts.Should().Be(1);
    }

    [Fact]
    public void The_issue_client_has_its_own_timeout_of_forty_five_seconds_by_default()
    {
        var options = DependencyInjection.ReadOptions(new ConfigurationBuilder().Build());

        options.IssueTimeout.Should().Be(TimeSpan.FromSeconds(45));
        options.BookingTimeout.Should().Be(TimeSpan.FromSeconds(60), "confirm and status keep their own");
    }

    [Fact]
    public void An_issue_timeout_the_poller_could_overtake_stops_startup()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["TripsAfrica:IssueTimeoutSeconds"] = "120" })
            .Build();

        var read = () => DependencyInjection.ReadOptions(configuration);

        read.Should().Throw<InvalidOperationException>().WithMessage("*IssueTimeoutSeconds*");
    }

    // ---------------------------------------------------------------------------- building

    private static SupplierIssueRequest IssueRequest(SupplierProductType product, string? tripType, string? tripMode) =>
        new(product, "8646790ccb9a4d0997a6b52693287256", tripType, tripMode, ["36516|12QFDT"], "order-line:1");

    private static TripsAfricaTicketing Ticketing(CountingHandler network)
    {
        // Straight onto the stub: what the audit handler adds is SupplierHttpClientRegistrationTests' job.
        var http = new HttpClient(network) { BaseAddress = new Uri("https://trips.test/") };
        var credentials = new TripsAfricaCredentials(new FixedCredentialStore(), new TripsAfricaOptions());

        return new TripsAfricaTicketing(
            new TripsAfricaIssueHttp(http), new TripsAfricaBookingHttp(http), credentials, new TripsAfricaSupplier(SupplierId));
    }

    /// <summary>AddTripsAfrica as the hosts call it, with only the network swapped for the stub.</summary>
    private static ServiceProvider RealRegistration(CountingHandler network, int issueTimeoutSeconds)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TripsAfrica:BaseUrl"] = "https://trips.test",
                ["TripsAfrica:IssueTimeoutSeconds"] = issueTimeoutSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ISupplierCallRecorder, RecordingSupplierCallRecorder>();
        services.AddTripsAfrica(configuration);

        // The primary handler is the network, not a pipeline handler: the registration's check that
        // nothing but the audit handler sits in the pipeline still runs, and still passes.
        services.AddHttpClient<TripsAfricaIssueHttp, TripsAfricaIssueHttp>().ConfigurePrimaryHttpMessageHandler(() => network);

        return services.BuildServiceProvider();
    }

    private static TripsAfricaTicketing RealTicketing(ServiceProvider provider) =>
        new(
            provider.GetRequiredService<TripsAfricaIssueHttp>(),
            provider.GetRequiredService<TripsAfricaBookingHttp>(),
            new TripsAfricaCredentials(new FixedCredentialStore(), provider.GetRequiredService<TripsAfricaOptions>()),
            new TripsAfricaSupplier(SupplierId));

    /// <summary>Answers every request the same way, and counts them.</summary>
    private sealed class CountingHandler : HttpMessageHandler
    {
        private readonly Func<CancellationToken, Task<HttpResponseMessage>> _answer;
        private int _attempts;

        public CountingHandler(HttpStatusCode status, string body) =>
            _answer = _ => Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });

        private CountingHandler(Func<CancellationToken, Task<HttpResponseMessage>> answer) => _answer = answer;

        public int Attempts => _attempts;

        public List<string> Paths { get; } = [];

        public List<string> Bodies { get; } = [];

        /// <summary>A supplier that has gone quiet: it never answers, and only notices being cancelled.</summary>
        public static CountingHandler NeverAnswering() =>
            new(async cancellationToken =>
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
                throw new UnreachableException();
            });

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _attempts);

            lock (Paths)
            {
                Paths.Add(request.RequestUri!.AbsolutePath);
            }

            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);

            lock (Bodies)
            {
                Bodies.Add(body);
            }

            return await _answer(cancellationToken);
        }
    }

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
}
